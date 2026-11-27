// Visual-only smoothing of authoritative positions. PURE (no Pixi, no DOM, no clock): the caller passes local monotonic milliseconds.
//
// Authority stays with the server. Nothing here predicts a collision, a pickup or a score, and a drawn position is never sent anywhere.
//   RemoteTrack     other players: render slightly in the past and interpolate between real snapshots (15 Hz multiplayer feed).
//   LocalSmoother   the locally controlled player: ease toward the newest AUTHORITATIVE position, no extrapolation past it. A drawn point
//                   therefore always lies on a path the server confirmed, so a head cannot visibly cross a wall that the server stopped it at.
// A jump larger than any legal movement (restore, reset, long stall) snaps instead of sliding across the arena.

/** Largest legal move per server tick is 10 world units (cardinal, 10240 fixed). Anything beyond a few ticks of that is a discontinuity. */
export const MAX_UNITS_PER_TICK = 10;

export interface Vec { x: number; y: number }

interface Sample { readonly t: number; readonly x: number; readonly y: number }

/** Estimates how local time relates to server time from arrival stamps (lower envelope: a fast packet shows the true offset, slow ones are jitter). */
export class ServerClock {
  private offset: number | null = null;
  /** `serverMs` is tick * 1000 / tickRate. Returns nothing; read `toServerMs`. */
  observe(serverMs: number, receivedAtMs: number): void {
    const sample = receivedAtMs - serverMs;
    if (this.offset === null || sample < this.offset) this.offset = sample;
    else this.offset += (sample - this.offset) * 0.02; // slow upward drift so clock skew cannot pin the estimate forever
  }
  reset(): void { this.offset = null; }
  get ready(): boolean { return this.offset !== null; }
  toServerMs(localMs: number): number { return localMs - (this.offset ?? 0); }
}

export interface RemoteOptions {
  /** Smallest/largest render delay (ms). The delay is 1.5x the observed gap between snapshots, clamped to this range. */
  readonly minDelayMs: number;
  readonly maxDelayMs: number;
  readonly snapDistance: number;
  readonly maxSamples: number;
}
export const REMOTE_DEFAULTS: RemoteOptions = { minDelayMs: 66, maxDelayMs: 200, snapDistance: 8 * MAX_UNITS_PER_TICK, maxSamples: 48 };

export class RemoteTrack {
  private samples: Sample[] = [];
  private gapMs = 66;
  private readonly clock: ServerClock;
  private readonly options: RemoteOptions;
  constructor(clock: ServerClock, options: RemoteOptions = REMOTE_DEFAULTS) {
    this.clock = clock;
    this.options = options;
  }
  push(tick: number, tickRate: number, x: number, y: number): void {
    const t = (tick * 1000) / tickRate;
    const last = this.samples[this.samples.length - 1];
    if (last && t <= last.t) {
      if (t < last.t) this.samples = []; // the tick went backwards: a new run or a resync baseline; never blend across it
      else return; // duplicate tick
    }
    const prev = this.samples[this.samples.length - 1];
    if (prev) this.gapMs = t - prev.t;
    this.samples.push({ t, x, y });
    if (this.samples.length > this.options.maxSamples) this.samples.shift();
  }
  clear(): void { this.samples = []; }
  get delayMs(): number { return Math.min(this.options.maxDelayMs, Math.max(this.options.minDelayMs, this.gapMs * 1.5)); }
  /** The position to draw at local time `nowMs`, or null before the first snapshot. Never beyond the newest snapshot. */
  sample(nowMs: number): Vec | null {
    const n = this.samples.length;
    if (n === 0) return null;
    const first = this.samples[0]!;
    const last = this.samples[n - 1]!;
    const t = this.clock.toServerMs(nowMs) - this.delayMs;
    if (t <= first.t) return { x: first.x, y: first.y };
    if (t >= last.t) return { x: last.x, y: last.y };
    for (let i = n - 1; i > 0; i--) {
      const b = this.samples[i]!;
      const a = this.samples[i - 1]!;
      if (t >= a.t) {
        if (Math.hypot(b.x - a.x, b.y - a.y) > this.options.snapDistance) return { x: a.x, y: a.y }; // discontinuity: hold, then jump at b.t
        const k = (t - a.t) / (b.t - a.t);
        return { x: a.x + (b.x - a.x) * k, y: a.y + (b.y - a.y) * k };
      }
    }
    return { x: last.x, y: last.y };
  }
}

export interface LocalPreset { readonly label: string; readonly tauMs: number }
/** Presets the player can compare while playing. "raw" draws exactly the newest authoritative position. */
export const LOCAL_PRESETS = {
  raw: { label: "Raw", tauMs: 0 },
  light: { label: "Light", tauMs: 35 },
  smooth: { label: "Smooth", tauMs: 70 },
} as const satisfies Record<string, LocalPreset>;
export type LocalPresetId = keyof typeof LOCAL_PRESETS;

export class LocalSmoother {
  private pos: Vec | null = null;
  private target: Vec | null = null;
  private tauMs: number;
  private readonly snapDistance: number;
  constructor(tauMs: number, snapDistance = 8 * MAX_UNITS_PER_TICK) {
    this.tauMs = tauMs;
    this.snapDistance = snapDistance;
  }
  setTau(tauMs: number): void { this.tauMs = tauMs; }
  setTarget(x: number, y: number): void {
    this.target = { x, y };
    if (!this.pos) this.pos = { x, y };
  }
  /** Drop the drawn position (new run, restart). The next target is drawn exactly. */
  reset(): void { this.pos = null; this.target = null; }
  update(dtMs: number): Vec | null {
    if (!this.pos || !this.target) return null;
    const dx = this.target.x - this.pos.x, dy = this.target.y - this.pos.y;
    if (this.tauMs <= 0 || Math.hypot(dx, dy) > this.snapDistance) {
      this.pos = { x: this.target.x, y: this.target.y };
    } else {
      const k = 1 - Math.exp(-Math.max(0, dtMs) / this.tauMs);
      this.pos = { x: this.pos.x + dx * k, y: this.pos.y + dy * k };
    }
    return this.pos;
  }
  /** Distance between what is drawn and the newest authoritative position: the visual lag, in world units. */
  get lag(): number { return this.pos && this.target ? Math.hypot(this.target.x - this.pos.x, this.target.y - this.pos.y) : 0; }
}
