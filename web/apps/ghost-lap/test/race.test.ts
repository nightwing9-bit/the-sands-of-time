// Validates: Spec 13 task 13.11 (Ghost Challenge and the cinematic historical Fork). Pure journey rules, the strict wire checks, and the race
// transport's handling of the Start reply, the pushed frames and input. No game rule is re-derived: every number here is a server field.
import assert from "node:assert/strict";
import { test } from "node:test";
import type { HistoryFrameWire } from "../src/history/historyWire.ts";
import { PlaySession } from "../src/play/PlaySession.ts";
import { RaceTransport, racePlayFrame, recordedPlayFrame, type RaceRun } from "../src/race/RaceTransport.ts";
import {
  challengePlan, checkStart, clearPendingRace, forkPlan, interpretFinish, interpretFinishError, mayCross, MONTAGE_MAX_MS, MONTAGE_MIN_SPAN, montageDurationMs, montageStartTick,
  montageTick, newRaceKey, RACE_KEY, RACE_RETRY_MS, readPendingRace, writePendingRace,
} from "../src/race/raceModel.ts";
import {
  parseFinish, parsePrepared, parseRaceFrame, parseResult, parseStarted, raceFailureText, type RaceAsk, type RaceFrameWire, type RaceResultWire,
} from "../src/race/raceWire.ts";
import { SoloHubError, type SoloFrameWire } from "../src/solo/soloWire.ts";

const ORIGINAL = "01a1f348-2f96-7931-b389-e135fa088bf8";
const FORK: RaceAsk = { kind: "Fork", originalId: ORIGINAL, forkTick: 1140, mascotId: 2 };
const CHALLENGE: RaceAsk = { kind: "Challenge", originalId: ORIGINAL, forkTick: 0, mascotId: 2 };
const HASH = (n: number) => `h${n}`.padEnd(64, "0");

const ghostAt = (tick: number, over: Partial<HistoryFrameWire> = {}): HistoryFrameWire => ({
  tick, xFixed: (200 + tick) * 1024, yFixed: 450 * 1024, charge: 2, overlit: false, overlitDeadlineTick: 0, movementPoints: tick, pickupPoints: 50, penaltyPoints: 0, totalScore: tick + 50,
  pickups: 2, bursts: 0, heldMoveX: 1, heldMoveY: 0, burstLatch: false, collectedCount: 2, activeCandleIds: [], events: [], stateHash: HASH(tick), final: tick === 2700, distanceFixed: 0, ...over,
});
const livingAt = (tick: number, over: Partial<SoloFrameWire> = {}): SoloFrameWire => ({
  tick, xFixed: (200 + tick) * 1024, yFixed: 450 * 1024, charge: 2, overlitDeadlineTick: 0, totalScore: tick + 50, final: tick === 2700,
  distanceFixed: 0, overlit: false, movementPoints: tick, pickupPoints: 50, penaltyPoints: 0, pickups: 2, bursts: 0, burstHits: 0, heldMoveX: 0, heldMoveY: 0, burstLatch: false,
  collectedCount: 2, ackedInputSeq: -1, activeCandleIds: [], events: [], ...over,
});
const preparedWire = (ask: RaceAsk, over: Record<string, unknown> = {}) => ({
  originalId: ask.originalId, phase: "Ready", finalTick: 2700, finalStateHash: HASH(2700), rulesVersion: "ghost-v1", layoutVersion: "arena-v1", tickRateHz: 30, kind: ask.kind,
  forkTick: ask.forkTick, firstInputTick: ask.forkTick + 1, versions: null, ...over,
});
const startedWire = (ask: RaceAsk, key = "race-k", over: Record<string, unknown> = {}) => ({
  operationKey: key, challengeId: "c0000000-0000-4000-8000-000000000001", originalId: ask.originalId, seed: 7, mascotId: ask.mascotId, rulesVersion: "ghost-v1", layoutVersion: "arena-v1",
  tickRateHz: 30, roundTicks: 2700, living: livingAt(ask.forkTick), ghost: ghostAt(ask.forkTick), kind: ask.kind, forkTick: ask.forkTick, firstInputTick: ask.forkTick + 1,
  sourceFinalStateHash: HASH(2700), versions: null, ...over,
});
const frameWire = (tick: number, over: Partial<RaceFrameWire> = {}): RaceFrameWire => ({ tick, living: livingAt(tick), ghost: ghostAt(tick), delta: 0, final: false, ...over });

