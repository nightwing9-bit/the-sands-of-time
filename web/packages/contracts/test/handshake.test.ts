// Validates: Requirements R04.08 (contract freshness; incompatible major versions block operations)
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { GENERATED_API_CONTRACT_VERSION } from '../src/generated/contract-version.ts';
import { describeHandshake, evaluateContractVersions, localContractVersions } from '../src/handshake.ts';
import { KNOWN_TIMELINE_ERROR_CODES } from '../src/timeline-errors.ts';
import { createMarrowTransport } from '../src/transport.ts';

const remote = (overrides: Record<string, unknown> = {}) => ({
  apiContract: GENERATED_API_CONTRACT_VERSION,
  realtimeSchemaVersion: localContractVersions().realtimeSchemaVersion,
  timelineErrorCodes: [...KNOWN_TIMELINE_ERROR_CODES],
  ...overrides,
});

test('the same version is compatible; an older minor warns; a newer minor is fine', () => {
  assert.deepEqual(evaluateContractVersions(remote()), { compatible: true, remote: remote(), warnings: [] });
  const local = { apiContract: '1.2.0', realtimeSchemaVersion: 1 };
  const older = evaluateContractVersions(remote({ apiContract: '1.1.9' }), local);
  assert.ok(older.compatible && older.warnings.length === 1);
  const newer = evaluateContractVersions(remote({ apiContract: '1.4.0' }), local);
  assert.ok(newer.compatible && newer.warnings.length === 0);
});

test('a different major version blocks, in both directions, with a readable message', () => {
  for (const apiContract of ['2.0.0', '0.9.0']) {
    const verdict = evaluateContractVersions(remote({ apiContract }));
    assert.ok(!verdict.compatible && verdict.reason === 'api_major', apiContract);
    assert.match(verdict.message, new RegExp(`${GENERATED_API_CONTRACT_VERSION.replaceAll('.', '\\.')}.*${apiContract.replaceAll('.', '\\.')}`));
  }
});

test('a different realtime schema, an unknown timeline error code or an unreadable answer blocks', () => {
  const realtime = evaluateContractVersions(remote({ realtimeSchemaVersion: 2 }));
  assert.ok(!realtime.compatible && realtime.reason === 'realtime_schema');
  const unknownCode = evaluateContractVersions(remote({ timelineErrorCodes: [...KNOWN_TIMELINE_ERROR_CODES, 'Teapot'] }));
  assert.ok(!unknownCode.compatible && unknownCode.reason === 'unknown_timeline_error_code');
  for (const garbage of [null, 'x', {}, remote({ apiContract: 'one' }), remote({ realtimeSchemaVersion: '1' }), remote({ timelineErrorCodes: 'all' }), remote({ timelineErrorCodes: [1] })]) {
    const verdict = evaluateContractVersions(garbage);
    assert.equal(verdict.compatible, false, JSON.stringify(garbage));
  }
});

test('a server that knows fewer timeline error codes than the client is still compatible', () => {
  const verdict = evaluateContractVersions(remote({ timelineErrorCodes: ['NotFound'] }));
  assert.equal(verdict.compatible, true);
});

function hostFetch(body: unknown, calls: string[]): typeof fetch {
  return (async (input: string | URL | Request) => {
    calls.push(String(input));
    return new Response(JSON.stringify(body), { status: 200, headers: { 'content-type': 'application/json' } });
  }) as typeof fetch;
}

test('the transport refuses every call but /meta/contracts until the host proved compatible', async () => {
  const calls: string[] = [];
  const transport = createMarrowTransport({ apiBaseUrl: 'https://api.test', realtimeBaseUrl: 'https://rt.test', fetch: hostFetch(remote(), calls) });

  const early = await transport.api.get('/auth/staff/me');
  assert.ok(!early.ok && early.failure.kind === 'incompatible' && early.failure.reason === 'handshake_required');
  assert.deepEqual(calls, [], 'nothing was sent before the handshake');
  assert.throws(() => transport.createRealtimeLink(), /compatible verdict/);

  const negotiated = await transport.negotiate();
  assert.ok(negotiated.ok && negotiated.value.compatible);
  assert.deepEqual(calls, ['https://api.test/meta/contracts']);
  assert.equal(describeHandshake(negotiated), `Server contract ${GENERATED_API_CONTRACT_VERSION} is compatible.`);

  const later = await transport.api.get('/auth/staff/me');
  assert.ok(later.ok, 'unlocked after a compatible verdict');
});

test('an incompatible host keeps the gate closed and says why', async () => {
  const calls: string[] = [];
  const transport = createMarrowTransport({ apiBaseUrl: '', realtimeBaseUrl: '', fetch: hostFetch(remote({ apiContract: '9.0.0' }), calls) });
  const negotiated = await transport.negotiate();
  assert.ok(negotiated.ok && !negotiated.value.compatible);
  const blocked = await transport.api.post('/auth/guest/start');
  assert.ok(!blocked.ok && blocked.failure.kind === 'incompatible' && blocked.failure.reason === 'api_major');
  assert.equal(calls.length, 1, 'only the handshake request was ever sent');
  assert.throws(() => transport.createRealtimeLink(), /compatible verdict/);
  assert.match(describeHandshake(negotiated), /9\.0\.0/);
});

test('an unreachable host leaves the gate closed and reports offline', async () => {
  const transport = createMarrowTransport({
    apiBaseUrl: '',
    realtimeBaseUrl: '',
    fetch: (async () => {
      throw new TypeError('fetch failed');
    }) as typeof fetch,
  });
  const negotiated = await transport.negotiate();
  assert.ok(!negotiated.ok && negotiated.failure.kind === 'offline');
  assert.equal(transport.verdict, null);
  assert.match(describeHandshake(negotiated), /Cannot reach the server/);
  assert.equal(describeHandshake(null), 'Checking server contract…');
});
