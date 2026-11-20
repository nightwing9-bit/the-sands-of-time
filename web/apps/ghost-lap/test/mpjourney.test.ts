// Validates: Spec 13 task 13.8 (multiplayer journey). Pure view-model rules, the session reset a rematch relies on, and the transport's
// handling of a second match in the same room. No Pixi/DOM and no network: the transport test drives its real frame handler with wire-shaped
// frames and a stubbed room read, which is the only seam a unit test can honestly use. Nothing here re-implements a game rule.
import assert from "node:assert/strict";
import { test } from "node:test";
import {
  countdownSecondsLeft, formatRoomCode, lobbyCards, matchHasEnded, matchIsSettled, noticeText, normalizeCodeInput, ordinal, resultModel, startAdvice,
} from "../src/mp/model.ts";
import { failureText, HubCallError, type MatchOutcomeWire, type MatchPlayerResultWire, type RoomView, type SnapshotFrame } from "../src/play/multiplayerWire.ts";
import { MultiplayerTransport } from "../src/play/MultiplayerTransport.ts";
import { PlaySession } from "../src/play/PlaySession.ts";
import type { ConnectionState, InputAnswerView, PlayFrame, PlayTransport } from "../src/play/playTypes.ts";
import type { ControlIntent } from "../src/presentation/types.ts";

const member = (id: string, over: Partial<RoomView["members"][number]> = {}): RoomView["members"][number] => ({ memberId: id, role: "competitor", ready: false, connected: true, isHost: false, slot: null, ...over });
const room = (over: Partial<RoomView> = {}): RoomView => ({ roomId: "r1", version: 1, phase: "lobby", matchStatus: null, maxCompetitors: 4, countdownRemainingMs: 0, notice: null, members: [], round: null, ...over });
const row = (slot: number, placement: number, score: number, over: Partial<MatchPlayerResultWire> = {}): MatchPlayerResultWire => ({
  slot, memberId: `m${slot}`, outcome: "finisher", forfeitTick: 0, competitivePlacement: placement, totalScore: score, movementPoints: score, pickupPoints: 0, penaltyPoints: 0,
  pickups: 0, bursts: 0, burstHits: 0, timesHitByBurst: 0, contactDrainsSuffered: 0, ...over,
});
const outcome = (decision: string, players: MatchPlayerResultWire[], abortReason: string | null = null): MatchOutcomeWire => ({
  matchId: "a", decision, abortReason, resultDigest: null, ratedEligible: false, eligibilityReason: null, ratingAlgorithmId: null, players,
});

test("room codes: input keeps only what the server cannot ignore; display groups in fours", () => {
  assert.equal(normalizeCodeInput(" ab-cd ef_gh "), "ABCDEFGH");
  assert.equal(formatRoomCode("abcdefgh"), "ABCD-EFGH");
  assert.equal(formatRoomCode("abc"), "ABC");
});

test("lobby: before the countdown no member has a slot (join order only); once sealed the server's slots decide the cards", () => {
  const members = [member("a", { isHost: true }), member("b"), member("w", { role: "spectator" })];
  const before = lobbyCards(room({ members }), "b");
  assert.deepEqual(before.map((c) => [c.member?.memberId ?? null, c.slot]), [["a", null], ["b", null], [null, null], [null, null]]);
  assert.equal(before[1]!.isYou, true);
  // The sealed order is NOT the join order: b holds slot 0, a holds slot 2.
  const sealed = lobbyCards(room({ phase: "countdown", members, round: { matchId: "m", authorityGeneration: "g", layoutId: "arena-v1", layoutFingerprint: "f", targetTicks: 2700, slots: [{ slot: 0, memberId: "b" }, { slot: 2, memberId: "a" }] } }), "b");
  assert.deepEqual(sealed.map((c) => [c.member?.memberId ?? null, c.slot]), [["b", 0], [null, null], ["a", 2], [null, null]]);
});

