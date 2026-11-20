// Loads the ONE generated character atlas (scripts/build-character-atlas.py) and hands out frame textures. One instance is shared by every
// CharacterView; its owner (the scene or the study) calls destroy() when the scene goes away.
import { Assets, Rectangle, Texture } from "pixi.js";
import { CHARACTER_ATLAS } from "../config/assetRegistry.ts";
import atlas from "../config/characterAtlas.json";

export type FrameKey = keyof typeof atlas.frames;
export type BodyKey = Extract<FrameKey, `body_${string}`>;
export type ShapeId = "circle" | "square" | "diamond" | "squircle";

export class CharacterTextures {
  private readonly cache = new Map<string, Texture>();
  private destroyed = false;
  /** Loads in flight plus live instances share ONE cached atlas texture (React StrictMode mounts twice): it is unloaded only at zero. */
  private static users = 0;
  // Pixi unload is async: a new load must wait until the old cached Texture is evicted.
  private static unloading: Promise<void> = Promise.resolve();
  private static releaseAtlas(): void {
    if (--CharacterTextures.users === 0) {
      CharacterTextures.unloading = CharacterTextures.unloading.then(() => Assets.unload(CHARACTER_ATLAS));
    }
  }
  private constructor(private readonly sheet: Texture) {}

  static async load(signal?: AbortSignal): Promise<CharacterTextures> {
    CharacterTextures.users++;
    let sheet: Texture;
    try {
      await CharacterTextures.unloading;
      sheet = await Assets.load<Texture>(CHARACTER_ATLAS);
    } catch (error) {
      CharacterTextures.releaseAtlas();
      throw error;
    }
    if (signal?.aborted) {
      CharacterTextures.releaseAtlas();
      throw signal.reason ?? new DOMException("aborted", "AbortError");
    }
    // The atlas holds 160 px heads drawn at roughly 50 units: mip-mapping keeps thin brows and the diamond edge clean when minified.
    // Frames are separated by 8 px gutters so mip levels cannot bleed one part into its neighbour.
    sheet.source.autoGenerateMipmaps = true;
    sheet.source.style.scaleMode = "linear";
    sheet.source.style.mipmapFilter = "linear";
    return new CharacterTextures(sheet);
  }

  frame(key: FrameKey): Texture {
    let texture = this.cache.get(key);
    if (!texture) {
      const f = atlas.frames[key];
      texture = new Texture({ source: this.sheet.source, frame: new Rectangle(f.x, f.y, f.w, f.h) });
      this.cache.set(key, texture);
    }
    return texture;
  }

  /** Frame size in atlas (Double) pixels. */
  size(key: FrameKey): { w: number; h: number } { return atlas.frames[key]; }

  destroy(): void {
    if (this.destroyed) return;
    this.destroyed = true;
    for (const texture of this.cache.values()) texture.destroy(false);
    this.cache.clear();
    CharacterTextures.releaseAtlas();
  }
}
