// ONE module for the day/night look (skill refs 05/06: "LightingPresets"). Every number is visual tuning from a screenshot, NOT game truth:
// candle light radius is never a pickup radius (the server's candle collider is 24x24).
//
// Composition, bottom to top (src/stage/ArenaWorld.ts):
//   environment  snow + walls, multiplied by `environmentTint` (the night grade). Nothing else is tinted: not heads, not the ghost, not flames.
//   lights       one soft radial pool per ACTIVE candle, all sharing one generated texture.
//   actors       candles (flames), ghost, living heads. React HUD is DOM and is never touched.
//
// Night grade is a multiply, not a filter: it costs nothing per frame (no offscreen pass), keeps the baked monster shadow (the multiply keeps
// every ratio in the snow), and avoids ColorMatrixFilter.night() which is green night-vision.
// The warm pool is ADDITIVE at night. To make warm light on a blue-graded floor the pool colour is (warm white - night tint): lit snow then ends
// up near the warm white instead of greying out; in the day the same pool is a faint normal-blend orange wash.
export type EnvironmentMode = "day" | "night";

export interface PoolPreset {
  /** World units. Soft all the way to zero; there is no hard edge. */
  readonly radius: number;
  readonly blend: "normal" | "add";
  readonly color: number;
  /** Peak strength at the candle (before the per-frame flicker gain). */
  readonly alpha: number;
  /** The pool is centred a little below the flame centre, where the wax would be. World units. */
  readonly dy: number;
}

export interface LightingPreset {
  readonly environmentTint: number;
  readonly pool: PoolPreset;
}

export const LIGHTING: Readonly<Record<EnvironmentMode, LightingPreset>> = {
  day: {
    environmentTint: 0xffffff,
    pool: { radius: 64, blend: "normal", color: 0xffb347, alpha: 0.2, dy: 6 },
  },
  night: {
    // Deep midnight blue (13.5 tuning pass; was 0x566a94 cool slate). Snow detail and the giant monster shadow stay readable.
    environmentTint: 0x384a7a,
    // (1.0, 0.82, 0.58) warm white minus the tint (0.22, 0.29, 0.48) = (0.78, 0.53, 0.10). Lit snow still ends near the same warm white.
    pool: { radius: 112, blend: "add", color: 0xc7871a, alpha: 0.95, dy: 6 },
  },
};

/** World units per pixel of the ORIGINAL candle sheet. The flame is about 100 source px tall: 0.36 makes it roughly 36 units, half a head. */
export const CANDLE_UNITS_PER_SOURCE_PX = 0.36;

/** Telegraph marker (30 ticks before a candle can be collected): a thin warm ring, static. No flame and no light until it is active. */
export const TELEGRAPH_RING = { radius: 20, width: 2, color: 0xe9a23b, alpha: 0.8 } as const;

/** Spectral ghost palette and edge (skill ref 06 section C seeds; tuned by eye in the night study). */
export const SPECTRAL = {
  dark: 0x1d516a,
  mid: 0x62b9c5,
  high: 0xe8ffff,
  edge: 0x83e4eb,
  /** Body alpha: translucent but never invisible on pale snow. */
  bodyAlpha: 0.66,
  /** Outer edge strength and its width in world units. */
  edgeAlpha: 0.7,
  edgeUnits: 1.6,
} as const;
