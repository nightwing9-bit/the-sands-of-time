// Reusable scene-swap transition (skill ref 06 F/G): a short CRT interference on the game view and/or the smoke sheet covering the canvas, with
// ONE hook, `onCovered`, that fires while the scene is fully hidden. It knows nothing about night, history or forks: the caller decides what to
// swap. The historical rewind montage is NOT here (13.11 owns it).
//
//   CRT    pixi-filters CRTFilter on the GAME VIEW only (the world container, through the shared FilterStack; never the DOM HUD), 420 ms,
//          smooth ramp up and down, the static re-seeds at most 12 times a second. No vignette, no curvature, no tint.
//   smoke  the verified atlas (smokeAtlas.json): the flat card of every frame is mapped to the whole canvas. Playback starts at the first visible
//          frame; the scene is swapped when the card is exactly opaque (frames 28 to 45), then the smoke clears. Removed from the tree afterwards.
//   reduced motion (or smoke textures missing): no filter and no animation. A flat slate cover fades in over 160 ms, the swap happens at full
//          cover, it fades out. Same state, same hook, nothing that flashes.
//
// Lifetime: cancel() ends it at once with no swap callback (a swap that already happened is the caller's); dispose() also frees everything.
import { Graphics, Sprite, Texture, type Ticker } from "pixi.js";
import { CRTFilter } from "pixi-filters/crt";
import type { ArenaWorld } from "../stage/ArenaWorld.ts";
import { CRT, REDUCED_FADE_MS, REDUCED_HOLD_MS, SMOKE } from "./effectPresets.ts";
import { prefersReducedMotion } from "./motion.ts";
import { SMOKE_FRAME_H, SMOKE_FRAME_W, SMOKE_TIMELINE, type SmokeTextures } from "./SmokeAssets.ts";
import { crtEnvelope, reducedCoverAlpha, smokeFrameAt, smokeSchedule } from "./timeline.ts";

export type TransitionEnd = "complete" | "cancelled";
export interface TransitionRequest {
  /** Static interference on the game view. Default true. */
  readonly crt?: boolean;
  /** Smoke covers the canvas (and the swap waits for full cover). Default true. */
  readonly smoke?: boolean;
  /** The scene is fully hidden: swap it now, synchronously. Called at most once, never after cancel(). */
  readonly onCovered?: () => void;
  readonly onDone?: (how: TransitionEnd) => void;
}
interface Run {
  readonly req: TransitionRequest;
  readonly mode: "full" | "reduced";
  readonly crt: boolean;
  readonly smoke: boolean;
  elapsedMs: number;
  covered: boolean;
  lastSeedBucket: number;
}

export class SceneTransition {
  private readonly crtFilter = new CRTFilter({ curvature: 0, lineWidth: 0, lineContrast: 0, noise: 0, noiseSize: CRT.noiseSizePx, vignetting: 0, vignettingAlpha: 0, vignettingBlur: 0, seed: 0, time: 0 });
  private readonly sprite = new Sprite({ visible: false, anchor: 0.5, label: "smoke-transition" });
  private readonly cover = new Graphics();
  private run: Run | null = null;
  private warm = 2;
  private warmAdded = false;
  private disposed = false;
  private readonly onTick = (t: Ticker) => this.update(t.deltaMS);
  /** Completed transitions in this object's lifetime (read by the preview and tests; not game state). */
  completed = 0;

  constructor(private readonly world: ArenaWorld, private readonly smoke: SmokeTextures | null) {
    this.cover.rect(0, 0, 1, 1).fill({ color: SMOKE.coverColor }); // unit square, scaled to the canvas
    this.cover.visible = false;
    world.screenLayer.addChild(this.sprite, this.cover);
    world.ticker.add(this.onTick);
  }

  get active(): boolean { return this.run !== null; }
  /** Filters on the game view right now (0 when idle). */
  get filtersOnWorld(): number { return this.world.fxStack.count; }
  get smokeVisible(): boolean { return this.sprite.visible; }
  get coverVisible(): boolean { return this.cover.visible; }

  play(req: TransitionRequest = {}): void {
    if (this.disposed) return;
    if (this.run) this.finish("cancelled");
    const wantSmoke = req.smoke ?? true;
    const reduced = prefersReducedMotion() || (wantSmoke && !this.smoke);
    const run: Run = { req, mode: reduced ? "reduced" : "full", crt: !reduced && (req.crt ?? true), smoke: !reduced && wantSmoke, elapsedMs: 0, covered: false, lastSeedBucket: -1 };
    this.run = run;
    if (run.crt) {
      this.crtFilter.noise = 0;
      this.crtFilter.lineContrast = 0;
      this.crtFilter.lineWidth = 0;
      this.world.fxStack.add(this.crtFilter);
    }
  }

