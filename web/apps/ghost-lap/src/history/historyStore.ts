// Immutable history frames, the bounded chunk loader and the identity-keyed cache (Spec 13, task 13.10). No React, no Pixi.
//
// A saved Original or child is immutable, so its frames are cached by WHO it is and WHICH version it was verified under, never by list
// position: `kind : id : finalStateHash : versionKey`. The browser loads one history in at most ten 300-state requests, one at a time (the
// server shares four verification slots across all reads); scrubbing only reads memory. Dragging toward a part that is not loaded yet
// moves that ONE chunk to the front of the queue, so seeking is bounded by ten requests in total, never one per pointer movement.
//
// Nothing here invents a state. A tick that is not loaded is "not loaded", and the callers say so.
import { SoloHttpError } from "../solo/soloWire.ts";
import { FINAL_TICK, TRACE_MAX, type HistoryFrameWire, type TraceChunk, type TraceIdentity, type TraceKind } from "./historyWire.ts";

export const chunkOf = (tick: number): number => Math.floor(tick / TRACE_MAX);
export const chunkStart = (index: number): number => index * TRACE_MAX;
export const chunkTotal = (terminal: number): number => chunkOf(terminal) + 1;
/** Number of states a chunk request asks for (the last chunk of a 2701-state history holds the single state at tick 2700). */
export const chunkCount = (index: number, terminal: number): number => Math.min(TRACE_MAX, terminal + 1 - chunkStart(index));

/** What the library already knows about the history it is about to open. Anything a chunk contradicts is refused, not trusted. */
export interface ExpectedHistory {
  readonly kind: TraceKind;
  readonly id: string;
  readonly finalStateHash?: string;
  readonly versionKey?: string;
  readonly sourceId?: string;
  readonly forkTick?: number;
}

/** A chunk that is not the history that was asked for, or contradicts frames already held. Never retried: the data is wrong, not late. */
export class HistoryIntegrityError extends Error {
  constructor(message: string) { super(message); this.name = "HistoryIntegrityError"; }
}

export const historyKey = (e: { kind: TraceKind; id: string; finalStateHash: string; versionKey: string }): string => `${e.kind}:${e.id.toLowerCase()}:${e.finalStateHash}:${e.versionKey}`;

export type MarkerKind = "candle" | "burst";
export interface HistoryMarker { readonly tick: number; readonly index: number; readonly kind: MarkerKind }

export class HistoryFrames {
  private slots: (HistoryFrameWire | undefined)[] = [];
  private held = 0;
  private ident: TraceIdentity | null = null;
  private markerList: HistoryMarker[] = [];
  /** Bumps on every accepted chunk, so a view can cheaply know the frames changed. */
  version = 0;

  readonly expected: ExpectedHistory;
  constructor(expected: ExpectedHistory) { this.expected = expected; }

  get identity(): TraceIdentity | null { return this.ident; }
  /** Last tick of the history; the nominal 2700 until the first chunk confirms it. */
  get terminalTick(): number { return this.ident?.terminalTick ?? FINAL_TICK; }
  get heldCount(): number { return this.held; }
  get complete(): boolean { return this.ident !== null && this.held === this.ident.terminalTick + 1; }
  get markers(): readonly HistoryMarker[] { return this.markerList; }

  has(tick: number): boolean { return this.slots[tick] !== undefined; }
  get(tick: number): HistoryFrameWire | undefined { return this.slots[tick]; }
  chunkLoaded(index: number): boolean {
    const from = chunkStart(index), to = Math.min(from + TRACE_MAX - 1, this.terminalTick);
    for (let t = from; t <= to; t++) if (this.slots[t] === undefined) return false;
    return true;
  }
  /** Contiguous runs of loaded ticks, [from, to] inclusive, for the rail's "ready" shading. */
  loadedRuns(): [number, number][] {
    const runs: [number, number][] = [];
    let start = -1;
    for (let t = 0; t <= this.terminalTick; t++) {
      if (this.slots[t] !== undefined) { if (start < 0) start = t; } else if (start >= 0) { runs.push([start, t - 1]); start = -1; }
    }
    if (start >= 0) runs.push([start, this.terminalTick]);
    return runs;
  }

