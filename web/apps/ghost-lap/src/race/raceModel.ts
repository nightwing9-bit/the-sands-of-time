// Pure rules of the Ghost Challenge / Fork journey (Spec 13, task 13.11). No React, no Pixi, no network.
// Nothing here is a game rule or a history. It decides WHEN the presentation may move on (never before the server said Ready), which REAL recorded
// ticks a rewind montage visits, whether the server's first state is the state the player inspected, and how a save answer is worded.
import type { HistoryFrameWire } from "../history/historyWire.ts";
import type { RaceFinishWire, RaceKind, RaceResultWire, RaceStartedWire } from "./raceWire.ts";
import { isKeyOf, RACE_ROUND_TICKS } from "./raceWire.ts";

/** What the player chose. A Challenge always starts at T0; a Fork is at an explicit tick 0..2699 (a Fork at T0 is still a Fork). */
export interface RacePlan {
  readonly kind: RaceKind;
  readonly originalId: string;
  readonly mascotId: number;
  readonly forkTick: number;
  /** From the library row / replay, when known: pins the recorded frames this journey reuses from the history cache. */
  readonly finalStateHash?: string;
  readonly versionKey?: string;
}
export const challengePlan = (o: { originalId: string; mascotId: number; finalStateHash?: string; versionKey?: string }): RacePlan => ({ kind: "Challenge", forkTick: 0, ...o });
export const forkPlan = (o: { originalId: string; mascotId: number; forkTick: number; finalStateHash?: string; versionKey?: string }): RacePlan => ({ kind: "Fork", ...o });

/** `^[A-Za-z0-9._-]{1,90}$` (StartChallengeRequest). A UUID suffix keeps keys unique per guest and race. */
export const RACE_KEY = /^[A-Za-z0-9._-]{1,90}$/;
export function newRaceKey(random: () => string = () => crypto.randomUUID()): string {
  const key = `race-${random()}`;
  if (!RACE_KEY.test(key)) throw new Error("Generated operation key is not valid");
  return key;
}

export const raceTitle = (kind: RaceKind): string => (kind === "Fork" ? "Fork" : "Ghost Challenge");
/** Where the player's own future begins: the first tick whose decisions are theirs. */
export const firstOwnTick = (plan: Pick<RacePlan, "forkTick">): number => plan.forkTick + 1;

// ---- the rewind montage -------------------------------------------------------------------------------------------------------------------
/** Skill ref 08/06: about one second of backwards history, whatever the interval. */
export const MONTAGE_MAX_MS = 1000;
export const MONTAGE_MIN_MS = 240;
/** Below this many ticks to rewind there is nothing worth showing: the scene is simply at the chosen tick. */
export const MONTAGE_MIN_SPAN = 3;
/** A short rewind plays at most this many times faster than the recording (so 4 s of play does not smear into a flash). */
const MONTAGE_MAX_SPEEDUP = 4;

/** Length of the montage for a rewind of `span` recorded ticks; 0 when there is no montage. */
export function montageDurationMs(span: number): number {
  if (!Number.isFinite(span) || span < MONTAGE_MIN_SPAN) return 0;
  return Math.round(Math.min(MONTAGE_MAX_MS, Math.max(MONTAGE_MIN_MS, (span * 1000) / 30 / MONTAGE_MAX_SPEEDUP)));
}
const easeOutCubic = (u: number): number => 1 - (1 - u) ** 3;
/**
 * The recorded tick shown at montage progress `u` (0..1): it starts at `from` (the last tick the montage is allowed to use), runs DOWN through
 * recorded integer ticks, fast at first and gently settling, and is exactly `to` at u = 1. Never below `to`, never above `from`, never increasing.
 */
export function montageTick(u: number, from: number, to: number): number {
  if (from <= to) return to;
  if (!(u > 0)) return from;
  if (u >= 1) return to;
  return Math.max(to, Math.min(from, Math.round(from + (to - from) * easeOutCubic(u))));
}
/**
 * The highest tick the montage may start from: the end of the UNBROKEN run of loaded states that begins at `to`. The montage only ever shows
 * states that are really held, so a part of the history that has not arrived is never skipped over, guessed or interpolated.
 */
export function montageStartTick(to: number, terminal: number, has: (tick: number) => boolean): number {
  if (!has(to)) return to;
  let t = to;
  while (t < terminal && has(t + 1)) t++;
  return t;
}

// ---- gates ---------------------------------------------------------------------------------------------------------------------------
/**
 * The presentation may leave the day world for the night one only when ALL of these hold. `prepared` is the server's own Ready reply (parsed and
 * checked against the ask); no timer, smoke or animation can stand in for it.
 */
export function mayCross(s: { prepared: boolean; recordedAtStart: boolean; needsMontage: boolean; montageDone: boolean }): boolean {
  return s.prepared && s.recordedAtStart && (!s.needsMontage || s.montageDone);
}

// ---- the first state really is the state that was inspected ----------------------------------------------------------------------------------
export type StartCheck =
  | { readonly kind: "matched"; readonly detail: string }
  | { readonly kind: "unchecked"; readonly detail: string }
  | { readonly kind: "differs"; readonly detail: string; readonly problems: readonly string[] };

/**
 * A Fork inherits the ENTIRE canonical state at its tick, and the ghost is the original at that same tick. So the server's first frames must equal the
 * recorded state the player was looking at: same position, charge, score parts and pickup counts for the living head, and the same state hash for the
 * ghost. Reports what differs instead of hiding it. (The living frame carries no hash of its own.)
 */
