// ONE module for every transient effect number (skill ref 06: "All presets live in one effect-config module"). Pure data, no Pixi.
// Every value is VISUAL tuning from the 13.6 preview, never game truth. The only number tied to authority is BURST_REACH_UNITS.
//
// What is deliberately NOT here (ref 06 B/I): no drawn ring, no sparks, no RGB split, no camera shake or zoom, no body blur, no bloom.
// The Burst is a displacement of world pixels (the hero) plus a faint cyan accent on the wave's leading rim (SHOCKWAVE.tint, added after the
// original approval to read better on snow), plus the emitting head's own existing halo.

/** GameRulesV1.BurstRadiusFixed = 160 * 1024 fixed units = 160 world units between collider centres. The wave's visible front never exceeds it. */
export const BURST_REACH_UNITS = 160;

/** Faint cool accent folded into the SAME shockwave pass (no second filter, no drawn shape). It follows the wave's own band and fade. */
export interface ShockwaveTint {
  /** Linear RGB 0..1 of the accent. Pale cyan-blue, deliberately not saturated. */
  readonly color: readonly [number, number, number];
  /** Peak amount the world pixels are multiplied toward `color` at the rim, 0..1. Kept low: 0.3 already turns white snow clearly pale cyan. */
  readonly strength: number;
  /** Tiny additive lift at the rim so the accent still shows over dark (night) pixels, where a multiply would vanish. */
  readonly lift: number;
  /** Where the accent peaks across the ring band: -1 = inner edge, 0 = ring centre, +1 = outer (leading) edge. */
  readonly rimAt: number;
  /** Half width of the accent in the same -1..1 band units. rimAt + halfWidth must be <= 1 so the accent is exactly 0 at the band's edge (no seam). */
  readonly halfWidth: number;
  /** Extra decay: the accent fades with (filter fade)^fadePower, so it dies sooner than the distortion (1 = same pace). */
  readonly fadePower: number;
}
export interface ShockwavePreset {
  /** Whole effect, ms of visual time (the ring is outward-only, 0 at the head edge -> reach). */
  readonly durationMs: number;
  /** Distance of the ring's CENTRE from the Burst position at the first frame, world units. Inner edge = this - wavelength/2 stays clear of the emitting head. */
  readonly startRadius: number;
  /** Ring width, world units. */
  readonly wavelength: number;
  /** Peak pixel displacement, world units (scaled to screen pixels at use). */
  readonly amplitude: number;
  /** Neutral. The filter's own brightness stays 1; the colour accent is `tint`. */
  readonly brightness: number;
  readonly tint: ShockwaveTint;
}
export const SHOCKWAVE: ShockwavePreset = {
  durationMs: 640,
  startRadius: 62,
  wavelength: 52,
  amplitude: 15,
  brightness: 1,
  tint: { color: [0.40, 0.74, 1], strength: 1.0, lift: 0.05, rimAt: 0.4, halfWidth: 0.6, fadePower: 1.4 },
};

export interface HaloPulsePreset {
  readonly attackMs: number;
  readonly decayMs: number;
  /** Alpha reached at the top of the pulse (the existing halo sprite, never a second shape). */
  readonly peak: number;
  /** Extra scale of that same sprite at the top of the pulse (1 + gain). */
  readonly scaleGain: number;
}
export const HALO_PULSE: HaloPulsePreset = { attackMs: 45, decayMs: 560, peak: 1, scaleGain: 0.16 };
/** Reduced motion: opacity only, softer attack, no scale change, lower peak. Same state, no sudden jump. */
export const HALO_PULSE_REDUCED: HaloPulsePreset = { attackMs: 140, decayMs: 420, peak: 0.7, scaleGain: 0 };

export interface CrtPreset {
  readonly durationMs: number;
  readonly attackMs: number;
  readonly releaseMs: number;
  /** Peak static amount (CRTFilter `noise`, 0..1). Restrained: about half the filter's own default. */
  readonly noise: number;
  readonly noiseSizePx: number;
  readonly lineContrast: number;
  /** Interference pattern changes this many times a second at most (a slow shimmer, not a strobe). */
  readonly seedHz: number;
  /** When the scene may be swapped in a CRT-only transition (fraction of durationMs; the interference is strongest here). */
  readonly swapAt: number;
}
export const CRT: CrtPreset = { durationMs: 1420, attackMs: 140, releaseMs: 420, noise: 0.2, noiseSizePx: 2, lineContrast: 0.25, seedHz: 12, swapAt: 0.45 };

export const SMOKE = {
  /** Playback rate. The skill's nominal 30 fps was slowed to 22 in the 13.6 refinement (70 frames: 2.33 s -> 3.18 s); the exporter gave none. One number to change if the pace feels wrong. */
  fps: 22,
  /** Smoke starts this long after the CRT interference begins, so the static is already on screen when the first puffs appear. */
  startDelayMs: 120,
  /** The card rectangle is mapped to the canvas slightly oversized so its soft 2 px edge and the atlas inset never reach the screen. */
  overscan: 1.02,
  /** Neutral cover colour of the reduced-motion / fallback fade: the smoke's own mean pale card colour (213,222,236). */
  coverColor: 0xd5deec,
} as const;

/** Reduced motion: one opacity shift each way. */
export const REDUCED_FADE_MS = 160;
export const REDUCED_HOLD_MS = 40;
