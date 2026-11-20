// Validates: Spec 13 task 13.9 (Solo Original, save and My Histories; R13.26, R13.27). Pure view-model rules, the wire reads, and the solo transport's
// handling of the Original feed and input. The transport tests drive its frame/rejection handlers with wire-shaped frames.
import assert from "node:assert/strict";
import { test } from "node:test";
import {
  AUTO_RETRY_DELAYS_MS, childRow, clearPending, interpretSave, interpretSaveError, libraryCountText, newOperationKey, OPERATION_KEY, readMascot, readPending, secondsAt, shortId,
  verifySequentially, writeMascot, writePending, type Verification,
} from "../src/solo/model.ts";
import {
  getJson, getOriginal, httpFailureText, listChildren, listOriginals, SoloHttpError, type SoloChildWire, type SoloFrameWire, type SoloOriginalWire, type SoloRunStartedWire,
} from "../src/solo/soloWire.ts";
import { MIN_BURST_HOLD_MS, SoloTransport, soloPlayFrame } from "../src/solo/SoloTransport.ts";
import { PlaySession } from "../src/play/PlaySession.ts";

const RUN: SoloRunStartedWire = { operationKey: "k", runId: "run-1", seed: 7, mascotId: 2, rulesVersion: "ghost-v1", layoutVersion: "arena-v1", tickRateHz: 30, roundTicks: 2700 };
/** Legacy Original frame before the additive live projection. */
const basic = (over: Partial<SoloFrameWire> = {}): SoloFrameWire => ({
  tick: 10, xFixed: 200 * 1024, yFixed: 450 * 1024, charge: 0, overlitDeadlineTick: 0, totalScore: 3, final: false,
  distanceFixed: 0, overlit: false, movementPoints: 0, pickupPoints: 0, penaltyPoints: 0, pickups: 0, bursts: 0, burstHits: 0, heldMoveX: 0, heldMoveY: 0, burstLatch: false,
  collectedCount: 0, ackedInputSeq: -1, activeCandleIds: null, events: null, ...over,
});
const original = (over: Partial<SoloOriginalWire> = {}): SoloOriginalWire => ({
  id: "11111111-2222-3333-4444-555555555555", operationKey: "solo-x", seed: 1, mascotId: 1, rulesVersion: "ghost-v1", layoutVersion: "arena-v1", domainId: "ghost-lap", reducerVersion: "ghost-v1",
  inputSchemaVersion: "ghost-input/1", stateSchemaVersion: "ghost-state/1", contextSchemaVersion: "ghost-context/1", finalTick: 2700, finalStateHash: "a".repeat(64), lastRecordHash: "b".repeat(64),
  score: null, ...over,
});
const child = (over: Partial<SoloChildWire> = {}): SoloChildWire => ({
  id: "c1", sourceId: "s1", kind: "Fork", operationKey: "f", forkTick: 1140, finalTick: 2700, finalStateHash: "h", lastRecordHash: "r", score: 410, verified: true, status: "Completed", seed: 1, mascotId: 0,
  versions: { domainId: "ghost-lap", reducerVersion: "ghost-v1", inputSchemaVersion: "i", stateSchemaVersion: "s", contextSchemaVersion: "c", rulesVersion: "ghost-v1", layoutVersion: "arena-v1" }, ...over,
});

