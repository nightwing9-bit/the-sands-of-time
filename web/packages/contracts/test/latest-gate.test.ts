// Validates: Requirements R04.04 (stale and cancelled states); supports R04.02 (only the newest answer may be shown)
import assert from 'node:assert/strict';
import { test } from 'node:test';
import type { ApiResult } from '../src/failure.ts';
import { LatestRequestGate } from '../src/latest-gate.ts';

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((r) => (resolve = r));
  return { promise, resolve };
}

const ok = (value: string): ApiResult<string> => ({ ok: true, value, status: 200 });

test('a slow earlier answer is stale and never overwrites the newest one', async () => {
  const gate = new LatestRequestGate();
  const slow = deferred<ApiResult<string>>();
  const first = gate.run((signal) => {
    signal.addEventListener('abort', () => slow.resolve({ ok: false, failure: { kind: 'cancelled', by: 'caller' } }));
    return slow.promise;
  });
  const second = await gate.run(async () => ok('new'));
  assert.deepEqual(second, ok('new'));
  assert.deepEqual(await first, { ok: false, failure: { kind: 'stale' } });
});

test('a superseded request is stale even if it ignores the abort signal and succeeds late', async () => {
  const gate = new LatestRequestGate();
  const slow = deferred<ApiResult<string>>();
  const first = gate.run(() => slow.promise); // never looks at the signal
  const second = await gate.run(async () => ok('new'));
  slow.resolve(ok('old'));
  assert.deepEqual(await first, { ok: false, failure: { kind: 'stale' } });
  assert.deepEqual(second, ok('new'));
});

test('cancel aborts the in-flight call and reports cancelled even when the call returns data anyway', async () => {
  const gate = new LatestRequestGate();
  const slow = deferred<ApiResult<string>>();
  let aborted = false;
  const pending = gate.run((signal) => {
    signal.addEventListener('abort', () => (aborted = true));
    return slow.promise;
  });
  assert.equal(gate.busy, true);
  gate.cancel();
  assert.equal(aborted, true);
  slow.resolve(ok('late'));
  assert.deepEqual(await pending, { ok: false, failure: { kind: 'cancelled', by: 'caller' } });
  assert.equal(gate.busy, false);
  assert.deepEqual(await gate.run(async () => ok('after')), ok('after'), 'the gate is reusable after a cancel');
});

test('only the newest of many rapid requests delivers', async () => {
  const gate = new LatestRequestGate();
  const gates = Array.from({ length: 6 }, () => deferred<ApiResult<string>>());
  const runs = gates.map((g) => gate.run(() => g.promise));
  // answers arrive in reverse order
  for (let i = gates.length - 1; i >= 0; i--) gates[i]!.resolve(ok(`r${i}`));
  const results = await Promise.all(runs);
  assert.deepEqual(results.filter((r) => r.ok), [ok('r5')]);
  assert.equal(results.filter((r) => !r.ok && r.failure.kind === 'stale').length, 5);
});

test('an exception inside an operation propagates and does not wedge the gate', async () => {
  const gate = new LatestRequestGate();
  await assert.rejects(
    gate.run(async () => {
      throw new Error('bug');
    }),
    /bug/,
  );
  assert.equal(gate.busy, false);
  assert.deepEqual(await gate.run(async () => ok('fine')), ok('fine'));
});
