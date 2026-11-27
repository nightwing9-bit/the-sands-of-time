// The candles of one scene: animated flames (shared textures) plus their warm light pools. Used by day play now, and by night play/replay later,
// unchanged: only `setMode` differs.
//
// What it draws, per candle the SERVER frame lists:
//   active     flame sprite (own phase offset on the shared 30 fps loop) in `flameParent` + one warm pool in world.layers.lights
//   telegraph  a thin static ring only: not collectable yet, so no flame and no light
// Candles appear and disappear only because the list says so. Nothing here knows about sites, plans, pickups or scores; ids are opaque.
// No Filter, no per-frame allocation: `update()` swaps a texture reference and an alpha on at most a couple of dozen sprites.
import { Container, Graphics, Sprite } from "pixi.js";
import type { ArenaWorld } from "../stage/ArenaWorld.ts";
import { CANDLE_FPS, CANDLE_LOOPS, CANDLE_META, type CandleLoopId, type CandleTextures } from "./CandleAssets.ts";
import { frameAt, phaseOffsetFrames, poolGain } from "./candleTiming.ts";
import { CANDLE_UNITS_PER_SOURCE_PX, LIGHTING, TELEGRAPH_RING, type EnvironmentMode } from "../lighting/lightingPresets.ts";

export interface CandleSite { readonly id: string; readonly x: number; readonly y: number; readonly phase: "telegraph" | "active" }

interface Entry {
  readonly id: string;
  readonly offset: number;
  phase: "telegraph" | "active";
  frame: number;
  flame: Sprite | null;
  pool: Sprite | null;
  ring: Graphics | null;
}

const FLAME_SCALE = CANDLE_UNITS_PER_SOURCE_PX / CANDLE_META.derivation.crop.atlasScale; // world units per atlas pixel
const ANCHOR_X = CANDLE_META.anchor[0]! / CANDLE_META.cell;
const ANCHOR_Y = CANDLE_META.anchor[1]! / CANDLE_META.cell;
const REDUCED_FRAME_LOOP_INDEX = 3; // reduced motion: hold one calm flame frame

export class CandleField {
  private readonly entries = new Map<string, Entry>();
  private mode: EnvironmentMode;
  private loopId: CandleLoopId = "steady";
  private reverse = false;
  private readonly reduceMotion = typeof window !== "undefined" && window.matchMedia?.("(prefers-reduced-motion: reduce)").matches === true;
  private disposed = false;

  constructor(private readonly world: ArenaWorld, private readonly textures: CandleTextures, private readonly flameParent: Container, mode: EnvironmentMode = world.environmentMode) {
    this.mode = mode;
  }

  /** Same flames, same loop, same phases: only the light changes (skill ref 05). */
  setMode(mode: EnvironmentMode): void {
    if (mode === this.mode) return;
    this.mode = mode;
    for (const e of this.entries.values()) if (e.pool) this.stylePool(e.pool);
  }

  /** "steady" skips the measured white-pink flash run; "source" plays every frame of the sheet. Study choice; see candleAtlas.json loops. */
  setLoop(loop: CandleLoopId): void { this.loopId = loop; }
  /** Study only: plays the loop backwards so a human can judge the derived direction. */
  setReverse(on: boolean): void { this.reverse = on; }
  get count(): { active: number; telegraph: number } {
    let active = 0, telegraph = 0;
    for (const e of this.entries.values()) e.phase === "active" ? active++ : telegraph++;
    return { active, telegraph };
  }

  /** Replace the drawn set with exactly this list (the server frame's candles). Creates, re-phases and removes; never invents. */
  sync(list: readonly CandleSite[]): void {
    if (this.disposed) return;
    const live = new Set<string>();
    for (const c of list) {
      live.add(c.id);
      let e = this.entries.get(c.id);
      if (!e) {
        e = { id: c.id, offset: phaseOffsetFrames(c.id, CANDLE_LOOPS.steady.length), phase: c.phase, frame: -1, flame: null, pool: null, ring: null };
        this.entries.set(c.id, e);
        this.build(e, c);
      } else if (e.phase !== c.phase) {
        e.phase = c.phase;
        this.build(e, c);
      }
    }
    for (const [id, e] of this.entries) {
      if (!live.has(id)) {
        this.release(e);
        this.entries.delete(id);
      }
    }
  }

  /** Advance the flames to the visual clock. Call once per ticker frame. */
  update(nowMs: number): void {
    if (this.disposed) return;
    const loop = CANDLE_LOOPS[this.loopId];
    for (const e of this.entries.values()) {
      if (!e.flame) continue;
      const frame = this.reduceMotion ? (loop[REDUCED_FRAME_LOOP_INDEX] ?? 0) : frameAt(loop, nowMs, e.offset, CANDLE_FPS, this.reverse);
      if (frame === e.frame) continue;
      e.frame = frame;
      e.flame.texture = this.textures.frame(frame);
      if (e.pool) e.pool.alpha = LIGHTING[this.mode].pool.alpha * poolGain(CANDLE_META.lightGain[frame] ?? 1);
    }
  }

  private build(e: Entry, c: CandleSite): void {
    this.release(e, false);
    if (c.phase === "active") {
      const flame = new Sprite(this.textures.frame(0));
      flame.anchor.set(ANCHOR_X, ANCHOR_Y);
      flame.scale.set(FLAME_SCALE);
      flame.position.set(c.x, c.y);
      this.flameParent.addChild(flame);
      const pool = new Sprite(this.world.lightTexture);
      pool.anchor.set(0.5);
      this.stylePool(pool);
      pool.position.set(c.x, c.y + LIGHTING[this.mode].pool.dy);
      this.world.layers.lights.addChild(pool);
      e.flame = flame;
      e.pool = pool;
      e.frame = -1;
    } else {
      const ring = new Graphics();
      ring.circle(0, 0, TELEGRAPH_RING.radius).stroke({ width: TELEGRAPH_RING.width, color: TELEGRAPH_RING.color, alpha: TELEGRAPH_RING.alpha });
      ring.position.set(c.x, c.y);
      this.flameParent.addChild(ring);
      e.ring = ring;
    }
  }

  private stylePool(pool: Sprite): void {
    const p = LIGHTING[this.mode].pool;
    pool.width = pool.height = p.radius * 2;
    pool.tint = p.color;
    pool.blendMode = p.blend;
    pool.alpha = p.alpha;
  }

  private release(e: Entry, forget = true): void {
    e.flame?.destroy();
    e.pool?.destroy();
    e.ring?.destroy();
    e.flame = e.pool = null;
    e.ring = null;
    if (forget) e.frame = -1;
  }

  /** Removes every flame, pool and ring (new run, rematch, history seek). Shared textures stay with their owner. */
  clear(): void {
    for (const e of this.entries.values()) this.release(e);
    this.entries.clear();
  }

  dispose(): void {
    if (this.disposed) return;
    this.clear();
    this.disposed = true;
  }
}
