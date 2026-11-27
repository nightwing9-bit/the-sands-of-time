// ONE reusable head. The same class draws living heads in multiplayer, solo, night, replay and results, and the permanent-evil ghost head.
// It knows nothing about transports, rooms or screens: a caller feeds it authoritative facts (pose, charge, gaze, cues) and a clock.
//
//   root                       positioned at the authoritative world position (world units; the stage supplies the transform)
//   ├─ shadow                  tiny soft silhouette, offset bottom-right (light from the top-left). Living heads only.
//   ├─ halo                    the head's own silhouette, feathered, hue-matched. Alpha follows charge. NOT a bloom filter. Living heads only.
//   └─ head                    upright, never rotated. Put the later SpectralGhostEffect on THIS container for the ghost.
//      ├─ body                 Kenney body shape
//      └─ face                 crossfading face rigs (eyes, brows, mouth); only pupils move with gaze
//
// No Filter, no per-frame allocation: rigs are built once per face and cached; update() only changes alpha and positions.
import { Container, Sprite } from "pixi.js";
import type { ActorSlot, Charge } from "../presentation/types.ts";
import { CharacterTextures, type BodyKey, type FrameKey } from "./CharacterAssets.ts";
import { ExpressionState, gazeVector, isReaction, type FaceId } from "./expression.ts";
import { FACE_RECIPES, GAZE_TRAVEL, HALO_ALPHA, HALO_SCALE, HEAD_UNITS, SLOT_SPECS, type SlotSpec } from "./characterSpec.ts";
import { HALO_PULSE, HALO_PULSE_REDUCED, type HaloPulsePreset } from "../fx/effectPresets.ts";
import { haloPulseAt, haloPulseLength } from "../fx/timeline.ts";

export interface CharacterViewOptions {
  readonly slot: ActorSlot;
  /** "ghost" = historical head with the permanent evil face and no charge halo or shadow. Material (cyan) arrives with the night task. */
  readonly kind?: "living" | "ghost";
  readonly headUnits?: number;
  /** Overrides the body and hue only (the P1 comparison in the study). Everything else is the slot's. */
  readonly body?: { readonly key: BodyKey; readonly hue: number };
}

interface Rig {
  readonly container: Container;
  readonly pupils: { readonly sprite: Sprite; readonly baseX: number; readonly baseY: number; readonly travel: number }[];
  alpha: number;
  fadeMs: number;
}

const GAZE_EASE_MS = 55; // pupils glide rather than snap (a few frames); the gaze itself is the server's direction
const HALO_EASE_MS = 90;
const CALM_FADE_MS = 130; // charge stage crossfade (skill ref 04: 100..160 ms)
const REACTION_FADE_MS = 40; // a hit or fear reads on the frame it arrives

export class CharacterView {
  readonly root = new Container({ label: "character" });
  readonly head = new Container({ label: "head" });
  readonly expression: ExpressionState;
  readonly spec: SlotSpec;
  private readonly unit: number;
  private readonly faceLayer = new Container({ label: "face" });
  private readonly rigs = new Map<FaceId, Rig>();
  private readonly halo: Sprite | null;
  private readonly shadow: Sprite | null;
  private current: FaceId;
  private gaze = { x: 0, y: 0 };
  private shownGaze = { x: 0, y: 0 };
  private pinned: FaceId | null = null;
  /** Halo level that follows the authoritative charge (eased). The Burst pulse is layered on top and never changes it. */
  private haloLevel = 0;
  private haloScale = 1;
  private pulse: { readonly startMs: number; readonly preset: HaloPulsePreset } | null = null;
  private destroyed = false;

  constructor(private readonly textures: CharacterTextures, options: CharacterViewOptions) {
    const kind = options.kind ?? "living";
    this.spec = SLOT_SPECS[options.slot];
    const size = options.headUnits ?? HEAD_UNITS;
    this.unit = size / 160;
    this.expression = new ExpressionState(kind === "ghost");
    const hue = options.body?.hue ?? this.spec.hue;
    const shape = this.spec.shape;
    if (kind === "living") {
      this.shadow = new Sprite(textures.frame(`shadow_${shape}`));
      this.shadow.anchor.set(0.5);
      this.shadow.scale.set(this.unit * 0.96);
      this.shadow.position.set(size * 0.06, size * 0.085);
      this.shadow.tint = 0x2b4058;
      this.shadow.alpha = 0.2;
      this.halo = new Sprite(textures.frame(`halo_${shape}`));
      this.halo.anchor.set(0.5);
      this.halo.scale.set(this.unit);
      this.halo.tint = hue;
      this.halo.alpha = 0;
      this.root.addChild(this.shadow, this.halo);
    } else {
      this.shadow = null;
      this.halo = null;
    }
    const body = new Sprite(textures.frame(options.body?.key ?? this.spec.body));
    body.anchor.set(0.5);
    body.scale.set(this.unit);
    this.faceLayer.scale.set(this.unit * this.spec.faceScale);
    this.faceLayer.y = this.spec.faceDy * this.unit;
    this.head.addChild(body, this.faceLayer);
    this.root.addChild(this.head);
    this.current = this.expression.face(0);
    this.showRig(this.current, 0);
    this.rigs.get(this.current)!.alpha = 1;
    this.rigs.get(this.current)!.container.alpha = 1;
  }

