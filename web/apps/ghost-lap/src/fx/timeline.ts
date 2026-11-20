// PURE timing for the 13.6 effects (no Pixi, no DOM, no clock): everything takes elapsed milliseconds, so the rules are testable and the
// Pixi owners stay thin. Nothing here changes game state.
import { BURST_REACH_UNITS, CRT, HALO_PULSE, SHOCKWAVE, SMOKE, type CrtPreset, type HaloPulsePreset, type ShockwavePreset } from "./effectPresets.ts";

const clamp01 = (v: number) => (v < 0 ? 0 : v > 1 ? 1 : v);
export const easeOutCubic = (t: number) => 1 - (1 - clamp01(t)) ** 3;

export interface ShockwaveState {
  readonly active: boolean;
  /** Distance of the ring centre from the Burst position, world units. */
  readonly radius: number;
  /** Largest ring-centre radius of this effect (the filter's own fade reaches 0 there), world units. */
  readonly endRadius: number;
  /** Farthest edge of the ring at this instant, world units. */
  readonly front: number;
}
/** Ring centre ends half a wavelength inside the server reach, so the visible front never passes it. */
export function shockwaveEndRadius(p: ShockwavePreset = SHOCKWAVE): number { return BURST_REACH_UNITS - p.wavelength / 2; }
export function shockwaveAt(elapsedMs: number, p: ShockwavePreset = SHOCKWAVE): ShockwaveState {
  const endRadius = shockwaveEndRadius(p);
  const active = elapsedMs >= 0 && elapsedMs < p.durationMs;
  const radius = p.startRadius + (endRadius - p.startRadius) * easeOutCubic(elapsedMs / p.durationMs);
  return { active, radius, endRadius, front: radius + p.wavelength / 2 };
}

/** Halo pulse envelope 0..1: fast rise, then an eased fall to 0 (the head then shows the actual charge halo again). */
export function haloPulseAt(elapsedMs: number, p: HaloPulsePreset = HALO_PULSE): number {
  if (elapsedMs <= 0) return 0;
  if (elapsedMs < p.attackMs) return elapsedMs / p.attackMs;
  const d = (elapsedMs - p.attackMs) / p.decayMs;
  return d >= 1 ? 0 : (1 - d) ** 2;
}
export const haloPulseLength = (p: HaloPulsePreset = HALO_PULSE) => p.attackMs + p.decayMs;

/** CRT interference strength 0..1: ramps up, then down; 0 outside [0, durationMs]. Smooth, never a strobe. */
export function crtEnvelope(elapsedMs: number, p: CrtPreset = CRT): number {
  if (elapsedMs <= 0 || elapsedMs >= p.durationMs) return 0;
  const up = clamp01(elapsedMs / p.attackMs);
  const down = clamp01((p.durationMs - elapsedMs) / p.releaseMs);
  const s = (t: number) => t * t * (3 - 2 * t);
  return Math.min(s(up), s(down));
}

export interface SmokeMeta {
  readonly firstVisible: number;
  readonly lastVisible: number;
  /** First timeline frame whose whole card is exactly opaque, and the last one that still is. A scene swap is invisible anywhere in between. */
  readonly opaqueFrom: number;
  readonly opaqueTo: number;
}
/**
 * Timeline index shown `elapsedMs` after the smoke starts. Playback starts at the first VISIBLE frame (the exporter's empty leading frames
 * are dead time) and ends after the last visible one; null before the start / after the end.
 */
export function smokeFrameAt(elapsedMs: number, meta: SmokeMeta, fps: number = SMOKE.fps): number | null {
  if (elapsedMs < 0) return null;
  const frame = meta.firstVisible + Math.floor((elapsedMs * fps) / 1000);
  return frame > meta.lastVisible ? null : frame;
}
export interface SmokeSchedule { readonly totalMs: number; readonly swapAtMs: number; readonly clearAtMs: number }
/** Milliseconds after the smoke starts: when the card first is fully opaque (swap here), and when it stops being. */
export function smokeSchedule(meta: SmokeMeta, fps: number = SMOKE.fps): SmokeSchedule {
  const ms = (frames: number) => (frames * 1000) / fps;
  return {
    totalMs: ms(meta.lastVisible - meta.firstVisible + 1),
    swapAtMs: ms(meta.opaqueFrom - meta.firstVisible),
    clearAtMs: ms(meta.opaqueTo + 1 - meta.firstVisible),
  };
}

/** Reduced-motion cover alpha: one smooth fade in, a short hold (the swap happens at 1), one smooth fade out. */
export function reducedCoverAlpha(elapsedMs: number, fadeMs: number, holdMs: number): number {
  if (elapsedMs <= 0) return 0;
  if (elapsedMs < fadeMs) return elapsedMs / fadeMs;
  if (elapsedMs < fadeMs + holdMs) return 1;
  const t = (elapsedMs - fadeMs - holdMs) / fadeMs;
  return t >= 1 ? 0 : 1 - t;
}
