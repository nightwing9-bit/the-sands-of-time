// Loads the generated smoke atlas pages (scripts/build-smoke-atlas.py) and hands out shared frame textures by TIMELINE index.
// The 7540x4806 master is never loaded. Three pages of at most 1920x1890 (below 2048, so no GPU limit is approached); about 36 MB decoded.
// Same ownership pattern as CandleTextures: one instance per screen, ref-counted page unload, destroy() when the screen goes away.
import { Assets, Rectangle, Texture } from "pixi.js";
import atlas from "../config/smokeAtlas.json";
import { SMOKE_ATLAS_DIR } from "../config/assetRegistry.ts";
import type { SmokeMeta } from "./timeline.ts";

export const SMOKE_META = atlas.meta;
export const SMOKE_TIMELINE: SmokeMeta = {
  firstVisible: atlas.meta.firstVisibleFrame,
  lastVisible: atlas.meta.lastVisibleFrame,
  opaqueFrom: atlas.meta.cover.fullyOpaqueFrom,
  opaqueTo: atlas.meta.cover.fullyOpaqueTo,
};
/** Inset of every frame rectangle, atlas pixels. Linear filtering never reads a neighbouring frame. */
const INSET = 1;
export const SMOKE_FRAME_W = atlas.meta.frameSize.w - 2 * INSET;
export const SMOKE_FRAME_H = atlas.meta.frameSize.h - 2 * INSET;
const pageUrl = (file: string) => `${SMOKE_ATLAS_DIR}/${file}`;

export class SmokeTextures {
  private readonly frames = new Map<number, Texture>();
  private destroyed = false;
  private static users = 0;
  private static unloading: Promise<void> = Promise.resolve();
  private static release(): void {
    if (--SmokeTextures.users === 0) {
      const urls = atlas.meta.pages.map((p) => pageUrl(p.file));
      SmokeTextures.unloading = SmokeTextures.unloading.then(() => Assets.unload(urls));
    }
  }
  private constructor(pages: readonly Texture[]) {
    for (const [key, f] of Object.entries(atlas.frames)) {
      const page = pages[f.page]!;
      this.frames.set(Number(key), new Texture({ source: page.source, frame: new Rectangle(f.x + INSET, f.y + INSET, f.w - 2 * INSET, f.h - 2 * INSET) }));
    }
  }
  static async load(signal?: AbortSignal): Promise<SmokeTextures> {
    SmokeTextures.users++;
    let pages: Texture[];
    try {
      await SmokeTextures.unloading;
      pages = await Promise.all(atlas.meta.pages.map((p) => Assets.load<Texture>(pageUrl(p.file))));
    } catch (error) {
      SmokeTextures.release();
      throw error;
    }
    if (signal?.aborted) {
      SmokeTextures.release();
      throw signal.reason ?? new DOMException("aborted", "AbortError");
    }
    for (const t of pages) t.source.style.scaleMode = "linear"; // magnified about 2x; no mip-maps are needed
    return new SmokeTextures(pages);
  }
  /** Texture of a timeline frame, or null for an intentionally empty frame (leading / trailing, nothing is drawn). */
  frame(timelineIndex: number): Texture | null { return this.frames.get(timelineIndex) ?? null; }
  get size(): number { return this.frames.size; }
  destroy(): void {
    if (this.destroyed) return;
    this.destroyed = true;
    for (const t of this.frames.values()) t.destroy(false);
    this.frames.clear();
    SmokeTextures.release();
  }
}
