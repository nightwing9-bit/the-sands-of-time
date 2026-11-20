// Validates: Spec 13 task 13.4 (day play). Pure presentation logic, no Pixi/DOM. Frames are built in the shape the transport adapter produces
// (see src/play/playTypes.ts); the bridge test drives the REAL wire parser's output shapes through a fake fetch-free GreyboxWire, which is
// the only seam a unit test can honestly use. No game rule is re-implemented here: the tests only check ordering, identity and feedback.
import assert from "node:assert/strict";
import { test } from "node:test";
import { KeyState, sameIntent } from "../src/play/keyboardIntent.ts";
import { LocalSmoother, RemoteTrack, ServerClock } from "../src/play/visualSmoothing.ts";
import { BURST_CONFIRM_WINDOW_MS, PlaySession, STALL_MS } from "../src/play/PlaySession.ts";
import type { ConnectionState, InputAnswerView, PlayEvent, PlayFrame, PlayTransport, PlayerFrame } from "../src/play/playTypes.ts";
import type { ControlIntent } from "../src/presentation/types.ts";
import { TestHostBridge, arenaMatchesRenderDescriptor, toPlayFrame } from "../src/play/testHostBridge.ts";
import type { GreyboxWire } from "../src/greybox/greyboxWire.ts";
import type { Arena, LiveFrame } from "../src/greybox/types.ts";
import { ARENA_V1_RENDER } from "../src/arena/arenaV1Descriptor.ts";

const player = (over: Partial<PlayerFrame> = {}): PlayerFrame => ({
  slot: 0, x: 200, y: 450, charge: 0, overlit: false, overlitDeadlineTick: null, totalScore: 0, movementPoints: 0, pickupPoints: 0,
  penaltyPoints: 0, pickups: 0, bursts: 0, appliedSeq: -1, ...over,
});
const event = (key: string, kind: PlayEvent["kind"], slot = 0, over: Partial<PlayEvent> = {}): PlayEvent => ({ key, tick: 1, kind, raw: kind, slot, otherSlot: 0, value: 0, candleId: "", ...over });
const frame = (tick: number, at: number, over: Partial<PlayFrame> = {}): PlayFrame => ({
  identity: "run-a", tick, tickRate: 30, roundTicks: 2700, final: false, receivedAtMs: at, players: [player()], candles: [], events: [],
  you: { slot: 0, canControl: true }, eventsComplete: true, ...over,
});

class FakeTransport implements PlayTransport {
  readonly label = "fake";
  readonly production = false;
  sent: { intent: ControlIntent; reason: string }[] = [];
  private f?: (x: PlayFrame) => void;
  private c?: (x: ConnectionState) => void;
  private a?: (x: InputAnswerView) => void;
  onFrame(l: (x: PlayFrame) => void) { this.f = l; return () => undefined; }
  onConnection(l: (x: ConnectionState) => void) { this.c = l; return () => undefined; }
  onInputAnswer(l: (x: InputAnswerView) => void) { this.a = l; return () => undefined; }
  setIntent(intent: ControlIntent, reason: "change" | "neutral") { this.sent.push({ intent, reason }); }
  async start() {}
  dispose() {}
  push(x: PlayFrame) { this.f?.(x); }
  conn(x: ConnectionState) { this.c?.(x); }
  answer(x: InputAnswerView) { this.a?.(x); }
}

test("keys: opposite directions cancel, diagonals combine, Burst press and release are both real changes", () => {
  const k = new KeyState();
  assert.equal(k.press("KeyD"), true);
  assert.deepEqual(k.intent(), { moveX: 1, moveY: 0, requestBurst: false });
  k.press("ArrowLeft");
  assert.equal(k.intent().moveX, 0);
  k.release("ArrowLeft");
  k.press("KeyW");
  assert.deepEqual(k.intent(), { moveX: 1, moveY: -1, requestBurst: false });
  const before = k.intent();
  k.press("Space");
  assert.equal(k.intent().requestBurst, true);
  assert.equal(sameIntent(before, k.intent()), false);
  k.release("Space");
  assert.equal(sameIntent(before, k.intent()), true);
  assert.equal(k.press("KeyQ"), false);
  k.clear();
  assert.deepEqual(k.intent(), { moveX: 0, moveY: 0, requestBurst: false });
});