// ---- the rewind montage -------------------------------------------------------------------------------------------------------------------
test("montage: about a second for a long rewind, shorter for a short one, none when there is nothing to rewind", () => {
  assert.equal(montageDurationMs(0), 0);
  assert.equal(montageDurationMs(MONTAGE_MIN_SPAN - 1), 0);
  assert.equal(montageDurationMs(1560), MONTAGE_MAX_MS);
  assert.ok(montageDurationMs(40) < MONTAGE_MAX_MS && montageDurationMs(40) >= 240);
  assert.equal(montageDurationMs(Number.NaN), 0);
});
test("montage: starts at the newest allowed tick, only goes DOWN through integer ticks, and is exactly the fork tick at the end", () => {
  const from = 2700, to = 1140;
  assert.equal(montageTick(0, from, to), from);
  assert.equal(montageTick(1, from, to), to);
  assert.equal(montageTick(1.7, from, to), to);
  let prev = from + 1;
  for (let i = 0; i <= 600; i++) {
    const t = montageTick(i / 600, from, to);
    assert.ok(Number.isInteger(t) && t >= to && t <= from, `tick ${t} inside [${to}, ${from}]`);
    assert.ok(t <= prev, "never increases");
    prev = t;
  }
  assert.equal(montageTick(0.5, 1140, 1140), 1140, "nothing to rewind");
  assert.equal(montageTick(0.5, 1000, 1140), 1140, "a start below the target is never used");
  // It brakes into the moment: the last tenth of the progress covers far fewer ticks than the first.
  assert.ok(montageTick(0.9, from, to) - to < from - montageTick(0.1, from, to));
});
test("montage only starts from states that are really held: it stops at the first missing tick above the target", () => {
  const held = new Set<number>(); for (let t = 1000; t <= 1500; t++) held.add(t); for (let t = 1600; t <= 2700; t++) held.add(t);
  assert.equal(montageStartTick(1140, 2700, (t) => held.has(t)), 1500, "tick 1501 is missing, so the rewind never skips across the gap");
  assert.equal(montageStartTick(1650, 2700, (t) => held.has(t)), 2700);
  assert.equal(montageStartTick(1550, 2700, (t) => held.has(t)), 1550, "the target itself is not held: no montage");
  assert.equal(montageStartTick(2700, 2700, () => true), 2700);
});

// ---- the gates ------------------------------------------------------------------------------------------------------------------------------
test("the day-to-night cross needs the server's Ready, the recorded state at T and a finished rewind; nothing else can stand in", () => {
  const all = { prepared: true, recordedAtStart: true, needsMontage: true, montageDone: true };
  assert.equal(mayCross(all), true);
  assert.equal(mayCross({ ...all, prepared: false }), false, "no smoke or timer replaces a verified Ready");
  assert.equal(mayCross({ ...all, recordedAtStart: false }), false);
  assert.equal(mayCross({ ...all, montageDone: false }), false);
  assert.equal(mayCross({ ...all, needsMontage: false, montageDone: false }), true);
});

// ---- the first state is the state that was inspected ---------------------------------------------------------------------------------------------
test("start check: the server's first state equals the recording at T (hash, position, charge, score), and a difference is named", () => {
  const rec = ghostAt(1140);
  const ok = checkStart({ living: livingAt(1140), ghost: ghostAt(1140), forkTick: 1140 }, rec);
  assert.equal(ok.kind, "matched");
  const bad = checkStart({ living: livingAt(1140, { charge: 3 }), ghost: ghostAt(1140, { stateHash: HASH(9) }), forkTick: 1140 }, rec);
  assert.equal(bad.kind, "differs");
  assert.deepEqual(bad.kind === "differs" ? bad.problems : [], ["ghost state hash", "living charge"]);
  assert.equal(checkStart({ living: livingAt(1140), ghost: ghostAt(1140), forkTick: 1140 }, undefined).kind, "unchecked");
  assert.equal(checkStart({ living: livingAt(1140), ghost: ghostAt(1140), forkTick: 1140 }, ghostAt(1141)).kind, "unchecked");
});

