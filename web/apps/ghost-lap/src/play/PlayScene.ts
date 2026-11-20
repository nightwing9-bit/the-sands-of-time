// Draws a PlaySession into the ONE long-lived arena world (ArenaWorld.layers.actors). One ticker callback, no timers, no per-frame
// allocation beyond Pixi's own. It reads authoritative facts from the session and draws them; it decides nothing about the game.
//
//   actors
//   ├─ candles   13.5 CandleField: real animated flame (shared textures) when ACTIVE, thin ring when telegraphing; the warm pools go to
//   │            world.layers.lights. Positions/phase come only from the server frame (the verified plan resolves ActiveCandleIds).
//   ├─ ghost     13.5 optional historical ghost from frame.ghost (a verified history-trace tick). Beneath every living head; no gameplay.
//   ├─ heads     CharacterView per slot (13.3). Local head on top. Charge/cues from authoritative frames only.
//   └─ truth     optional outline of the NEWEST AUTHORITATIVE collider (48x48) of the local player: where the server says you are.
import { Container, Graphics } from "pixi.js";
import { CandleField } from "../candles/CandleField.ts";
import type { CandleTextures } from "../candles/CandleAssets.ts";
import { GhostTextures, GhostView } from "../ghost/SpectralGhost.ts";
import { LocalSmoother } from "./visualSmoothing.ts";
import type { EnvironmentMode } from "../lighting/lightingPresets.ts";
import type { ArenaWorld } from "../stage/ArenaWorld.ts";
import { CharacterView } from "../characters/CharacterView.ts";
import type { CharacterTextures } from "../characters/CharacterAssets.ts";
import { HEAD_UNITS } from "../characters/characterSpec.ts";
import type { ActorSlot } from "../presentation/types.ts";
import type { PlaySession } from "./PlaySession.ts";
import { ShockwaveFx } from "../fx/ShockwaveFx.ts";
import { prefersReducedMotion } from "../fx/motion.ts";

const COLLIDER = 48; // GameRulesV1 common square collider, world units

export class PlayScene {
  private readonly candleLayer = new Container({ label: "play-candles" });
  private readonly ghostLayer = new Container({ label: "play-ghost" });
  private readonly headLayer = new Container({ label: "play-heads" });
  private readonly truth = new Graphics();
  private readonly views = new Map<number, CharacterView>();
  private readonly candles: CandleField;
  /** 13.6: the one Burst distortion of this run. Disposed with the scene, cancelled with the run's identity. */
  readonly shockwave: ShockwaveFx;
  private readonly overlapWave: ShockwaveFx | null;
  private ghost: { view: GhostView; slot: number; smoother: LocalSmoother } | null = null;
  private ghostTextures: GhostTextures | null = null;
  private headUnits: number = HEAD_UNITS;
  private showTruth = false;
  private lastTick = -1;
  private lastIdentity: string | null = null;
  private winners: ReadonlySet<number> = new Set();
  private readonly prevDrawn = new Map<number, { x: number; y: number }>();
  private disposed = false;
  private readonly onTick: () => void;

  constructor(
    private readonly world: ArenaWorld,
    private readonly textures: CharacterTextures,
    private readonly session: PlaySession,
    candleTextures: CandleTextures,
    private readonly options?: { readonly overlappingBursts?: boolean; readonly visualTime?: () => number },
  ) {
    this.headLayer.sortableChildren = true;
    this.truth.visible = false;
    world.layers.actors.addChild(this.candleLayer, this.ghostLayer, this.headLayer, this.truth);
    this.candles = new CandleField(world, candleTextures, this.candleLayer);
    this.shockwave = new ShockwaveFx(world);
    this.overlapWave = options?.overlappingBursts ? new ShockwaveFx(world) : null;
    this.onTick = () => this.tick();
    world.ticker.add(this.onTick);
  }

  setHeadUnits(units: number): void {
    if (units === this.headUnits) return;
    this.headUnits = units;
    this.dropGhost();
    for (const v of this.views.values()) v.destroy();
    this.views.clear();
  }

  /** Day or night presentation. Environment grade + candle light only; heads, flames and ghost keep their colours. */
  setMode(mode: EnvironmentMode): void {
    this.world.setEnvironment(mode);
    this.candles.setMode(mode);
  }

  setShowTruth(on: boolean): void {
    this.showTruth = on;
    this.truth.visible = on;
  }

  private viewFor(slot: number): CharacterView {
    let v = this.views.get(slot);
    if (!v) {
      v = new CharacterView(this.textures, { slot: slot as ActorSlot, headUnits: this.headUnits });
      this.views.set(slot, v);
      this.headLayer.addChild(v.root);
    }
    return v;
  }

