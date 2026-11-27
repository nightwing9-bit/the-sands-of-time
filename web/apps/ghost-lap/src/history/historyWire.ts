// Wire shapes, shape checks and the bounded HTTP reads of the owner history routes (Spec 13, task 13.10).
// Checked against GhostLap.Contracts/Solo/GhostLapSoloContracts.cs (SoloTraceView, SoloChildTraceView, SoloComparisonView) and
// Hosts.Realtime/GhostLap/Solo/GhostLapSoloHistoryRoutes.cs. These are the server's own camelCase field names; nothing here is invented.
//
// Two different resources, never one disguised as the other:
//   original   GET /api/ghostlap/solo/originals/{id}/trace?from=&count=&v=      -> SoloTraceView      (source identity + frames)
//   child      GET /api/ghostlap/solo/children/{id}/trace?from=&count=&v=        -> SoloChildTraceView (child identity + frames, prefix included)
//   curve      GET /api/ghostlap/solo/children/{id}/comparison?from=&count=&stride=&v=
// Both trace shapes are normalised to ONE TraceChunk so the store never needs to know which route produced a frame, but the identity keeps
// `kind`, so an Original chunk can never be ingested into a child's history or the reverse.
import { getJson, SoloHttpError, ORIGINALS_ROUTE, CHILDREN_ROUTE, type GhostEventWire, type SoloPinnedWire } from "../solo/soloWire.ts";
import type { CandleSite } from "../candles/CandleField.ts";

export const HISTORY_VERSION = "solo-history/1";
/** `GhostLapSoloWire.MaxTraceCount`: one trace request carries 1..300 states. */
export const TRACE_MAX = 300;
/** A completed Original or child spans tick 0..2700 (2701 states). */
export const FINAL_TICK = 2700;
/** The curve is requested once at this stride: 0, 10, ... 2700 is 271 points, inside the 300-point limit. */
export const CURVE_STRIDE = 10;

/** `SoloGhostFrame`: the state AFTER exactly `tick` transitions. Read-only history, never a living entity. */
export interface HistoryFrameWire {
  tick: number; xFixed: number; yFixed: number; charge: number; overlit: boolean; overlitDeadlineTick: number;
  movementPoints: number; pickupPoints: number; penaltyPoints: number; totalScore: number; pickups: number; bursts: number;
  heldMoveX: number; heldMoveY: number; burstLatch: boolean; collectedCount: number;
  activeCandleIds: string[]; events: GhostEventWire[]; stateHash: string; final: boolean; distanceFixed: number;
  visibleCandles?: CandleSite[] | null;
}

export type TraceKind = "original" | "child";
export type ChildKind = "Challenge" | "Fork";

/** Who a trace belongs to. Repeated on every chunk by the server, so frames of different histories or versions are never mixed. */
export interface TraceIdentity {
  readonly kind: TraceKind;
  readonly id: string;
  /** Child traces only: the Original this future grew from. */
  readonly sourceId: string | null;
  readonly childKind: ChildKind | null;
  /** 0 for an Original and for a Challenge (it starts at tick 0); the exact fork tick for a Fork. */
  readonly forkTick: number;
  readonly finalStateHash: string;
  readonly versionKey: string;
  readonly terminalTick: number;
}
export interface TraceChunk { readonly identity: TraceIdentity; readonly fromTick: number; readonly nextTick: number | null; readonly frames: readonly HistoryFrameWire[] }

export interface ComparisonPointWire {
  tick: number; sourceScore: number; childScore: number; delta: number; afterFork: boolean; final: boolean; sourceStateHash: string; childStateHash: string;
}
export interface ComparisonWire {
  childId: string; sourceId: string; kind: ChildKind; forkTick: number; finalTick: number; fromTick: number; stride: number; nextTick: number | null;
  sourceFinalStateHash: string; childFinalStateHash: string; points: ComparisonPointWire[];
}

// ---- shape checks --------------------------------------------------------------------------------------------------------------------
const isRecord = (v: unknown): v is Record<string, unknown> => typeof v === "object" && v !== null && !Array.isArray(v);
const isInt = (v: unknown): v is number => typeof v === "number" && Number.isSafeInteger(v);
const isStr = (v: unknown): v is string => typeof v === "string" && v.length > 0;
const malformed = (what: string): never => { throw new SoloHttpError("malformed", 200, null, `malformed ${what}`); };
const sameId = (a: string, b: string) => a.toLowerCase() === b.toLowerCase();

export const versionKey = (v: SoloPinnedWire): string =>
  [v.domainId, v.reducerVersion, v.inputSchemaVersion, v.stateSchemaVersion, v.contextSchemaVersion, v.rulesVersion, v.layoutVersion].join("|");

