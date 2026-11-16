// Typed REST client over the generated OpenAPI `paths`. Path, method, path parameters, JSON body and the success body
// type all come from `generated/hosts-api.ts`; there is no hand-written DTO here. Every failure is a `TransportFailure`
// value (R04.04): the methods never throw for an expected failure and never return a half-typed body.
import type { paths } from './generated/hosts-api.ts';
import { classifyHttpFailure, classifyThrown, type ApiResult, type TransportFailure } from './failure.ts';

type HttpMethod = 'get' | 'post' | 'put' | 'patch' | 'delete';
type SuccessStatus = 200 | 201 | 202 | 204;

type OperationOf<P extends keyof paths, M extends HttpMethod> = NonNullable<paths[P][M]>;

/** Paths that define the HTTP method M. */
export type PathsWith<M extends HttpMethod> = {
  [P in keyof paths]: [NonNullable<paths[P][M]>] extends [never] ? never : P;
}[keyof paths];

type JsonContent<R> = R extends { content: { 'application/json': infer B } } ? B : undefined;
type ResponsesOf<Op> = Op extends { responses: infer R } ? R : never;
type SuccessBody<Op> = {
  [S in keyof ResponsesOf<Op> & SuccessStatus]: JsonContent<ResponsesOf<Op>[S]>;
}[keyof ResponsesOf<Op> & SuccessStatus];

type JsonBody<Op> = Op extends { requestBody?: infer B }
  ? [NonNullable<B>] extends [never]
    ? never
    : NonNullable<B> extends { content: { 'application/json': infer J } }
      ? Exclude<J, null>
      : never
  : never;

type PathParams<Op> = Op extends { parameters: { path?: infer P } } ? NonNullable<P> : never;

type WithBody<Op> = [JsonBody<Op>] extends [never] ? unknown : { body: JsonBody<Op> };
type WithPath<Op> = [PathParams<Op>] extends [never] ? unknown : { path: PathParams<Op> };

export type RequestOptions<Op> = { signal?: AbortSignal; idempotencyKey?: string } & WithBody<Op> & WithPath<Op>;

type NeedsOptions<Op> = [JsonBody<Op>] extends [never] ? ([PathParams<Op>] extends [never] ? false : true) : true;
type CallArgs<Op> = NeedsOptions<Op> extends true ? [options: RequestOptions<Op>] : [options?: RequestOptions<Op>];

export type ResponseBody<P extends keyof paths, M extends HttpMethod> = SuccessBody<OperationOf<P, M>>;

export interface ApiClientOptions {
  /** Origin and optional base path, e.g. `https://api.example.test`. Empty string means same origin. */
  baseUrl: string;
  /** Defaults to the global `fetch`. */
  fetch?: typeof fetch;
  /** Staff bearer token, or null for none. Read on every request; never stored or logged here. */
  getAccessToken?: () => string | null;
  /** Double-submit CSRF token for cookie-authenticated mutations, or null. Sent as `X-CSRF-Token`. */
  getCsrfToken?: () => string | null;
  /** Per-request limit. Default 15000. */
  timeoutMs?: number;
  /** Called before sending. Return a failure to refuse the call (used for the contract gate). */
  guard?: (path: string) => TransportFailure | null;
}

export interface ApiClient {
  get<P extends PathsWith<'get'>>(path: P, ...args: CallArgs<OperationOf<P, 'get'>>): Promise<ApiResult<SuccessBody<OperationOf<P, 'get'>>>>;
  post<P extends PathsWith<'post'>>(path: P, ...args: CallArgs<OperationOf<P, 'post'>>): Promise<ApiResult<SuccessBody<OperationOf<P, 'post'>>>>;
  put<P extends PathsWith<'put'>>(path: P, ...args: CallArgs<OperationOf<P, 'put'>>): Promise<ApiResult<SuccessBody<OperationOf<P, 'put'>>>>;
  patch<P extends PathsWith<'patch'>>(path: P, ...args: CallArgs<OperationOf<P, 'patch'>>): Promise<ApiResult<SuccessBody<OperationOf<P, 'patch'>>>>;
  delete<P extends PathsWith<'delete'>>(path: P, ...args: CallArgs<OperationOf<P, 'delete'>>): Promise<ApiResult<SuccessBody<OperationOf<P, 'delete'>>>>;
}

