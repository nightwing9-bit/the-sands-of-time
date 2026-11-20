// Which candle-sheet frame a candle shows. PURE (no Pixi, no JSON import) so it is testable with the plain Node runner; CandleField feeds it
// the loop arrays from candleAtlas.json.
//
// Presentation only. A candle exists, appears and vanishes only because the server frame lists it (ActiveCandleIds + the verified plan). The
// phase offset below is derived from the candle's OPAQUE id purely to de-synchronise the flames; it is never parsed as a site number and it
// never decides anything about the game.

/** FNV-1a over the id's UTF-16 units. Stable across runs, platforms and day/night, so a candle keeps its flicker phase through a mode change. */
export function phaseOffsetFrames(candleId: string, loopLength: number): number {
  let h = 0x811c9dc5;
  for (let i = 0; i < candleId.length; i++) h = Math.imul(h ^ candleId.charCodeAt(i), 0x01000193) >>> 0;
  return loopLength > 0 ? h % loopLength : 0;
}

/** Position inside a loop of `length` frames at `nowMs` for a candle with `offset`, at `fps`. `reverse` plays the same loop backwards (study only). */
export function loopPosition(nowMs: number, offset: number, length: number, fps: number, reverse = false): number {
  if (length <= 0) return 0;
  const step = Math.floor((nowMs * fps) / 1000);
  const p = (reverse ? offset - step : offset + step) % length;
  return p < 0 ? p + length : p;
}

/** The atlas frame index to show: the loop's frame at that position. */
export function frameAt(loop: readonly number[], nowMs: number, offset: number, fps: number, reverse = false): number {
  return loop[loopPosition(nowMs, offset, loop.length, fps, reverse)] ?? 0;
}

/** Warm-pool strength multiplier for a frame: the frame's measured flame mass, softened so a flash lifts the light without blinding it. */
export function poolGain(rawGain: number, exponent = 0.5, max = 1.4): number {
  return Math.min(max, Math.pow(Math.max(rawGain, 0), exponent));
}
