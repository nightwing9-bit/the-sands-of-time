// Validates: Requirements R04.04, R04.08 against a running Hosts.Api. Skipped unless CONTRACTS_LIVE_API_ORIGIN is set, e.g.
//   CONTRACTS_LIVE_API_ORIGIN=http://127.0.0.1:5199 npm test -w @sands/contracts
// Needs no database for these calls: the handshake and the unauthenticated denial never reach PostgreSQL.
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { describeHandshake } from '../src/handshake.ts';
import { viewStateOf } from '../src/failure.ts';
import { KNOWN_TIMELINE_ERROR_CODES } from '../src/timeline-errors.ts';
import { createMarrowTransport } from '../src/transport.ts';

const origin = process.env.CONTRACTS_LIVE_API_ORIGIN;
const skip = origin ? false : 'CONTRACTS_LIVE_API_ORIGIN is not set (no running Hosts.Api)';

test('live: the host handshake is compatible and lists exactly the codes this client handles', { skip }, async () => {
  const transport = createMarrowTransport({ apiBaseUrl: origin!, realtimeBaseUrl: origin! });
  const negotiated = await transport.negotiate();
  assert.ok(negotiated.ok, JSON.stringify(negotiated));
  assert.ok(negotiated.value.compatible, describeHandshake(negotiated));
  assert.deepEqual([...negotiated.value.remote.timelineErrorCodes].sort(), [...KNOWN_TIMELINE_ERROR_CODES].sort());
});

test('live: an unauthenticated call is a typed permission denial', { skip }, async () => {
  const transport = createMarrowTransport({ apiBaseUrl: origin!, realtimeBaseUrl: origin! });
  assert.ok((await transport.negotiate()).ok);
  const result = await transport.api.get('/auth/staff/me');
  assert.ok(!result.ok);
  assert.equal(result.failure.kind, 'denied');
  assert.equal(result.failure.kind === 'denied' && result.failure.reason, 'unauthenticated');
  assert.equal(result.failure.kind === 'denied' && result.failure.code, 'session_inactive');
  assert.equal(viewStateOf(result), 'denied');
});

test('live: an unreachable host is offline, not an exception', { skip }, async () => {
  const transport = createMarrowTransport({ apiBaseUrl: 'http://127.0.0.1:9', realtimeBaseUrl: '', timeoutMs: 2000 });
  const negotiated = await transport.negotiate();
  assert.ok(!negotiated.ok && negotiated.failure.kind === 'offline');
});
