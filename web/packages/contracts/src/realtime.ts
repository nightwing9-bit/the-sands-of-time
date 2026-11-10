// Realtime hub protocol v1. Mirror of src/Marrow.Kernel/Realtime/{RealtimeEnvelope,RealtimeProtocol}.cs.
// Both sides must change together; tests/Integration/Realtime compares this file with the server's actual JSON.
// Self-contained (no imports) and erasable-syntax only, so Node can run it directly with type stripping.

export const REALTIME_SCHEMA_VERSION = 1;
export const REALTIME_HUB_PATH = '/hubs/realtime';

export const RealtimeHubMethods = {
  subscribe: 'Subscribe',
  unsubscribe: 'Unsubscribe',
  getSnapshot: 'GetSnapshot',
} as const;

/** Server-to-client methods. */
export const RealtimeClientMethods = { receive: 'Receive' } as const;

/** Exact envelope wire fields in canonical order. No aliases (messageId, streamSequence, serverTime) exist. */
export const REALTIME_ENVELOPE_FIELDS = [
  'schemaVersion',
  'streamId',
  'sequence',
  'eventId',
  'serverUtc',
  'kind',
  'payload',
] as const;

export interface RealtimeEnvelope<TPayload = unknown> {
  schemaVersion: number;
  streamId: string;
  /** Per-stream monotonic decimal string. Not a gameplay tick. */
  sequence: string;
  eventId: string;
  /** ISO-8601 server time. */
  serverUtc: string;
  kind: string;
  payload: TPayload;
}

// Compile-time proof that the interface keys and REALTIME_ENVELOPE_FIELDS are the same set.
type FieldName = (typeof REALTIME_ENVELOPE_FIELDS)[number];
type SameKeys<A, B> = [A] extends [B] ? ([B] extends [A] ? true : false) : false;
export const envelopeFieldsMatchInterface: SameKeys<FieldName, keyof RealtimeEnvelope> = true;

export interface RealtimeSnapshot<TPayload = unknown> {
  schemaVersion: number;
  streamId: string;
  sequence: string;
  payload: TPayload;
}

export type RealtimeFailureCode = 'forbidden' | 'invalid_stream' | 'rate_limited' | 'subscription_limit' | 'unavailable';

export interface RealtimeReply<TPayload = unknown> {
  ok: boolean;
  code: RealtimeFailureCode | null;
  snapshot: RealtimeSnapshot<TPayload> | null;
}

const SEQUENCE = /^(0|[1-9][0-9]{0,18})$/;
const STREAM_ID = /^[a-z][a-z0-9-]{0,31}:[A-Za-z0-9_-]{1,96}$/;
const KIND = /^[a-z][a-z0-9._-]{0,63}$/;
const MAX_SEQUENCE = 9223372036854775807n;

/** Canonical decimal string to bigint, or null. */
export function parseSequence(text: unknown): bigint | null {
  if (typeof text !== 'string' || !SEQUENCE.test(text)) return null;
  const value = BigInt(text);
  return value <= MAX_SEQUENCE ? value : null;
}

/** Strict parse: exactly the seven fields, schema version 1, canonical sequence, valid stream id/kind and time. */
export function parseRealtimeEnvelope(raw: unknown): RealtimeEnvelope | null {
  if (typeof raw !== 'object' || raw === null || Array.isArray(raw)) return null;
  const record = raw as Record<string, unknown>;
  const keys = Object.keys(record);
  if (keys.length !== REALTIME_ENVELOPE_FIELDS.length) return null;
  for (const key of keys) {
    if (!(REALTIME_ENVELOPE_FIELDS as readonly string[]).includes(key)) return null;
  }
  const { schemaVersion, streamId, sequence, eventId, serverUtc, kind, payload } = record;
  if (schemaVersion !== REALTIME_SCHEMA_VERSION) return null;
  if (typeof streamId !== 'string' || !STREAM_ID.test(streamId)) return null;
  if (parseSequence(sequence) === null) return null;
  if (typeof eventId !== 'string' || eventId.length === 0) return null;
  if (typeof serverUtc !== 'string' || Number.isNaN(Date.parse(serverUtc))) return null;
  if (typeof kind !== 'string' || !KIND.test(kind)) return null;
  return { schemaVersion, streamId, sequence: sequence as string, eventId, serverUtc, kind, payload };
}