// ---- wire: a reply for something else is refused --------------------------------------------------------------------------------------------------
test("Prepared: exactly the asked original, kind and fork tick under the pinned rules; anything else is refused and starts nothing", () => {
  assert.equal(parsePrepared(preparedWire(FORK), FORK).firstInputTick, 1141);
  assert.equal(parsePrepared(preparedWire(CHALLENGE), CHALLENGE).forkTick, 0);
  const refused = (v: unknown, ask: RaceAsk) => assert.throws(() => parsePrepared(v, ask), (e) => e instanceof SoloHubError && e.code.startsWith("mismatch:"));
  refused(preparedWire(FORK, { originalId: "11111111-2222-4333-8444-555555555555" }), FORK);
  refused(preparedWire(FORK, { kind: "Challenge" }), FORK);
  refused(preparedWire(FORK, { forkTick: 1139, firstInputTick: 1140 }), FORK);
  refused(preparedWire(FORK, { firstInputTick: 1140 }), FORK);
  refused(preparedWire(FORK, { phase: "Running" }), FORK);
  refused(preparedWire(FORK, { rulesVersion: "ghost-v2" }), FORK);
  refused(preparedWire(FORK, { layoutVersion: "arena-v2" }), FORK);
  refused({ nope: 1 }, FORK);
  assert.equal(parsePrepared(preparedWire(FORK, { originalId: ORIGINAL.toUpperCase() }), FORK).kind, "Fork", "ids compare case-insensitively");
  assert.match(raceFailureText(new SoloHubError("race", "mismatch:kind")), /different kind than the one you chose/);
});
test("Started: bound to the key, the ask and the fork tick; both frames are the state AFTER the fork tick", () => {
  const ok = parseStarted(startedWire(FORK), FORK, "race-k");
  assert.equal(ok.living.tick, 1140);
  assert.equal(ok.firstInputTick, 1141);
  const refused = (v: unknown) => assert.throws(() => parseStarted(v, FORK, "race-k"), (e) => e instanceof SoloHubError && e.code.startsWith("mismatch:"));
  refused(startedWire(FORK, "other-key"));
  assert.equal(parseStarted(startedWire(FORK, "fork.race-k"), FORK, "race-k").operationKey, "fork.race-k", "the server files a Fork under fork.<key>");
  refused(startedWire(FORK, "challenge.race-k"));
  refused(startedWire(FORK, "race-k", { kind: "Challenge" }));
  refused(startedWire(FORK, "race-k", { mascotId: 1 }));
  refused(startedWire(FORK, "race-k", { living: livingAt(1141) }));
  refused(startedWire(FORK, "race-k", { ghost: ghostAt(1139) }));
  refused(startedWire(FORK, "race-k", { roundTicks: 2000 }));
  refused(startedWire(FORK, "race-k", { firstInputTick: 1140 }));
});
test("Frame: living and ghost must be the SAME absolute tick as the frame, or it is ignored", () => {
  assert.equal(parseRaceFrame(frameWire(1200))?.tick, 1200);
  assert.equal(parseRaceFrame({ ...frameWire(1200), ghost: ghostAt(1199) }), null);
  assert.equal(parseRaceFrame({ ...frameWire(1200), living: livingAt(1201) }), null);
  assert.equal(parseRaceFrame({ ...frameWire(1200), delta: "x" }), null);
  assert.equal(parseRaceFrame({ ...frameWire(1200), missed: [{ tick: 1199, living: "no", ghost: [] }] }), null);
  assert.equal(parseRaceFrame(null), null);
});
test("Result: the server's own subtraction must hold, and a Fork is not a Challenge", () => {
  const result = (over: Record<string, unknown> = {}) => ({
    challengeId: "c1", originalId: ORIGINAL, kind: "Fork", operationKey: "race-k", forkTick: 1140, finalTick: 2700, childFinalStateHash: HASH(1), childLastRecordHash: HASH(2), livingScore: 764,
    sourceScore: 1670, delta: -906, sourceFinalStateHash: HASH(3), sourceLastRecordHash: HASH(4), seed: 1, mascotId: 2, status: "Completed", ...over,
  });
  assert.equal(parseResult(result())?.delta, -906);
  assert.equal(parseResult(result({ delta: -905 })), null);
  assert.equal(parseFinish({ status: "Completed", result: result({ delta: 1 }), code: null }), null);
  assert.equal(parseFinish({ status: "Pending", result: null, code: "unavailable" })?.code, "unavailable");
});

