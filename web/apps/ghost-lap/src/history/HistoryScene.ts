// Draws one saved history into the ONE long-lived arena world (ArenaWorld.layers.actors). It reads recorded states and draws them; it
// decides nothing, collects nothing and sends nothing.
//
//   actors
//   ├─ ghostLayer   the SOURCE original at the same tick (optional, only after the player loaded it): the real CharacterView head, cyan material
//   └─ headLayer    the head of the history being viewed: exact recorded position, charge face from the recorded charge, gaze from the recorded held vector
//
// Exactness: a paused or seeked view is drawn at EXACTLY the recorded integer tick. Only while local playback runs does the position ease
// between two neighbouring real states (so 30 recorded steps per second do not look like 30 hops on a 60 Hz display); that ease never
// leaves the segment between those two states and is never used for any number.
//
import { Container } from "pixi.js";
import type { CandleTextures } from "../candles/CandleAssets.ts";
import { CandleField } from "../candles/CandleField.ts";
import { CharacterView } from "../characters/CharacterView.ts";
import type { CharacterTextures } from "../characters/CharacterAssets.ts";
import { HEAD_UNITS } from "../characters/characterSpec.ts";
import { GhostTextures, GhostView } from "../ghost/SpectralGhost.ts";
import type { ActorSlot, Charge } from "../presentation/types.ts";
import type { ArenaWorld } from "../stage/ArenaWorld.ts";
import type { HistoryFrameWire } from "./historyWire.ts";

/** A pose to draw: the recorded state at `tick`, optionally eased toward the next recorded state by `frac` (0 = exactly `tick`). */
export interface ScenePose { readonly a: HistoryFrameWire; readonly b: HistoryFrameWire | null; readonly frac: number }

const FIXED = 1024;
const sign = (n: number): -1 | 0 | 1 => (n > 0 ? 1 : n < 0 ? -1 : 0);

export class HistoryScene {
  private readonly candleLayer = new Container({ label: "history-candles" });
  private readonly candles: CandleField | null;
  private readonly ghostLayer = new Container({ label: "history-ghost" });
  private readonly headLayer = new Container({ label: "history-head" });
  private readonly head: CharacterView;
  private ghost: GhostView | null = null;
  private ghostTextures: GhostTextures | null = null;
  private shown: ScenePose | null = null;
  private pose: ScenePose | null = null;
  private ghostPose: ScenePose | null = null;
  private dimmed = false;
  private disposed = false;
  private readonly onTick: () => void;

  constructor(private readonly world: ArenaWorld, private readonly characters: CharacterTextures, readonly slot: ActorSlot, textures?: CandleTextures) {
    this.candles = textures ? new CandleField(world, textures, this.candleLayer) : null;
    world.layers.actors.addChild(this.candleLayer, this.ghostLayer, this.headLayer);
    this.head = new CharacterView(characters, { slot, headUnits: HEAD_UNITS });
    this.headLayer.addChild(this.head.root);
    this.head.root.visible = false;
    this.onTick = () => this.tick();
    world.ticker.add(this.onTick);
  }

  /** The state to show. `null` hides the head (nothing loaded yet). */
  setPose(pose: ScenePose | null): void { this.pose = pose; }
  /** The source original's state at the same tick, or null to drop the ghost. */
  setGhostPose(pose: ScenePose | null): void { this.ghostPose = pose; }
  /** True while the shown tick's data is not here yet: the last real state stays visible, softly dimmed, never replaced by an invented one. */
  setPending(pending: boolean): void { this.dimmed = pending; }

  private tick(): void {
    if (this.disposed) return;
    const now = performance.now();
    const dt = Math.min(100, this.world.ticker.deltaMS);
    const p = this.pose;
    if (p) {
      if (p.a !== this.shown?.a) this.candles?.sync(p.a.visibleCandles ?? []);
      this.shown = p;
      const at = interpolate(p);
      this.head.setPosition(at.x, at.y);
      this.head.setCharge(Math.min(4, Math.max(0, p.a.charge)) as Charge, now);
      this.head.setGaze(sign(p.a.heldMoveX), sign(p.a.heldMoveY));
      this.head.root.visible = true;
    } else this.head.root.visible = !!this.shown && this.head.root.visible;
    this.head.root.alpha = this.dimmed ? 0.55 : 1;
    this.head.update(now, dt);
    this.candles?.update(now);
    const g = this.ghostPose;
    if (!g) { this.dropGhost(); return; }
    if (!this.ghost) {
      this.ghostTextures ??= new GhostTextures(this.world.renderer, this.characters);
      this.ghost = new GhostView(this.ghostTextures, this.slot, HEAD_UNITS);
      this.ghostLayer.addChild(this.ghost.root);
    }
    const at = interpolate(g);
    this.ghost.setPosition(at.x, at.y);
  }

  private dropGhost(): void {
    this.ghost?.destroy();
    this.ghost = null;
  }

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    this.world.ticker.remove(this.onTick);
    this.dropGhost();
    this.ghostTextures?.destroy();
    this.head.destroy();
    this.candles?.dispose();
    this.candleLayer.destroy({ children: true });
    this.ghostLayer.destroy({ children: true });
    this.headLayer.destroy({ children: true });
  }
}

/** World units at the pose: exactly the recorded state when `frac` is 0 or there is no next state. */
export function interpolate(p: ScenePose): { x: number; y: number } {
  const ax = p.a.xFixed / FIXED, ay = p.a.yFixed / FIXED;
  if (!p.b || p.frac <= 0) return { x: ax, y: ay };
  const t = Math.min(1, p.frac);
  return { x: ax + (p.b.xFixed / FIXED - ax) * t, y: ay + (p.b.yFixed / FIXED - ay) * t };
}