// ---- the Original feed -------------------------------------------------------------------------------------------------------------------
test("original frame: head = chosen mascot, world units, and the missing fields stay missing (no candles, no events, no ack, no parts)", () => {
  const f = soloPlayFrame(RUN, basic({ tick: 31, xFixed: 1024 * 640, yFixed: 1024 * 300, charge: 3, totalScore: 42 }), 500, true);
  assert.equal(f.identity, "run-1");
  assert.deepEqual(f.you, { slot: 2, canControl: true });
  const p = f.players[0]!;
  assert.deepEqual([p.slot, p.x, p.y, p.charge, p.totalScore], [2, 640, 300, 3, 42]);
  assert.equal(p.appliedSeq, null, "the default -1 ack is not an applied-sequence floor");
  assert.deepEqual(f.candles, [], "ActiveCandleIds carry no coordinates and are never rebuilt from the seed");
  assert.deepEqual(f.events, []);
  assert.equal(f.eventsComplete, false);
});
test("original frame: Overlit comes from the nonzero deadline (GameModel), not from the sender's always-false flag", () => {
  const on = soloPlayFrame(RUN, basic({ charge: 4, overlitDeadlineTick: 480, overlit: false }), 0, true).players[0]!;
  assert.deepEqual([on.overlit, on.overlitDeadlineTick], [true, 480]);
  const off = soloPlayFrame(RUN, basic({ charge: 3 }), 0, true).players[0]!;
  assert.deepEqual([off.overlit, off.overlitDeadlineTick], [false, null]);
});
test("Original projects server candles and maps ordered live and coalesced events to the selected mascot", () => {
  const pickup = { kind: "CandleCollected", slot: 0, otherSlot: 0, value: 25, candleId: "c-0001" };
  const visibleCandles = [{ id: "c-0002", x: 640, y: 300, phase: "telegraph" as const }];
  const f = soloPlayFrame(RUN, basic({ tick: 90, seq: 89, visibleCandles, missed: [{ tick: 89, events: [pickup] }],
    events: [{ kind: "Mystery", slot: 0, otherSlot: 0, value: 0, candleId: "" }], pickupPoints: 25, ackedInputSeq: 12 }), 0, true);
  assert.deepEqual(f.events.map((e) => [e.key, e.kind, e.slot]), [["run-1:89:0", "CandleCollected", 2], ["run-1:90:0", "Unknown", 2]]);
  assert.deepEqual(f.candles, visibleCandles);
  assert.equal(f.snapshotSequence, 89);
  assert.equal(f.players[0]!.pickupPoints, 25);
  assert.equal(f.players[0]!.appliedSeq, 12);
  assert.equal(f.eventsComplete, true);
});