export function checkStart(started: Pick<RaceStartedWire, "living" | "ghost" | "forkTick">, recorded: HistoryFrameWire | undefined): StartCheck {
  if (!recorded || recorded.tick !== started.forkTick) return { kind: "unchecked", detail: `The recorded state at T${started.forkTick} was not loaded, so the start could not be compared.` };
  const problems: string[] = [];
  const g = started.ghost, l = started.living;
  if (g.stateHash !== recorded.stateHash) problems.push("ghost state hash");
  const same = (name: string, a: number, b: number) => { if (a !== b) problems.push(name); };
  same("ghost x", g.xFixed, recorded.xFixed); same("ghost y", g.yFixed, recorded.yFixed);
  same("ghost score", g.totalScore, recorded.totalScore);
  same("living x", l.xFixed, recorded.xFixed); same("living y", l.yFixed, recorded.yFixed);
  same("living charge", l.charge, recorded.charge); same("living score", l.totalScore, recorded.totalScore);
  if (l.pickups !== undefined) same("living pickups", l.pickups, recorded.pickups);
  if (l.bursts !== undefined) same("living bursts", l.bursts, recorded.bursts);
  if (problems.length > 0) return { kind: "differs", detail: `The server's first state at T${started.forkTick} differs from the recording: ${problems.join(", ")}.`, problems };
  return { kind: "matched", detail: `The first state is the recording at T${started.forkTick}: same position, charge, score and state hash.` };
}

// ---- save ----------------------------------------------------------------------------------------------------------------------------
export type RaceSave =
  | { readonly kind: "saving"; readonly attempt: number }
  | { readonly kind: "pending"; readonly reason: string | null }
  | { readonly kind: "saved"; readonly result: RaceResultWire }
  | { readonly kind: "failed"; readonly code: string; readonly retryable: boolean }
  | { readonly kind: "interrupted" };

/** Same bounded same-key tries as the Solo save (the server already retries its own completion three times). Then the player decides. */
export const RACE_RETRY_MS: readonly number[] = [1000, 2000, 3000, 5000];
const RETRYABLE = new Set(["unavailable", "server_busy", "save_failed", "error"]);
const NOT_A_RACE = new Set(["not_found", "not_complete"]);

/**
 * Turns the server's `ChallengeFinishView` into what the player sees. Saved ONLY for a Completed answer whose verified child is the one that was
 * raced: same kind, same original, same operation key, same fork tick, a full round. Anything else is never shown as saved.
 */
export function interpretFinish(view: RaceFinishWire, ask: { kind: RaceKind; originalId: string; forkTick: number; key: string }, attempt: number): RaceSave {
  if (view.status === "Completed") {
    const r = view.result;
    const ok = !!r && r.status === "Completed" && r.kind === ask.kind && r.originalId.toLowerCase() === ask.originalId.toLowerCase() && isKeyOf(ask.kind, ask.key, r.operationKey) &&
      r.forkTick === ask.forkTick && r.finalTick === RACE_ROUND_TICKS;
    return ok ? { kind: "saved", result: r } : { kind: "failed", code: "save_unconfirmed", retryable: true };
  }
  if (view.status === "Pending") return attempt <= RACE_RETRY_MS.length ? { kind: "saving", attempt } : { kind: "pending", reason: null };
  const code = view.code ?? "save_failed";
  if (NOT_A_RACE.has(code)) return { kind: "interrupted" };
  return { kind: "failed", code, retryable: RETRYABLE.has(code) };
}
/** A hub-level refusal or a thrown connection error while asking for the result. */
export function interpretFinishError(code: string | null, text: string | null): RaceSave {
  if (code && NOT_A_RACE.has(code)) return { kind: "interrupted" };
  return { kind: "pending", reason: text ?? code };
}

// ---- the one thing remembered in the browser ---------------------------------------------------------------------------------------------
/**
 * A durable claim on a save, so a refresh after the last tick can still ask the server for THAT race's result by its key (FinishFork for a Fork,
 * FinishChallenge for a Challenge). It is never a result, a score or a child.
 */
export interface PendingRace { readonly key: string; readonly kind: RaceKind; readonly originalId: string; readonly forkTick: number; readonly startedAtMs: number }
export const PENDING_RACE_STORAGE_KEY = "ghostlap.race.pending";
type Store = Pick<Storage, "getItem" | "setItem" | "removeItem">;
const browserStore = (): Store | null => { try { return typeof localStorage === "undefined" ? null : localStorage; } catch { return null; } };

export function readPendingRace(store: Store | null = browserStore()): PendingRace | null {
  try {
    const raw = store?.getItem(PENDING_RACE_STORAGE_KEY);
    if (!raw) return null;
    const v = JSON.parse(raw) as Partial<PendingRace>;
    if (typeof v.key !== "string" || !RACE_KEY.test(v.key) || (v.kind !== "Challenge" && v.kind !== "Fork") || typeof v.originalId !== "string" || typeof v.forkTick !== "number" || typeof v.startedAtMs !== "number") return null;
    return { key: v.key, kind: v.kind, originalId: v.originalId, forkTick: v.forkTick, startedAtMs: v.startedAtMs };
  } catch { return null; }
}
export function writePendingRace(p: PendingRace, store: Store | null = browserStore()): void { try { store?.setItem(PENDING_RACE_STORAGE_KEY, JSON.stringify(p)); } catch { /* recovery by key is simply not offered */ } }
export function clearPendingRace(store: Store | null = browserStore()): void { try { store?.removeItem(PENDING_RACE_STORAGE_KEY); } catch { /* ignore */ } }