// ---- the save: FinishFork vs FinishChallenge, and what counts as saved --------------------------------------------------------------------------
const resultOf = (over: Partial<RaceResultWire> = {}): RaceResultWire => ({
  challengeId: "c1", originalId: ORIGINAL, kind: "Fork", operationKey: "race-k", forkTick: 1140, finalTick: 2700, childFinalStateHash: HASH(1), childLastRecordHash: HASH(2), livingScore: 764,
  sourceScore: 1670, delta: -906, sourceFinalStateHash: HASH(3), sourceLastRecordHash: HASH(4), seed: 1, mascotId: 2, status: "Completed", ...over,
});
const ASK = { kind: "Fork" as const, originalId: ORIGINAL, forkTick: 1140, key: "race-k" };
test("save: Saved only for a Completed answer about the race that was played", () => {
  assert.equal(interpretFinish({ status: "Completed", result: resultOf(), code: null }, ASK, 1).kind, "saved");
  const wrong = (over: Partial<RaceResultWire>) => assert.deepEqual(interpretFinish({ status: "Completed", result: resultOf(over), code: null }, ASK, 1), { kind: "failed", code: "save_unconfirmed", retryable: true });
  wrong({ kind: "Challenge" });
  wrong({ originalId: "11111111-2222-4333-8444-555555555555" });
  wrong({ operationKey: "another" });
  wrong({ operationKey: "challenge.race-k" });
  assert.equal(interpretFinish({ status: "Completed", result: resultOf({ operationKey: "fork.race-k" }), code: null }, ASK, 1).kind, "saved", "the minted key is the one the server answers with");
  wrong({ forkTick: 1141 });
  wrong({ finalTick: 2699 });
  wrong({ status: "Pending" });
  assert.equal(interpretFinish({ status: "Completed", result: null, code: null }, ASK, 1).kind, "failed", "Completed without a result is never saved");
});
test("save: Pending retries the same key a bounded number of times, then waits for the player; a missing race is not a failure to retry", () => {
  const pending = { status: "Pending", result: null, code: "unavailable" };
  assert.deepEqual(interpretFinish(pending, ASK, 1), { kind: "saving", attempt: 1 });
  assert.equal(interpretFinish(pending, ASK, RACE_RETRY_MS.length).kind, "saving");
  assert.equal(interpretFinish(pending, ASK, RACE_RETRY_MS.length + 1).kind, "pending");
  assert.equal(interpretFinish({ status: "Failed", result: null, code: "not_complete" }, ASK, 1).kind, "interrupted");
  assert.deepEqual(interpretFinish({ status: "Failed", result: null, code: "server_busy" }, ASK, 1), { kind: "failed", code: "server_busy", retryable: true });
  assert.deepEqual(interpretFinish({ status: "Failed", result: null, code: "corrupt_history" }, ASK, 1), { kind: "failed", code: "corrupt_history", retryable: false });
  assert.equal(interpretFinishError("not_found", null).kind, "interrupted");
  assert.equal(interpretFinishError(null, "offline").kind, "pending");
});
test("keys and the one remembered claim", () => {
  const key = newRaceKey(() => "abc-123");
  assert.equal(key, "race-abc-123");
  assert.ok(RACE_KEY.test(newRaceKey()));
  assert.throws(() => newRaceKey(() => "x".repeat(200)));
  const mem = new Map<string, string>();
  const store = { getItem: (k: string) => mem.get(k) ?? null, setItem: (k: string, v: string) => void mem.set(k, v), removeItem: (k: string) => void mem.delete(k) };
  writePendingRace({ key, kind: "Fork", originalId: ORIGINAL, forkTick: 1140, startedAtMs: 5 }, store);
  assert.deepEqual(readPendingRace(store), { key, kind: "Fork", originalId: ORIGINAL, forkTick: 1140, startedAtMs: 5 });
  mem.set("ghostlap.race.pending", JSON.stringify({ key: "bad key!", kind: "Fork", originalId: "x", forkTick: 1, startedAtMs: 1 }));
  assert.equal(readPendingRace(store), null);
  clearPendingRace(store);
  assert.equal(readPendingRace(store), null);
  assert.deepEqual(forkPlan({ originalId: ORIGINAL, mascotId: 1, forkTick: 0 }), { kind: "Fork", originalId: ORIGINAL, mascotId: 1, forkTick: 0 });
  assert.equal(challengePlan({ originalId: ORIGINAL, mascotId: 1 }).forkTick, 0);
});

