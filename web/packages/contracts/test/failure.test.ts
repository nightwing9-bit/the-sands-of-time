// Validates: Requirements R04.04 (explicit stale/cancelled/denied/offline states)
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { classifyHttpFailure, classifyThrown, describeFailure, viewStateOf, type ApiResult, type TransportFailure } from '../src/failure.ts';

const problem = (code: string, status: number) => ({ code, status, traceId: 'abc123' });

test('401 and 403 are permission states with a reason, never a usable result', () => {
  const cases: Array<[number, string, string]> = [
    [401, 'session_inactive', 'unauthenticated'],
    [401, 'invalid_credentials', 'unauthenticated'],
    [401, 'invalid_refresh_token', 'unauthenticated'],
    [403, 'mfa_required', 'mfa_required'],
    [403, 'csrf_rejected', 'csrf'],
    [403, 'origin_rejected', 'csrf'],
    [403, 'guest_banned', 'banned'],
    [403, 'invalid_mfa_code', 'forbidden'],
    [403, 'something_unrecognised', 'forbidden'],
  ];
  for (const [status, code, reason] of cases) {
    const failure = classifyHttpFailure(status, problem(code, status));
    assert.deepEqual(failure, { kind: 'denied', reason, status, code, traceId: 'abc123' }, `${status} ${code}`);
    assert.equal(viewStateOf({ ok: false, failure }), 'denied');
    assert.ok(describeFailure(failure).length > 0);
  }
});

test('a denial without a readable body is still a denial', () => {
  assert.deepEqual(classifyHttpFailure(403, undefined), { kind: 'denied', reason: 'forbidden', status: 403 });
  assert.deepEqual(classifyHttpFailure(401, 'not json object'), { kind: 'denied', reason: 'unauthenticated', status: 401 });
  assert.deepEqual(classifyHttpFailure(403, [1, 2]), { kind: 'denied', reason: 'forbidden', status: 403 });
});

test('other statuses become server, rate-limited or not-found style failures', () => {
  assert.deepEqual(classifyHttpFailure(500, problem('error', 500)), { kind: 'server', status: 500, code: 'error', traceId: 'abc123' });
  assert.deepEqual(classifyHttpFailure(404, problem('not_found', 404)), { kind: 'server', status: 404, code: 'not_found', traceId: 'abc123' });
  const headers = { get: (name: string) => (name === 'retry-after' ? '7' : null) };
  assert.deepEqual(classifyHttpFailure(429, undefined, headers), { kind: 'rate_limited', status: 429, retryAfterMs: 7000 });
  assert.deepEqual(classifyHttpFailure(429, undefined, { get: () => 'Wed, 21 Oct 2026 07:28:00 GMT' }), { kind: 'rate_limited', status: 429 });
});

test('oversized or hostile problem fields are ignored, not echoed', () => {
  const failure = classifyHttpFailure(500, { code: 'x'.repeat(500), traceId: 'y'.repeat(500), timelineError: null });
  assert.deepEqual(failure, { kind: 'server', status: 500 });
});

test('thrown errors split into cancelled, timeout and offline', () => {
  const controller = new AbortController();
  controller.abort();
  assert.deepEqual(classifyThrown(new DOMException('aborted', 'AbortError'), controller.signal), { kind: 'cancelled', by: 'caller' });
  assert.deepEqual(classifyThrown(new DOMException('aborted', 'AbortError'), undefined, () => true), { kind: 'offline', cause: 'timeout' });
  assert.deepEqual(classifyThrown(new TypeError('fetch failed')), { kind: 'offline', cause: 'network' });
  assert.deepEqual(classifyThrown('weird'), { kind: 'offline', cause: 'network' });
});

test('every failure kind has a view state and readable text', () => {
  const failures: TransportFailure[] = [
    { kind: 'cancelled', by: 'caller' },
    { kind: 'cancelled', by: 'server' },
    { kind: 'stale' },
    { kind: 'offline', cause: 'network' },
    { kind: 'offline', cause: 'timeout' },
    { kind: 'offline', cause: 'disconnected' },
    { kind: 'denied', reason: 'unauthenticated', status: 401 },
    { kind: 'incompatible', reason: 'api_major', message: 'Update.' },
    { kind: 'rate_limited', status: 429 },
    { kind: 'server', status: 500 },
    { kind: 'malformed', message: 'bad' },
  ];
  const expectedState: Record<TransportFailure['kind'], string> = {
    cancelled: 'cancelled',
    stale: 'loading',
    offline: 'offline',
    denied: 'denied',
    timeline: 'error',
    incompatible: 'incompatible',
    rate_limited: 'error',
    server: 'error',
    malformed: 'error',
  };
  for (const failure of failures) {
    assert.equal(viewStateOf({ ok: false, failure }), expectedState[failure.kind], failure.kind);
    assert.ok(describeFailure(failure).length > 5, failure.kind);
  }
  const ok: ApiResult<number> = { ok: true, value: 1, status: 200 };
  assert.equal(viewStateOf(ok), 'ready');
  assert.equal(viewStateOf(null), 'loading');
});

test('failure text never includes server-provided codes, only the trace id', () => {
  const text = describeFailure({ kind: 'server', status: 500, code: 'secret_internal_code', traceId: 'trace-9' });
  assert.ok(text.includes('trace-9'));
  assert.ok(!text.includes('secret_internal_code'));
});
