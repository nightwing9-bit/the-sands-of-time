// The one long-lived PixiJS application of the Ghost Lap browser (13.2). It owns the canvas, the 1600x900 world transform and the
// shared layer hierarchy. Later tasks add children to these layers; they never create another Application or transform.
//
//   world (scale + letterbox offset, 1600x900 server units)
//   ├─ environment   the ONLY container the night grade multiplies (13.5)
//   │  ├─ ground     snow image (one sprite, baked monster shadow, never overlaid with another shadow)
//   │  └─ walls      contact shadows + grey Kenney wall assemblies from the arena descriptor
//   ├─ lights        13.5: warm candle pools (sprites sharing one generated radial texture), above the graded environment, below every actor
//   ├─ actors        candles (flames), ghost, heads: never graded, same transform as everything else
//   └─ debug         optional geometry overlay (the exact ArenaV1 rectangles)
// stage (screen space, above the world)
//   └─ screen        13.6: transition overlays (smoke / reduced-motion cover). Never inside the world transform, never filtered.
//
// The world container is the game view that 13.6 effects filter (Burst distortion, CRT) through `fxStack`, only while they are active;
// an idle arena has no filters and no per-frame allocation. The ticker renders the scene; nothing here changes game state.
import { Application, Assets, Container, Graphics, Rectangle, Sprite, Texture, type Renderer } from "pixi.js";
import { LIGHTING, type EnvironmentMode } from "../lighting/lightingPresets.ts";
import { SNOW_ARENA, GREY_WALLS_ATLAS } from "../config/assetRegistry.ts";
import atlasMeta from "../config/greyWallsAtlas.json";
import type { ArenaObstacle, ArenaRenderDescriptor } from "../arena/arenaV1Descriptor.ts";
import { FilterStack } from "../fx/FilterStack.ts";

/** World units covered by one wall tile cell. Chosen so every ArenaV1 rectangle (40 thick, multiples of 20) is a whole number of cells; other sizes adapt. */
const CELL = 20;
type Frame = keyof typeof atlasMeta.frames;

export interface ArenaWorldLayers { readonly ground: Container; readonly walls: Container; readonly lights: Container; readonly actors: Container; readonly debug: Container }

/** One soft white radial falloff, generated once (no asset file). Smoothstep falloff to exactly zero at the rim, so a pool has no visible edge. */
function createLightTexture(): Texture {
  const size = 128;
  const canvas = document.createElement("canvas");
  canvas.width = canvas.height = size;
  const g = canvas.getContext("2d")!;
  const gradient = g.createRadialGradient(size / 2, size / 2, 0, size / 2, size / 2, size / 2);
  for (let i = 0; i <= 16; i++) {
    const t = i / 16; // 0 centre .. 1 rim
    const falloff = 1 - t * t * (3 - 2 * t);
    gradient.addColorStop(t, `rgba(255,255,255,${(falloff * falloff).toFixed(4)})`);
  }
  g.fillStyle = gradient;
  g.fillRect(0, 0, size, size);
  return Texture.from(canvas);
}

export class ArenaWorld {
  readonly layers: ArenaWorldLayers;
  private readonly world = new Container({ label: "world" });
  private readonly environment = new Container({ label: "environment" });
  private readonly screen = new Container({ label: "screen-fx" });
  /** The only writer of the game view's filter list (13.6). Filters are added while an effect runs and removed when it ends. */
  readonly fxStack: FilterStack;
  private readonly frames = new Map<string, Texture>();
  /** Shared white radial for every candle pool. Owned here, destroyed with the world. */
  readonly lightTexture: Texture = createLightTexture();
  private mode: EnvironmentMode = "day";
  private disposed = false;
  private readonly hostObserver: ResizeObserver;