test("start advice names the real blocker and never allows fewer than two ready, connected competitors", () => {
  assert.equal(startAdvice(room({ members: [member("a", { ready: true })] })).allowed, false);
  assert.match(startAdvice(room({ members: [member("a", { ready: true }), member("b")] })).reason, /ready/i);
  assert.match(startAdvice(room({ members: [member("a", { ready: true }), member("b", { ready: true, connected: false })] })).reason, /disconnected/i);
  assert.equal(startAdvice(room({ members: [member("a", { ready: true }), member("b", { ready: true }), member("w", { role: "spectator" })] })).allowed, true);
  assert.equal(startAdvice(room({ phase: "countdown", members: [member("a", { ready: true }), member("b", { ready: true })] })).allowed, false);
});

test("countdown counts down from the server's own reading, never below zero, and only while counting", () => {
  const v = room({ phase: "countdown", countdownRemainingMs: 3000 });
  assert.equal(countdownSecondsLeft(v, 1000, 1000), 3);
  assert.equal(countdownSecondsLeft(v, 1000, 2100), 2);
  assert.equal(countdownSecondsLeft(v, 1000, 9000), 0);
  assert.equal(countdownSecondsLeft(room({ phase: "lobby" }), 0, 0), null);
  assert.equal(noticeText("countdown_cancelled_unready")?.includes("no longer ready"), true);
  assert.equal(noticeText("start_admitted"), null);
});

test("match end and settlement follow the server's status, not the frame alone", () => {
  assert.equal(matchHasEnded("running", false), false);
  assert.equal(matchHasEnded("running", true), true);
  assert.equal(matchHasEnded("pending_finalization", false), true);
  assert.equal(matchHasEnded(null, false), false);
  assert.equal(matchIsSettled("pending_finalization"), false);
  assert.equal(matchIsSettled("aborted"), true);
  assert.equal(matchIsSettled("unverified"), true);
});

test("results: a single finisher wins, the viewer is told Victory only when it is them, placements read as ordinals", () => {
  const o = outcome("finalized", [row(1, 2, 80), row(0, 1, 120), row(2, 3, 10)]);
  const asWinner = resultModel(o, "m0");
  assert.equal(asWinner.status, "finalized");
  assert.equal(asWinner.headline, "Victory");
  assert.deepEqual(asWinner.winners, [0]);
  assert.deepEqual(asWinner.rows.map((r) => r.slot), [0, 1, 2]);
  assert.equal(resultModel(o, "m1").headline, "P1 wins");
  assert.equal(resultModel(o, "spectator").headline, "P1 wins");
  assert.deepEqual([1, 2, 3, 4, 11, 12, 13, 21, 22].map(ordinal), ["1st", "2nd", "3rd", "4th", "11th", "12th", "13th", "21st", "22nd"]);
});

test("results: an authentic tie shares first place and the next placement skips (1, 1, 3); both tied slots are winners", () => {
  const m = resultModel(outcome("finalized", [row(0, 1, 90), row(2, 1, 90), row(1, 3, 40)]), "m1");
  assert.equal(m.headline, "A tie for first");
  assert.deepEqual(m.winners, [0, 2]);
  assert.deepEqual(m.rows.map((r) => [r.slot, r.tied]), [[0, true], [2, true], [1, false]]);
  assert.match(m.sub ?? "", /P1 and P3 share first place on 90/);
});

test("results: a forfeit ranks below finishers, is labelled, and a forfeit alone never produces a winner", () => {
  const m = resultModel(outcome("finalized", [row(0, 1, 70), row(1, 2, 200, { outcome: "forfeit", forfeitTick: 900 })]), "m0");
  assert.deepEqual(m.winners, [0]);
  assert.equal(m.rows[1]!.forfeited, true);
  assert.match(m.sub ?? "", /P2 forfeited/);
  const none = resultModel(outcome("finalized", [row(0, 1, 5, { outcome: "forfeit" }), row(1, 1, 5, { outcome: "forfeit" })]), "m0");
  assert.equal(none.headline, "No one finished");
  assert.deepEqual(none.winners, []);
});