  /** Authoritative position in world units. */
  setPosition(x: number, y: number): void { this.root.position.set(x, y); }

  /** Authoritative Soulfire charge 0..4. Faces and halo follow it; the numeric HUD is separate and always exact. */
  setCharge(charge: Charge, nowMs: number): void { this.expression.setCharge(charge, nowMs); }

  /** Effective movement direction (held input or recorded vector). (0,0) keeps the last valid gaze. */
  setGaze(x: -1 | 0 | 1, y: -1 | 0 | 1): void { this.gaze = gazeVector(x, y, this.gaze); }

  get face(): FaceId { return this.current; }
  /**
   * A confirmed Burst by THIS head (13.6): momentarily raises the head's own existing halo sprite (alpha and a little scale), then it settles
   * on the halo of the actual charge, which after a Burst is the new one. Restarts from the latest call. `reduced` = opacity only, no scale.
   */
  pulseHalo(nowMs: number, reduced = false): void {
    if (!this.halo) return;
    this.pulse = { startMs: nowMs, preset: reduced ? HALO_PULSE_REDUCED : HALO_PULSE };
  }
  /** True while a halo pulse is running (the preview and tests read it). */
  get pulsing(): boolean { return this.pulse !== null; }

  /** PREVIEW FIXTURES ONLY (the development study): holds a face still so a timed override can be inspected. Game scenes never call this. */
  pinFace(face: FaceId | null): void { this.pinned = face; }

  /** Call once per frame with the visual clock and the frame time. Cheap: no allocation after the first time each face is shown. */
  update(nowMs: number, dtMs: number): void {
    if (this.destroyed) return;
    const wanted = this.pinned ?? this.expression.face(nowMs);
    if (wanted !== this.current) {
      const fade = isReaction(wanted) || isReaction(this.current) ? REACTION_FADE_MS : CALM_FADE_MS;
      this.current = wanted;
      this.showRig(wanted, fade);
    }
    // gaze easing (frame-rate independent)
    const kGaze = 1 - Math.exp(-dtMs / GAZE_EASE_MS);
    this.shownGaze.x += (this.gaze.x - this.shownGaze.x) * kGaze;
    this.shownGaze.y += (this.gaze.y - this.shownGaze.y) * kGaze;
    for (const [id, rig] of this.rigs) {
      const target = id === this.current ? 1 : 0;
      const step = dtMs / Math.max(1, rig.fadeMs);
      rig.alpha = target > rig.alpha ? Math.min(target, rig.alpha + step) : Math.max(target, rig.alpha - step);
      rig.container.alpha = rig.alpha;
      rig.container.visible = rig.alpha > 0.001;
      if (rig.container.visible) for (const p of rig.pupils) p.sprite.position.set(p.baseX + this.shownGaze.x * GAZE_TRAVEL * p.travel, p.baseY + this.shownGaze.y * GAZE_TRAVEL * p.travel);
    }
    if (this.halo) {
      const base = HALO_ALPHA[this.expression.charge];
      this.haloLevel += (base - this.haloLevel) * (1 - Math.exp(-dtMs / HALO_EASE_MS));
      if (Math.abs(this.haloLevel - base) < 0.002) this.haloLevel = base;
      const baseScale = HALO_SCALE[this.expression.charge];
      this.haloScale += (baseScale - this.haloScale) * (1 - Math.exp(-dtMs / HALO_EASE_MS));
      if (Math.abs(this.haloScale - baseScale) < 0.001) this.haloScale = baseScale;
      let alpha = this.haloLevel;
      let scale = this.haloScale;
      const p = this.pulse;
      if (p) {
        const elapsed = nowMs - p.startMs;
        if (elapsed >= haloPulseLength(p.preset)) this.pulse = null;
        else {
          const e = haloPulseAt(elapsed, p.preset);
          alpha = Math.max(alpha, p.preset.peak * e);
          scale = Math.max(scale, 1 + p.preset.scaleGain * e);
        }
      }
      this.halo.alpha = alpha;
      this.halo.scale.set(this.unit * scale);
    }
  }
  private showRig(id: FaceId, fadeMs: number): void {
    let rig = this.rigs.get(id);
    if (!rig) {
      rig = this.build(id);
      this.rigs.set(id, rig);
      this.faceLayer.addChild(rig.container);
    }
    rig.fadeMs = fadeMs;
    rig.container.visible = true;
  }

  private build(id: FaceId): Rig {
    const container = new Container({ label: `face-${id}` });
    const pupils: Rig["pupils"][number][] = [];
    for (const part of FACE_RECIPES[id]) {
      const sprite = new Sprite(this.textures.frame(part.frame as FrameKey));
      sprite.anchor.set(0.5);
      sprite.position.set(part.x, part.y);
      sprite.scale.set(part.sx ?? 1, part.sy ?? 1);
      if (part.rot) sprite.rotation = part.rot;
      container.addChild(sprite);
      if (part.gaze !== undefined) pupils.push({ sprite, baseX: part.x, baseY: part.y, travel: part.gaze });
    }
    container.alpha = 0;
    return { container, pupils, alpha: 0, fadeMs: CALM_FADE_MS };
  }

  /** Detaches and frees the view. Textures are shared and stay with their owner. */
  destroy(): void {
    if (this.destroyed) return;
    this.destroyed = true;
    this.root.destroy({ children: true });
    this.rigs.clear();
  }
}