export type CursorAction = 'apply' | 'duplicate' | 'buffered' | 'gap';

export interface SnapshotOutcome<T> {
  accepted: boolean;
  ready: T[];
  gapRemains: boolean;
}

/**
 * Ordering state for one stream; same rules as the C# RealtimeStreamCursor. Starts awaiting a snapshot; events before
 * it are buffered (bounded), events at or below the applied sequence are duplicates, the next one is applied, a jump
 * ahead is a gap that requires a snapshot refetch.
 */
export class RealtimeStreamCursor<T> {
  readonly streamId: string;
  private readonly maxBuffered: number;
  private readonly buffer = new Map<bigint, T>();
  private applied: bigint | null = null;
  private awaiting = true;

  constructor(streamId: string, maxBuffered = 256) {
    this.streamId = streamId;
    this.maxBuffered = maxBuffered;
  }

  get sequence(): bigint | null {
    return this.applied;
  }

  get awaitingSnapshot(): boolean {
    return this.awaiting;
  }

  reset(): void {
    this.buffer.clear();
    this.applied = null;
    this.awaiting = true;
  }

  accept(sequence: bigint, item: T): CursorAction {
    if (this.applied !== null && sequence <= this.applied) return 'duplicate';
    if (this.awaiting) {
      this.hold(sequence, item);
      return 'buffered';
    }
    if (this.applied !== null && sequence === this.applied + 1n) {
      this.applied = sequence;
      return 'apply';
    }
    this.awaiting = true;
    this.hold(sequence, item);
    return 'gap';
  }

  applySnapshot(sequence: bigint): SnapshotOutcome<T> {
    if (this.applied !== null && sequence < this.applied) {
      return { accepted: false, ready: [], gapRemains: this.awaiting };
    }
    let current = sequence;
    const ready: T[] = [];
    for (const key of [...this.buffer.keys()].sort((a, b) => (a < b ? -1 : a > b ? 1 : 0))) {
      if (key <= current) {
        this.buffer.delete(key);
      } else if (key === current + 1n) {
        ready.push(this.buffer.get(key) as T);
        this.buffer.delete(key);
        current = key;
      } else {
        break;
      }
    }
    this.applied = current;
    this.awaiting = this.buffer.size > 0;
    return { accepted: true, ready, gapRemains: this.awaiting };
  }

  private hold(sequence: bigint, item: T): void {
    if (this.buffer.size >= this.maxBuffered && !this.buffer.has(sequence)) {
      let last = -1n;
      for (const key of this.buffer.keys()) if (key > last) last = key;
      if (sequence > last) return; // the snapshot that must follow covers it
      this.buffer.delete(last);
    }
    this.buffer.set(sequence, item);
  }
}

/** The subset of @microsoft/signalr HubConnection this client needs; pass the real connection. */
export interface RealtimeHubConnection {
  invoke<T = unknown>(methodName: string, ...args: unknown[]): Promise<T>;
  on(methodName: string, handler: (...args: unknown[]) => void): void;
  off(methodName: string, handler: (...args: unknown[]) => void): void;
  onreconnected(callback: (connectionId?: string) => void): void;
}

export interface StreamHandlers<TState = unknown, TEvent = unknown> {
  /** Replace local state with the authoritative snapshot. */
  onSnapshot(payload: TState, sequence: bigint): void;
  /** Apply one in-order event. */
  onEvent(envelope: RealtimeEnvelope<TEvent>): void;
  /** Subscribe or refetch was refused; local state is no longer live. */
  onFailure?(code: RealtimeFailureCode): void;
}

