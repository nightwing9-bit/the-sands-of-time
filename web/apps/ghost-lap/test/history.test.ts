// Validates: Spec 13 task 13.10 (hourglass-led timeline, replay and comparison; R13.28, R13.29, R13.33).
// Pure wire parsing, the identity-pinned frame store, the bounded chunk loader (ten requests, never one per movement), fork eligibility,
// local playback limits and the same-tick comparison. Synthetic frames stand in for the server here: this is NOT evidence about real histories.
import assert from "node:assert/strict";
import { test } from "node:test";
import {
  CURVE_STRIDE, fetchChildChunk, fetchComparison, fetchOriginalChunk, parseChildTrace, parseComparison, parseOriginalTrace, versionKey, type HistoryFrameWire, type TraceChunk, type TraceIdentity,
} from "../src/history/historyWire.ts";
import {
  CACHE_LIMIT, cachedHistoryCount, chunkCount, chunkOf, chunkStart, chunkTotal, clearHistoryCache, describeLoadError, HistoryFrames, HistoryIntegrityError, HistoryLoader, historyKey, openHistory,
} from "../src/history/historyStore.ts";
import { advancePlayback, clockText, compareAt, forkVerdict, keyTick, ribbonOf, shortClock, signed, tickFromFraction } from "../src/history/model.ts";
import { metaFromChild, metaFromOriginal, metaOfTarget, parseReplayParams } from "../src/history/target.ts";
import { SoloHttpError, type SoloChildWire, type SoloOriginalWire } from "../src/solo/soloWire.ts";

const ID = "0f0f0f0f-1111-4222-8333-444444444444";
const CHILD = "aaaaaaaa-1111-4222-8333-444444444444";
const VERSIONS = { domainId: "ghost-lap", reducerVersion: "ghost-v1", inputSchemaVersion: "ghost-input/1", stateSchemaVersion: "ghost-state/1", contextSchemaVersion: "ghost-context/1", rulesVersion: "ghost-v1", layoutVersion: "arena-v1" };
const HASH = "f".repeat(64);

const frame = (tick: number, over: Partial<HistoryFrameWire> = {}): HistoryFrameWire => ({
  tick, xFixed: (200 + tick) * 1024, yFixed: 450 * 1024, charge: 0, overlit: false, overlitDeadlineTick: 0, movementPoints: tick, pickupPoints: 0, penaltyPoints: 0, totalScore: tick, pickups: 0, bursts: 0,
  heldMoveX: 1, heldMoveY: 0, burstLatch: false, collectedCount: 0, activeCandleIds: [], events: [], stateHash: `h${tick}`, final: tick === 2700, distanceFixed: 0, ...over,
});
const originalIdentity = (over: Partial<TraceIdentity> = {}): TraceIdentity => ({ kind: "original", id: ID, sourceId: null, childKind: null, forkTick: 0, finalStateHash: HASH, versionKey: versionKey(VERSIONS), terminalTick: 2700, ...over });
const childIdentity = (over: Partial<TraceIdentity> = {}): TraceIdentity => ({ kind: "child", id: CHILD, sourceId: ID, childKind: "Fork", forkTick: 1140, finalStateHash: "c".repeat(64), versionKey: versionKey(VERSIONS), terminalTick: 2700, ...over });
const chunkOfFrames = (identity: TraceIdentity, from: number, count: number, make: (t: number) => HistoryFrameWire = frame): TraceChunk => {
  const frames = Array.from({ length: count }, (_, i) => make(from + i));
  const last = from + count - 1;
  return { identity, fromTick: from, nextTick: last >= identity.terminalTick ? null : last + 1, frames };
};

