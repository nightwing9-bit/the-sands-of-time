// Run: npm test -w @sands/contracts  (Node >= 23.6 strips the types; no build step, no extra dependency.)
import assert from 'node:assert/strict';
import { test } from 'node:test';
import {
  parseRealtimeEnvelope,
  parseSequence,
  RealtimeStreamClient,
  RealtimeStreamCursor,
  type RealtimeEnvelope,
  type RealtimeHubConnection,
  type RealtimeReply,
} from '../src/realtime.ts';

const valid = {
  schemaVersion: 1,
  streamId: 'probe:room-1',
  sequence: '7',
  eventId: '0190f0a0-0000-7000-8000-000000000000',
  serverUtc: '2026-11-10T12:00:00.123+00:00',
  kind: 'probe.advanced',
  payload: { value: 7 },
};

test('parseSequence accepts only canonical decimal strings within int64', () => {
  assert.equal(parseSequence('0'), 0n);
  assert.equal(parseSequence('9223372036854775807'), 9223372036854775807n);
  for (const bad of ['', '01', '-1', '1.0', '1e3', ' 1', '9223372036854775808', 7]) {
    assert.equal(parseSequence(bad), null, String(bad));
  }
});

test('parseRealtimeEnvelope requires exactly the seven fields', () => {
  assert.deepEqual(parseRealtimeEnvelope(valid), valid);
  assert.equal(parseRealtimeEnvelope({ ...valid, messageId: 'x' }), null);
  const { serverUtc: _dropped, ...missing } = valid;
  assert.equal(parseRealtimeEnvelope({ ...missing, serverTime: valid.serverUtc }), null);
  assert.equal(parseRealtimeEnvelope({ ...valid, sequence: 7 }), null);
  assert.equal(parseRealtimeEnvelope({ ...valid, schemaVersion: 2 }), null);
  assert.equal(parseRealtimeEnvelope({ ...valid, streamId: 'Probe:x' }), null);
});

test('cursor buffers before the snapshot, drops duplicates, applies in order and reports gaps', () => {
  const cursor = new RealtimeStreamCursor<number>('probe:a');
  assert.equal(cursor.accept(4n, 4), 'buffered');
  assert.equal(cursor.accept(5n, 5), 'buffered');
  assert.deepEqual(cursor.applySnapshot(3n), { accepted: true, ready: [4, 5], gapRemains: false });
  assert.equal(cursor.accept(5n, 5), 'duplicate');
  assert.equal(cursor.accept(6n, 6), 'apply');
  assert.equal(cursor.accept(8n, 8), 'gap');
  assert.equal(cursor.accept(9n, 9), 'buffered');
  assert.deepEqual(cursor.applySnapshot(8n), { accepted: true, ready: [9], gapRemains: false });
  assert.equal(cursor.applySnapshot(2n).accepted, false);
  assert.equal(cursor.sequence, 9n);
});

test('cursor converges for random drop/duplicate/reorder patterns', () => {
  let seed = 12345;
  const random = () => ((seed = (seed * 1103515245 + 12345) % 2147483648) / 2147483648);
  for (let run = 0; run < 200; run++) {
    const cursor = new RealtimeStreamCursor<bigint>('probe:a', 8);
    const applied: bigint[] = [];
    const take = (outcome: { ready: bigint[] }) => applied.push(...outcome.ready);
    let source = 0n;
    take(cursor.applySnapshot(0n));
    applied.push(0n);
    for (let i = 0; i < 60; i++) {
      source += 1n;
      const r = random();
      const deliveries = r < 0.15 ? [] : r < 0.25 ? [source, source] : [source];
      for (const s of deliveries) {
        const action = cursor.accept(s, s);
        if (action === 'apply') applied.push(s);
        if (action === 'gap' || cursor.awaitingSnapshot) {
          if (random() < 0.5) {
            // the snapshot replaces local state with the source's state at `source`
            take(cursor.applySnapshot(source));
            applied.push(source);
          }
        }
      }
    }
    take(cursor.applySnapshot(source));
    applied.push(source);
    const unique = [...new Set(applied)];
    for (let i = 1; i < unique.length; i++) assert.ok(unique[i]! > unique[i - 1]!, 'applied out of order');
    assert.equal(cursor.sequence, source);
  }
});

/** A scripted stand-in for the hub so the client logic can be exercised without a network. */
class ScriptedConnection implements RealtimeHubConnection {
  handlers = new Map<string, (...args: any[]) => void>();
  reconnected: Array<() => void> = [];
  snapshots: Array<RealtimeReply> = [];
  calls: string[] = [];
  async invoke<T>(method: string, ...args: unknown[]): Promise<T> {
    this.calls.push(`${method}:${String(args[0])}`);
    return this.snapshots.shift() as T;
  }
  on(method: string, handler: (...args: any[]) => void): void {
    this.handlers.set(method, handler);
  }
  off(method: string): void {
    this.handlers.delete(method);
  }
  onreconnected(callback: () => void): void {
    this.reconnected.push(callback);
  }
  push(sequence: number): void {
    this.handlers.get('Receive')!({ ...valid, streamId: 'probe:a', sequence: String(sequence), payload: { value: sequence } });
  }
}

const snapshot = (sequence: number): RealtimeReply => ({
  ok: true,
  code: null,
  snapshot: { schemaVersion: 1, streamId: 'probe:a', sequence: String(sequence), payload: { value: sequence } },
});

test('client refetches a snapshot on a gap and after reconnect', async () => {
  const connection = new ScriptedConnection();
  const client = new RealtimeStreamClient(connection);
  let value = -1;
  connection.snapshots.push(snapshot(2));
  await client.subscribe<{ value: number }, { value: number }>('probe:a', {
    onSnapshot: (p) => (value = p.value),
    onEvent: (e: RealtimeEnvelope<{ value: number }>) => (value = e.payload.value),
  });
  assert.equal(value, 2);
  connection.push(3);
  assert.equal(value, 3);
  connection.snapshots.push(snapshot(5));
  connection.push(5); // 4 was lost
  await new Promise((r) => setImmediate(r));
  assert.equal(value, 5);
  assert.deepEqual(connection.calls, ['Subscribe:probe:a', 'GetSnapshot:probe:a']);
  connection.snapshots.push(snapshot(9));
  connection.reconnected.forEach((cb) => cb());
  await new Promise((r) => setImmediate(r));
  assert.equal(value, 9);
  assert.equal(connection.calls.at(-1), 'Subscribe:probe:a');
});
