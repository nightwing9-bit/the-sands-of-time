// The shared typed client bundle both app shells mount. One factory, so the Revenant and Ghost Lap shells use the same
// REST client, contract gate and hub link, differing only in the endpoints and credentials they pass in.
import { createApiClient, type ApiClient } from './api-client.ts';
import type { ApiResult, TransportFailure } from './failure.ts';
import { evaluateContractVersions, incompatibleFailure, type HandshakeVerdict } from './handshake.ts';
import { RealtimeLink, type RealtimeLinkOptions } from './realtime-link.ts';
import { createRealtimeConnection } from './signalr.ts';

export interface MarrowTransportOptions {
  /** Origin of Hosts.Api (REST). */
  apiBaseUrl: string;
  /** Origin of Hosts.Realtime (SignalR). */
  realtimeBaseUrl: string;
  getAccessToken?: () => string | null;
  getCsrfToken?: () => string | null;
  fetch?: typeof fetch;
  timeoutMs?: number;
  link?: RealtimeLinkOptions;
}

export interface MarrowTransport {
  /** Refuses every call except `/meta/contracts` until `negotiate` proved the host compatible. */
  api: ApiClient;
  /** Creates the supervised hub link. Call `start()` only after `negotiate` returned a compatible verdict. */
  createRealtimeLink(): RealtimeLink;
  /** Fetches `/meta/contracts` and opens or closes the gate. Safe to call again (for example after going offline). */
  negotiate(signal?: AbortSignal): Promise<ApiResult<HandshakeVerdict>>;
  /** Last verdict, or null before the first successful `negotiate`. */
  readonly verdict: HandshakeVerdict | null;
}

export function createMarrowTransport(options: MarrowTransportOptions): MarrowTransport {
  let verdict: HandshakeVerdict | null = null;

  const guard = (path: string): TransportFailure | null => {
    if (path === '/meta/contracts') return null;
    if (verdict === null) {
      return { kind: 'incompatible', reason: 'handshake_required', message: 'Checking the server contract version first. Try again in a moment.' };
    }
    return verdict.compatible ? null : incompatibleFailure(verdict);
  };

  const api = createApiClient({
    baseUrl: options.apiBaseUrl,
    fetch: options.fetch,
    getAccessToken: options.getAccessToken,
    getCsrfToken: options.getCsrfToken,
    timeoutMs: options.timeoutMs,
    guard,
  });

  return {
    api,
    get verdict() {
      return verdict;
    },
    async negotiate(signal) {
      const response = await api.get('/meta/contracts', signal ? { signal } : undefined);
      if (!response.ok) return response;
      verdict = evaluateContractVersions(response.value);
      return { ok: true, value: verdict, status: response.status };
    },
    createRealtimeLink() {
      if (verdict === null || !verdict.compatible) {
        throw new Error('Run negotiate() and get a compatible verdict before opening the realtime link.');
      }
      const connection = createRealtimeConnection({ baseUrl: options.realtimeBaseUrl, getAccessToken: options.getAccessToken });
      return new RealtimeLink(connection, options.link);
    },
  };
}
