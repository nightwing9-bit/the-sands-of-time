// Validates: Requirements R04.04, R04.05, R04.08 (typed REST client, explicit failures, no promiscuous types)
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { createApiClient } from '../src/api-client.ts';

interface Captured {
  url: string;
  method: string;
  headers: Record<string, string>;
  body: string | undefined;
  credentials: RequestCredentials | undefined;
}

function scripted(responder: (call: Captured, signal: AbortSignal) => Promise<Response> | Response): { fetch: typeof fetch; calls: Captured[] } {
  const calls: Captured[] = [];
  const impl = async (input: string | URL | Request, init?: RequestInit): Promise<Response> => {
    const call: Captured = {
      url: String(input),
      method: init?.method ?? 'GET',
      headers: (init?.headers ?? {}) as Record<string, string>,
      body: typeof init?.body === 'string' ? init.body : undefined,
      credentials: init?.credentials,
    };
    calls.push(call);
    return responder(call, init?.signal as AbortSignal);
  };
  return { fetch: impl as typeof fetch, calls };
}

const json = (status: number, body: unknown, headers: Record<string, string> = {}) =>
  new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json', ...headers } });

const session = {
  accessToken: 'AT',
  tokenType: 'Bearer',
  accessExpiresAtUtc: '2026-11-10T12:00:00Z',
  subject: 'staff:1',
  roles: ['operator'],
  mfaEnrolled: false,
  mfaVerifiedUntilUtc: null,
  csrfToken: 'CSRF1',
};

test('post sends JSON, credentials, bearer and csrf headers and returns the typed body', async () => {
  const { fetch, calls } = scripted(() => json(200, session));
  const api = createApiClient({ baseUrl: 'https://api.test/', fetch, getAccessToken: () => 'TOKEN', getCsrfToken: () => 'CSRF' });
  const result = await api.post('/auth/staff/login', { body: { userName: 'ops', password: 'pw', totpCode: null } });
  assert.ok(result.ok);
  assert.equal(result.value.csrfToken, 'CSRF1');
  const call = calls[0]!;
  assert.equal(call.url, 'https://api.test/auth/staff/login');
  assert.equal(call.method, 'POST');
  assert.equal(call.credentials, 'include');
  assert.equal(call.headers.Authorization, 'Bearer TOKEN');
  assert.equal(call.headers['X-CSRF-Token'], 'CSRF');
  assert.equal(call.headers['Content-Type'], 'application/json');
  assert.deepEqual(JSON.parse(call.body!), { userName: 'ops', password: 'pw', totpCode: null });
});

test('GET carries no csrf header and no body; path parameters are encoded', async () => {
  const { fetch, calls } = scripted(() => new Response(null, { status: 204 }));
  const api = createApiClient({ baseUrl: '', fetch, getCsrfToken: () => 'CSRF' });
  const result = await api.post('/auth/admin/guests/{guestId}/ban', { path: { guestId: 'a b/c' } });
  assert.ok(result.ok);
  assert.equal(result.status, 204);
  assert.equal(calls[0]!.url, '/auth/admin/guests/a%20b%2Fc/ban');
  assert.equal(calls[0]!.headers['X-CSRF-Token'], 'CSRF');
  await api.get('/meta/contracts');
  assert.equal(calls[1]!.headers['X-CSRF-Token'], undefined);
  assert.equal(calls[1]!.body, undefined);
});

test('permission denials come back as values with no body', async () => {
  const { fetch } = scripted(() => json(403, { code: 'mfa_required', status: 403, traceId: 't1' }));
  const api = createApiClient({ baseUrl: '', fetch });
  const result = await api.post('/auth/staff/mfa/verify', { body: { code: '123456' } });
  assert.equal(result.ok, false);
  assert.deepEqual(!result.ok && result.failure, { kind: 'denied', reason: 'mfa_required', status: 403, code: 'mfa_required', traceId: 't1' });
  assert.equal('value' in result, false, 'a denied call must not carry data');
});

test('a Timeline error body becomes a typed timeline failure', async () => {
  const { fetch } = scripted(() => json(409, { code: 'conflict', status: 409, timelineError: 'IncompatibleVersion' }));
  const api = createApiClient({ baseUrl: '', fetch });
  const result = await api.get('/auth/staff/me');
  assert.ok(!result.ok && result.failure.kind === 'timeline' && result.failure.code === 'IncompatibleVersion');
});

test('network failure is offline, caller abort is cancelled, slow server is a timeout', async () => {
  const dead = createApiClient({ baseUrl: '', fetch: scripted(() => Promise.reject(new TypeError('fetch failed'))).fetch });
  assert.deepEqual((await dead.get('/meta/contracts')) as unknown, { ok: false, failure: { kind: 'offline', cause: 'network' } });

  const hangs = (_call: Captured, signal: AbortSignal) =>
    new Promise<Response>((_resolve, reject) => signal.addEventListener('abort', () => reject(new DOMException('aborted', 'AbortError'))));

  const controller = new AbortController();
  const slow = createApiClient({ baseUrl: '', fetch: scripted(hangs).fetch });
  const pending = slow.get('/meta/contracts', { signal: controller.signal });
  controller.abort();
  assert.deepEqual((await pending) as unknown, { ok: false, failure: { kind: 'cancelled', by: 'caller' } });

  const limited = createApiClient({ baseUrl: '', fetch: scripted(hangs).fetch, timeoutMs: 20 });
  assert.deepEqual((await limited.get('/meta/contracts')) as unknown, { ok: false, failure: { kind: 'offline', cause: 'timeout' } });

  const already = new AbortController();
  already.abort();
  const calls = scripted(() => json(200, {}));
  const skipped = createApiClient({ baseUrl: '', fetch: calls.fetch });
  assert.deepEqual((await skipped.get('/meta/contracts', { signal: already.signal })) as unknown, { ok: false, failure: { kind: 'cancelled', by: 'caller' } });
  assert.equal(calls.calls.length, 0, 'an already-cancelled call is never sent');
});

test('a 2xx answer that is not JSON is malformed, not a value', async () => {
  const { fetch } = scripted(() => new Response('<html>proxy error</html>', { status: 200 }));
  const api = createApiClient({ baseUrl: '', fetch });
  const result = await api.get('/meta/contracts');
  assert.ok(!result.ok && result.failure.kind === 'malformed');
});

test('the guard can refuse a call before anything is sent', async () => {
  const { fetch, calls } = scripted(() => json(200, {}));
  const api = createApiClient({
    baseUrl: '',
    fetch,
    guard: () => ({ kind: 'incompatible', reason: 'api_major', message: 'Update.' }),
  });
  const result = await api.get('/auth/staff/me');
  assert.ok(!result.ok && result.failure.kind === 'incompatible');
  assert.equal(calls.length, 0);
});

test('a missing path parameter is a typed failure, not a request to a literal {placeholder}', async () => {
  const { fetch, calls } = scripted(() => json(200, {}));
  const api = createApiClient({ baseUrl: '', fetch });
  const result = await api.post('/auth/admin/guests/{guestId}/ban', { path: {} as { guestId: string } });
  assert.ok(!result.ok && result.failure.kind === 'malformed');
  assert.equal(calls.length, 0);
});
