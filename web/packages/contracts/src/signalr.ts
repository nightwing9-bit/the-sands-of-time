// The one place that touches @microsoft/signalr. Both apps get their hub connection from here, so they cannot drift in
// transport, logging or auth. The returned connection is wrapped by `RealtimeLink`; nothing else needs the library.
import { HttpTransportType, HubConnectionBuilder, LogLevel, type HubConnection } from '@microsoft/signalr';
import { REALTIME_HUB_PATH } from './realtime.ts';
import type { SupervisedHubConnection } from './realtime-link.ts';

export interface RealtimeConnectionOptions {
  /** Origin of the Realtime host, e.g. `https://realtime.example.test`. Empty string means same origin. */
  baseUrl: string;
  /**
   * Staff access token provider. Browsers cannot set headers on a WebSocket, so SignalR sends it as `access_token` on
   * the hub URL only; the server accepts it on that path and keeps it out of logs. Omit for guests (cookie + Origin).
   */
  getAccessToken?: () => string | null;
  hubPath?: string;
  keepAliveMs?: number;
  serverTimeoutMs?: number;
  reconnectDelaysMs?: number[];
}

/** Builds (does not start) the hub connection: WebSockets only, SignalR automatic reconnect, warnings-only logging. */
export function createRealtimeConnection(options: RealtimeConnectionOptions): SupervisedHubConnection {
  return createAuthenticatedHubConnection(options);
}

/** Same authenticated WebSocket convention for domain hubs; callers own their domain recovery. */
export function createAuthenticatedHubConnection(options: RealtimeConnectionOptions): HubConnection {
  const url = options.baseUrl.replace(/\/+$/, '') + (options.hubPath ?? REALTIME_HUB_PATH);
  const getAccessToken = options.getAccessToken;
  const connection: HubConnection = new HubConnectionBuilder()
    .withUrl(url, {
      transport: HttpTransportType.WebSockets,
      skipNegotiation: true,
      withCredentials: true,
      ...(getAccessToken ? { accessTokenFactory: () => getAccessToken() ?? '' } : {}),
    })
    .withAutomaticReconnect(options.reconnectDelaysMs ?? [0, 2000, 10000, 30000])
    // Anything below Warning logs connection URLs, which carry the staff access token.
    .configureLogging(LogLevel.Warning)
    .build();
  if (options.keepAliveMs) connection.keepAliveIntervalInMilliseconds = options.keepAliveMs;
  if (options.serverTimeoutMs) connection.serverTimeoutInMilliseconds = options.serverTimeoutMs;
  return connection;
}

export { Subject as HubInputSubject } from "@microsoft/signalr";