// ---- transport: frames and input -----------------------------------------------------------------------------------------------------------
interface Sent { seq: number; clientTick: number; moveX: number; moveY: number; requestBurst: boolean }
function liveTransport(clock = { t: 1000 }) {
  const t = new SoloTransport({ mascotId: 2, operationKey: "solo-k", baseUrl: "http://localhost", now: () => clock.t });
  const sent: Sent[] = [];
  const any = t as unknown as { started: unknown; stream: { next: (i: Sent) => void; complete: () => void } | null; locked: boolean; receive: (f: SoloFrameWire) => void; rejected: (r: object) => void; anchor: { tick: number; at: number } };
  any.started = RUN;
  any.locked = false;
  any.stream = { next: (i) => sent.push(i), complete: () => undefined };
  any.anchor = { tick: 0, at: clock.t };
  return { t, any, sent, clock };
}
test("transport drops duplicates and stale frames, flags a missing sequence and locks on the final tick", () => {
  const { t, any } = liveTransport();
  const s = new PlaySession(t);
  any.started = null;
  any.receive(basic({ tick: 3 }));
  assert.equal(s.frame, null);
  any.started = RUN;
  any.receive(basic({ tick: 5, totalScore: 9 }));
  any.receive(basic({ tick: 4, totalScore: 99 })); // older delivery
  assert.equal(s.frame?.tick, 5);
  assert.equal(s.hud(0).players[0]!.totalScore, 9);
  assert.equal(s.hud(0).eventsComplete, false);
  assert.equal(t.delivery, "basic");
  const rich = { events: [], visibleCandles: [], missed: [] };
  any.receive(basic({ ...rich, tick: 6, seq: 1 }));
  assert.equal(t.delivery, "rich");
  assert.equal(s.frame?.eventsComplete, true);
  any.receive(basic({ ...rich, tick: 7, seq: 1 }));
  assert.equal(s.frame?.tick, 6);
  any.receive(basic({ ...rich, tick: 8, seq: 3 }));
  assert.equal(s.frame?.eventsComplete, false);
  any.receive(basic({ ...rich, tick: 2700, seq: 4, final: true }));
  assert.equal(s.frame?.you.canControl, false);
  assert.equal(s.frame?.eventsComplete, false);
  any.receive(basic({ ...rich, tick: 2701, seq: 5 }));
  assert.equal(s.frame?.tick, 2700);
  s.dispose();
});
test("input: strictly increasing sequence, client tick estimated from the last committed tick", () => {
  const { t, any, sent, clock } = liveTransport();
  any.receive(basic({ tick: 100 }));
  clock.t += 100; // 100 ms = 3 ticks at 30 Hz
  t.setIntent({ moveX: 1, moveY: 0, requestBurst: false }, "change");
  t.setIntent({ moveX: 1, moveY: -1, requestBurst: false }, "change");
  assert.deepEqual(sent.map((i) => i.seq), [0, 1]);
  assert.equal(sent[0]!.clientTick, 100 + 1 + 3);
  t.dispose();
});
test("a Burst release inside the minimum hold is deferred, so press and release can never share a server tick", async () => {
  const { t, sent } = liveTransport();
  t.setIntent({ moveX: 0, moveY: 0, requestBurst: true }, "change");
  t.setIntent({ moveX: 0, moveY: 0, requestBurst: false }, "change"); // released at once (a tap)
  assert.deepEqual(sent.map((i) => i.requestBurst), [true], "the release waits");
  await new Promise((r) => setTimeout(r, MIN_BURST_HOLD_MS + 40));
  assert.deepEqual(sent.map((i) => i.requestBurst), [true, false]);
  t.dispose();
});
test("a refused Burst press waits out a frozen commit and retries a bounded number of times", () => {
  const { t, any, sent, clock } = liveTransport();
  const answers: { rejection: string | null; willRetry?: boolean }[] = [];
  t.onInputAnswer((a) => answers.push(a));
  t.setIntent({ moveX: 0, moveY: 0, requestBurst: true }, "change");
  for (let i = 0; i < 6; i++) {
    const tick = 200 + i;
    any.rejected({ seq: i, code: "CommitPending", burstRequested: true, class: "burst_rejected", nextServerTick: tick });
    assert.equal(any.anchor.tick, tick - 1);
    assert.equal(sent.length, Math.min(i + 1, 5), "a frozen tick is never retried immediately");
    any.receive(basic({ tick }));
  }
  const presses = sent.filter((i) => i.requestBurst).length;
  assert.equal(presses, 1 + 4, "one press plus at most four retries");
  assert.equal(answers.length, 6);
  assert.equal(answers[0]!.willRetry, true);
  assert.equal(answers[5]!.willRetry, false);
  clock.t += 0;
  assert.equal(any.anchor.tick, 205);
  t.dispose();
});
test("a refused movement is reported but never re-sent", () => {
  const { t, any, sent } = liveTransport();
  const answers: unknown[] = [];
  t.onInputAnswer((a) => answers.push(a));
  t.setIntent({ moveX: 1, moveY: 0, requestBurst: false }, "change");
  any.rejected({ seq: 0, code: "stale_sequence", burstRequested: false, class: "benign_movement", nextServerTick: 0 });
  assert.equal(sent.length, 1);
  assert.equal(answers.length, 1);
  t.dispose();
});
test("RunEnded(final) locks input and reports an ended connection; RunEnded(interrupted) reports a failure and no original", () => {
  const a = liveTransport();
  const states: string[] = [];
  a.t.onConnection((s) => states.push(s.kind));
  (a.t as unknown as { runEnded: (e: object) => void }).runEnded({ reason: "final", code: null });
  a.t.setIntent({ moveX: 1, moveY: 0, requestBurst: false }, "change");
  assert.deepEqual(states, ["ended"]);
  assert.equal(a.sent.length, 0);
  const b = liveTransport();
  const s2: { kind: string; reason?: string }[] = [];
  b.t.onConnection((s) => s2.push(s));
  (b.t as unknown as { runEnded: (e: object) => void }).runEnded({ reason: "interrupted", code: "unavailable" });
  assert.equal(s2[0]!.kind, "failed");
  assert.match(s2[0]!.reason ?? "", /not saved/);
});