test("results: pending and aborted name no winner, no placement and no rows", () => {
  const pending = resultModel(outcome("pending", []), "m0");
  const aborted = resultModel(outcome("aborted", [], "authority_lost"), "m0");
  for (const m of [pending, aborted]) { assert.deepEqual(m.winners, []); assert.deepEqual(m.rows, []); }
  assert.equal(pending.status, "pending");
  assert.equal(aborted.headline, "Match aborted");
  assert.match(aborted.sub ?? "", /authority_lost/);
});

test("server failure codes become plain sentences; an unknown code is shown, not hidden", () => {
  assert.match(failureText(new HubCallError("JoinRoom", "room_full")), /four players/);
  assert.match(failureText(new HubCallError("Rematch", "finalization_pending")), /still recording/);
  assert.match(failureText(new HubCallError("X", "brand_new_code")), /brand_new_code/);
  assert.equal(failureText("Arena layout mismatch; refusing play"), "Arena layout mismatch; refusing play");
});

// ---- the second match of a room ---------------------------------------------------------------------------------------------------
const wire = (matchId: string, generationId: string, tick: number, sequence: number, over: Partial<SnapshotFrame> = {}): SnapshotFrame => ({
  snapshot: { matchId, generationId, rulesId: "ghost-mp-v2", layoutId: "arena-v1", tick, sequence, isFinal: false, activeCandleIds: [], players: [
    { slot: 0, status: "active", connected: true, xFixed: 200 * 1024, yFixed: 450 * 1024, moveX: 0, moveY: 0, charge: 0, overlitDeadlineTick: 0, pickups: 0, bursts: 0, burstHits: 0, movementPoints: 0, pickupPoints: 0, penaltyPoints: 0, totalScore: 0, lastSequence: -1 },
    { slot: 1, status: "grace", connected: false, graceEndsTick: tick + 300, xFixed: 1400 * 1024, yFixed: 450 * 1024, moveX: 0, moveY: 0, charge: 0, overlitDeadlineTick: 0, pickups: 0, bursts: 0, burstHits: 0, movementPoints: 0, pickupPoints: 0, penaltyPoints: 0, totalScore: 0, lastSequence: -1 },
  ] },
  lifecycleVersion: 1, matchStatus: "running", isResync: false, you: { role: "competitor", slot: 0, controllerEpoch: 1, canControl: true, appliedSeq: -1 },
  events: { truncated: false, items: [{ key: tick * 1024, tick, index: 0, kind: "candle_collected", slot: 0, otherSlot: -1, value: 10, candleId: "c-0001" }] }, visibleCandles: [], ...over,
});
const roundOf = (matchId: string, generationId: string): RoomView["round"] => ({ matchId, authorityGeneration: generationId, layoutId: "arena-v1", layoutFingerprint: "fp", targetTicks: 2700, slots: [{ slot: 0, memberId: "me" }, { slot: 1, memberId: "you" }] });

test("rematch: a new match in the same room replaces the old one after one room read; a late frame of the old match is dropped, never an error", async () => {
  const t = new MultiplayerTransport({ baseUrl: "http://localhost" });
  const any = t as unknown as { joined: unknown; fingerprint: string; call: (m: string, ...a: unknown[]) => Promise<unknown>; receive: (f: SnapshotFrame) => void };
  const lobby = room({ version: 5, phase: "lobby", matchStatus: null, round: null, members: [member("me"), member("you")] });
  const fresh = room({ version: 6, phase: "start_admitted", matchStatus: "running", round: roundOf("B", "g2"), members: [member("me"), member("you")] });
  any.fingerprint = "fp";
  any.joined = { view: room({ version: 4, phase: "start_admitted", matchStatus: "running", round: roundOf("A", "g1") }), memberId: "me", role: "competitor", controllerEpoch: 1, roomCode: "ABCDEFGH" };
  const got: PlayFrame[] = [];
  const states: ConnectionState[] = [];
  t.onFrame((f) => got.push(f));
  t.onConnection((s) => states.push(s));
  const calls: string[] = [];
  any.call = async (m: string) => { calls.push(m); return m === "Rematch" ? { view: lobby, detail: null } : fresh; };
  any.receive(wire("A", "g1", 10, 5));
  assert.equal(got.length, 1);
  assert.equal(got[0]!.players[1]!.status, "grace"); // presence reaches the HUD
  assert.equal(got[0]!.players[1]!.graceEndsTick, 310);
  assert.equal(got[0]!.players[0]!.graceEndsTick, null);
  await t.rematch(); // the real method: the room returns to its lobby and the transport forgets match A
  any.receive(wire("A", "g1", 11, 6)); // a late frame of the finished match
  assert.equal(got.length, 1);
  assert.equal(states.some((s) => s.kind === "failed"), false);
  any.receive(wire("B", "g2", 1, 1)); // first frame of the new match: the room view is the lobby, so the transport reads it once
  await new Promise((r) => setTimeout(r, 0));
  assert.deepEqual(calls, ["Rematch", "GetRoom"]);
  assert.equal(got.length, 2);
  assert.equal(got[1]!.identity, "B:g2");
  assert.equal(states.some((s) => s.kind === "failed"), false);
});