  private constructor(private readonly app: Application, private readonly host: HTMLElement, private readonly worldW: number, private readonly worldH: number, private readonly snow: Texture, private readonly atlas: Texture) {
    this.layers = {
      ground: new Container({ label: "ground" }),
      walls: new Container({ label: "walls" }),
      lights: new Container({ label: "lights" }),
      actors: new Container({ label: "actors" }),
      debug: new Container({ label: "debug" }),
    };
    this.environment.addChild(this.layers.ground, this.layers.walls);
    this.world.addChild(this.environment, this.layers.lights, this.layers.actors, this.layers.debug);
    app.stage.addChild(this.world, this.screen);
    this.fxStack = new FilterStack(this.world, worldW, worldH);
    // Snow keeps its aspect (1672x941 vs 16:9 differs by 0.05%): contain inside the world, centred, never stretched or cropped.
    const s = Math.min(worldW / snow.width, worldH / snow.height);
    const sprite = new Sprite(snow);
    sprite.scale.set(s);
    sprite.position.set((worldW - snow.width * s) / 2, (worldH - snow.height * s) / 2);
    this.layers.ground.addChild(sprite);
    app.renderer.on("resize", () => this.fit());
    // Pixi's resizeTo only follows WINDOW resize events. The stage also changes size without one (the history dock appears and the whole
    // 16:9 stage re-fits above it), which left a stale canvas that the host then cropped. Observing the host keeps canvas == stage.
    this.hostObserver = new ResizeObserver(() => app.resize());
    this.hostObserver.observe(host);
    this.fit();
  }

  /** Creates the application inside `host`. Throws if `signal` aborted while loading (the caller then has nothing to clean up). */
  static async create(host: HTMLElement, world: { width: number; height: number }, signal: AbortSignal): Promise<ArenaWorld> {
    const app = new Application();
    await app.init({
      resizeTo: host, antialias: true, backgroundAlpha: 0, autoDensity: true,
      resolution: Math.min(window.devicePixelRatio || 1, 2), // capped by design: more pixels do not improve a 1672 px image
      preference: "webgl",
    });
    try {
      const [snow, atlasSheet] = await Promise.all([Assets.load<Texture>(SNOW_ARENA), Assets.load<Texture>(GREY_WALLS_ATLAS)]);
      signal.throwIfAborted();
      host.appendChild(app.canvas);
      app.canvas.dataset.testid = "arena-canvas";
      return new ArenaWorld(app, host, world.width, world.height, snow, atlasSheet);
    } catch (error) {
      app.destroy(true, { children: true });
      throw error;
    }
  }

  /** The application's one ticker: scene content (heads, later candles and effects) animates from it instead of creating timers. */
  get ticker() { return this.app.ticker; }
  /** The renderer, for one-off bakes (the spectral ghost texture). Never used to draw per frame. */
  get renderer(): Renderer { return this.app.renderer; }
  get environmentMode(): EnvironmentMode { return this.mode; }
  /**
   * Day or night for the ENVIRONMENT only (snow + walls). Heads, flames, ghost and the React HUD are siblings/DOM and keep their own colours.
   * A multiply tint: no filter pass, no allocation. Candle pools follow through CandleField.setMode.
   */
  setEnvironment(mode: EnvironmentMode): void {
    this.mode = mode;
    this.environment.tint = LIGHTING[mode].environmentTint;
  }
  /** Screen-space layer above the world, for full-canvas transition overlays. */
  get screenLayer(): Container { return this.screen; }
  /** Canvas size in CSS pixels (the stage is 16:9, so this is normally the world contained and centred). */
  get screenSize(): { width: number; height: number } { return { width: this.app.screen.width, height: this.app.screen.height }; }
  /** Server-world to screen. Exposed so later layers/HUD anchors can be checked against the same numbers. */
  get transform() { return { scale: this.world.scale.x, x: this.world.x, y: this.world.y }; }

  private fit() {
    const w = this.app.screen.width, h = this.app.screen.height;
    const s = Math.min(w / this.worldW, h / this.worldH); // 16:9 contain; the CSS stage is already 16:9 so this is normally edge to edge
    this.world.scale.set(s);
    this.world.position.set((w - this.worldW * s) / 2, (h - this.worldH * s) / 2);
  }

  private frame(key: Frame): Texture {
    let texture = this.frames.get(key);
    if (!texture) {
      const f = atlasMeta.frames[key];
      texture = new Texture({ source: this.atlas.source, frame: new Rectangle(f.x, f.y, f.w, f.h) });
      this.frames.set(key, texture);
    }
    return texture;
  }