// ---- wire --------------------------------------------------------------------------------------------------------------------------------
const originalBody = (from: number, count: number, over: Record<string, unknown> = {}) => ({
  contractVersion: "solo-history/1", source: { originalId: ID, finalStateHash: HASH, terminalTick: 2700, versions: VERSIONS }, fromTick: from, count,
  nextTick: from + count > 2700 ? null : from + count, frames: Array.from({ length: count }, (_, i) => frame(from + i)), ...over,
});
test("an Original trace slice is accepted and normalised with its identity", () => {
  const chunk = parseOriginalTrace(originalBody(0, 300), ID);
  assert.equal(chunk.frames.length, 300);
  assert.equal(chunk.nextTick, 300);
  assert.deepEqual([chunk.identity.kind, chunk.identity.forkTick, chunk.identity.sourceId], ["original", 0, null]);
  assert.equal(parseOriginalTrace(originalBody(2700, 1), ID).nextTick, null, "the slice holding tick 2700 ends the history");
  const visibleCandles = [{ id: "c-0000", x: 120.5, y: 300, phase: "telegraph" }] as const;
  const withCandles = parseOriginalTrace(originalBody(0, 1, { frames: [frame(0, { visibleCandles: [...visibleCandles] })] }), ID);
  const held = new HistoryFrames(originalIdentity());
  held.ingest(withCandles);
  assert.deepEqual(held.get(0)?.visibleCandles, visibleCandles);
  assert.throws(() => parseOriginalTrace(originalBody(0, 1, { frames: [{ ...frame(0), visibleCandles: [{ ...visibleCandles[0], x: "120" }] }] }), ID), SoloHttpError);
});
test("a trace slice is refused if it is about another original, another contract version, has a gap, or a wrong nextTick", () => {
  assert.throws(() => parseOriginalTrace(originalBody(0, 3), "ffffffff-1111-4222-8333-444444444444"), SoloHttpError);
  assert.throws(() => parseOriginalTrace(originalBody(0, 3, { contractVersion: "solo-history/2" }), ID), SoloHttpError);
  const gap = originalBody(0, 3); gap.frames[2] = frame(5);
  assert.throws(() => parseOriginalTrace(gap, ID), SoloHttpError);
  assert.throws(() => parseOriginalTrace(originalBody(0, 3, { nextTick: 7 }), ID), SoloHttpError);
  assert.throws(() => parseOriginalTrace(originalBody(0, 301), ID), SoloHttpError, "more than 300 states in one slice is not the contract");
});
test("a child trace is its own shape: child identity, kind, fork tick and parent, never an Original's", () => {
  const body = { contractVersion: "solo-history/1", childId: CHILD, sourceId: ID, kind: "Fork", forkTick: 1140, sourceFinalStateHash: HASH, childFinalStateHash: "c".repeat(64), versions: VERSIONS, terminalTick: 2700, fromTick: 1100, count: 3, nextTick: 1103, frames: [1100, 1101, 1102].map((t) => frame(t)) };
  const chunk = parseChildTrace(body, CHILD);
  assert.deepEqual([chunk.identity.kind, chunk.identity.childKind, chunk.identity.forkTick, chunk.identity.sourceId], ["child", "Fork", 1140, ID]);
  assert.throws(() => parseOriginalTrace(body, CHILD), SoloHttpError, "a child answer is not an Original trace");
  assert.throws(() => parseChildTrace(originalBody(0, 3), ID), SoloHttpError, "an Original answer is not a child trace");
});
test("the comparison curve must satisfy delta = child - source at every point", () => {
  const point = (tick: number, s: number, c: number) => ({ tick, sourceScore: s, childScore: c, delta: c - s, afterFork: tick > 1140, final: tick === 2700, sourceStateHash: "a", childStateHash: "b" });
  const body = { contractVersion: "solo-history/1", childId: CHILD, sourceId: ID, kind: "Fork", forkTick: 1140, finalTick: 2700, sourceFinalStateHash: HASH, childFinalStateHash: "c", fromTick: 0, stride: 10, nextTick: null, points: [point(0, 0, 0), point(10, 5, 5), point(1150, 300, 340)] };
  assert.equal(parseComparison(body, CHILD).points[2]!.delta, 40);
  assert.throws(() => parseComparison({ ...body, points: [point(0, 0, 0), { ...point(10, 5, 5), delta: 9 }] }, CHILD), SoloHttpError);
  assert.throws(() => parseComparison({ ...body, points: [point(10, 5, 5), point(10, 5, 5)] }, CHILD), SoloHttpError, "ticks must strictly increase");
});
test("trace reads use the right route, the pinned version and the 1..300 bound; out-of-contract slices never reach the network", async () => {
  const seen: string[] = [];
  const fake = (async (url: string) => {
    seen.push(url);
    const body = url.includes("/children/") ? { contractVersion: "solo-history/1", childId: CHILD, sourceId: ID, kind: "Fork", forkTick: 1140, sourceFinalStateHash: HASH, childFinalStateHash: "c", versions: VERSIONS, terminalTick: 2700, fromTick: 0, count: 1, nextTick: 1, frames: [frame(0)] } : originalBody(0, 1);
    return new Response(JSON.stringify(body), { status: 200, headers: { "content-type": "application/json" } });
  }) as unknown as typeof fetch;
  await fetchOriginalChunk(ID, 0, 1, undefined, fake);
  await fetchChildChunk(CHILD, 0, 1, undefined, fake);
  assert.equal(seen[0], `/api/ghostlap/solo/originals/${ID}/trace?from=0&count=1&v=solo-history%2F1`);
  assert.equal(seen[1], `/api/ghostlap/solo/children/${CHILD}/trace?from=0&count=1&v=solo-history%2F1`);
  await assert.rejects(() => fetchOriginalChunk(ID, 0, 301, undefined, fake), RangeError);
  await assert.rejects(() => fetchOriginalChunk(ID, 0, 0, undefined, fake), RangeError);
  await assert.rejects(() => fetchChildChunk(CHILD, 2701, 1, undefined, fake), RangeError);
  assert.equal(seen.length, 2);
  const curve: string[] = [];
  await fetchComparison(CHILD, undefined, (async (u: string) => { curve.push(u); return new Response("{}", { status: 200, headers: { "content-type": "application/json" } }); }) as unknown as typeof fetch).catch(() => undefined);
  assert.match(curve[0]!, /comparison\?from=0&count=271&stride=10&v=/, `one request carries the whole curve: 271 points at stride ${CURVE_STRIDE}`);
});

