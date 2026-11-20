// Loads the ONE generated candle flame atlas (scripts/build-candle-atlas.py) and hands out shared frame textures. Same ownership pattern as
// CharacterTextures: one instance per scene/screen, every candle shares its 90 textures, destroy() when the scene goes away.
// The 4130x3501 master is never loaded in the browser.
import { Assets, Rectangle, Texture } from "pixi.js";
import { CANDLE_FLAME_ATLAS } from "../config/assetRegistry.ts";
import atlas from "../config/candleAtlas.json";

export type CandleLoopId = "steady" | "source";
export const CANDLE_META = atlas.meta;
export const CANDLE_FPS: number = atlas.meta.fps;
export const CANDLE_LOOPS: Readonly<Record<CandleLoopId, readonly number[]>> = {
  steady: atlas.meta.loops.steady.frames,
  source: atlas.meta.loops.source.frames,
};

export class CandleTextures {
  private readonly frames: Texture[] = [];
  private destroyed = false;
  /** Loads in flight plus live instances share one cached atlas texture (React StrictMode mounts twice); unloaded only at zero users. */
  private static users = 0;
  private static unloading: Promise<void> = Promise.resolve();
  private static release(): void {
    if (--CandleTextures.users === 0) CandleTextures.unloading = CandleTextures.unloading.then(() => Assets.unload(CANDLE_FLAME_ATLAS));
  }
  private constructor(private readonly sheet: Texture) {
    for (let i = 0; i < atlas.meta.frameCount; i++) {
      const f = atlas.frames[String(i) as keyof typeof atlas.frames];
      this.frames.push(new Texture({ source: sheet.source, frame: new Rectangle(f.x, f.y, f.w, f.h) }));
    }
  }

  static async load(signal?: AbortSignal): Promise<CandleTextures> {
    CandleTextures.users++;
    let sheet: Texture;
    try {
      await CandleTextures.unloading;
      sheet = await Assets.load<Texture>(CANDLE_FLAME_ATLAS);
    } catch (error) {
      CandleTextures.release();
      throw error;
    }
    if (signal?.aborted) {
      CandleTextures.release();
      throw signal.reason ?? new DOMException("aborted", "AbortError");
    }
    // The flame is drawn at roughly half its atlas size: mip-mapping keeps the thin spark curl clean. Cells have fully transparent borders
    // (the build asserts it), so mip levels cannot bleed one frame into its neighbour.
    sheet.source.autoGenerateMipmaps = true;
    sheet.source.style.scaleMode = "linear";
    sheet.source.style.mipmapFilter = "linear";
    return new CandleTextures(sheet);
  }

  frame(index: number): Texture { return this.frames[index] ?? this.frames[0]!; }
  get size(): number { return this.frames.length; }

  destroy(): void {
    if (this.destroyed) return;
    this.destroyed = true;
    for (const t of this.frames) t.destroy(false);
    this.frames.length = 0;
    CandleTextures.release();
  }
}
