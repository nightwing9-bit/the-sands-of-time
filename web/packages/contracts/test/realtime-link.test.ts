// Validates: Requirements R04.04 (offline/denied/recovering states; recover after snapshot fetch)
import assert from 'node:assert/strict';
import { test } from 'node:test';
import type { RealtimeReply } from '../src/realtime.ts';
import { RealtimeLink, type RealtimeLinkState, type SupervisedHubConnection } from '../src/realtime-link.ts';

const STREAM = 'probe:a';

const snapshot = (sequence: number, streamId = STREAM): RealtimeReply => ({
  ok: true,
  code: null,
  snapshot: { schemaVersion: 1, streamId, sequence: String(sequence), payload: { value: sequence } },
});
const refusal = (code: 'forbidden' | 'unavailable'): RealtimeReply => ({ ok: false, code, snapshot: null });

class ScriptedHub implements SupervisedHubConnection {
  handlers = new Map<string, (...args: unknown[]) => void>();
  reconnected: Array<() => void> = [];
  reconnecting: Array<() => void> = [];
  closed: Array<() => void> = [];
  replies: Array<RealtimeReply | Error> = [];
  calls: string[] = [];
  startFailures = 0;
  starts = 0;
  stops = 0;
  async start(): Promise<void> {
    this.starts += 1;
    if (this.startFailures > 0) {
      this.startFailures -= 1;
      throw new Error('cannot connect');
    }
  }
  async stop(): Promise<void> {
    this.stops += 1;
  }
  async invoke<T>(method: string, ...args: unknown[]): Promise<T> {
    this.calls.push(`${method}:${String(args[0])}`);
    const next = this.replies.shift();
    if (next instanceof Error) throw next;
    return next as T;
  }
  on(method: string, handler: (...args: unknown[]) => void): void {
    this.handlers.set(method, handler);
  }
  off(method: string): void {
    this.handlers.delete(method);
  }
  onreconnected(callback: () => void): void {
    this.reconnected.push(callback);
  }
  onreconnecting(callback: () => void): void {
    this.reconnecting.push(callback);
  }
  onclose(callback: () => void): void {
    this.closed.push(callback);
  }
  drop(): void {
    this.reconnecting.forEach((cb) => cb());
  }
  recover(): void {
    this.reconnected.forEach((cb) => cb());
  }
  giveUp(): void {
    this.closed.forEach((cb) => cb());
  }
}

/** Manual timers so backoff is observable and instant. */
function manualTimers() {
  const scheduled: Array<{ id: number; delay: number; run: () => void }> = [];
  let next = 1;
  return {
    scheduled,
    options: {
      setTimer: (run: () => void, delay: number) => {
        const id = next++;
        scheduled.push({ id, delay, run });
        return id;
      },
      clearTimer: (id: unknown) => {
        const i = scheduled.findIndex((t) => t.id === id);
        if (i >= 0) scheduled.splice(i, 1);
      },
    },
    fireNext() {
      const timer = scheduled.shift();
      assert.ok(timer, 'a timer was expected');
      timer.run();
    },
  };
}

const settle = () => new Promise((resolve) => setImmediate(resolve));

function track(link: RealtimeLink): RealtimeLinkState[] {
  const states: RealtimeLinkState[] = [link.state];
  link.onStateChange((s) => states.push(s));
  return states;
}

test('online only after the snapshot is applied; a dropped connection is stale until a new snapshot arrives', async () => {
  const hub = new ScriptedHub();
  const link = new RealtimeLink(hub, manualTimers().options);
  const states = track(link);
  await link.start();
  assert.equal(link.state, 'online', 'no streams yet: connected is online');

  let value = -1;
  hub.replies.push(snapshot(2));
  await link.subscribe<{ value: number }, { value: number }>(STREAM, {
    onSnapshot: (p) => (value = p.value),
    onEvent: (e) => (value = e.payload.value),
  });
  assert.equal(link.state, 'online');
  assert.equal(value, 2);

  hub.drop();
  assert.equal(link.state, 'reconnecting', 'shown as reconnecting, not online');

  hub.replies.push(snapshot(9)); // events 3..9 were missed while away
  hub.recover();
  assert.equal(link.state, 'syncing', 'connected again but not trusted until the snapshot lands');
  await settle();
  assert.equal(value, 9, 'state replaced by the authoritative snapshot');
  assert.equal(link.state, 'online');
  assert.deepEqual(states, ['connecting', 'syncing', 'online', 'syncing', 'online', 'reconnecting', 'syncing', 'online']);
});

test('when reconnect gives up the link is offline, restarts with growing delays and resyncs', async () => {
  const hub = new ScriptedHub();
  const timers = manualTimers();
  const link = new RealtimeLink(hub, { ...timers.options, restartDelaysMs: [100, 200, 400] });
  await link.start();
  hub.replies.push(snapshot(1));
  let value = -1;
  await link.subscribe<{ value: number }, { value: number }>(STREAM, { onSnapshot: (p) => (value = p.value), onEvent: () => {} });

  hub.giveUp();
  assert.equal(link.state, 'offline');
  assert.deepEqual(timers.scheduled.map((t) => t.delay), [100]);

  hub.startFailures = 1; // first restart attempt fails
  timers.fireNext();
  await settle();
  assert.equal(link.state, 'offline');
  assert.deepEqual(timers.scheduled.map((t) => t.delay), [200], 'backoff grew');

  hub.replies.push(snapshot(7));
  timers.fireNext();
  await settle();
  assert.equal(link.state, 'online');
  assert.equal(value, 7, 'resynced from a snapshot after the manual restart');
  assert.equal(timers.scheduled.length, 0);
  assert.equal(hub.starts, 3);
});