  /** Replaces the wall visuals with assemblies for exactly these rectangles. Safe to call again for a different layout. */
  setWalls(obstacles: readonly ArenaObstacle[] | null) {
    this.clear(this.layers.walls);
    if (!obstacles) return;
    const shadow = new Graphics();
    // Shadow, one Graphics, no filters. (1) A very tight, faint contact shadow hugging each wall. (2) Five soft expanding rings around all
    // four sides (alpha accumulates toward the wall). Upward-biased: the outer ring reaches 13 units above and 6.5 to the sides/below.
    for (const r of obstacles) shadow.roundRect(r.x0 - 1.5, r.y0 - 1.5, r.x1 - r.x0 + 3, r.y1 - r.y0 + 3, 4).fill({ color: 0x33445e, alpha: 0.07 });
    for (let i = 5; i >= 1; i--) {
      const up = 2.6 * i;
      const side = 1.3 * i;
      for (const r of obstacles) {
        shadow.roundRect(r.x0 - side, r.y0 - up, r.x1 - r.x0 + 2 * side, r.y1 - r.y0 + up + side, 4 + side).fill({ color: 0x33445e, alpha: 0.045 });
      }
    }
    this.layers.walls.addChild(shadow);
    obstacles.forEach((r, index) => this.layers.walls.addChild(this.assemble(r, index)));
  }

  /** One rectangle -> a whole number of cells per axis (cell size adapts slightly to fit the rectangle exactly): corners, edges, interior. */
  private assemble(r: ArenaObstacle, index: number): Container {
    const group = new Container({ label: `wall-${index}` });
    const cols = Math.max(2, Math.round((r.x1 - r.x0) / CELL)), rows = Math.max(2, Math.round((r.y1 - r.y0) / CELL));
    const tw = (r.x1 - r.x0) / cols, th = (r.y1 - r.y0) / rows;
    for (let row = 0; row < rows; row++) {
      for (let col = 0; col < cols; col++) {
        const v = row === 0 ? "t" : row === rows - 1 ? "b" : "";
        const h = col === 0 ? "l" : col === cols - 1 ? "r" : "";
        const part = (v && h ? v + h : v || h || "c") as "tl" | "t" | "tr" | "l" | "c" | "r" | "bl" | "b" | "br";
        // Sparse stone flecks on every wall: variant chosen by a position hash (about 1 in 4 cells stays clean), from three Kenney-derived
        // fleck windows, so no cluster repeats in a row. Bevel/outline pixels are identical in all variants, so bevels stay continuous.
        const hash = (Math.imul(col + 1, 73856093) ^ Math.imul(row + 1, 19349663) ^ Math.imul(index + 1, 83492791)) >>> 0;
        const variant = ["A", "F1", "F2", "F3"][(hash >>> 7) % 4];
        const sprite = new Sprite(this.frame(`${variant}/${part}` as Frame));
        sprite.width = tw + 0.02; // 0.02 unit overlap hides sub-pixel seams between cells
        sprite.height = th + 0.02;
        sprite.position.set(r.x0 + col * tw, r.y0 + row * th);
        group.addChild(sprite);
      }
    }
    return group;
  }

  /** Optional check: the exact obstacle rectangles and world frame in one stroke colour, above everything. */
  setGeometryOverlay(on: boolean, obstacles: readonly ArenaObstacle[]) {
    this.clear(this.layers.debug);
    if (!on) return;
    const g = new Graphics();
    g.rect(0, 0, this.worldW, this.worldH).stroke({ width: 2, color: 0x00a6c8, alpha: 0.9 });
    for (const r of obstacles) g.rect(r.x0, r.y0, r.x1 - r.x0, r.y1 - r.y0).stroke({ width: 1.5, color: 0xff2d7a, alpha: 0.95 });
    this.layers.debug.addChild(g);
  }

  private clear(layer: Container) {
    for (const child of layer.removeChildren()) child.destroy({ children: true });
  }

  destroy() {
    if (this.disposed) return;
    this.disposed = true;
    this.hostObserver.disconnect();
    this.fxStack.clear();
    this.app.destroy(true, { children: true }); // sprites only; shared textures are released below
    for (const texture of this.frames.values()) texture.destroy(false);
    this.frames.clear();
    this.lightTexture.destroy(true);
    void Assets.unload([SNOW_ARENA, GREY_WALLS_ATLAS]);
  }
}

export function describeArena(d: ArenaRenderDescriptor) { return `${d.layout} (${d.authority})`; }
