// Typed transport failures for REST and hub calls (R04.04). Expected failures are values, never thrown, and each one
// names an explicit UI state. The server's failure body is `ApiProblem` (generated): `{ code, status, traceId?, timelineError? }`.
import type { components } from './generated/hosts-api.ts';
import { describeTimelineError, isTimelineErrorCode, type TimelineErrorCode, type TimelineErrorInfo } from './timeline-errors.ts';

export type ApiProblem = components['schemas']['ApiProblem'];

export type DeniedReason =
  | 'unauthenticated' // 401: sign in again
  | 'forbidden' // 403 or Timeline Forbidden: signed in, not allowed
  | 'mfa_required' // staff action needs a fresh TOTP step-up
  | 'csrf' // csrf_rejected / origin_rejected: reload the page to get a fresh token
  | 'banned'; // guest_banned

export type OfflineCause = 'network' | 'timeout' | 'disconnected';

export type IncompatibleReason = 'api_major' | 'realtime_schema' | 'unknown_timeline_error_code' | 'handshake_required';

/**
 * Every way a call can fail. `stale` and `cancelled` are not errors to show in red: stale means a newer request
 * replaced this one, cancelled means the caller (or the server) stopped it and nothing changed.
 */
export type TransportFailure =
  | { kind: 'cancelled'; by: 'caller' | 'server' }
  | { kind: 'stale' }
  | { kind: 'offline'; cause: OfflineCause }
  | { kind: 'denied'; reason: DeniedReason; status: number; code?: string; traceId?: string }
  | { kind: 'timeline'; code: TimelineErrorCode; info: TimelineErrorInfo; status: number; traceId?: string }
  | { kind: 'incompatible'; reason: IncompatibleReason; message: string }
  | { kind: 'rate_limited'; status: 429; retryAfterMs?: number }
  | { kind: 'server'; status: number; code?: string; traceId?: string }
  | { kind: 'malformed'; message: string };

export type ApiResult<T> = { ok: true; value: T; status: number } | { ok: false; failure: TransportFailure };

/** The explicit states a view can be in while talking to the server. */
export type TransportViewState = 'loading' | 'ready' | 'cancelled' | 'denied' | 'offline' | 'incompatible' | 'error';

export function viewStateOf(result: ApiResult<unknown> | null): TransportViewState {
  if (result === null) return 'loading';
  if (result.ok) return 'ready';
  switch (result.failure.kind) {
    case 'stale':
      return 'loading'; // a newer request is in flight; this answer must not be shown
    case 'cancelled':
      return 'cancelled';
    case 'denied':
      return 'denied';
    case 'offline':
      return 'offline';
    case 'incompatible':
      return 'incompatible';
    case 'timeline':
      return result.failure.info.viewState === 'denied'
        ? 'denied'
        : result.failure.info.viewState === 'cancelled'
          ? 'cancelled'
          : result.failure.info.viewState === 'incompatible'
            ? 'incompatible'
            : 'error';
    case 'rate_limited':
    case 'server':
    case 'malformed':
      return 'error';
  }
}

/** Short user-facing text for a failure. Never includes server detail, tokens or ids other than the trace id. */
export function describeFailure(failure: TransportFailure): string {
  switch (failure.kind) {
    case 'cancelled':
      return failure.by === 'server' ? 'The server cancelled the request. Nothing was changed.' : 'Request cancelled. Nothing was changed.';
    case 'stale':
      return 'A newer request replaced this one.';
    case 'offline':
      return failure.cause === 'timeout' ? 'The server took too long to answer.' : 'You are offline or the server cannot be reached.';
    case 'denied':
      switch (failure.reason) {
        case 'unauthenticated':
          return 'Your session is not active. Sign in again.';
        case 'mfa_required':
          return 'This action needs a fresh authenticator code.';
        case 'csrf':
          return 'The request was rejected for safety. Reload the page and try again.';
        case 'banned':
          return 'This guest session has been blocked.';
        case 'forbidden':
          return 'You do not have permission to do that.';
      }
    case 'timeline':
      return failure.info.message;
    case 'incompatible':
      return failure.message;
    case 'rate_limited':
      return 'Too many requests. Wait a moment and try again.';
    case 'server':
      return failure.traceId ? `The server could not complete the request (trace ${failure.traceId}).` : 'The server could not complete the request.';
    case 'malformed':
      return 'The server answered with something this version does not understand.';
  }
}