/**
 * Typed, loss-tolerant subscriber. On subscribe, a detected gap or a reconnect it fetches a snapshot with the latest
 * stream sequence and resumes from it; it never assumes SignalR delivered every message.
 */
export class RealtimeStreamClient {
  private readonly connection: RealtimeHubConnection;
  private readonly streams = new Map<string, { cursor: RealtimeStreamCursor<RealtimeEnvelope>; handlers: StreamHandlers<unknown, unknown> }>();
  private readonly receive = (raw: unknown): void => this.onReceive(raw);

  constructor(connection: RealtimeHubConnection) {
    this.connection = connection;
    connection.on(RealtimeClientMethods.receive, this.receive);
    connection.onreconnected(() => {
      void this.resync();
    });
  }

  async subscribe<TState, TEvent>(streamId: string, handlers: StreamHandlers<TState, TEvent>): Promise<RealtimeReply<TState>> {
    this.streams.set(streamId, { cursor: new RealtimeStreamCursor(streamId), handlers: handlers as StreamHandlers<unknown, unknown> });
    return this.resubscribe(streamId) as Promise<RealtimeReply<TState>>;
  }

  /**
   * Resubscribes every stream and replaces local state with a fresh snapshot. Called after a reconnect and by the
   * link's own restart. A failed invoke (connection dropped again) is reported as `unavailable` and the stream stays
   * registered, so the next resync retries it; it never becomes an unhandled rejection.
   */
  async resync(): Promise<void> {
    await Promise.all([...this.streams.keys()].map((streamId) => this.guarded(streamId, () => this.resubscribe(streamId))));
  }

  async unsubscribe(streamId: string): Promise<void> {
    this.streams.delete(streamId);
    await this.connection.invoke(RealtimeHubMethods.unsubscribe, streamId);
  }

  dispose(): void {
    this.connection.off(RealtimeClientMethods.receive, this.receive);
    this.streams.clear();
  }

  private async resubscribe(streamId: string): Promise<RealtimeReply> {
    const entry = this.streams.get(streamId);
    if (!entry) return { ok: false, code: 'forbidden', snapshot: null };
    entry.cursor.reset();
    const reply = await this.connection.invoke<RealtimeReply>(RealtimeHubMethods.subscribe, streamId);
    this.applyReply(streamId, reply);
    return reply;
  }

  private async refetch(streamId: string): Promise<void> {
    const reply = await this.connection.invoke<RealtimeReply>(RealtimeHubMethods.getSnapshot, streamId);
    this.applyReply(streamId, reply);
  }

  private applyReply(streamId: string, reply: RealtimeReply): void {
    const entry = this.streams.get(streamId);
    if (!entry) return;
    const sequence = reply.ok && reply.snapshot ? parseSequence(reply.snapshot.sequence) : null;
    if (!reply.ok || !reply.snapshot || sequence === null || reply.snapshot.streamId !== streamId) {
      this.streams.delete(streamId);
      entry.handlers.onFailure?.(reply.code ?? 'unavailable');
      return;
    }
    const outcome = entry.cursor.applySnapshot(sequence);
    if (!outcome.accepted) return;
    entry.handlers.onSnapshot(reply.snapshot.payload, sequence);
    for (const envelope of outcome.ready) entry.handlers.onEvent(envelope);
    if (outcome.gapRemains) void this.guarded(streamId, () => this.refetch(streamId));
  }

  private async guarded(streamId: string, operation: () => Promise<unknown>): Promise<void> {
    try {
      await operation();
    } catch {
      this.streams.get(streamId)?.handlers.onFailure?.('unavailable');
    }
  }

  private onReceive(raw: unknown): void {
    const envelope = parseRealtimeEnvelope(raw);
    if (!envelope) return;
    const entry = this.streams.get(envelope.streamId);
    if (!entry) return;
    const action = entry.cursor.accept(parseSequence(envelope.sequence) as bigint, envelope);
    if (action === 'apply') entry.handlers.onEvent(envelope);
    else if (action === 'gap') void this.guarded(envelope.streamId, () => this.refetch(envelope.streamId));
  }
}