interface RuntimeOptions {
  idempotencyKey?: string;
  signal?: AbortSignal;
  body?: unknown;
  path?: Record<string, string | number>;
}

const DEFAULT_TIMEOUT_MS = 15_000;
const MUTATING = new Set<HttpMethod>(['post', 'put', 'patch', 'delete']);
const PATH_PARAM = /\{([A-Za-z0-9_]+)\}/g;

export function createApiClient(options: ApiClientOptions): ApiClient {
  const base = options.baseUrl.replace(/\/+$/, '');

  async function send(method: HttpMethod, template: string, runtime: RuntimeOptions): Promise<ApiResult<unknown>> {
    const refused = options.guard?.(template) ?? null;
    if (refused) return { ok: false, failure: refused };
    if (runtime.signal?.aborted) return { ok: false, failure: { kind: 'cancelled', by: 'caller' } };

    let url: string;
    try {
      url = base + fillPath(template, runtime.path);
    } catch (error) {
      return { ok: false, failure: { kind: 'malformed', message: error instanceof Error ? error.message : 'Bad path parameter.' } };
    }

    const headers: Record<string, string> = { Accept: 'application/json' };
    const token = options.getAccessToken?.() ?? null;
    if (token) headers.Authorization = `Bearer ${token}`;
    if (MUTATING.has(method)) {
      if (runtime.idempotencyKey) headers['Idempotency-Key'] = runtime.idempotencyKey;
      const csrf = options.getCsrfToken?.() ?? null;
      if (csrf) headers['X-CSRF-Token'] = csrf;
    }
    let body: string | undefined;
    if (runtime.body !== undefined) {
      headers['Content-Type'] = 'application/json';
      body = JSON.stringify(runtime.body);
    }

    const timeout = new AbortController();
    let timedOut = false;
    const timer = setTimeout(() => {
      timedOut = true;
      timeout.abort();
    }, options.timeoutMs ?? DEFAULT_TIMEOUT_MS);
    const signal = runtime.signal ? AbortSignal.any([runtime.signal, timeout.signal]) : timeout.signal;

    try {
      const response = await (options.fetch ?? fetch)(url, { method: method.toUpperCase(), headers, body, signal, credentials: 'include', cache: 'no-store' });
      const text = await response.text();
      const parsed = parseJson(text);
      if (!response.ok) return { ok: false, failure: classifyHttpFailure(response.status, parsed.ok ? parsed.value : undefined, response.headers) };
      if (response.status === 204 || text.length === 0) return { ok: true, value: undefined, status: response.status };
      if (!parsed.ok) return { ok: false, failure: { kind: 'malformed', message: 'The response was not valid JSON.' } };
      return { ok: true, value: parsed.value, status: response.status };
    } catch (error) {
      return { ok: false, failure: classifyThrown(error, runtime.signal, () => timedOut) };
    } finally {
      clearTimeout(timer);
    }
  }

  // The public method types are precise; this single cast is the only place they meet the runtime shape.
  const call = (method: HttpMethod) => (path: string, runtime?: RuntimeOptions) => send(method, path, runtime ?? {});
  return {
    get: call('get'),
    post: call('post'),
    put: call('put'),
    patch: call('patch'),
    delete: call('delete'),
  } as ApiClient;
}

function fillPath(template: string, params: Record<string, string | number> | undefined): string {
  return template.replace(PATH_PARAM, (_whole, name: string) => {
    const value = params?.[name];
    if (value === undefined) throw new Error(`Missing path parameter ${name}.`);
    return encodeURIComponent(String(value));
  });
}

function parseJson(text: string): { ok: true; value: unknown } | { ok: false } {
  if (text.length === 0) return { ok: true, value: undefined };
  try {
    return { ok: true, value: JSON.parse(text) as unknown };
  } catch {
    return { ok: false };
  }
}