// ---- frames for the scene ---------------------------------------------------------------------------------------------------------------------------
const RUN: RaceRun = { identity: "race-k", mascotId: 2, kind: "Fork", forkTick: 1140, tickRateHz: 30, roundTicks: 2700 };
test("race frame: the living head is the original's mascot, the ghost is the server's ghost position, and the readout is same-tick", () => {
  const f = racePlayFrame(RUN, frameWire(1200, { delta: -4, ghost: ghostAt(1200, { xFixed: 500 * 1024, yFixed: 300 * 1024, totalScore: 777, charge: 4 }), living: livingAt(1200, { totalScore: 773, charge: 1 }) }), 10, true, true, false);
  assert.deepEqual(f.you, { slot: 2, canControl: true });
  assert.deepEqual(f.ghost, { slot: 2, x: 500, y: 300 });
  assert.deepEqual(f.race, { kind: "Fork", forkTick: 1140, sourceScore: 777, sourceCharge: 4, delta: -4 });
  assert.equal(f.players[0]!.totalScore, 773);
  assert.equal(f.players[0]!.charge, 1);
  assert.deepEqual(f.candles, [], "no candle positions are invented when the server sends none");
  assert.equal(f.identity, "race-k");
});
test("race frame: the GHOST's events (pickups, Bursts) never become living events; only the living ones do, in order, with stable keys", () => {
  const pickup = { kind: "CandleCollected", slot: 0, otherSlot: 0, value: 25, candleId: "c-0001" };
  const burst = { kind: "BurstManual", slot: 0, otherSlot: 0, value: 0, candleId: "" };
  const f = racePlayFrame(RUN, frameWire(1300, {
    ghost: ghostAt(1300, { events: [pickup, burst] }), living: livingAt(1300, { events: [{ kind: "Mystery", slot: 0, otherSlot: 0, value: 0, candleId: "" }] }),
    missed: [{ tick: 1299, living: [pickup], ghost: [burst, burst, burst] }],
  }), 0, true, true, false);
  assert.deepEqual(f.events.map((e) => [e.key, e.kind, e.slot]), [["race-k:1299:0", "CandleCollected", 2], ["race-k:1300:0", "Unknown", 2]]);
  assert.equal(f.eventsComplete, true);
});
test("race frame: a truncated or gapped delivery says so, and the Start reply's own frame carries no events (they are the recorded past)", () => {
  assert.equal(racePlayFrame(RUN, frameWire(1300, { missedTruncated: true }), 0, true, true, false).eventsComplete, false);
  assert.equal(racePlayFrame(RUN, frameWire(1300), 0, true, true, true).eventsComplete, false);
  const first = racePlayFrame(RUN, { tick: 1140, living: livingAt(1140, { events: [{ kind: "CandleCollected", slot: 0, otherSlot: 0, value: 25, candleId: "c-9" }] }), ghost: ghostAt(1140), delta: 0, final: false }, 0, true, false, false);
  assert.deepEqual(first.events, [], "a pickup recorded at the fork tick is history, not a new living pickup");
  assert.equal(first.eventsComplete, true);
  assert.deepEqual(racePlayFrame(RUN, frameWire(2700, { final: true }), 0, true, true, false).you, { slot: 2, canControl: false });
});
test("the night reveal draws the RECORDED state at T for the head and its ghost, and cannot be controlled", () => {
  const visibleCandles = [{ id: "c-0007", x: 120, y: 300, phase: "active" }] as const;
  const f = recordedPlayFrame(RUN, ghostAt(1140, { charge: 3, visibleCandles: [...visibleCandles] }), 0);
  assert.deepEqual(f.you, { slot: 2, canControl: false });
  assert.equal(f.tick, 1140);
  assert.deepEqual([f.players[0]!.x, f.ghost!.x], [1340, 1340], "head and ghost share the recorded position exactly (no forced separation)");
  assert.equal(f.players[0]!.charge, 3);
  assert.equal(f.race!.delta, 0);
  assert.deepEqual(f.events, []);
  assert.deepEqual(f.candles, visibleCandles);
});