// ---- save outcomes -----------------------------------------------------------------------------------------------------------------------
test("save: only Saved WITH the verified original is saved; Saved without one is never shown as saved", () => {
  assert.equal(interpretSave({ status: "Saved", original: original({ score: 812 }), code: null }, 1).kind, "saved");
  const bare = interpretSave({ status: "Saved", original: null, code: null }, 1);
  assert.deepEqual(bare, { kind: "failed", code: "save_unconfirmed", retryable: true });
  assert.equal(interpretSave({ status: "Saved", original: original(), code: null }, 1).kind, "failed");
  assert.equal(interpretSave({ status: "Saved", original: original({ score: 812, finalTick: 2699 }), code: null }, 1).kind, "failed");
});
test("save: SavePending retries with the same key a bounded number of times, then waits for the player", () => {
  const pending = { status: "SavePending", original: null, code: "unavailable" };
  assert.equal(interpretSave(pending, 1).kind, "saving");
  assert.equal(interpretSave(pending, AUTO_RETRY_DELAYS_MS.length).kind, "saving");
  assert.deepEqual(interpretSave(pending, AUTO_RETRY_DELAYS_MS.length + 1), { kind: "pending", reason: "The game server is unavailable right now." });
});
test("save: failures are typed; an unfinished/interrupted key is 'not an original', a busy server is retryable", () => {
  assert.equal(interpretSave({ status: "Failed", original: null, code: "not_complete" }, 1).kind, "interrupted");
  assert.equal(interpretSave({ status: "Failed", original: null, code: "not_found" }, 1).kind, "interrupted");
  assert.deepEqual(interpretSave({ status: "Failed", original: null, code: "unavailable" }, 1), { kind: "failed", code: "unavailable", retryable: true });
  assert.deepEqual(interpretSave({ status: "Failed", original: null, code: "verification_failed" }, 1), { kind: "failed", code: "verification_failed", retryable: false });
  assert.deepEqual(interpretSaveError("server_busy", "busy"), { kind: "pending", reason: "busy" });
  assert.equal(interpretSaveError("not_found", null).kind, "interrupted");
});

// ---- durable key ---------------------------------------------------------------------------------------------------------------------------
class MemStore { m = new Map<string, string>(); getItem(k: string) { return this.m.get(k) ?? null; } setItem(k: string, v: string) { this.m.set(k, v); } removeItem(k: string) { this.m.delete(k); } }
test("operation keys obey the server's 1-100 character set; the pending record round-trips and rejects garbage", () => {
  const key = newOperationKey();
  assert.match(key, OPERATION_KEY);
  assert.ok(key.length <= 100);
  const store = new MemStore();
  assert.equal(readPending(store), null);
  writePending({ key, mascotId: 3, startedAtMs: 5, ended: "final" }, store);
  assert.deepEqual(readPending(store), { key, mascotId: 3, startedAtMs: 5, ended: "final" });
  clearPending(store);
  assert.equal(readPending(store), null);
  store.setItem("ghostlap.solo.pending", JSON.stringify({ key: "bad key!", mascotId: 0, startedAtMs: 1, ended: "final" }));
  assert.equal(readPending(store), null);
  writeMascot(2, store);
  assert.equal(readMascot(store), 2);
  store.setItem("ghostlap.solo.mascot", "9");
  assert.equal(readMascot(store), 0);
});