  /** Accepts one served slice. Throws HistoryIntegrityError if it names a different history, a different version, or contradicts a held state. */
  ingest(chunk: TraceChunk): void {
    const id = chunk.identity, want = this.expected;
    if (id.kind !== want.kind || id.id.toLowerCase() !== want.id.toLowerCase()) throw new HistoryIntegrityError(`The server answered with a different ${want.kind === "child" ? "saved future" : "original"}.`);
    if (want.finalStateHash !== undefined && id.finalStateHash !== want.finalStateHash) throw new HistoryIntegrityError("This history's final-state hash differs from the one in your library.");
    if (want.versionKey !== undefined && id.versionKey !== want.versionKey) throw new HistoryIntegrityError("This history was served under different rules versions than your library lists.");
    if (want.sourceId !== undefined && id.sourceId !== null && id.sourceId.toLowerCase() !== want.sourceId.toLowerCase()) throw new HistoryIntegrityError("This future names a different source original.");
    if (want.forkTick !== undefined && id.forkTick !== want.forkTick) throw new HistoryIntegrityError("This future's fork tick differs from the one in your library.");
    const held = this.ident;
    if (held) {
      if (held.finalStateHash !== id.finalStateHash || held.versionKey !== id.versionKey || held.terminalTick !== id.terminalTick || held.forkTick !== id.forkTick || held.sourceId !== id.sourceId) {
        throw new HistoryIntegrityError("Two slices of this history disagree about its identity, so none of it is trusted.");
      }
    }
    // Validate the whole slice before storing any of it, so a bad chunk leaves the held frames untouched.
    for (const f of chunk.frames) {
      const old = this.slots[f.tick];
      if (old !== undefined && old.stateHash !== f.stateHash) throw new HistoryIntegrityError(`The state at tick ${f.tick} changed between two reads of an immutable history.`);
    }
    if (!held) { this.ident = id; this.slots = new Array<HistoryFrameWire | undefined>(id.terminalTick + 1).fill(undefined); }
    let added = 0;
    for (const f of chunk.frames) {
      if (this.slots[f.tick] !== undefined) continue;
      this.slots[f.tick] = f;
      this.held++;
      added++;
      f.events.forEach((e, index) => {
        const kind: MarkerKind | null = e.kind === "CandleCollected" ? "candle" : e.kind === "BurstManual" || e.kind === "BurstAuto" ? "burst" : null;
        if (kind) this.markerList.push({ tick: f.tick, index, kind });
      });
    }
    if (added > 0) { this.markerList.sort((a, b) => a.tick - b.tick || a.index - b.index); this.version++; }
  }
}

// ---- loader --------------------------------------------------------------------------------------------------------------------------
export interface LoadFailure { readonly text: string; readonly retryable: boolean; readonly busy: boolean; readonly integrity: boolean }
export type LoadStatus =
  | { readonly kind: "idle" }
  | { readonly kind: "loading"; readonly chunk: number; readonly attempt: number }
  | { readonly kind: "complete" }
  | { readonly kind: "failed"; readonly failure: LoadFailure };

export type ChunkFetcher = (from: number, count: number, signal: AbortSignal) => Promise<TraceChunk>;
export interface LoaderOptions {
  /** Waits before re-asking after the server answered "busy" (429). Bounded: after the last one the load stops with a retryable failure. */
  readonly busyDelaysMs?: readonly number[];
  readonly sleep?: (ms: number, signal: AbortSignal) => Promise<void>;
  /** Maps a transport error to words. Kept injectable so this file never imports UI wording. */
  readonly describe?: (error: unknown) => LoadFailure;
}

const defaultSleep = (ms: number, signal: AbortSignal) => new Promise<void>((resolve, reject) => {
  const timer = setTimeout(resolve, ms);
  signal.addEventListener("abort", () => { clearTimeout(timer); reject(signal.reason); }, { once: true });
});

export function describeLoadError(error: unknown): LoadFailure {
  if (error instanceof HistoryIntegrityError) return { text: error.message, retryable: false, busy: false, integrity: true };
  if (error instanceof SoloHttpError) {
    const t: Record<string, [string, boolean]> = {
      offline: ["The server can't be reached.", true],
      unauthorized: ["Your guest session isn't valid. Reload the page to start a fresh one.", false],
      not_found: ["That history isn't available to you. It may not be yours, or it never finished.", false],
      busy: ["The server is verifying other histories. Try again in a moment.", true],
      unavailable: ["The history service is unavailable right now.", true],
      conflict: ["The server couldn't verify this history (its stored record failed a check or uses unsupported rules).", false],
      bad_request: ["The server refused that request.", false],
      unreachable: ["The history service didn't answer as a game server would. Solo may not be enabled on this server, or its route isn't reachable from here.", false],
      malformed: ["The history service sent something this page doesn't understand.", false],
    };
    const [text, retryable] = t[error.kind] ?? ["The history could not be loaded.", false];
    return { text, retryable, busy: error.kind === "busy", integrity: false };
  }
  return { text: error instanceof Error ? error.message : String(error), retryable: false, busy: false, integrity: false };
}

export class HistoryLoader {
  private state: LoadStatus = { kind: "idle" };
  private readonly listeners = new Set<() => void>();
  private priority: number | null = null;
  private run: AbortController | null = null;
  private running = false;
  private readonly busyDelays: readonly number[];
  private readonly sleep: (ms: number, signal: AbortSignal) => Promise<void>;
  private readonly describe: (error: unknown) => LoadFailure;
  /** Requests actually sent, for the evidence that scrubbing never queries per movement. */
  requests = 0;