test('a refused stream is reported as denied and nothing is delivered for it', async () => {
  const hub = new ScriptedHub();
  const link = new RealtimeLink(hub, manualTimers().options);
  await link.start();
  hub.replies.push(refusal('forbidden'));
  const failures: string[] = [];
  let snapshots = 0;
  const reply = await link.subscribe(STREAM, { onSnapshot: () => (snapshots += 1), onEvent: () => {}, onFailure: (c) => failures.push(c) });
  assert.equal(reply.ok, false);
  assert.deepEqual(failures, ['forbidden']);
  assert.equal(snapshots, 0);
  assert.equal(link.state, 'denied');
  // a later event for the refused stream is ignored
  hub.handlers.get('Receive')!({ schemaVersion: 1, streamId: STREAM, sequence: '3', eventId: 'e', serverUtc: '2026-11-10T12:00:00Z', kind: 'probe.advanced', payload: {} });
  assert.equal(snapshots, 0);
});

test('one refused stream among live ones does not mark the whole link denied', async () => {
  const hub = new ScriptedHub();
  const link = new RealtimeLink(hub, manualTimers().options);
  await link.start();
  hub.replies.push(snapshot(1), refusal('forbidden'));
  await link.subscribe(STREAM, { onSnapshot: () => {}, onEvent: () => {} });
  await link.subscribe('probe:b', { onSnapshot: () => {}, onEvent: () => {}, onFailure: () => {} });
  assert.equal(link.state, 'online');
});

test('a failed resubscribe after reconnect is a visible failure, not an unhandled rejection, and is retried', async () => {
  const unhandled: unknown[] = [];
  const onUnhandled = (reason: unknown) => unhandled.push(reason);
  process.on('unhandledRejection', onUnhandled);
  try {
    const hub = new ScriptedHub();
    const timers = manualTimers();
    const link = new RealtimeLink(hub, { ...timers.options, restartDelaysMs: [50] });
    await link.start();
    hub.replies.push(snapshot(1));
    const failures: string[] = [];
    let value = -1;
    await link.subscribe<{ value: number }, { value: number }>(STREAM, {
      onSnapshot: (p) => (value = p.value),
      onEvent: () => {},
      onFailure: (c) => failures.push(c),
    });

    hub.drop();
    hub.replies.push(new Error('connection dropped again'));
    hub.recover();
    await settle();
    assert.deepEqual(failures, ['unavailable']);
    assert.equal(link.state, 'syncing', 'still not trusted');
    assert.equal(timers.scheduled.length, 1, 'a resync is scheduled');

    hub.replies.push(snapshot(4));
    timers.fireNext();
    await settle();
    assert.equal(value, 4);
    assert.equal(link.state, 'online');
    await settle();
    assert.deepEqual(unhandled, []);
  } finally {
    process.off('unhandledRejection', onUnhandled);
  }
});

test('a gap on a live stream triggers one snapshot fetch and the link stays online', async () => {
  const hub = new ScriptedHub();
  const link = new RealtimeLink(hub, manualTimers().options);
  await link.start();
  hub.replies.push(snapshot(2));
  const events: number[] = [];
  let value = -1;
  await link.subscribe<{ value: number }, { value: number }>(STREAM, { onSnapshot: (p) => (value = p.value), onEvent: (e) => events.push(e.payload.value) });
  const push = (sequence: number) =>
    hub.handlers.get('Receive')!({ schemaVersion: 1, streamId: STREAM, sequence: String(sequence), eventId: `e${sequence}`, serverUtc: '2026-11-10T12:00:00Z', kind: 'probe.advanced', payload: { value: sequence } });
  push(3);
  hub.replies.push(snapshot(5));
  push(5); // 4 lost
  await settle();
  assert.deepEqual(events, [3]);
  assert.equal(value, 5);
  assert.deepEqual(hub.calls, [`Subscribe:${STREAM}`, `GetSnapshot:${STREAM}`]);
  assert.equal(link.state, 'online');
});

test('dispose stops the connection, cancels timers and silences listeners', async () => {
  const hub = new ScriptedHub();
  const timers = manualTimers();
  const link = new RealtimeLink(hub, timers.options);
  const states = track(link);
  await link.start();
  hub.giveUp();
  assert.equal(timers.scheduled.length, 1);
  await link.dispose();
  assert.equal(timers.scheduled.length, 0);
  assert.equal(hub.stops, 1);
  assert.equal(link.state, 'closed');
  hub.giveUp(); // the stop's own close event must not restart anything
  assert.equal(timers.scheduled.length, 0);
  assert.equal(states.at(-1), 'offline', 'listeners were detached before the closed state');
});
