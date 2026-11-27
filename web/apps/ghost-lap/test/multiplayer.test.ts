import assert from "node:assert/strict";
import { test } from "node:test";
import { multiplayerFrame } from "../src/play/MultiplayerTransport.ts";
import type { SnapshotFrame } from "../src/play/multiplayerWire.ts";

test("production mapping uses fixed positions, real event keys, recipient slot, visible candles and applied floor", () => {
  const wire: SnapshotFrame = {
    snapshot: { matchId: "match-a", generationId: "generation-a", rulesId: "ghost-mp-v2", layoutId: "arena-v1", tick: 42, sequence: 21, isFinal: false,
      activeCandleIds: ["c-0010"], players: [{ slot: 3, status: "active", connected: true, xFixed: 200 * 1024, yFixed: 450 * 1024,
        moveX: 1, moveY: -1, charge: 4, overlitDeadlineTick: 240, pickups: 4, bursts: 0, burstHits: 0,
        movementPoints: 10, pickupPoints: 40, penaltyPoints: 5, totalScore: 45, lastSequence: 17 }] },
    lifecycleVersion: 1, matchStatus: "running", isResync: false,
    you: { role: "competitor", slot: 3, controllerEpoch: 2, canControl: true, appliedSeq: 17 },
    events: { truncated: false, items: [{ key: 42 * 1024 + 2, tick: 42, index: 2, kind: "burst_manual", slot: 3, otherSlot: -1, value: 0, candleId: null }] },
    visibleCandles: [{ id: "c-0010", x: 700, y: 600, phase: "active" }],
  };
  const f = multiplayerFrame(wire, 100, [{ slot: 3, memberId: "sealed-member" }]);
  assert.equal(f.players[0]!.memberId, "sealed-member");
  assert.deepEqual([f.players[0]!.x, f.players[0]!.y], [200, 450]);
  assert.equal(f.you.slot, 3); assert.equal(f.players[0]!.appliedSeq, 17);
  assert.equal(f.snapshotSequence, 21); assert.equal(f.players[0]!.overlit, true);
  assert.equal(f.events[0]!.kind, "BurstManual"); assert.equal(f.events[0]!.key, `match-a:generation-a:${42 * 1024 + 2}`);
  assert.deepEqual(f.candles, wire.visibleCandles);
  const spectator = multiplayerFrame({ ...wire, you: { ...wire.you, slot: -1, canControl: false } });
  assert.equal(spectator.you.slot, -1); assert.equal(spectator.you.canControl, false);
  assert.notEqual(multiplayerFrame({ ...wire, snapshot: { ...wire.snapshot, generationId: "generation-b" } }).events[0]!.key, f.events[0]!.key);
});