test("remote interpolation lies between real snapshots, never beyond the newest, and never blends across a teleport", () => {
  const clock = new ServerClock();
  const track = new RemoteTrack(clock);
  // 15 Hz feed (every 2 ticks), arriving with a constant 40 ms network delay.
  const arrive = (tick: number, x: number) => { const at = (tick * 1000) / 30 + 40; clock.observe((tick * 1000) / 30, at); track.push(tick, 30, x, 100); };
  for (let i = 0; i <= 10; i++) arrive(i * 2, 100 + i * 20);
  // render time = now - offset - delay. With delay = 1.5 gaps = 100 ms, at local time of tick 20 arrival the render time is tick 20 minus 100 ms.
  const nowAtTick20 = (20 * 1000) / 30 + 40;
  const p = track.sample(nowAtTick20)!;
  assert.ok(p.x < 300 && p.x > 200, `x=${p.x} must be behind the newest (300) and ahead of an earlier sample`);
  assert.equal(track.sample(nowAtTick20 + 10_000)!.x, 300, "clamped at the newest snapshot, no extrapolation");
  track.push(30, 30, 1500, 100); // an 1200-unit jump 5 ticks later: a restore or a stall, not movement
  // Delay is now clamped to 200 ms (the gap grew to 10 ticks). Render time = tick 25: inside the jump, so the head HOLDS at the old sample.
  const midJump = (25 * 1000) / 30 + 40 + 200;
  assert.equal(track.sample(midJump)!.x, 300);
  assert.equal(track.sample((30 * 1000) / 30 + 40 + 200 + 1)!.x, 1500);
  track.push(1, 30, 0, 0); // the tick went backwards: new baseline
  assert.deepEqual(track.sample(0), { x: 0, y: 0 });
});

test("local smoothing: raw is exact, eased never overshoots or leads, a discontinuity snaps", () => {
  const raw = new LocalSmoother(0);
  raw.setTarget(10, 10);
  raw.setTarget(30, 10);
  assert.deepEqual(raw.update(16), { x: 30, y: 10 });
  const s = new LocalSmoother(60);
  s.setTarget(0, 0);
  s.setTarget(20, 0);
  let last = 0;
  for (let i = 0; i < 40; i++) {
    const p = s.update(16.7)!;
    assert.ok(p.x >= last && p.x <= 20, `x=${p.x} moves toward the target and never passes it`);
    last = p.x;
  }
  assert.ok(Math.abs(last - 20) < 0.5);
  s.setTarget(900, 0); // far beyond one legal move: restore/reset
  assert.equal(s.update(16.7)!.x, 900);
  assert.equal(s.lag, 0);
});

test("session: stale and duplicate frames are ignored; a new identity resets everything remembered", () => {
  const t = new FakeTransport();
  const s = new PlaySession(t);
  t.push(frame(10, 0, { events: [event("run-a:10:0", "CandleCollected", 0, { value: 100 })] }));
  t.push(frame(8, 10));
  assert.equal(s.frame!.tick, 10, "older tick ignored");
  t.push(frame(10, 20));
  assert.equal(s.hud(30).feed.length, 1, "duplicate tick adds nothing");
  t.push(frame(11, 33, { identity: "run-b", events: [event("run-b:11:0", "CandleCollected", 0, { value: 100 })] }));
  assert.equal(s.frame!.identity, "run-b");
  assert.equal(s.hud(40).feed.length, 1, "the old run's feed did not survive");
});

test("session: an event key is consumed once even if a later frame repeats it; cues go to the victim", () => {
  const t = new FakeTransport();
  const s = new PlaySession(t);
  const hit = event("k1", "BurstHit", 1, { otherSlot: 0, value: 40 });
  const fear = event("k2", "ContactDrain", 0, { otherSlot: 1, value: 1 });
  t.push(frame(5, 0, { players: [player(), player({ slot: 1 })], events: [hit, fear] }));
  t.push(frame(6, 33, { players: [player(), player({ slot: 1 })], events: [hit, fear] }));
  const cues = s.drainCues();
  assert.deepEqual(cues.map((c) => [c.kind, c.slot]), [["hit", 1], ["fear", 0]]);
  assert.equal(s.drainCues().length, 0);
});

test("session: Burst is requested, admitted and only CONFIRMED by the server's own event", () => {
  const t = new FakeTransport();
  const s = new PlaySession(t);
  t.push(frame(1, 0, { players: [player({ charge: 4 })] }));
  s.intent({ moveX: 0, moveY: 0, requestBurst: true }, "change", 100);
  assert.equal(s.hud(100).burst!.stage, "requested");
  t.answer({ seq: 3, accepted: true, rejection: null, burstRequested: true, sentAtMs: 100, answeredAtMs: 112 });
  assert.equal(s.hud(113).burst!.stage, "admitted");
  t.push(frame(4, 150, { players: [player({ charge: 0 })], events: [event("run-a:4:0", "BurstManual", 0, { value: 1 })] }));
  assert.equal(s.hud(151).burst!.stage, "confirmed");
  assert.equal(s.drainCues()[0]!.kind, "release");
});