  readonly frames: HistoryFrames;
  private readonly fetchChunk: ChunkFetcher;

  constructor(frames: HistoryFrames, fetchChunk: ChunkFetcher, options: LoaderOptions = {}) {
    this.frames = frames;
    this.fetchChunk = fetchChunk;
    this.busyDelays = options.busyDelaysMs ?? [800, 1600, 3200];
    this.sleep = options.sleep ?? defaultSleep;
    this.describe = options.describe ?? describeLoadError;
    if (frames.complete) this.state = { kind: "complete" };
  }

  get status(): LoadStatus { return this.state; }
  subscribe = (fn: () => void): (() => void) => { this.listeners.add(fn); return () => { this.listeners.delete(fn); }; };
  private set(next: LoadStatus): void { this.state = next; for (const fn of this.listeners) fn(); }
  /** Notifies after frames changed (a chunk landed). */
  private changed(): void { for (const fn of this.listeners) fn(); }

  /** The user is looking at (or heading for) this tick: if it is not loaded, its chunk is requested next. */
  want(tick: number): void {
    if (!Number.isInteger(tick) || tick < 0 || tick > this.frames.terminalTick || this.frames.has(tick)) return;
    this.priority = chunkOf(tick);
  }

  /** Starts (or resumes after abort/failure) loading. Idempotent while running. */
  start(): void {
    if (this.running || this.frames.complete) { if (this.frames.complete && this.state.kind !== "complete") this.set({ kind: "complete" }); return; }
    this.running = true;
    this.run = new AbortController();
    void this.loop(this.run.signal);
  }
  retry(): void { if (this.state.kind === "failed" && this.state.failure.retryable) this.start(); }
  /** Stops network work (leaving the screen, switching history). Held frames stay cached; `start()` resumes. */
  abort(): void {
    this.run?.abort();
    this.run = null;
    this.running = false;
    if (this.state.kind === "loading") this.state = { kind: "idle" };
  }

  private nextChunk(): number | null {
    const total = chunkTotal(this.frames.terminalTick);
    if (this.priority !== null && this.priority < total && !this.frames.chunkLoaded(this.priority)) return this.priority;
    this.priority = null;
    for (let i = 0; i < total; i++) if (!this.frames.chunkLoaded(i)) return i;
    return null;
  }

  private async loop(signal: AbortSignal): Promise<void> {
    try {
      for (;;) {
        const index = this.nextChunk();
        if (index === null) { this.set({ kind: "complete" }); return; }
        let attempt = 0;
        for (;;) {
          if (signal.aborted) return;
          this.set({ kind: "loading", chunk: index, attempt });
          try {
            this.requests++;
            const chunk = await this.fetchChunk(chunkStart(index), chunkCount(index, this.frames.terminalTick), signal);
            if (signal.aborted) return;
            this.frames.ingest(chunk);
            this.changed();
            break;
          } catch (error) {
            if (signal.aborted) return;
            const failure = this.describe(error);
            if (failure.busy && attempt < this.busyDelays.length) { await this.sleep(this.busyDelays[attempt]!, signal); attempt++; continue; }
            this.set({ kind: "failed", failure });
            return;
          }
        }
      }
    } catch {
      // An abort rejected a sleep or a fetch; the owner already moved on.
    } finally {
      if (this.run?.signal === signal) { this.running = false; this.run = null; }
    }
  }
}

// ---- cache ---------------------------------------------------------------------------------------------------------------------------
/** At most this many histories stay in memory (about 2701 states each). The least recently opened is dropped first. */
export const CACHE_LIMIT = 6;
const cache = new Map<string, { frames: HistoryFrames; loader: HistoryLoader }>();

export interface OpenedHistory { readonly frames: HistoryFrames; readonly loader: HistoryLoader; readonly cached: boolean }

/** Returns the cached frames for this exact identity, or a new empty set with its loader. `cacheKey` must come from the library's own hash and versions. */
export function openHistory(cacheKey: string, expected: ExpectedHistory, fetchFor: (id: string) => ChunkFetcher, options?: LoaderOptions): OpenedHistory {
  const hit = cache.get(cacheKey);
  if (hit) { cache.delete(cacheKey); cache.set(cacheKey, hit); return { ...hit, cached: true }; }
  const frames = new HistoryFrames(expected);
  const loader = new HistoryLoader(frames, fetchFor(expected.id), options);
  cache.set(cacheKey, { frames, loader });
  while (cache.size > CACHE_LIMIT) {
    const oldest = cache.keys().next().value as string;
    cache.get(oldest)?.loader.abort();
    cache.delete(oldest);
  }
  return { frames, loader, cached: false };
}
export function clearHistoryCache(): void { for (const e of cache.values()) e.loader.abort(); cache.clear(); }
export function cachedHistoryCount(): number { return cache.size; }
