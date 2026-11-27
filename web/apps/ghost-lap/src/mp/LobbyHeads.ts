// The four approved heads, standing in the lobby once the server has SEALED the round (Spec 13, task 13.8).
//
// This is not a character picker. The server assigns slots when the countdown starts (RoundSealing orders the roster by a room secret), so a
// head appears only for a card whose slot the server has named. Before that the lobby shows no head at all rather than a guess. The heads are
// the same CharacterView the match draws, with the calm neutral face; nothing here reads or implies Soulfire.
import { Container } from "pixi.js";
import type { ArenaWorld } from "../stage/ArenaWorld.ts";
import { CharacterView } from "../characters/CharacterView.ts";
import type { CharacterTextures } from "../characters/CharacterAssets.ts";
import type { ActorSlot } from "../presentation/types.ts";
import { prefersReducedMotion } from "../fx/motion.ts";

/** Card centres in world units (1600 x 900). The DOM cards use the same numbers as stage percentages, so heads and cards share one transform. */
export const LOBBY_CARD_X: readonly number[] = [320, 640, 960, 1280];
export const LOBBY_HEAD_Y = 338;
const SCALE = 1.9;
const POP_MS = 320;

export interface LobbyHeadSpec { readonly index: number; readonly slot: number; /** Disconnected members are drawn faded. */ readonly present: boolean }

interface Placed { readonly view: CharacterView; readonly slot: number; readonly index: number; bornMs: number; emphasis: number }

export class LobbyHeads {
  private readonly layer = new Container({ label: "lobby-heads" });
  private placed = new Map<number, Placed>();
  private selected: number | null = null;
  private disposed = false;
  private readonly onTick: () => void;

  constructor(private readonly world: ArenaWorld, private readonly textures: CharacterTextures) {
    world.layers.actors.addChild(this.layer);
    this.onTick = () => this.tick();
    world.ticker.add(this.onTick);
  }

  /** The heads to show, one per card index. Anything not listed is removed. A slot that changes card is rebuilt, which replays the pop. */
  set(specs: readonly LobbyHeadSpec[]): void {
    if (this.disposed) return;
    const now = performance.now();
    const keep = new Set<number>();
    for (const spec of specs) {
      keep.add(spec.index);
      let p = this.placed.get(spec.index);
      if (p && p.slot !== spec.slot) { p.view.destroy(); this.placed.delete(spec.index); p = undefined; }
      if (!p) {
        const view = new CharacterView(this.textures, { slot: spec.slot as ActorSlot });
        view.setCharge(this.selected === spec.index ? 3 : 2, now); // the calm neutral face; not a Soulfire claim
        view.setPosition(LOBBY_CARD_X[spec.index] ?? 800, LOBBY_HEAD_Y);
        this.layer.addChild(view.root);
        p = { view, slot: spec.slot, index: spec.index, bornMs: now, emphasis: this.selected === spec.index ? 1 : 0 };
        this.placed.set(spec.index, p);
      }
      p.view.root.alpha = spec.present ? 1 : 0.4;
    }
    for (const [index, p] of this.placed) if (!keep.has(index)) { p.view.destroy(); this.placed.delete(index); }
  }

  /**
   * Solo mascot choice (13.9): the chosen head is awake (the happy face and its small aura) and a little larger. Presentation of a pending
   * choice only; it is not a Soulfire claim and the multiplayer lobby never calls it.
   */
  setSelected(index: number | null): void {
    if (this.disposed) return;
    this.selected = index;
    const now = performance.now();
    for (const p of this.placed.values()) p.view.setCharge(p.index === index ? 3 : 2, now);
  }

  private tick(): void {
    if (this.disposed || this.placed.size === 0) return;
    const now = performance.now();
    const dt = Math.min(100, this.world.ticker.deltaMS);
    const reduced = prefersReducedMotion();
    for (const p of this.placed.values()) {
      const t = reduced ? 1 : Math.min(1, (now - p.bornMs) / POP_MS);
      const eased = 1 - (1 - t) * (1 - t) * (1 + 1.1 * (1 - t)); // ease-out
      const target = p.index === this.selected ? 1 : 0;
      p.emphasis = reduced ? target : p.emphasis + (target - p.emphasis) * Math.min(1, dt / 90);
      p.view.root.scale.set(SCALE * (0.55 + 0.45 * eased) * (1 + 0.12 * p.emphasis));
      p.view.update(now, dt);
    }
  }

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    this.world.ticker.remove(this.onTick);
    for (const p of this.placed.values()) p.view.destroy();
    this.placed.clear();
    this.layer.destroy({ children: true });
  }
}
