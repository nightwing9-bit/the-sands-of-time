// The seam between the AudioDirector (rules, no browser) and a sound engine (browser Web Audio, or a fake in tests).
import type { BusId, CueId } from "./cues.ts";

export type CueLoadState = "idle" | "loading" | "ready" | "failed";

export interface PlayOptions {
  readonly bus: BusId;
  readonly loop: boolean;
  /** Per-source trim in dB (CUES[cue].trimDb, or a dev override). */
  readonly gainDb: number;
  readonly fadeInMs: number;
  /** Called ONLY when the source reached its natural end. Never after stop(), never for loops. This is what sequences the night ambience. */
  readonly onEnded?: () => void;
}

export interface PlayHandle {
  readonly cue: CueId;
  /** True from play() until the source ended naturally or stop() was called. */
  readonly live: boolean;
  /** Fade out and release. Idempotent. A stopped handle never calls onEnded. */
  stop(fadeOutMs: number): void;
  setGainDb(db: number): void;
}

export interface AudioBackend {
  /** True when sound can actually be heard now (context running after a user gesture). */
  readonly unlocked: boolean;
  /** Create/resume the engine. Must be called from a real user gesture. Resolves to `unlocked`. */
  unlock(): Promise<boolean>;
  /** Bus level as a linear gain. Remembered before unlock and applied when the engine exists. */
  setBusGain(bus: BusId, gain: number, rampMs: number): void;
  /** Fetch and decode in this order, one at a time. No-op before unlock. */
  preload(order: readonly CueId[]): void;
  cueState(cue: CueId): CueLoadState;
  /** Start a source. If the cue is still decoding it starts when ready, unless stopped first. */
  play(cue: CueId, options: PlayOptions): PlayHandle;
  /** Unlock, context-state or load-state changed. */
  onChange(listener: () => void): () => void;
  dispose(): void;
}