test("session: an admitted Burst that never resolves is reported as not confirmed with the real charge; a refusal is shown as refused", () => {
  const t = new FakeTransport();
  const s = new PlaySession(t);
  t.push(frame(1, 0, { players: [player({ charge: 2 })] }));
  s.intent({ moveX: 0, moveY: 0, requestBurst: true }, "change", 0);
  t.answer({ seq: 1, accepted: true, rejection: null, burstRequested: true, sentAtMs: 0, answeredAtMs: 5 });
  s.render(BURST_CONFIRM_WINDOW_MS + 10, 16);
  assert.equal(s.hud(BURST_CONFIRM_WINDOW_MS + 10).burst!.stage, "not_confirmed");
  assert.match(s.hud(BURST_CONFIRM_WINDOW_MS + 10).burst!.detail, /charge 2\/4/);
  s.intent({ moveX: 0, moveY: 0, requestBurst: false }, "change", 2000);
  s.intent({ moveX: 0, moveY: 0, requestBurst: true }, "change", 2100);
  t.answer({ seq: 2, accepted: false, rejection: "late_tick", burstRequested: true, sentAtMs: 2100, answeredAtMs: 2110 });
  assert.equal(s.hud(2111).burst!.stage, "refused");
  assert.equal(s.hud(2111).metrics.rejectedInputs, 1);
});

test("session: a controller that CanControl=false sends nothing; a stall is reported as connection trouble despite a 'live' transport", () => {
  const t = new FakeTransport();
  const s = new PlaySession(t);
  t.conn({ kind: "live" });
  t.push(frame(1, 0, { you: { slot: 0, canControl: false } }));
  s.intent({ moveX: 1, moveY: 0, requestBurst: false }, "change", 5);
  assert.equal(t.sent.length, 0);
  t.push(frame(2, 40, { you: { slot: 0, canControl: true } }));
  s.intent({ moveX: 1, moveY: 0, requestBurst: false }, "change", 50);
  assert.equal(t.sent.length, 1);
  const hud = s.hud(40 + STALL_MS + 1);
  assert.equal(hud.stalled, true);
  assert.equal(hud.connection.kind, "degraded");
  // Control lost mid-hold: the held input is neutralised toward the server.
  t.push(frame(3, 80, { you: { slot: 0, canControl: false } }));
  assert.equal(t.sent.at(-1)!.reason, "neutral");
});

test("bridge mapping: solo event keys are run + tick + index, candles carry the server phase, and the descriptor check is real", () => {
  const arena: Arena = {
    rulesVersion: "ghost-v1", layoutVersion: "arena-v1", tickRate: 30, roundTicks: 2700, width: 1600, height: 900, colliderHalf: 24, candleHalf: 12, burstRadius: 150, maxCharge: 4,
    obstacles: ARENA_V1_RENDER.obstacles.map((o) => ({ ...o })),
  };
  assert.equal(arenaMatchesRenderDescriptor(arena), true);
  assert.equal(arenaMatchesRenderDescriptor({ ...arena, obstacles: arena.obstacles.slice(1) }), false);
  const live = {
    sessionId: "S", kind: "original", status: "Running", error: null, sourceId: null, forkTick: null, tickRate: 30,
    world: {
      tick: 9, stateHash: null, isFinal: false, collectedCount: 0,
      players: [{ slot: 0, mascotId: 0, x: 210.5, y: 450, charge: 2, overlit: false, overlitDeadlineTick: 0, movementPoints: 3, pickupPoints: 0, penaltyPoints: 0, totalScore: 3, pickups: 0, bursts: 0, lastSequence: 4 }],
      activeCandles: [{ id: "c-0001", x: 300, y: 120, activateTick: 5, expireTick: 215 }],
      telegraphCandles: [{ id: "c-0002", x: 250, y: 300, activateTick: 20, expireTick: 230 }],
      events: [{ kind: "CandleCollected", slot: 0, otherSlot: 0, value: 100, candleId: "c-0001" }, { kind: "SomethingNew", slot: 0, otherSlot: 0, value: 0, candleId: "" }],
    },
    ghost: null, aligned: null, completedId: null, finalStateHash: null,
  } as unknown as LiveFrame;
  const f = toPlayFrame(live, arena, 1234);
  assert.deepEqual(f.events.map((e) => [e.key, e.kind]), [["S:9:0", "CandleCollected"], ["S:9:1", "Unknown"]]);
  assert.deepEqual(f.candles.map((c) => [c.id, c.phase]), [["c-0002", "telegraph"], ["c-0001", "active"]]);
  assert.equal(f.players[0]!.appliedSeq, 4);
  assert.equal(f.you.canControl, true);
  assert.equal(f.eventsComplete, false, "polling can skip a tick, so the bridge never claims a complete event log");
});