  private tick(): void {
    if (this.disposed) return;
    const now = this.options?.visualTime?.() ?? performance.now();
    const dt = Math.min(100, this.world.ticker.deltaMS);
    const drawn = this.session.render(now, dt);
    const frame = this.session.frame;
    if (!frame) {
      // The session was cleared (a multiplayer rematch returned to the lobby): the finished match must not stay drawn.
      if (this.lastIdentity !== null) this.clearRun(null);
      return;
    }
    if (frame.identity !== this.lastIdentity) this.clearRun(frame.identity); // a new run or match: nothing of the old one may survive
    if (frame.tick !== this.lastTick) {
      this.lastTick = frame.tick;
      this.candles.sync(frame.candles);
    }
    this.candles.update(now);
    this.drawGhost(frame, dt);
    for (const cue of this.session.drainCues()) {
      const view = this.views.get(cue.slot) ?? (frame.players.some((p) => p.slot === cue.slot) ? this.viewFor(cue.slot) : undefined);
      if (!view) continue;
      if (cue.kind === "release") {
        // One confirmed Burst = one face, one halo pulse, one world distortion; a repeated key (resync, duplicate frame) does none of them.
        if (view.expression.beginRelease(now, cue.key, now + (cue.untilMs ?? 0))) {
          view.pulseHalo(now, prefersReducedMotion());
          if (cue.burst) {
            const wave = (this.shockwave.active && this.overlapWave) || this.shockwave;
            wave.trigger(cue.burst.x, cue.burst.y);
          }
        }
      } else view.expression.cue(cue.kind, now, cue.key);
    }
    for (const p of frame.players) {
      const view = this.viewFor(p.slot);
      const at = drawn.get(p.slot) ?? { x: p.x, y: p.y };
      view.setPosition(at.x, at.y);
      view.setCharge(p.charge, now);
      view.expression.setWinner(this.winners.has(p.slot));
      const mine = p.slot === frame.you.slot;
      const held = this.session.held;
      if (mine && frame.you.canControl) view.setGaze(held.moveX, held.moveY);
      else {
        const prev = this.prevDrawn.get(p.slot);
        if (prev) {
          const dx = at.x - prev.x, dy = at.y - prev.y;
          if (Math.hypot(dx, dy) > 0.15) view.setGaze((Math.abs(dx) > 0.15 ? Math.sign(dx) : 0) as -1 | 0 | 1, (Math.abs(dy) > 0.15 ? Math.sign(dy) : 0) as -1 | 0 | 1);
        }
      }
      this.prevDrawn.set(p.slot, { x: at.x, y: at.y });
      view.root.zIndex = mine ? 10 : p.slot;
      view.update(now, dt);
      if (mine && this.showTruth) {
        this.truth.clear();
        this.truth.rect(p.x - COLLIDER / 2, p.y - COLLIDER / 2, COLLIDER, COLLIDER).stroke({ width: 2, color: 0x00a6c8, alpha: 0.95 });
      }
    }
  }

  /** Expression stacks, candles, heads and remembered keys live in the views, so destroying them is what forgets the old run. */
  private clearRun(identity: string | null): void {
    for (const v of this.views.values()) v.destroy();
    this.views.clear();
    this.candles.clear();
    this.dropGhost();
    this.shockwave.cancel();
    this.overlapWave?.cancel();
    this.prevDrawn.clear();
    this.winners = new Set();
    this.lastIdentity = identity;
    this.lastTick = -1;
  }

  /** The confirmed winners of a FINALIZED result (ties included): their heads show the winner face. Never called for pending or aborted results. */
  setWinners(slots: readonly number[]): void { this.winners = new Set(slots); }

  /** The historical ghost: exactly the traced position (through the SAME visual ease as the local head, so an exact overlap stays exact). */
  private drawGhost(frame: NonNullable<PlaySession["frame"]>, dtMs: number): void {
    const g = frame.ghost;
    if (!g) return this.dropGhost();
    if (!this.ghost || this.ghost.slot !== g.slot) {
      this.dropGhost();
      this.ghostTextures ??= new GhostTextures(this.world.renderer, this.textures);
      const view = new GhostView(this.ghostTextures, g.slot, this.headUnits);
      this.ghostLayer.addChild(view.root);
      this.ghost = { view, slot: g.slot, smoother: new LocalSmoother(this.session.localTauMs) };
    }
    this.ghost.smoother.setTau(this.session.localTauMs);
    this.ghost.smoother.setTarget(g.x, g.y);
    const at = this.ghost.smoother.update(dtMs);
    if (at) this.ghost.view.setPosition(at.x, at.y);
  }

  private dropGhost(): void {
    this.ghost?.view.destroy();
    this.ghost = null;
  }

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    this.world.ticker.remove(this.onTick);
    this.shockwave.dispose();
    this.overlapWave?.dispose();
    for (const v of this.views.values()) v.destroy();
    this.views.clear();
    this.dropGhost();
    this.ghostTextures?.destroy();
    this.candles.dispose();
    this.candleLayer.destroy({ children: true });
    this.ghostLayer.destroy({ children: true });
    this.headLayer.destroy();
    this.truth.destroy();
  }
}