const AUTH_CSRF_CODES = new Set(['csrf_rejected', 'origin_rejected']);

function readProblem(body: unknown): { code?: string; traceId?: string; timelineError?: unknown } {
  if (typeof body !== 'object' || body === null || Array.isArray(body)) return {};
  const record = body as Record<string, unknown>;
  const out: { code?: string; traceId?: string; timelineError?: unknown } = {};
  if (typeof record.code === 'string' && record.code.length <= 64) out.code = record.code;
  if (typeof record.traceId === 'string' && record.traceId.length <= 128) out.traceId = record.traceId;
  if (record.timelineError !== undefined && record.timelineError !== null) out.timelineError = record.timelineError;
  return out;
}

/** Maps one Timeline error code to a failure. `Forbidden` is a permission state, `Cancelled` a cancelled state. */
export function timelineFailure(code: TimelineErrorCode, status: number, traceId?: string): TransportFailure {
  if (code === 'Forbidden') return compact({ kind: 'denied', reason: 'forbidden', status, code, traceId });
  if (code === 'Cancelled') return { kind: 'cancelled', by: 'server' };
  return compact({ kind: 'timeline', code, info: describeTimelineError(code), status, traceId });
}

/**
 * Classifies a non-2xx HTTP answer. A `timelineError` the client does not know is a contract problem, not a guess:
 * it becomes `incompatible`, so a new server-side code can never be shown as a usable result.
 */
export function classifyHttpFailure(status: number, body: unknown, headers?: { get(name: string): string | null }): TransportFailure {
  const problem = readProblem(body);
  if (problem.timelineError !== undefined) {
    if (!isTimelineErrorCode(problem.timelineError)) {
      return {
        kind: 'incompatible',
        reason: 'unknown_timeline_error_code',
        message: 'The server reported a timeline error this version does not know. Update the app to continue.',
      };
    }
    return timelineFailure(problem.timelineError, status, problem.traceId);
  }
  if (status === 429) {
    const retryAfter = headers?.get('retry-after');
    const seconds = retryAfter !== null && retryAfter !== undefined && /^\d{1,6}$/.test(retryAfter) ? Number(retryAfter) : undefined;
    return seconds === undefined ? { kind: 'rate_limited', status: 429 } : { kind: 'rate_limited', status: 429, retryAfterMs: seconds * 1000 };
  }
  if (status === 401) return compact({ kind: 'denied', reason: 'unauthenticated', status, code: problem.code, traceId: problem.traceId });
  if (status === 403) {
    const reason: DeniedReason =
      problem.code === 'mfa_required'
        ? 'mfa_required'
        : problem.code !== undefined && AUTH_CSRF_CODES.has(problem.code)
          ? 'csrf'
          : problem.code === 'guest_banned'
            ? 'banned'
            : 'forbidden';
    return compact({ kind: 'denied', reason, status, code: problem.code, traceId: problem.traceId });
  }
  return compact({ kind: 'server', status, code: problem.code, traceId: problem.traceId });
}

/** Classifies an exception from `fetch` or from reading its body. */
export function classifyThrown(error: unknown, signal?: AbortSignal, timedOut?: () => boolean): TransportFailure {
  if (signal?.aborted && !timedOut?.()) return { kind: 'cancelled', by: 'caller' };
  if (timedOut?.()) return { kind: 'offline', cause: 'timeout' };
  const name = typeof error === 'object' && error !== null ? (error as { name?: unknown }).name : undefined;
  if (name === 'AbortError') return { kind: 'cancelled', by: 'caller' };
  if (name === 'TimeoutError') return { kind: 'offline', cause: 'timeout' };
  return { kind: 'offline', cause: 'network' };
}

/** Drops keys whose value is undefined so failures compare and serialize cleanly. */
function compact<T extends object>(value: T): T {
  for (const key of Object.keys(value) as (keyof T)[]) {
    if (value[key] === undefined) delete value[key];
  }
  return value;
}
