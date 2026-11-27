// Wire shapes and shape checks of the Ghost Challenge / Fork hub methods (Spec 13, task 13.11).
// Checked against GhostLap.Contracts/Solo/GhostLapSoloContracts.cs (GhostLapChallengeWire, ChallengePrepared, ChallengeStarted, ChallengeFrameView,
// ChallengeFinishView, ChallengeResultView) on /hubs/ghostlap-solo. These are the server's own camelCase names; nothing here is invented.
//
//   PrepareChallenge({originalId}) | PrepareFork({originalId, forkTick})   -> ChallengePrepared   Ready, or a typed failure; NOTHING has started
//   StartChallenge({operationKey})                                         -> ChallengeStarted    the live 30 Hz clock ADVANCES FROM THIS REPLY
//   StreamChallengeInputs(SoloInputItem)                                   one client-to-server stream
//   ChallengeFrame / ChallengeEnded / InputRejected                        server pushes, latest-wins
//   FinishChallenge(key) | FinishFork(key)                                 -> ChallengeFinishView idempotent, durable
//   CancelChallenge()                                                      -> bool                discards an unfinished challenge (never a result)
//
// A reply that does not match what was asked for (another original, another kind, another fork tick, another rules/layout version) is refused,
// never "adapted": the player would otherwise be shown a different history than the one they chose.
import { isHistoryFrame, type HistoryFrameWire } from "../history/historyWire.ts";
import { hubFailureText, SoloHubError, type GhostEventWire, type SoloFrameWire, type SoloPinnedWire } from "../solo/soloWire.ts";

export type RaceKind = "Challenge" | "Fork";
export const RACE_ROUND_TICKS = 2700;
export const RACE_TICK_RATE = 30;
export const RACE_RULES = "ghost-v1";
export const RACE_LAYOUT = "arena-v1";

export interface RacePreparedWire {
  originalId: string; phase: string; finalTick: number; finalStateHash: string; rulesVersion: string; layoutVersion: string; tickRateHz: number;
  kind: RaceKind; forkTick: number; firstInputTick: number; versions: SoloPinnedWire | null;
}
export interface RaceStartedWire {
  operationKey: string; challengeId: string; originalId: string; seed: number; mascotId: number; rulesVersion: string; layoutVersion: string; tickRateHz: number;
  roundTicks: number; living: SoloFrameWire; ghost: HistoryFrameWire; kind: RaceKind; forkTick: number; firstInputTick: number; sourceFinalStateHash: string;
  versions: SoloPinnedWire | null;
}
export interface RaceMissedWire { tick: number; living: GhostEventWire[]; ghost: GhostEventWire[] }
export interface RaceFrameWire {
  tick: number; living: SoloFrameWire; ghost: HistoryFrameWire; delta: number; final: boolean; seq?: number; missed?: RaceMissedWire[] | null; missedTruncated?: boolean;
}
export interface RaceResultWire {
  challengeId: string; originalId: string; kind: RaceKind; operationKey: string; forkTick: number; finalTick: number; childFinalStateHash: string; childLastRecordHash: string;
  livingScore: number; sourceScore: number; delta: number; sourceFinalStateHash: string; sourceLastRecordHash: string; seed: number; mascotId: number; status: string;
}
export interface RaceFinishWire { status: string; result: RaceResultWire | null; code: string | null }

/** What was asked for. A reply is checked against it. */
export interface RaceAsk { readonly kind: RaceKind; readonly originalId: string; readonly forkTick: number; readonly mascotId: number }

// ---- shape checks --------------------------------------------------------------------------------------------------------------------
const isRecord = (v: unknown): v is Record<string, unknown> => typeof v === "object" && v !== null && !Array.isArray(v);
const isInt = (v: unknown): v is number => typeof v === "number" && Number.isSafeInteger(v);
const isStr = (v: unknown): v is string => typeof v === "string" && v.length > 0;
// The C# contract sends an Int64 seed; JavaScript may round it. It is server metadata here and never used to rebuild anything.
const isSeed = (v: unknown): v is number => typeof v === "number" && Number.isInteger(v) && Math.abs(v) <= 2 ** 63;
const sameId = (a: string, b: string) => a.toLowerCase() === b.toLowerCase();
const isKind = (v: unknown): v is RaceKind => v === "Challenge" || v === "Fork";
const isEvent = (v: unknown): v is GhostEventWire => isRecord(v) && isStr(v.kind) && isInt(v.slot) && isInt(v.otherSlot) && isInt(v.value) && typeof v.candleId === "string";