  /** Ends immediately: no filter, no smoke, no cover, no swap callback. onDone("cancelled") still fires so a caller can unblock its UI. */
  cancel(): void {
    if (this.run) this.finish("cancelled");
  }

  private finish(how: TransitionEnd): void {
    const run = this.run;
    this.run = null;
    this.world.fxStack.remove(this.crtFilter);
    this.sprite.visible = false;
    this.cover.visible = false;
    if (!run) return;
    if (how === "complete") this.completed += 1;
    run.req.onDone?.(how);
  }

  private swap(run: Run): void {
    if (run.covered) return;
    run.covered = true;
    run.req.onCovered?.();
  }

  private update(dtMs: number): void {
    if (this.disposed) return;
    const run = this.run;
    if (!run) {
      // Once, early in the arena's life: compile the CRT shader off-screen-effect (no noise, no lines) so the first real transition has no hitch.
      if (this.warm > 0) {
        if (!this.warmAdded) { this.warmAdded = true; this.world.fxStack.add(this.crtFilter); } else if (--this.warm === 0) this.world.fxStack.remove(this.crtFilter);
      }
      return;
    }
    if (this.warm > 0) { this.warm = 0; if (this.warmAdded && !run.crt) this.world.fxStack.remove(this.crtFilter); }
    run.elapsedMs += Math.min(dtMs, 50); // a stalled frame can never jump over the cover window
    const t = run.elapsedMs;
    if (run.mode === "reduced") return this.updateReduced(run, t);
    if (run.crt) this.updateCrt(run, t);
    if (run.smoke) return this.updateSmoke(run, t);
    // CRT only: swap at the strongest part of the interference, end with it.
    if (!run.covered && t >= CRT.durationMs * CRT.swapAt) this.swap(run);
    if (t >= CRT.durationMs) this.finish("complete");
  }

  private updateCrt(run: Run, t: number): void {
    const e = crtEnvelope(t, CRT);
    const f = this.crtFilter;
    f.noise = CRT.noise * e;
    f.lineContrast = CRT.lineContrast * e;
    f.lineWidth = e > 0.01 ? 1 : 0; // the filter's scan-line banding exists whenever lineWidth > 0, so it is off while the envelope is 0
    f.time = t * 0.012; // slow scan roll
    const bucket = Math.floor((t * CRT.seedHz) / 1000);
    if (bucket !== run.lastSeedBucket) { run.lastSeedBucket = bucket; f.seed = 0.37 + 0.61 * ((bucket * 7919) % 97) / 97; }
    if (t >= CRT.durationMs) this.world.fxStack.remove(f);
  }

  private updateSmoke(run: Run, t: number): void {
    const smoke = this.smoke!;
    const local = t - SMOKE.startDelayMs;
    const frame = smokeFrameAt(local, SMOKE_TIMELINE);
    if (frame === null) {
      if (local >= 0) { // past the last visible frame (a swap that somehow never ran still runs here, while nothing is shown)
        this.swap(run);
        this.finish("complete");
      }
      return;
    }
    const texture = smoke.frame(frame) ?? Texture.EMPTY;
    const sprite = this.sprite;
    sprite.texture = texture;
    sprite.visible = texture !== Texture.EMPTY;
    const { width, height } = this.world.screenSize;
    const k = Math.max(width / SMOKE_FRAME_W, height / SMOKE_FRAME_H) * SMOKE.overscan;
    sprite.scale.set(k);
    sprite.position.set(width / 2, height / 2);
    // The swap happens in this same frame's update, before it renders, while the card is exactly opaque: nothing can pop.
    if (!run.covered && frame >= SMOKE_TIMELINE.opaqueFrom) this.swap(run);
  }

  private updateReduced(run: Run, t: number): void {
    const a = reducedCoverAlpha(t, REDUCED_FADE_MS, REDUCED_HOLD_MS);
    const { width, height } = this.world.screenSize;
    const g = this.cover;
    g.scale.set(width, height);
    g.alpha = a;
    g.visible = a > 0;
    if (!run.covered && t >= REDUCED_FADE_MS) this.swap(run);
    if (t >= REDUCED_FADE_MS * 2 + REDUCED_HOLD_MS) this.finish("complete");
  }

  /** Total running time of a full smoke transition from play(), ms (for a caller that shows a label or waits). */
  static fullDurationMs(): number { return SMOKE.startDelayMs + smokeSchedule(SMOKE_TIMELINE).totalMs; }

  dispose(): void {
    if (this.disposed) return;
    this.cancel();
    this.disposed = true;
    this.world.ticker.remove(this.onTick);
    this.world.fxStack.remove(this.crtFilter);
    this.crtFilter.destroy();
    this.sprite.destroy();
    this.cover.destroy();
  }
}