// ---- transport -----------------------------------------------------------------------------------------------------------------------------------
interface Sent { seq: number; clientTick: number; moveX: number; moveY: number; requestBurst: boolean }
function fakeHub(handlers: Record<string, (...args: unknown[]) => unknown>) {
  const calls: { method: string; args: unknown[] }[] = [];
  return {
    calls,
    hub: {
      invoke: async (method: string, ...args: unknown[]) => { calls.push({ method, args }); const h = handlers[method]; if (!h) throw new Error(`unexpected ${method}`); return h(...args); },
      on: () => undefined, onclose: () => undefined, start: async () => undefined, stop: async () => undefined,
    },
  };
}
function transportFor(ask: RaceAsk, handlers: Record<string, (...args: unknown[]) => unknown>, clock = { t: 1000 }) {
  const t = new RaceTransport({ ...ask, operationKey: "race-k", baseUrl: "http://localhost", now: () => clock.t });
  const fake = fakeHub(handlers);
  const any = t as unknown as { hub: unknown; prepared: unknown; started: unknown; locked: boolean; hubOpen: boolean; receive: (f: unknown) => void; rejected: (r: object) => void; runEnded: (e: object) => void; anchor: { tick: number; at: number } };
  any.hub = fake.hub;
  any.hubOpen = true;
  return { t, any, fake, clock };
}
test("begin: parses the Start reply against the ask, then the server's first frame reaches the scene and control opens at fork tick + 1", async () => {
  const { t, any, fake } = transportFor(FORK, { StartChallenge: () => ({ ok: true, code: null, value: startedWire(FORK) }), StreamChallengeInputs: () => new Promise(() => undefined) });
  const s = new PlaySession(t);
  any.prepared = parsePrepared(preparedWire(FORK), FORK);
  const run = await t.begin();
  assert.equal(run.firstInputTick, 1141);
  assert.deepEqual(fake.calls[0], { method: "StartChallenge", args: [{ operationKey: "race-k" }] });
  assert.equal(s.frame?.tick, 1140);
  assert.equal(s.frame?.you.canControl, true);
  assert.equal(s.hud(0).connection.kind, "live");
  assert.deepEqual(s.frame?.events, []);
  assert.equal(any.anchor.tick, 1140, "the input tick estimate starts at the fork tick, not at 0");
  t.dispose();
});
test("begin: a Start reply for another kind or fork tick is refused and nothing becomes live", async () => {
  const { t, any } = transportFor(FORK, { StartChallenge: () => ({ ok: true, code: null, value: startedWire(FORK, "race-k", { forkTick: 0, firstInputTick: 1, living: livingAt(0), ghost: ghostAt(0) }) }) });
  any.prepared = parsePrepared(preparedWire(FORK), FORK);
  await assert.rejects(() => t.begin(), (e) => e instanceof SoloHubError && e.code === "mismatch:forkTick");
  assert.equal(any.started, null);
  t.setIntent({ moveX: 1, moveY: 0, requestBurst: false }, "change"); // no stream: nothing can be sent
  t.dispose();
});
test("begin: never without a Ready reply, and never twice; a typed server refusal surfaces as itself", async () => {
  const a = transportFor(FORK, { StartChallenge: () => ({ ok: true, code: null, value: startedWire(FORK) }) });
  await assert.rejects(() => a.t.begin(), (e) => e instanceof SoloHubError && e.code === "not_ready");
  assert.equal(a.fake.calls.length, 0, "StartChallenge is not even called before the server said Ready");
  const b = transportFor(FORK, { StartChallenge: () => ({ ok: false, code: "not_ready", value: null }) });
  (b.any as { prepared: unknown }).prepared = parsePrepared(preparedWire(FORK), FORK);
  await assert.rejects(() => b.t.begin(), (e) => e instanceof SoloHubError && e.code === "not_ready");
  a.t.dispose(); b.t.dispose();
});
test("pushed frames: stale and duplicate ticks are dropped, a sequence gap is flagged, the final frame locks input and ends control", async () => {
  const { t, any } = transportFor(FORK, { StartChallenge: () => ({ ok: true, code: null, value: startedWire(FORK) }), StreamChallengeInputs: () => new Promise(() => undefined) });
  const s = new PlaySession(t);
  any.prepared = parsePrepared(preparedWire(FORK), FORK);
  await t.begin();
  any.receive(frameWire(1141, { seq: 1, delta: 3 }));
  any.receive(frameWire(1141, { seq: 2, delta: 99 })); // same tick again
  any.receive(frameWire(1140, { seq: 3, delta: 99 })); // older
  assert.equal(s.frame?.tick, 1141);
  assert.equal(s.frame?.race?.delta, 3);
  assert.equal(s.frame?.eventsComplete, true);
  any.receive({ ...frameWire(1142, { seq: 3 }), ghost: ghostAt(1143) }); // not a coherent frame: ignored
  assert.equal(s.frame?.tick, 1141);
  any.receive(frameWire(1144, { seq: 5 })); // seq 4 never arrived
  assert.equal(s.frame?.eventsComplete, false);
  any.receive(frameWire(2700, { seq: 6, final: true }));
  assert.equal(s.frame?.you.canControl, false);
  any.receive(frameWire(2701, { seq: 7 }));
  assert.equal(s.frame?.tick, 2700);
  s.dispose();
});
test("input: a Fork starts at the restored sequence plus one and aims its first press at T+1", async () => {
  const sent: Sent[] = [];
  const { t, any, clock } = transportFor(FORK, {
    StartChallenge: () => ({ ok: true, code: null, value: startedWire(FORK, "race-k", { living: livingAt(1140, { ackedInputSeq: 521 }) }) }),
    StreamChallengeInputs: (stream) => {
      (stream as { subscribe: (observer: { next: (item: Sent) => void; complete: () => void }) => void })
        .subscribe({ next: (item) => sent.push(item), complete: () => undefined });
      return new Promise(() => undefined);
    },
  });
  any.prepared = parsePrepared(preparedWire(FORK), FORK);
  await t.begin();
  try {
    const pump = t as unknown as { keepaliveTick: () => void };
    t.setIntent({ moveX: 1, moveY: 0, requestBurst: false }, "change");
    assert.deepEqual(sent[0], { seq: 522, clientTick: 1141, moveX: 1, moveY: 0, requestBurst: false });
    any.rejected({ seq: 522, code: "CommitPending", class: "benign_movement", burstRequested: false, nextServerTick: 1141 });
    any.receive(frameWire(1141, { seq: 1, living: livingAt(1141, { ackedInputSeq: 521 }) }));
    clock.t += 100;
    pump.keepaliveTick();
    assert.deepEqual(sent[1], { ...sent[0], seq: 523, clientTick: 1145 });

    t.setIntent({ moveX: 0, moveY: 0, requestBurst: false }, "change");
    assert.deepEqual(sent[2], { ...sent[1], seq: 524, moveX: 0 });
    any.receive(frameWire(1145, { seq: 2, living: livingAt(1145, { ackedInputSeq: 524 }) }));
    pump.keepaliveTick();
    assert.equal(sent.length, 3, "the acknowledged release stops neutral retries");

    any.runEnded({ reason: "final", code: null });
    t.setIntent({ moveX: -1, moveY: 0, requestBurst: false }, "change");
    assert.equal(sent.length, 3, "nothing is sent after the race ended");
  } finally {
    t.dispose();
  }
});
test("ChallengeEnded(interrupted) is a failure with no result; a lost connection before the start says nothing was started", () => {
  const a = transportFor(FORK, {});
  const states: { kind: string; reason?: string }[] = [];
  a.t.onConnection((s) => states.push(s));
  a.any.runEnded({ reason: "interrupted", code: "unavailable" });
  assert.equal(states[0]!.kind, "failed");
  assert.match(states[0]!.reason ?? "", /not saved/);
  a.t.dispose();
});