function isPinned(v: unknown): v is SoloPinnedWire {
  return isRecord(v) && isStr(v.domainId) && isStr(v.reducerVersion) && isStr(v.inputSchemaVersion) && isStr(v.stateSchemaVersion) && isStr(v.contextSchemaVersion) &&
    isStr(v.rulesVersion) && isStr(v.layoutVersion);
}
/** The living SoloFrame of a challenge: only what the browser reads is required; the rest is passed through as delivered. */
export function isLivingFrame(v: unknown): v is SoloFrameWire {
  return isRecord(v) && isInt(v.tick) && v.tick >= 0 && isInt(v.xFixed) && isInt(v.yFixed) && isInt(v.charge) && v.charge >= 0 && v.charge <= 4 && isInt(v.totalScore) &&
    isInt(v.overlitDeadlineTick) && typeof v.final === "boolean" && (v.events == null || (Array.isArray(v.events) && v.events.every(isEvent))) &&
    (v.visibleCandles == null || (Array.isArray(v.visibleCandles) && v.visibleCandles.every((c) => isRecord(c) && isStr(c.id) && typeof c.x === "number" && typeof c.y === "number" && (c.phase === "active" || c.phase === "telegraph"))));
}

/**
 * The server mints the stored operation key from the client's key and the kind (SoloBranchKinds.KeyFor): `fork.<key>` for a Fork, `challenge.<key>`
 * for a Challenge. Replies and results name the minted key. Accepting exactly that (or the bare key) also proves the server filed the race under the
 * right operation: a Fork's result is found by FinishFork under `fork.`, never by FinishChallenge.
 */
export const KEY_PREFIX: Readonly<Record<RaceKind, string>> = { Fork: "fork.", Challenge: "challenge." };
export const mintedKey = (kind: RaceKind, clientKey: string): string => `${KEY_PREFIX[kind]}${clientKey}`;
export const isKeyOf = (kind: RaceKind, clientKey: string, replied: string): boolean => replied === clientKey || replied === mintedKey(kind, clientKey);

const mismatch = (what: string): never => { throw new SoloHubError("race", `mismatch:${what}`); };

/** `ChallengePrepared`, checked against the ask. Throws a SoloHubError("race", "mismatch:…") when the server prepared something else. */
export function parsePrepared(v: unknown, ask: RaceAsk): RacePreparedWire {
  if (!isRecord(v) || !isStr(v.originalId) || !isStr(v.phase) || !isInt(v.finalTick) || !isStr(v.finalStateHash) || !isStr(v.rulesVersion) || !isStr(v.layoutVersion) ||
    !isInt(v.tickRateHz) || !isKind(v.kind) || !isInt(v.forkTick) || !isInt(v.firstInputTick)) return mismatch("shape");
  if (!sameId(v.originalId, ask.originalId)) mismatch("original");
  if (v.phase !== "Ready") mismatch("phase");
  if (v.kind !== ask.kind) mismatch("kind");
  if (v.forkTick !== ask.forkTick || v.firstInputTick !== ask.forkTick + 1) mismatch("forkTick");
  if (v.finalTick !== RACE_ROUND_TICKS || v.tickRateHz !== RACE_TICK_RATE || v.rulesVersion !== RACE_RULES || v.layoutVersion !== RACE_LAYOUT) mismatch("rules");
  return { originalId: v.originalId, phase: v.phase, finalTick: v.finalTick, finalStateHash: v.finalStateHash, rulesVersion: v.rulesVersion, layoutVersion: v.layoutVersion,
    tickRateHz: v.tickRateHz, kind: v.kind, forkTick: v.forkTick, firstInputTick: v.firstInputTick, versions: isPinned(v.versions) ? v.versions : null };
}

/** `ChallengeStarted`, checked against the ask and the prepared reply. Its two frames are the state AFTER `forkTick` transitions. */
export function parseStarted(v: unknown, ask: RaceAsk, key: string): RaceStartedWire {
  if (!isRecord(v) || !isStr(v.operationKey) || !isStr(v.challengeId) || !isStr(v.originalId) || !isSeed(v.seed) || !isInt(v.mascotId) || !isStr(v.rulesVersion) || !isStr(v.layoutVersion) ||
    !isInt(v.tickRateHz) || !isInt(v.roundTicks) || !isLivingFrame(v.living) || !isHistoryFrame(v.ghost) || !isKind(v.kind) || !isInt(v.forkTick) || !isInt(v.firstInputTick) ||
    typeof v.sourceFinalStateHash !== "string") return mismatch("shape");
  if (!isKeyOf(ask.kind, key, v.operationKey)) mismatch("key");
  if (!sameId(v.originalId, ask.originalId)) mismatch("original");
  if (v.kind !== ask.kind) mismatch("kind");
  if (v.forkTick !== ask.forkTick || v.firstInputTick !== ask.forkTick + 1) mismatch("forkTick");
  if (v.mascotId !== ask.mascotId) mismatch("mascot");
  if (v.roundTicks !== RACE_ROUND_TICKS || v.tickRateHz !== RACE_TICK_RATE || v.rulesVersion !== RACE_RULES || v.layoutVersion !== RACE_LAYOUT) mismatch("rules");
  if (v.living.tick !== ask.forkTick || v.ghost.tick !== ask.forkTick) mismatch("startTick");
  return { operationKey: v.operationKey, challengeId: v.challengeId, originalId: v.originalId, seed: v.seed, mascotId: v.mascotId, rulesVersion: v.rulesVersion, layoutVersion: v.layoutVersion,
    tickRateHz: v.tickRateHz, roundTicks: v.roundTicks, living: v.living, ghost: v.ghost, kind: v.kind, forkTick: v.forkTick, firstInputTick: v.firstInputTick,
    sourceFinalStateHash: v.sourceFinalStateHash, versions: isPinned(v.versions) ? v.versions : null };
}

