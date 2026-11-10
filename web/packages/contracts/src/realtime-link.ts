// Connection supervision around the typed hub client (R04.04). SignalR can drop, retry a few times and then give up;
// the sequence-gap protocol in `realtime.ts` repairs missed events but says nothing about what the user sees. This
// link keeps one explicit state, restarts a closed connection with bounded backoff, and reports `online` only after
// every stream has been re-synchronised from an authoritative snapshot. Hub messages are never trusted to be complete.
import {
  RealtimeStreamClient,
  type RealtimeFailureCode,
  type RealtimeHubConnection,
  type RealtimeReply,
  type StreamHandlers,
} from './realtime.ts';

/** The subset of @microsoft/signalr `HubConnection` the link needs (a real connection satisfies it structurally). */
export interface SupervisedHubConnection extends RealtimeHubConnection {
  start(): Promise<void>;
  stop(): Promise<void>;
  onreconnecting(callback: (error?: Error) => void): void;
  onclose(callback: (error?: Error) => void): void;
}

/**
 * - `connecting`: first start in progress.
 * - `syncing`: connected, snapshots not yet applied (initial or after a drop). Show stale data as stale.
 * - `online`: connected and every stream holds a snapshot-based, gap-checked state.
 * - `reconnecting`: connection lost, automatic retry running.
 * - `offline`: gave up for now; a restart is scheduled unless the link was disposed.
 * - `denied`: every requested stream was refused (membership or ownership). Nothing is delivered. A refusal of one stream
 *   among live ones is reported only to that stream's `onFailure('forbidden')`.
 * - `closed`: disposed by the app.
 */
export type RealtimeLinkState = 'connecting' | 'syncing' | 'online' | 'reconnecting' | 'offline' | 'denied' | 'closed';

export interface RealtimeLinkOptions {
  /** Restart delays in ms, last value repeats. Default 1s, 2s, 5s, 10s, 30s. */
  restartDelaysMs?: readonly number[];
  /** Timer injection for tests. */
  setTimer?: (callback: () => void, delayMs: number) => unknown;
  clearTimer?: (handle: unknown) => void;
}

export type RealtimeLinkListener = (state: RealtimeLinkState) => void;

const DEFAULT_DELAYS = [1_000, 2_000, 5_000, 10_000, 30_000] as const;

export class RealtimeLink {
  private readonly connection: SupervisedHubConnection;
  private readonly client: RealtimeStreamClient;
  private readonly delays: readonly number[];
  private readonly setTimer: (callback: () => void, delayMs: number) => unknown;
  private readonly clearTimer: (handle: unknown) => void;
  private readonly listeners = new Set<RealtimeLinkListener>();
  private readonly pending = new Set<string>();
  private readonly denied = new Set<string>();
  /** Streams the app subscribed to and the server has not refused. */
  private readonly knownStreams = new Set<string>();
  private resyncHandle: unknown = null;
  private current: RealtimeLinkState = 'connecting';
  private restartHandle: unknown = null;
  private attempts = 0;
  private disposed = false;

  constructor(connection: SupervisedHubConnection, options: RealtimeLinkOptions = {}) {
    this.connection = connection;
    this.delays = options.restartDelaysMs && options.restartDelaysMs.length > 0 ? options.restartDelaysMs : DEFAULT_DELAYS;
    this.setTimer = options.setTimer ?? ((callback, delay) => setTimeout(callback, delay));
    this.clearTimer = options.clearTimer ?? ((handle) => clearTimeout(handle as ReturnType<typeof setTimeout>));
    this.client = new RealtimeStreamClient(connection);
    connection.onreconnecting(() => {
      // The stream client resubscribes on `onreconnected`; until then every stream is untrusted.
      for (const id of this.knownStreams) this.pending.add(id);
      this.set('reconnecting');
    });
    connection.onclose(() => {
      if (this.disposed) return;
      for (const id of this.knownStreams) this.pending.add(id);
      this.set('offline');
      this.scheduleRestart();
    });
    // RealtimeStreamClient already registered onreconnected to resync; this extra hook only advances the state.
    connection.onreconnected(() => {
      this.attempts = 0;
      this.set(this.pending.size > 0 ? 'syncing' : 'online');
    });
  }

  get state(): RealtimeLinkState {
    return this.current;
  }

