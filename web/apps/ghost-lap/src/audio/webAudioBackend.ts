// The browser sound engine: plain Web Audio (no new dependency; @pixi/sound is not installed and is not needed for loops, buses and end callbacks).
//
//   source(buffer) -> per-source gain (trim + fades) -> bus gain (music | sfx) -> master -> destination
//
// Why Web Audio and not HTMLAudio: a decoded buffer loops with no gap, two sources can share a bus fade, and `ended` is a real completion event
// (the night ambience sequence depends on it). The cost is that every file is fetched and decoded fully: gameLoading.wav and ghost_main_theme.wav
// are 14.8 MB and 13.4 MB of 24-bit PCM. A smaller derived runtime format is a later, verified step that must keep these masters (see doc 25).
//
// The context is created on the first real user gesture (unlock), never at import time, so the browser logs no autoplay warning and nothing
// plays early. Failed fetch/decode is remembered per cue (one console warning) and never throws into gameplay.
import type { AudioBackend, CueLoadState, PlayHandle, PlayOptions } from "./backend.ts";
import { CUES, type BusId, type CueId } from "./cues.ts";

/** Full-scale headroom for the whole mix (a single constant, not a compressor). */
const MASTER_DB = -2;
const dbToGain = (db: number): number => 10 ** (db / 20);

type ContextCtor = new (options?: AudioContextOptions) => AudioContext;
interface WithWebkit { AudioContext?: ContextCtor; webkitAudioContext?: ContextCtor }

class WebHandle implements PlayHandle {
  readonly cue: CueId;
  private readonly options: PlayOptions;
  private readonly owner: WebAudioBackend;
  private db: number;
  private stopped = false;
  private ended = false;
  private source: AudioBufferSourceNode | null = null;
  private gain: GainNode | null = null;

  constructor(cue: CueId, options: PlayOptions, owner: WebAudioBackend) {
    this.cue = cue;
    this.options = options;
    this.owner = owner;
    this.db = options.gainDb;
  }

  get live(): boolean { return !this.stopped && !this.ended; }

  /** Called when the cue is decoded (immediately if it already was). A handle stopped while decoding never starts. */
  begin(buffer: AudioBuffer, ctx: AudioContext, bus: GainNode): void {
    if (this.stopped) { this.release(); return; }
    const source = ctx.createBufferSource();
    source.buffer = buffer;
    source.loop = this.options.loop;
    const gain = ctx.createGain();
    const now = ctx.currentTime;
    const target = dbToGain(this.db);
    if (this.options.fadeInMs > 0) {
      gain.gain.setValueAtTime(0, now);
      gain.gain.linearRampToValueAtTime(target, now + this.options.fadeInMs / 1000);
    } else gain.gain.setValueAtTime(target, now);
    source.connect(gain);
    gain.connect(bus);
    source.onended = () => {
      this.ended = true;
      const natural = !this.stopped;
      this.release();
      if (natural) this.options.onEnded?.(); // stop() never reports completion; only a real end does
    };
    this.source = source;
    this.gain = gain;
    source.start();
  }

  fail(): void {
    this.ended = true; // never started, never completes: the owner shows the cue as failed
    this.release();
  }

  stop(fadeOutMs: number): void {
    if (this.stopped) return;
    this.stopped = true;
    const { source, gain } = this;
    if (!source || !gain) { this.owner.forget(this); return; }
    try {
      const ctx = this.owner.context;
      if (!ctx || fadeOutMs <= 0) { source.stop(); return; }
      const now = ctx.currentTime;
      gain.gain.cancelScheduledValues(now);
      gain.gain.setValueAtTime(gain.gain.value, now);
      gain.gain.linearRampToValueAtTime(0, now + fadeOutMs / 1000);
      source.stop(now + fadeOutMs / 1000 + 0.02);
    } catch {
      /* already ended or the context closed */
    }
  }

  setGainDb(db: number): void {
    this.db = db;
    const ctx = this.owner.context;
    if (!this.gain || !ctx || this.stopped) return;
    this.gain.gain.setTargetAtTime(dbToGain(db), ctx.currentTime, 0.03);
  }

  private release(): void {
    try { this.source?.disconnect(); this.gain?.disconnect(); } catch { /* already disconnected */ }
    this.source = null;
    this.gain = null;
    this.owner.forget(this);
  }
}

export class WebAudioBackend implements AudioBackend {
  private ctx: AudioContext | null = null;
  private master: GainNode | null = null;
  private readonly buses: Partial<Record<BusId, GainNode>> = {};
  private readonly busGain: Record<BusId, number> = { music: 1, sfx: 1 };
  private readonly buffers = new Map<CueId, Promise<AudioBuffer>>();
  private readonly states = new Map<CueId, CueLoadState>();
  private readonly handles = new Set<WebHandle>();
  private readonly listeners = new Set<() => void>();
  private unlocking: Promise<boolean> | null = null;
  private disposed = false;