function isVersions(v: unknown): v is SoloPinnedWire {
  return isRecord(v) && isStr(v.domainId) && isStr(v.reducerVersion) && isStr(v.inputSchemaVersion) && isStr(v.stateSchemaVersion) && isStr(v.contextSchemaVersion) &&
    isStr(v.rulesVersion) && isStr(v.layoutVersion);
}
function isEvent(v: unknown): v is GhostEventWire {
  return isRecord(v) && isStr(v.kind) && isInt(v.slot) && isInt(v.otherSlot) && isInt(v.value) && typeof v.candleId === "string";
}
function isVisibleCandle(v: unknown): v is CandleSite {
  return isRecord(v) && isStr(v.id) && Number.isFinite(v.x) && Number.isFinite(v.y) && (v.phase === "active" || v.phase === "telegraph");
}
export function isHistoryFrame(v: unknown): v is HistoryFrameWire {
  return isRecord(v) && isInt(v.tick) && v.tick >= 0 && isInt(v.xFixed) && isInt(v.yFixed) && isInt(v.charge) && v.charge >= 0 && v.charge <= 4 && typeof v.overlit === "boolean" &&
    isInt(v.overlitDeadlineTick) && isInt(v.movementPoints) && isInt(v.pickupPoints) && isInt(v.penaltyPoints) && isInt(v.totalScore) && isInt(v.pickups) && isInt(v.bursts) &&
    isInt(v.heldMoveX) && isInt(v.heldMoveY) && typeof v.burstLatch === "boolean" && isInt(v.collectedCount) && Array.isArray(v.activeCandleIds) &&
    v.activeCandleIds.every((c) => typeof c === "string") && Array.isArray(v.events) && v.events.every(isEvent) && isStr(v.stateHash) && typeof v.final === "boolean" &&
    (v.visibleCandles == null || (Array.isArray(v.visibleCandles) && v.visibleCandles.every(isVisibleCandle)));
}
function frames(v: unknown, from: number, count: number, terminal: number, what: string): HistoryFrameWire[] {
  if (!Array.isArray(v) || v.length !== count || count < 1 || count > TRACE_MAX) return malformed(`${what} frames`);
  const out: HistoryFrameWire[] = [];
  for (let i = 0; i < v.length; i++) {
    const f = v[i];
    // Consecutive ticks only: a gap or a repeat means this is not the slice the server said it was.
    if (!isHistoryFrame(f) || f.tick !== from + i || f.tick > terminal) return malformed(`${what} frame ${i}`);
    out.push({ ...f, distanceFixed: isInt(f.distanceFixed) ? f.distanceFixed : 0 });
  }
  return out;
}
const nextOf = (v: unknown, last: number, terminal: number, what: string): number | null => {
  const expected = last >= terminal ? null : last + 1;
  if ((v ?? null) !== expected) return malformed(`${what} nextTick`);
  return expected;
};

/** `SoloTraceView` of an Original. `expectedId` is the id that was asked for: a chunk about another original is refused. */
export function parseOriginalTrace(body: unknown, expectedId: string): TraceChunk {
  if (!isRecord(body) || body.contractVersion !== HISTORY_VERSION || !isRecord(body.source) || !isInt(body.fromTick) || !isInt(body.count)) return malformed("trace");
  const s = body.source;
  if (!isStr(s.originalId) || !sameId(s.originalId, expectedId) || !isStr(s.finalStateHash) || !isInt(s.terminalTick) || s.terminalTick < 1 || s.terminalTick > FINAL_TICK || !isVersions(s.versions)) {
    return malformed("trace source");
  }
  const list = frames(body.frames, body.fromTick, body.count, s.terminalTick, "trace");
  return {
    identity: { kind: "original", id: s.originalId, sourceId: null, childKind: null, forkTick: 0, finalStateHash: s.finalStateHash, versionKey: versionKey(s.versions), terminalTick: s.terminalTick },
    fromTick: body.fromTick, nextTick: nextOf(body.nextTick, list[list.length - 1]!.tick, s.terminalTick, "trace"), frames: list,
  };
}

/** `SoloChildTraceView` of a saved Challenge or Fork: the child's own states, inherited prefix included. */
export function parseChildTrace(body: unknown, expectedId: string): TraceChunk {
  if (!isRecord(body) || body.contractVersion !== HISTORY_VERSION || !isStr(body.childId) || !sameId(body.childId, expectedId) || !isStr(body.sourceId) ||
    (body.kind !== "Challenge" && body.kind !== "Fork") || !isInt(body.forkTick) || body.forkTick < 0 || !isStr(body.childFinalStateHash) || !isVersions(body.versions) ||
    !isInt(body.terminalTick) || body.terminalTick < 1 || body.terminalTick > FINAL_TICK || !isInt(body.fromTick) || !isInt(body.count) || body.forkTick > body.terminalTick) {
    return malformed("child trace");
  }
  const list = frames(body.frames, body.fromTick, body.count, body.terminalTick, "child trace");
  return {
    identity: { kind: "child", id: body.childId, sourceId: body.sourceId, childKind: body.kind, forkTick: body.forkTick, finalStateHash: body.childFinalStateHash, versionKey: versionKey(body.versions), terminalTick: body.terminalTick },
    fromTick: body.fromTick, nextTick: nextOf(body.nextTick, list[list.length - 1]!.tick, body.terminalTick, "child trace"), frames: list,
  };
}