  onStateChange(listener: RealtimeLinkListener): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  /** Opens the connection. A failure here is reported as `offline` with a scheduled restart, not thrown. */
  async start(): Promise<void> {
    this.set('connecting');
    await this.open();
  }

  /**
   * Subscribes to a stream. Resolves with the server's reply (snapshot or typed refusal). `handlers.onSnapshot` and
   * `onEvent` are called exactly as in `RealtimeStreamClient`; the link wraps them only to track recovery.
   */
  async subscribe<TState, TEvent>(streamId: string, handlers: StreamHandlers<TState, TEvent>): Promise<RealtimeReply<TState>> {
    this.knownStreams.add(streamId);
    this.pending.add(streamId);
    this.denied.delete(streamId);
    const wrapped: StreamHandlers<TState, TEvent> = {
      onSnapshot: (payload, sequence) => {
        this.pending.delete(streamId);
        handlers.onSnapshot(payload, sequence);
        this.recompute();
      },
      onEvent: (envelope) => handlers.onEvent(envelope),
      onFailure: (code: RealtimeFailureCode) => {
        this.pending.delete(streamId);
        if (code === 'forbidden' || code === 'invalid_stream') {
          this.knownStreams.delete(streamId);
          this.denied.add(streamId);
        } else {
          // rate_limited, subscription_limit or unavailable: still our stream, but its state is not trustworthy.
          this.pending.add(streamId);
          if (code === 'unavailable' || code === 'rate_limited') this.scheduleResync();
        }
        handlers.onFailure?.(code);
        this.recompute();
      },
    };
    if (this.current === 'online' || this.current === 'denied') this.set('syncing');
    try {
      return await this.client.subscribe(streamId, wrapped);
    } catch (error) {
      // The invoke failed (connection not started or dropped). The stream stays pending and is resynced on recovery.
      this.pending.add(streamId);
      throw error;
    }
  }

  async unsubscribe(streamId: string): Promise<void> {
    this.knownStreams.delete(streamId);
    this.pending.delete(streamId);
    this.denied.delete(streamId);
    await this.client.unsubscribe(streamId);
    this.recompute();
  }

  async dispose(): Promise<void> {
    this.disposed = true;
    if (this.restartHandle !== null) this.clearTimer(this.restartHandle);
    if (this.resyncHandle !== null) this.clearTimer(this.resyncHandle);
    this.restartHandle = null;
    this.resyncHandle = null;
    this.client.dispose();
    this.listeners.clear();
    this.current = 'closed';
    try {
      await this.connection.stop();
    } catch {
      // Already stopped or never started.
    }
  }

  private async open(): Promise<void> {
    try {
      await this.connection.start();
    } catch {
      if (this.disposed) return;
      this.set('offline');
      this.scheduleRestart();
      return;
    }
    if (this.disposed) return;
    this.attempts = 0;
    this.set('syncing');
    // A restart after `onclose` has no `onreconnected` callback, so resync here; on the first start there is nothing yet.
    if (this.knownStreams.size > 0) await this.client.resync();
    this.recompute();
  }

  private scheduleResync(): void {
    if (this.disposed || this.resyncHandle !== null) return;
    const delay = this.delays[Math.min(this.attempts, this.delays.length - 1)] ?? 30_000;
    this.attempts += 1;
    this.resyncHandle = this.setTimer(() => {
      this.resyncHandle = null;
      if (this.disposed || this.current === 'offline' || this.current === 'reconnecting') return;
      void this.client.resync().then(() => this.recompute());
    }, delay);
  }

  private scheduleRestart(): void {
    if (this.disposed || this.restartHandle !== null) return;
    const delay = this.delays[Math.min(this.attempts, this.delays.length - 1)] ?? 30_000;
    this.attempts += 1;
    this.restartHandle = this.setTimer(() => {
      this.restartHandle = null;
      void this.open();
    }, delay);
  }

  private recompute(): void {
    if (this.disposed || this.current === 'offline' || this.current === 'reconnecting' || this.current === 'connecting') return;
    if (this.pending.size > 0) this.set('syncing');
    else this.set(this.knownStreams.size === 0 && this.denied.size > 0 ? 'denied' : 'online');
  }

  private set(state: RealtimeLinkState): void {
    if (this.disposed || state === this.current) return;
    this.current = state;
    for (const listener of [...this.listeners]) listener(state);
  }
}