  get context(): AudioContext | null { return this.ctx; }
  get unlocked(): boolean { return !this.disposed && this.ctx !== null && this.ctx.state === "running"; }

  onChange(listener: () => void): () => void {
    this.listeners.add(listener);
    return () => { this.listeners.delete(listener); };
  }
  private emit(): void { for (const l of this.listeners) l(); }

  unlock(): Promise<boolean> {
    if (this.disposed) return Promise.resolve(false);
    this.unlocking ??= this.doUnlock().finally(() => { this.unlocking = null; });
    return this.unlocking;
  }

  private async doUnlock(): Promise<boolean> {
    try {
      if (!this.ctx) this.create();
      const ctx = this.ctx!;
      if (ctx.state !== "running") await ctx.resume();
      return ctx.state === "running";
    } catch (error) {
      console.warn("Ghost Lap audio: the browser did not allow sound yet", error);
      return false;
    }
  }

  private create(): void {
    const w = window as unknown as WithWebkit;
    const Ctor = w.AudioContext ?? w.webkitAudioContext;
    if (!Ctor) throw new Error("Web Audio is not available in this browser");
    const ctx = new Ctor({ latencyHint: "interactive" });
    const master = ctx.createGain();
    master.gain.value = dbToGain(MASTER_DB);
    master.connect(ctx.destination);
    for (const bus of ["music", "sfx"] as const) {
      const node = ctx.createGain();
      node.gain.value = this.busGain[bus];
      node.connect(master);
      this.buses[bus] = node;
    }
    ctx.onstatechange = () => this.emit();
    this.ctx = ctx;
    this.master = master;
  }

  setBusGain(bus: BusId, gain: number, rampMs: number): void {
    this.busGain[bus] = gain;
    const node = this.buses[bus];
    const ctx = this.ctx;
    if (!node || !ctx) return;
    const now = ctx.currentTime;
    node.gain.cancelScheduledValues(now);
    node.gain.setValueAtTime(node.gain.value, now);
    node.gain.linearRampToValueAtTime(gain, now + Math.max(0, rampMs) / 1000);
  }

  cueState(cue: CueId): CueLoadState { return this.states.get(cue) ?? "idle"; }

  private setState(cue: CueId, state: CueLoadState): void {
    this.states.set(cue, state);
    this.emit();
  }

  /** Fetch + decode once per cue. A failure is cached (the rejected promise) so it is reported once and never retried in a loop. */
  private load(cue: CueId): Promise<AudioBuffer> {
    const known = this.buffers.get(cue);
    if (known) return known;
    const ctx = this.ctx;
    if (!ctx) return Promise.reject(new Error("audio is not unlocked"));
    this.setState(cue, "loading");
    const promise = fetch(CUES[cue].url)
      .then((res) => {
        if (!res.ok) throw new Error(`${res.status} ${res.statusText}`);
        return res.arrayBuffer();
      })
      .then((data) => ctx.decodeAudioData(data))
      .then(
        (buffer) => { this.setState(cue, "ready"); return buffer; },
        (error: unknown) => {
          console.warn(`Ghost Lap audio: ${CUES[cue].url} could not be loaded or decoded`, error);
          this.setState(cue, "failed");
          throw error;
        },
      );
    promise.catch(() => undefined); // the rejection is handled for callers that did not attach their own
    this.buffers.set(cue, promise);
    return promise;
  }

  preload(order: readonly CueId[]): void {
    if (!this.ctx || this.disposed) return;
    void (async () => {
      for (const cue of order) {
        if (this.disposed) return;
        try { await this.load(cue); } catch { /* reported by load(); continue with the next cue */ }
      }
    })();
  }

  play(cue: CueId, options: PlayOptions): PlayHandle {
    const handle = new WebHandle(cue, options, this);
    const ctx = this.ctx;
    const bus = this.buses[options.bus];
    if (!ctx || !bus || this.disposed) { handle.fail(); return handle; }
    this.handles.add(handle);
    this.load(cue).then(
      (buffer) => { if (this.disposed) handle.fail(); else handle.begin(buffer, ctx, bus); },
      () => handle.fail(),
    );
    return handle;
  }

  /** @internal */
  forget(handle: WebHandle): void { this.handles.delete(handle); }

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    for (const h of [...this.handles]) h.stop(0);
    this.handles.clear();
    this.listeners.clear();
    this.master?.disconnect();
    void this.ctx?.close().catch(() => undefined);
    this.ctx = null;
  }
}