test("bridge input queue: a Burst tap is sent as press THEN release, in order, with increasing sequence; neutral uses the neutral route", async () => {
  const sent: { seq: number; burst: boolean; mx: number }[] = [];
  const calls: string[] = [];
  let release: (() => void) | null = null;
  const answer = (accepted = true) => ({ accepted, rejection: null, appliesToTick: 5, nextTick: 5, lastSequence: sent.at(-1)?.seq ?? -1 });
  const wire = {
    async input(_id: string, seq: number, _t: number, i: ControlIntent) {
      sent.push({ seq, burst: i.requestBurst, mx: i.moveX });
      calls.push("input");
      await new Promise<void>((r) => { release = r; }); // the request stays in flight until the test releases it
      return answer();
    },
    async neutral() { calls.push("neutral"); return answer(); },
  } as unknown as GreyboxWire;
  const bridge = new TestHostBridge({ wire, now: () => 0 });
  (bridge as unknown as { sessionId: string }).sessionId = "S";
  const flush = () => new Promise<void>((r) => setTimeout(r, 0));
  bridge.setIntent({ moveX: 1, moveY: 0, requestBurst: false }, "change"); // in flight
  bridge.setIntent({ moveX: 1, moveY: 0, requestBurst: true }, "change"); // press while the first is still out...
  bridge.setIntent({ moveX: 1, moveY: 0, requestBurst: false }, "change"); // ...and released again before it returns
  await flush();
  for (let i = 0; i < 4 && release; i++) { const r: () => void = release; release = null; r(); await flush(); await flush(); }
  assert.deepEqual(sent.map((s) => s.burst), [false, true, false], "the press was not swallowed by the release");
  assert.deepEqual(sent.map((s) => s.seq), [0, 1, 2]);
  bridge.setIntent({ moveX: 0, moveY: 0, requestBurst: false }, "neutral");
  await flush();
  assert.equal(calls.at(-1), "neutral");
  bridge.dispose();
});

test("bridge: a Burst press refused as CommitPending is re-sent (bounded), and the session says so instead of 'refused'", async () => {
  const sent: { seq: number; burst: boolean }[] = [];
  const views: InputAnswerView[] = [];
  const wire = {
    async input(_id: string, seq: number, _t: number, i: ControlIntent) {
      sent.push({ seq, burst: i.requestBurst });
      const refuse = i.requestBurst && sent.filter((s) => s.burst).length === 1; // only the first press is refused
      return { accepted: !refuse, rejection: refuse ? "commit_pending" : null, appliesToTick: 5, nextTick: 5, lastSequence: seq };
    },
  } as unknown as GreyboxWire;
  const bridge = new TestHostBridge({ wire, now: () => 0 });
  (bridge as unknown as { sessionId: string }).sessionId = "S";
  bridge.onInputAnswer((a) => views.push(a));
  const flush = () => new Promise<void>((r) => setTimeout(r, 0));
  bridge.setIntent({ moveX: 0, moveY: 0, requestBurst: true }, "change");
  for (let i = 0; i < 6; i++) await flush();
  assert.deepEqual(sent.map((s) => s.burst), [true, true], "the refused press was sent again");
  assert.deepEqual(views.map((v) => [v.accepted, v.willRetry]), [[false, true], [true, false]]);
  bridge.dispose();
});

test("same-tick control loss and new retained events are accepted; stale snapshot sequence cannot restore control", () => {
  const transport = new FakeTransport(); const session = new PlaySession(transport);
  transport.push(frame(10, 100, { snapshotSequence: 5 }));
  session.intent({ moveX: 1, moveY: 0, requestBurst: false }, "change", 101);
  transport.push(frame(10, 105, { snapshotSequence: 5, you: { slot: 0, canControl: false }, events: [event("retained", "ContactDrain")] }));
  assert.equal(session.frame?.you.canControl, false);
  assert.equal(session.held.moveX, 0);
  assert.equal(session.drainCues().length, 1);
  transport.push(frame(10, 110, { snapshotSequence: 4 }));
  assert.equal(session.frame?.you.canControl, false);
  session.dispose();
});