// ---- store -------------------------------------------------------------------------------------------------------------------------------
test("chunk arithmetic: 2701 states are ten requests, the last holding only tick 2700", () => {
  assert.equal(chunkTotal(2700), 10);
  assert.deepEqual([chunkOf(0), chunkOf(299), chunkOf(300), chunkOf(2700)], [0, 0, 1, 9]);
  assert.deepEqual([chunkStart(9), chunkCount(0, 2700), chunkCount(8, 2700), chunkCount(9, 2700)], [2700, 300, 300, 1]);
});
test("frames are pinned to the identity the library listed: another id, hash, version, source or fork tick is refused", () => {
  const ok = { kind: "original" as const, id: ID, finalStateHash: HASH, versionKey: versionKey(VERSIONS) };
  new HistoryFrames(ok).ingest(chunkOfFrames(originalIdentity(), 0, 3));
  assert.throws(() => new HistoryFrames({ ...ok, id: "ffffffff-1111-4222-8333-444444444444" }).ingest(chunkOfFrames(originalIdentity(), 0, 3)), HistoryIntegrityError);
  assert.throws(() => new HistoryFrames({ ...ok, finalStateHash: "0".repeat(64) }).ingest(chunkOfFrames(originalIdentity(), 0, 3)), HistoryIntegrityError);
  assert.throws(() => new HistoryFrames({ ...ok, versionKey: "other" }).ingest(chunkOfFrames(originalIdentity(), 0, 3)), HistoryIntegrityError);
  assert.throws(() => new HistoryFrames({ kind: "child", id: ID }).ingest(chunkOfFrames(originalIdentity(), 0, 3)), HistoryIntegrityError, "an Original can never fill a child's history");
  assert.throws(() => new HistoryFrames({ kind: "original", id: CHILD }).ingest(chunkOfFrames(childIdentity(), 0, 3)), HistoryIntegrityError, "a child can never fill an Original's history");
  assert.throws(() => new HistoryFrames({ kind: "child", id: CHILD, forkTick: 900 }).ingest(chunkOfFrames(childIdentity(), 0, 3)), HistoryIntegrityError);
  assert.throws(() => new HistoryFrames({ kind: "child", id: CHILD, sourceId: "ffffffff-1111-4222-8333-444444444444" }).ingest(chunkOfFrames(childIdentity(), 0, 3)), HistoryIntegrityError);
});
test("two slices that disagree about identity, or a held state that changes, are refused and leave what was held untouched", () => {
  const f = new HistoryFrames({ kind: "original", id: ID });
  f.ingest(chunkOfFrames(originalIdentity(), 0, 300));
  assert.throws(() => f.ingest(chunkOfFrames(originalIdentity({ finalStateHash: "0".repeat(64) }), 300, 300)), HistoryIntegrityError);
  assert.throws(() => f.ingest(chunkOfFrames(originalIdentity(), 299, 2, (t) => frame(t, { stateHash: `other${t}` }))), HistoryIntegrityError);
  assert.equal(f.heldCount, 300, "a refused slice stores nothing");
  assert.equal(f.get(299)!.stateHash, "h299");
  f.ingest(chunkOfFrames(originalIdentity(), 299, 2)); // an identical overlap is fine and adds only the new state
  assert.equal(f.heldCount, 301);
});
test("markers come only from recorded events; runs and chunk completeness follow what is held", () => {
  const f = new HistoryFrames({ kind: "original", id: ID });
  f.ingest(chunkOfFrames(originalIdentity(), 0, 300, (t) => frame(t, t === 40 ? { events: [{ kind: "CandleCollected", slot: 0, otherSlot: 0, value: 100, candleId: "c-1" }] } : t === 200 ? { events: [{ kind: "BurstManual", slot: 0, otherSlot: 0, value: 0, candleId: "" }, { kind: "OverlitCancelled", slot: 0, otherSlot: 0, value: 0, candleId: "" }] } : frame(t))));
  assert.deepEqual(f.markers.map((m) => [m.tick, m.kind]), [[40, "candle"], [200, "burst"]]);
  assert.deepEqual(f.loadedRuns(), [[0, 299]]);
  assert.equal(f.chunkLoaded(0), true);
  assert.equal(f.chunkLoaded(1), false);
  assert.equal(f.complete, false);
  assert.equal(f.has(1140), false, "an unloaded tick is not loaded; nothing invents it");
});