// ---- owner library reads -----------------------------------------------------------------------------------------------------------------------
const json = (status: number, body: unknown) => (async () => new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } })) as unknown as typeof fetch;
test("library: a list of originals is shape-checked; the list never carries a score (null), a verified read does", async () => {
  const fullWidthSeed = JSON.parse("9223372036854775807") as number;
  const list = await listOriginals(undefined, json(200, [original({ seed: fullWidthSeed }), { ...original({ id: "2" }), score: undefined }]));
  assert.equal(list.length, 2);
  assert.equal(list[0]!.seed, fullWidthSeed);
  assert.equal(list[0]!.score, null);
  assert.equal((await getOriginal("x", undefined, json(200, original({ score: 640 })))).score, 640);
  await assert.rejects(listOriginals(undefined, json(200, [{ id: "1" }])), (e: unknown) => e instanceof SoloHttpError && e.kind === "malformed");
});
test("library: a host that answers the route with HTML (no proxy / solo disabled) is 'unreachable', not data", async () => {
  const html = (async () => new Response("<!doctype html>", { status: 200, headers: { "content-type": "text/html" } })) as unknown as typeof fetch;
  await assert.rejects(getJson("/api/ghostlap/solo/originals", undefined, html), (e: unknown) => e instanceof SoloHttpError && e.kind === "unreachable");
  assert.match(httpFailureText(new SoloHttpError("unreachable", 200, null, "x")), /Solo may not be enabled|route/);
});
test("library: statuses map to honest kinds (401 session, 404 not yours/unfinished, 429 busy, 409 cannot verify, 503 unavailable)", async () => {
  const kinds: [number, string][] = [[401, "unauthorized"], [404, "not_found"], [429, "busy"], [409, "conflict"], [503, "unavailable"]];
  for (const [status, kind] of kinds) {
    await assert.rejects(getOriginal("x", undefined, json(status, { code: "x" })), (e: unknown) => e instanceof SoloHttpError && e.kind === kind, String(status));
  }
});
test("children page: items and NextAfter are read as sent, and a malformed child is refused", async () => {
  const page = await listChildren("after-1", undefined, (async (url: string) => { assert.match(String(url), /limit=10&after=after-1/); return new Response(JSON.stringify({ items: [child()], nextAfter: null }), { status: 200, headers: { "content-type": "application/json" } }); }) as unknown as typeof fetch);
  assert.equal(page.items.length, 1);
  assert.equal(page.nextAfter, null);
  await assert.rejects(listChildren(null, undefined, json(200, { items: [{ id: "c" }], nextAfter: null })), (e: unknown) => e instanceof SoloHttpError && e.kind === "malformed");
});

// ---- library view-model --------------------------------------------------------------------------------------------------------------------
test("verification sweep: sequential, newest first, stops (rows stay unverified) when the server says busy", async () => {
  const list = [original({ id: "a" }), original({ id: "b" }), original({ id: "c" })];
  const seen: [string, string][] = [];
  const read = async (id: string) => { if (id === "b") throw new SoloHttpError("busy", 429, null, "busy"); return original({ id, score: 5 }); };
  await verifySequentially(list, read, (o, s: Verification) => seen.push([o.id, s.kind]), () => false,
    (e) => ({ message: String(e), retryable: true, busy: e instanceof SoloHttpError && e.kind === "busy" }));
  assert.deepEqual(seen, [["a", "verifying"], ["a", "verified"], ["b", "verifying"], ["b", "unverified"]]);
});
test("child rows: a Fork names its tick, an unverified child shows no score, and there is no nested-fork field to act on", () => {
  const fork = childRow(child());
  assert.equal(fork.detail, "Forked at tick 1140 (38.0 s)");
  assert.equal(fork.scoreText, "410");
  assert.equal(fork.needsAttention, false);
  const bad = childRow(child({ verified: false, score: 410 }));
  assert.equal(bad.scoreText, "–");
  assert.equal(bad.needsAttention, true);
  assert.match(childRow(child({ kind: "Challenge", forkTick: 0 })).title, /^Ghost challenge/);
});
test("vocabulary: ids are short, seconds are tick/30, the 100-row cap is named so it is not read as a total", () => {
  assert.equal(shortId("11111111-2222-3333-4444-555555555555"), "11111111");
  assert.equal(secondsAt(2700), "90.0");
  assert.equal(libraryCountText(7, 100), "7");
  assert.equal(libraryCountText(100, 100), "newest 100");
});