export function parseComparison(body: unknown, expectedChildId: string): ComparisonWire {
  if (!isRecord(body) || body.contractVersion !== HISTORY_VERSION || !isStr(body.childId) || !sameId(body.childId, expectedChildId) || !isStr(body.sourceId) ||
    (body.kind !== "Challenge" && body.kind !== "Fork") || !isInt(body.forkTick) || !isInt(body.finalTick) || !isInt(body.fromTick) || !isInt(body.stride) || body.stride < 1 ||
    !isStr(body.sourceFinalStateHash) || !isStr(body.childFinalStateHash) || !Array.isArray(body.points) || body.points.length < 1 || body.points.length > TRACE_MAX) return malformed("comparison");
  let previous = -1;
  const points: ComparisonPointWire[] = [];
  for (const p of body.points) {
    if (!isRecord(p) || !isInt(p.tick) || p.tick <= previous || !isInt(p.sourceScore) || !isInt(p.childScore) || !isInt(p.delta) || typeof p.afterFork !== "boolean" ||
      typeof p.final !== "boolean" || p.delta !== p.childScore - p.sourceScore) return malformed("comparison point");
    previous = p.tick;
    points.push({ tick: p.tick, sourceScore: p.sourceScore, childScore: p.childScore, delta: p.delta, afterFork: p.afterFork, final: p.final,
      sourceStateHash: typeof p.sourceStateHash === "string" ? p.sourceStateHash : "", childStateHash: typeof p.childStateHash === "string" ? p.childStateHash : "" });
  }
  return {
    childId: body.childId, sourceId: body.sourceId, kind: body.kind, forkTick: body.forkTick, finalTick: body.finalTick, fromTick: body.fromTick, stride: body.stride,
    nextTick: isInt(body.nextTick) ? body.nextTick : null, sourceFinalStateHash: body.sourceFinalStateHash, childFinalStateHash: body.childFinalStateHash, points,
  };
}

// ---- bounded reads -------------------------------------------------------------------------------------------------------------------
function range(from: number, count: number): void {
  if (!Number.isInteger(from) || from < 0 || from > FINAL_TICK || !Number.isInteger(count) || count < 1 || count > TRACE_MAX) throw new RangeError(`trace slice ${from}+${count} is outside 0..${FINAL_TICK} and 1..${TRACE_MAX}`);
}
export async function fetchOriginalChunk(id: string, from: number, count: number, signal?: AbortSignal, fetchImpl?: typeof fetch): Promise<TraceChunk> {
  range(from, count);
  const body = await getJson(`${ORIGINALS_ROUTE}/${encodeURIComponent(id)}/trace?from=${from}&count=${count}&v=${encodeURIComponent(HISTORY_VERSION)}`, signal, fetchImpl);
  return parseOriginalTrace(body, id);
}
export async function fetchChildChunk(id: string, from: number, count: number, signal?: AbortSignal, fetchImpl?: typeof fetch): Promise<TraceChunk> {
  range(from, count);
  const body = await getJson(`${CHILDREN_ROUTE}/${encodeURIComponent(id)}/trace?from=${from}&count=${count}&v=${encodeURIComponent(HISTORY_VERSION)}`, signal, fetchImpl);
  return parseChildTrace(body, id);
}
/** The whole same-tick curve in ONE request (271 points at stride 10). Never called per slider movement. */
export async function fetchComparison(childId: string, signal?: AbortSignal, fetchImpl?: typeof fetch): Promise<ComparisonWire> {
  const count = Math.floor(FINAL_TICK / CURVE_STRIDE) + 1;
  const body = await getJson(`${CHILDREN_ROUTE}/${encodeURIComponent(childId)}/comparison?from=0&count=${count}&stride=${CURVE_STRIDE}&v=${encodeURIComponent(HISTORY_VERSION)}`, signal, fetchImpl);
  return parseComparison(body, childId);
}

/** `ChallengeResultView` read by id: what a refresh on a child's replay URL needs before the first chunk (head, kind, fork tick). */
export interface ChildSummaryWire { id: string; sourceId: string; kind: ChildKind; forkTick: number; mascotId: number; finalStateHash: string; livingScore: number | null }
export async function fetchChildSummary(id: string, signal?: AbortSignal, fetchImpl?: typeof fetch): Promise<ChildSummaryWire> {
  const body = await getJson(`${CHILDREN_ROUTE}/${encodeURIComponent(id)}`, signal, fetchImpl);
  if (!isRecord(body) || !isStr(body.challengeId) || !sameId(body.challengeId, id) || !isStr(body.originalId) || (body.kind !== "Challenge" && body.kind !== "Fork") || !isInt(body.forkTick) ||
    !isInt(body.mascotId) || body.mascotId < 0 || body.mascotId > 3 || !isStr(body.childFinalStateHash)) return malformed("child");
  return { id: body.challengeId, sourceId: body.originalId, kind: body.kind, forkTick: body.forkTick, mascotId: body.mascotId, finalStateHash: body.childFinalStateHash, livingScore: isInt(body.livingScore) ? body.livingScore : null };
}