// ---- loader ------------------------------------------------------------------------------------------------------------------------------
const instant = () => Promise.resolve();
function fakeServer(identity = originalIdentity()) {
  const asked: [number, number][] = [];
  const fetchChunk = async (from: number, count: number) => { asked.push([from, count]); await instant(); return chunkOfFrames(identity, from, count); };
  return { asked, fetchChunk };
}
const settle = (loader: HistoryLoader) => new Promise<void>((resolve) => { const off = loader.subscribe(() => { if (loader.status.kind === "complete" || loader.status.kind === "failed") { off(); resolve(); } }); });

test("a whole history is ten bounded requests, one at a time, in order", async () => {
  const { asked, fetchChunk } = fakeServer();
  const frames = new HistoryFrames({ kind: "original", id: ID });
  const loader = new HistoryLoader(frames, fetchChunk);
  const done = settle(loader);
  loader.start();
  await done;
  assert.deepEqual(asked, [[0, 300], [300, 300], [600, 300], [900, 300], [1200, 300], [1500, 300], [1800, 300], [2100, 300], [2400, 300], [2700, 1]]);
  assert.equal(frames.complete, true);
  assert.equal(frames.heldCount, 2701);
});
test("scrubbing never queries per movement: a thousand seeks move chunks in the queue but never add a request", async () => {
  const { asked, fetchChunk } = fakeServer();
  const frames = new HistoryFrames({ kind: "original", id: ID });
  const loader = new HistoryLoader(frames, fetchChunk);
  const done = settle(loader);
  loader.start();
  for (let i = 0; i < 1000; i++) loader.want((i * 37) % 2701);
  loader.want(1140);
  await done;
  assert.equal(asked.length, 10, "the whole history costs ten requests however much the person drags");
  assert.equal(new Set(asked.map(([from]) => from)).size, 10, "no chunk was asked for twice");
});
test("a seek into an unloaded chunk moves that chunk to the front", async () => {
  const { asked, fetchChunk } = fakeServer();
  const frames = new HistoryFrames({ kind: "original", id: ID });
  const loader = new HistoryLoader(frames, fetchChunk);
  const done = settle(loader);
  loader.want(1140); // chunk 3 before anything is loaded
  loader.start();
  await done;
  assert.deepEqual(asked[0], [900, 300]);
});
test("a busy answer waits and retries a bounded number of times, then reports a retryable failure; retry resumes", async () => {
  let calls = 0, fail = true;
  const sleeps: number[] = [];
  const frames = new HistoryFrames({ kind: "original", id: ID });
  const loader = new HistoryLoader(frames, async (from, count) => {
    calls++;
    if (fail) throw new SoloHttpError("busy", 429, null, "busy");
    return chunkOfFrames(originalIdentity(), from, count);
  }, { busyDelaysMs: [5, 10], sleep: async (ms) => { sleeps.push(ms); } });
  let done = settle(loader);
  loader.start();
  await done;
  assert.equal(calls, 3, "first try plus two bounded retries");
  assert.deepEqual(sleeps, [5, 10]);
  assert.equal(loader.status.kind, "failed");
  assert.ok(loader.status.kind === "failed" && loader.status.failure.retryable && loader.status.failure.busy);
  fail = false;
  done = settle(loader);
  loader.retry();
  await done;
  assert.equal(frames.complete, true);
});
test("an integrity failure is not retryable and stores nothing; the same history is not served under another hash", async () => {
  const frames = new HistoryFrames({ kind: "original", id: ID, finalStateHash: "0".repeat(64) });
  const loader = new HistoryLoader(frames, async (from, count) => chunkOfFrames(originalIdentity(), from, count));
  const done = settle(loader);
  loader.start();
  await done;
  assert.ok(loader.status.kind === "failed" && loader.status.failure.integrity && !loader.status.failure.retryable);
  loader.retry();
  assert.equal(frames.heldCount, 0);
  assert.equal(loader.requests, 1);
});
test("abort stops the network work, keeps what was loaded, and start resumes without re-asking for it", async () => {
  const asked: number[] = [];
  let release: (() => void) | null = null;
  const frames = new HistoryFrames({ kind: "original", id: ID });
  const loader = new HistoryLoader(frames, (from, count, signal) => new Promise<TraceChunk>((resolve, reject) => {
    asked.push(from);
    const finish = () => resolve(chunkOfFrames(originalIdentity(), from, count));
    if (asked.length === 3) { release = finish; signal.addEventListener("abort", () => reject(signal.reason), { once: true }); } else finish();
  }));
  loader.start();
  await new Promise((r) => setTimeout(r, 15));
  assert.deepEqual(asked, [0, 300, 600], "third request is in flight");
  loader.abort();
  release!(); // a late answer after abort must not be stored
  await new Promise((r) => setTimeout(r, 5));
  assert.equal(frames.heldCount, 600);
  const done = settle(loader);
  loader.start();
  await done;
  assert.deepEqual(asked.slice(3), [600, 900, 1200, 1500, 1800, 2100, 2400, 2700], "resumed at the first missing chunk");
});
test("the cache is keyed by identity: the same hash and version reuse frames, a different hash does not, and it is bounded", () => {
  clearHistoryCache();
  const fetchFor = () => async () => chunkOfFrames(originalIdentity(), 0, 1);
  const k = (hash: string) => historyKey({ kind: "original", id: ID, finalStateHash: hash, versionKey: "v" });
  const a = openHistory(k("h1"), { kind: "original", id: ID }, fetchFor);
  assert.equal(openHistory(k("h1"), { kind: "original", id: ID }, fetchFor).frames, a.frames);
  assert.equal(openHistory(k("h1"), { kind: "original", id: ID }, fetchFor).cached, true);
  assert.notEqual(openHistory(k("h2"), { kind: "original", id: ID }, fetchFor).frames, a.frames, "a different final-state hash is a different history");
  assert.notEqual(historyKey({ kind: "original", id: ID, finalStateHash: "h", versionKey: "v" }), historyKey({ kind: "child", id: ID, finalStateHash: "h", versionKey: "v" }));
  for (let i = 0; i < CACHE_LIMIT + 3; i++) openHistory(k(`x${i}`), { kind: "original", id: ID }, fetchFor);
  assert.equal(cachedHistoryCount(), CACHE_LIMIT);
  clearHistoryCache();
});
test("load failures are worded from the server's own classes", () => {
  assert.match(describeLoadError(new SoloHttpError("not_found", 404, null, "x")).text, /isn't available to you/);
  assert.equal(describeLoadError(new SoloHttpError("conflict", 409, null, "x")).retryable, false);
  assert.equal(describeLoadError(new SoloHttpError("unavailable", 503, null, "x")).retryable, true);
});

// ---- model -------------------------------------------------------------------------------------------------------------------------------
test("time is derived from the integer tick: T0, T1140 and T2700 read exactly, and ms never round into the next second", () => {
  assert.equal(clockText(0), "00:00.000");
  assert.equal(clockText(1140), "00:38.000");
  assert.equal(clockText(1417), "00:47.233");
  assert.equal(clockText(2699), "01:29.966");
  assert.equal(clockText(2700), "01:30.000");
  assert.equal(shortClock(2700), "1:30");
  assert.equal(tickFromFraction(0.4222, 2700), 1140);
  assert.equal(tickFromFraction(2, 2700), 2700);
  assert.equal(tickFromFraction(-1, 2700), 0);
});
test("keyboard: one tick, one second with Shift, ends, and previous/next recorded event", () => {
  const markers = [{ tick: 40 }, { tick: 200 }, { tick: 900 }];
  assert.equal(keyTick("ArrowRight", false, 10, 2700, markers), 11);
  assert.equal(keyTick("ArrowLeft", true, 10, 2700, markers), 0, "clamped at T0");
  assert.equal(keyTick("ArrowRight", true, 2690, 2700, markers), 2700);
  assert.equal(keyTick("Home", false, 500, 2700, markers), 0);
  assert.equal(keyTick("End", false, 500, 2700, markers), 2700);
  assert.equal(keyTick(".", false, 40, 2700, markers), 200);
  assert.equal(keyTick(",", false, 200, 2700, markers), 40);
  assert.equal(keyTick(",", false, 20, 2700, markers), 20, "no earlier event: stay");
  assert.equal(keyTick("x", false, 5, 2700, markers), null);
});
test("Fork is possible only on a confirmed Original at T0..T2699; T2700 and saved futures say why not", () => {
  const v = (tick: number, over: Partial<Parameters<typeof forkVerdict>[0]> = {}) => forkVerdict({ kind: "original", verified: true, tick, terminalTick: 2700, ...over });
  assert.equal(v(0).allowed, true);
  assert.equal(v(1140).allowed, true);
  assert.equal(v(2699).allowed, true);
  assert.equal(v(2700).allowed, false);
  assert.match(v(2700).reason!, /T2700/);
  assert.equal(v(100, { verified: false }).allowed, false);
  const child = v(100, { kind: "child" });
  assert.equal(child.allowed, false);
  assert.match(child.reason!, /can't be forked again/);
});
test("playback follows recorded time at 30 ticks per second and never passes a state that is not loaded", () => {
  const all = () => true;
  assert.deepEqual(advancePlayback(0, 1000, 2700, all), { cursor: 30, ended: false, waiting: false });
  const nearEnd = advancePlayback(2690, 1000, 2700, all);
  assert.equal(nearEnd.cursor, 2700, "playback stops exactly on the final recorded tick");
  assert.equal(nearEnd.ended, true, "arriving on the final tick ends viewing, so the screen pauses on T2700");
  assert.equal(advancePlayback(2700, 16, 2700, all).ended, true);
  const upTo = (n: number) => (t: number) => t <= n;
  const held = advancePlayback(95, 1000, 2700, upTo(100));
  assert.deepEqual(held, { cursor: 100, ended: false, waiting: true }, "stops on the last loaded state and says it is waiting");
  assert.deepEqual(advancePlayback(120, 1000, 2700, upTo(100)), { cursor: 120, ended: false, waiting: true }, "a cursor already past loaded data does not advance");
  assert.equal(advancePlayback(2700, 16, 2700, all).ended, true);
});
test("same-tick comparison: one shared state through the fork, exact where the data is exact, sampled and labelled otherwise", () => {
  const future = new HistoryFrames({ kind: "child", id: CHILD });
  future.ingest(chunkOfFrames(childIdentity(), 1100, 100, (t) => frame(t, { totalScore: t })));
  const source = new HistoryFrames({ kind: "original", id: ID });
  source.ingest(chunkOfFrames(originalIdentity(), 1100, 100, (t) => frame(t, { totalScore: t - 30 })));
  const pt = (tick: number, s: number, c: number) => ({ tick, sourceScore: s, childScore: c, delta: c - s, afterFork: tick > 1140, final: false, sourceStateHash: "a", childStateHash: "b" });
  const points = [pt(1130, 1130, 1130), pt(1140, 1140, 1140), pt(1150, 1120, 1150), pt(1160, 1130, 1160)];
  const base = { forkTick: 1140, future, points };
  assert.deepEqual(compareAt({ ...base, tick: 1120, source: null }), { mode: "exact", atTick: 1120, original: 1120, future: 1120, delta: 0 }, "before the split both worlds are one state");
  assert.deepEqual(compareAt({ ...base, tick: 1140, source: null }), { mode: "exact", atTick: 1140, original: 1140, future: 1140, delta: 0 });
  assert.deepEqual(compareAt({ ...base, tick: 1155, source: null }), { mode: "sampled", atTick: 1150, original: 1120, future: 1150, delta: 30 }, "between samples the server's earlier sample is shown AS a sample");
  assert.deepEqual(compareAt({ ...base, tick: 1155, source }), { mode: "exact", atTick: 1155, original: 1125, future: 1155, delta: 30 }, "with the original loaded the same tick is exact");
  assert.deepEqual(compareAt({ ...base, tick: 1160, source: null }), { mode: "exact", atTick: 1160, original: 1130, future: 1160, delta: 30 }, "a curve point exactly at the tick is exact");
  assert.equal(compareAt({ forkTick: 1140, future: new HistoryFrames({ kind: "child", id: CHILD }), source: null, points: null, tick: 1200 }).mode, "none");
});
test("the divergence ribbon starts at exactly zero at the fork and follows only real samples afterwards", () => {
  const pt = (tick: number, delta: number) => ({ tick, sourceScore: 0, childScore: delta, delta, afterFork: tick > 1140, final: false, sourceStateHash: "", childStateHash: "" });
  const r = ribbonOf([pt(0, 0), pt(1140, 0), pt(1500, -20), pt(2700, 60)], 1140, 2700);
  assert.deepEqual(r.points.map((p) => p.d), [0, -20, 60]);
  assert.equal(r.points[0]!.x, 1140 / 2700);
  assert.equal(r.maxAbs, 60);
  assert.deepEqual(ribbonOf(null, 1140, 2700).points, []);
  assert.deepEqual(ribbonOf([pt(0, 0), pt(1140, 0)], 1140, 2700).points, [], "no samples after the split: no ribbon");
  assert.equal(signed(30), "+30");
  assert.equal(signed(-4), "−4");
  assert.equal(signed(0), "±0");
});

// ---- target ------------------------------------------------------------------------------------------------------------------------------
const original = (over: Partial<SoloOriginalWire> = {}): SoloOriginalWire => ({
  id: ID, operationKey: "k", seed: 1, mascotId: 2, ...VERSIONS, finalTick: 2700, finalStateHash: HASH, lastRecordHash: "b".repeat(64), score: null, ...over,
});
const child = (over: Partial<SoloChildWire> = {}): SoloChildWire => ({
  id: CHILD, sourceId: ID, kind: "Fork", operationKey: "f", forkTick: 1140, finalTick: 2700, finalStateHash: "c".repeat(64), lastRecordHash: "r", score: 410, verified: true, status: "Completed", seed: 1, mascotId: 2, versions: VERSIONS, ...over,
});
test("a replay URL carries only a kind and a D-form id; anything else is ignored before it can reach a route", () => {
  assert.deepEqual(parseReplayParams(`?screen=replay&replay=child&id=${CHILD}`), { kind: "child", id: CHILD });
  assert.deepEqual(parseReplayParams(`?replay=original&id=${ID}`), { kind: "original", id: ID });
  assert.equal(parseReplayParams("?replay=child&id=../../etc"), null);
  assert.equal(parseReplayParams(`?replay=other&id=${ID}`), null);
  assert.equal(parseReplayParams("?replay=original"), null);
});
test("library rows become the pinned identity (hash + versions) the store will hold the server to", () => {
  const o = metaFromOriginal(original());
  assert.deepEqual([o.kind, o.finalStateHash, o.versionKey, o.forkTick, o.childKind], ["original", HASH, versionKey(VERSIONS), 0, null]);
  const c = metaFromChild(child(), original());
  assert.deepEqual([c.kind, c.childKind, c.forkTick, c.sourceId, c.versionKey], ["child", "Fork", 1140, ID, versionKey(VERSIONS)]);
  assert.equal(c.sourceKnown!.finalStateHash, HASH);
  assert.equal(metaFromChild(child({ kind: "Challenge", forkTick: 0 }), null).childKind, "Challenge");
  assert.equal(metaOfTarget({ kind: "child", id: CHILD, child: null, source: null }), null, "after a refresh only the id is known, so the server is asked who it is");
});
