// Cyan rim accent for the Burst shockwave. Pure string work (no Pixi, no DOM) so it is unit tested.
//
// pixi-filters 6.1.5's shockwave fragment ends with `color.rgb *= 1.0 + (uBrightness - 1.0) * p * fade;` (a brightness multiply that is a
// no-op at 1). That one line is replaced; every line of the distortion (band, displacement, clamp, fade) is kept byte for byte. Inside the
// ring band `diff` runs -1 (inner edge) .. +1 (outer edge) and `fade` is the filter's own 1 -> 0 decay, both already in scope there.
import type { ShockwaveTint } from "./effectPresets.ts";

/** The exact line of the stock shader that is replaced. Pinned to pixi-filters 6.1.5; if a future upgrade changes it, the patch refuses. */
export const BRIGHTNESS_LINE = "color.rgb *= 1.0 + (uBrightness - 1.0) * p * fade;";

const f = (n: number) => n.toFixed(4);

/** The GLSL that replaces BRIGHTNESS_LINE: the original brightness line, then the rim accent. */
export function tintSnippet(t: ShockwaveTint): string {
  const [r, g, b] = t.color;
  return [
    BRIGHTNESS_LINE,
    // Bell over the band, exactly 0 at diff = rimAt +/- halfWidth (<= the band edge), squared so the colour hugs the rim.
    `float rimT = max(0.0, 1.0 - pow((diff - ${f(t.rimAt)}) / ${f(t.halfWidth)}, 2.0));`,
    `float tintK = rimT * rimT * pow(fade, ${f(t.fadePower)});`,
    `vec3 tintC = vec3(${f(r)}, ${f(g)}, ${f(b)});`,
    // Multiply keeps the snow/wall detail underneath (a glass tint, not a paint layer); the small lift keeps it visible on dark pixels.
    `color.rgb *= mix(vec3(1.0), tintC, ${f(t.strength)} * tintK);`,
    `color.rgb += tintC * ${f(t.lift)} * tintK * color.a;`,
  ].join("\n    ");
}

/** Returns the stock fragment with the rim accent added, or null when the stock shader is not the expected one (caller keeps the stock). */
export function tintShockwaveFragment(fragment: string, t: ShockwaveTint): string | null {
  if (!fragment.includes(BRIGHTNESS_LINE)) return null;
  return fragment.replace(BRIGHTNESS_LINE, tintSnippet(t));
}