/** One `ChallengeFrame`. Returns null for anything that is not a well-formed frame (the transport ignores it rather than guessing). */
export function parseRaceFrame(v: unknown): RaceFrameWire | null {
  if (!isRecord(v) || !isInt(v.tick) || !isLivingFrame(v.living) || !isHistoryFrame(v.ghost) || !isInt(v.delta) || typeof v.final !== "boolean") return null;
  if (v.living.tick !== v.tick || v.ghost.tick !== v.tick) return null;
  let missed: RaceMissedWire[] | null = null;
  if (v.missed != null) {
    if (!Array.isArray(v.missed)) return null;
    missed = [];
    for (const m of v.missed) {
      if (!isRecord(m) || !isInt(m.tick) || !Array.isArray(m.living) || !m.living.every(isEvent) || !Array.isArray(m.ghost) || !m.ghost.every(isEvent)) return null;
      missed.push({ tick: m.tick, living: m.living, ghost: m.ghost });
    }
  }
  return { tick: v.tick, living: v.living, ghost: v.ghost, delta: v.delta, final: v.final, ...(isInt(v.seq) ? { seq: v.seq } : {}), missed, missedTruncated: v.missedTruncated === true };
}

export function parseResult(v: unknown): RaceResultWire | null {
  if (!isRecord(v) || !isStr(v.challengeId) || !isStr(v.originalId) || !isKind(v.kind) || !isStr(v.operationKey) || !isInt(v.forkTick) || !isInt(v.finalTick) || !isStr(v.childFinalStateHash) ||
    !isStr(v.childLastRecordHash) || !isInt(v.livingScore) || !isInt(v.sourceScore) || !isInt(v.delta) || !isStr(v.sourceFinalStateHash) || !isStr(v.sourceLastRecordHash)) return null;
  if (v.delta !== v.livingScore - v.sourceScore) return null; // the server's own subtraction must hold
  return { challengeId: v.challengeId, originalId: v.originalId, kind: v.kind, operationKey: v.operationKey, forkTick: v.forkTick, finalTick: v.finalTick, childFinalStateHash: v.childFinalStateHash,
    childLastRecordHash: v.childLastRecordHash, livingScore: v.livingScore, sourceScore: v.sourceScore, delta: v.delta, sourceFinalStateHash: v.sourceFinalStateHash,
    sourceLastRecordHash: v.sourceLastRecordHash, seed: isSeed(v.seed) ? v.seed : 0, mascotId: isInt(v.mascotId) ? v.mascotId : 0, status: typeof v.status === "string" ? v.status : "Completed" };
}
export function parseFinish(v: unknown): RaceFinishWire | null {
  if (!isRecord(v) || !isStr(v.status)) return null;
  const result = v.result == null ? null : parseResult(v.result);
  if (v.result != null && result === null) return null;
  return { status: v.status, result, code: typeof v.code === "string" ? v.code : null };
}

// ---- words ---------------------------------------------------------------------------------------------------------------------------
const RACE_TEXT: Readonly<Record<string, string>> = {
  fork_not_allowed: "The last moment can't be forked: nothing is left to change. Pick an earlier one.",
  invalid_tick: "That moment isn't part of this history.",
  preparation_busy: "The server is preparing other histories right now. Try again in a moment.",
  challenge_active: "You already have a challenge or fork in progress, possibly in another tab. Finish or close it first.",
  not_ready: "The history wasn't ready to start.",
  not_complete: "This race hasn't finished on the server.",
  not_found: "That original isn't available to you. It may not be yours, or it never finished.",
  corrupt_history: "The stored recording failed its integrity check, so it can't be raced.",
  version_unsupported: "This recording was made under rules this server no longer supports.",
  verification_failed: "The server could not verify this recording.",
  save_failed: "The server could not finish saving this race.",
};
/** Words for a refused race call. Server codes first, then the shared hub wording; a reply that did not match the ask names what differed. */
export function raceFailureText(error: unknown): string {
  if (error instanceof SoloHubError) {
    if (error.code.startsWith("mismatch:")) return `The server answered with a different ${error.code.slice(9)} than the one you chose, so nothing was started.`;
    return RACE_TEXT[error.code] ?? hubFailureText(error);
  }
  return hubFailureText(error);
}
export const raceCodeText = (code: string): string => RACE_TEXT[code] ?? hubFailureText(new SoloHubError("race", code));