test("rematch: a frame whose match the room does NOT name is refused as an identity mismatch", async () => {
  const t = new MultiplayerTransport({ baseUrl: "http://localhost" });
  const any = t as unknown as { joined: unknown; fingerprint: string; call: () => Promise<unknown>; receive: (f: SnapshotFrame) => void };
  any.fingerprint = "fp";
  any.joined = { view: room({ version: 1, phase: "start_admitted", round: roundOf("A", "g1") }), memberId: "me", role: "competitor", controllerEpoch: 1, roomCode: "X" };
  const states: ConnectionState[] = [];
  t.onConnection((s) => states.push(s));
  any.call = async () => room({ version: 2, phase: "start_admitted", round: roundOf("C", "g3") });
  any.receive(wire("B", "g2", 1, 1));
  await new Promise((r) => setTimeout(r, 0));
  assert.equal(states.at(-1)?.kind, "failed");
});

class FakeTransport implements PlayTransport {
  readonly label = "fake";
  readonly production = true;
  private f?: (x: PlayFrame) => void;
  onFrame(l: (x: PlayFrame) => void) { this.f = l; return () => undefined; }
  onConnection(_l: (x: ConnectionState) => void) { return () => undefined; }
  onInputAnswer(_l: (x: InputAnswerView) => void) { return () => undefined; }
  setIntent(_i: ControlIntent, _r: "change" | "neutral") {}
  async start() {}
  dispose() {}
  push(x: PlayFrame) { this.f?.(x); }
}

test("session.clear (rematch to lobby): no frame, no remembered event keys, so the next match's first event is fresh", () => {
  const tr = new FakeTransport();
  const s = new PlaySession(tr);
  const mk = (identity: string, key: string): PlayFrame => ({
    identity, tick: 5, tickRate: 30, roundTicks: 2700, final: false, receivedAtMs: 100, candles: [], eventsComplete: true, you: { slot: 0, canControl: true },
    players: [{ slot: 0, x: 1, y: 1, charge: 0, overlit: false, overlitDeadlineTick: null, totalScore: 7, movementPoints: 0, pickupPoints: 0, penaltyPoints: 0, pickups: 0, bursts: 0, appliedSeq: -1 }],
    events: [{ key, tick: 5, kind: "CandleCollected", raw: "candle_collected", slot: 0, otherSlot: -1, value: 10, candleId: "c" }],
  });
  const heard: string[] = [];
  s.onEvent((e) => heard.push(e.key));
  tr.push(mk("A:g1", "k1"));
  assert.equal(s.hud(0).feed.length, 1);
  s.clear();
  assert.equal(s.frame, null);
  assert.equal(s.hud(0).identity, null);
  assert.deepEqual(s.hud(0).feed, []);
  tr.push(mk("A:g1", "k1")); // even the same key is accepted again after a clear; a new match carries new keys anyway
  tr.push(mk("B:g2", "k2"));
  assert.deepEqual(heard, ["k1", "k1", "k2"]);
  assert.equal(s.hud(0).identity, "B:g2");
});
