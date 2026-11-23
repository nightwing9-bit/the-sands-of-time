// Soulfire Burst world distortion (skill ref 06 B): ONE pure ShockwaveFilter (pixi-filters 6, Pixi v8) centred on the authoritative Burst
// position and applied to the game view only (the world container; the DOM HUD is never touched).
//
// The distortion is the hero and is unchanged. The only addition is a faint cyan accent on the wave's leading rim, computed INSIDE the same
// shader pass (see shockwaveTint.ts; values in SHOCKWAVE.tint), so there is still one filter, no drawn ring, no sparks, no RGB split, no blur,
// no camera movement. The emitting head's halo pulse (CharacterView.pulseHalo) is the only companion and lives on the head, not here.
//
// Lifecycle: the filter is added to the world's FilterStack when a Burst starts and removed when it ends, so an idle arena has no filter and
// no offscreen pass. One filter is reused for every Burst. If another Burst arrives while one is running, the new one replaces it (a single
// shared distortion; sound and results stay authoritative). cancel() and dispose() leave the world exactly as before.
//
// Units: pixi-filters measures centre, radius, wavelength, amplitude and speed in CSS pixels of the filter's input rectangle. The filter area
// is the world rectangle, so world units * world scale = filter pixels, with no other offset.
import { GlProgram, Ticker } from "pixi.js";
import { ShockwaveFilter } from "pixi-filters/shockwave";
import type { ArenaWorld } from "../stage/ArenaWorld.ts";
import { SHOCKWAVE, type ShockwavePreset } from "./effectPresets.ts";
import { prefersReducedMotion } from "./motion.ts";
import { tintShockwaveFragment } from "./shockwaveTint.ts";
import { shockwaveAt, shockwaveEndRadius } from "./timeline.ts";

/** Nominal speed given to the filter; the radius is driven through `time` (time = radius / speed) so the ease is ours, not constant speed. */
const SPEED_PX = 100;
const WARM_FRAMES = 2;

export class ShockwaveFx {
  private readonly filter: ShockwaveFilter;
  private elapsedMs = 0;
  private running = false;
  private warm = WARM_FRAMES;
  private warmAdded = false;
  private x = 0;
  private y = 0;
  private disposed = false;
  private readonly onTick = (t: Ticker) => this.update(t.deltaMS);
  /** How many Bursts have started in this object's lifetime (the preview and tests read it; it is not game state). */
  started = 0;

  constructor(private readonly world: ArenaWorld, private readonly preset: ShockwavePreset = SHOCKWAVE) {
    this.filter = new ShockwaveFilter({ amplitude: 0, wavelength: preset.wavelength, brightness: preset.brightness, speed: SPEED_PX, radius: -1, time: 0, center: { x: 0, y: 0 } });
    this.addRimTint();
    world.ticker.add(this.onTick);
  }

  /** Swaps the stock fragment for the same one plus the rim accent. Same uniforms, same vertex shader. Keeps the stock shader if it ever differs. */
  private addRimTint(): void {
    const stock = this.filter.glProgram;
    const vertex = stock?.vertex;
    const fragment = stock?.fragment ? tintShockwaveFragment(stock.fragment, this.preset.tint) : null;
    if (!vertex || !fragment) { console.warn("ShockwaveFx: stock shockwave shader changed; rim tint skipped."); return; }
    this.filter.glProgram = GlProgram.from({ vertex, fragment, name: "shockwave-rim-tint-filter" });
  }

  get active(): boolean { return this.running; }
  /** Filters currently on the game view (all effects together). 0 when nothing is playing. */
  get filtersOnWorld(): number { return this.world.fxStack.count; }

  /**
   * Starts one distortion at an authoritative world position (world units, server frame). Returns false when nothing was started
   * (reduced motion: the halo pulse and the face carry the Burst; or the effect was disposed).
   */
  trigger(worldX: number, worldY: number): boolean {
    if (this.disposed || prefersReducedMotion()) return false;
    this.x = worldX;
    this.y = worldY;
    this.elapsedMs = 0;
    this.running = true;
    this.started += 1;
    this.writeUniforms(0);
    this.world.fxStack.add(this.filter);
    return true;
  }

  /** Stops at once and removes the filter (new match, route change, cancel). Safe to call at any time. */
  cancel(): void {
    this.running = false;
    this.elapsedMs = 0;
    this.warm = 0;
    this.world.fxStack.remove(this.filter);
  }

  private writeUniforms(elapsedMs: number): void {
    const scale = this.world.transform.scale;
    const s = shockwaveAt(elapsedMs, this.preset);
    const f = this.filter;
    f.centerX = this.x * scale;
    f.centerY = this.y * scale;
    f.amplitude = this.preset.amplitude * scale;
    f.wavelength = this.preset.wavelength * scale;
    f.radius = shockwaveEndRadius(this.preset) * scale;
    f.time = (s.radius * scale) / SPEED_PX;
  }

  private update(dtMs: number): void {
    if (this.disposed) return;
    // First frames of the arena's life: put the (inert) filter through the renderer once so the shader is compiled before the first real Burst.
    if (!this.running && this.warm > 0) {
      if (!this.warmAdded) {
        this.warmAdded = true;
        this.filter.amplitude = 0;
        this.filter.time = 0;
        this.world.fxStack.add(this.filter);
      } else if (--this.warm === 0) {
        this.world.fxStack.remove(this.filter);
      }
      return;
    }
    if (!this.running) return;
    this.elapsedMs += Math.min(dtMs, 50); // a stalled frame never skips the wave
    if (this.elapsedMs >= this.preset.durationMs) {
      this.cancel();
      return;
    }
    this.writeUniforms(this.elapsedMs);
  }

  dispose(): void {
    if (this.disposed) return;
    this.cancel();
    this.disposed = true;
    this.world.ticker.remove(this.onTick);
    this.filter.destroy();
  }
}
