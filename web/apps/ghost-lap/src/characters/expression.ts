// Which face a head shows. PURE logic (no Pixi, no DOM, no clock): the caller passes the visual wall-clock `nowMs`, so every rule is testable.
//
// THIS IS PRESENTATION, NOT A SERVER DTO. The server owns charge, hits, collisions, release and results; this module only decides what
// the face looks like, from facts a transport adapter has already confirmed:
//   charge            authoritative Soulfire 0..4 (PlayerSnapshot.Charge / challenge frame)
//   contactFear       authoritative weaker-contact: MatchEvent ContactDrain, ONLY for its victim `Slot` (the weaker player)
//   burstHit          authoritative MatchEvent BurstHit, ONLY for its victim `Slot`
//   release           an authoritative 4/4 release cue (BurstManual / BurstAuto) for the releasing slot
//   winner            a finalized result's confirmed winner(s), ties included
// Priority (skill ref 04, DIRECTOR BASELINE): winner > latest unexpired hit/fear override > release > charge face.
// Overrides expire back to the CURRENT charge face, never to the face that was showing when they began.
import type { Charge } from "../presentation/types.ts";

export type FaceId = "frown" | "neutral" | "happy" | "grin" | "release" | "hit" | "fear" | "winner" | "evil";
export type ReactionKind = "fear" | "hit";

/** characters.md: both reactions are a 2 second override. This is VISUAL wall time, not 60 historical ticks. */
export const REACTION_MS = 2000;
/** How many event keys are remembered for de-duplication. Keys are the server's MatchEvent.Key (Tick*1024+Index) or any stable id. */
const KEY_MEMORY = 64;

/** 0 and 1 are deliberately the same face (latest user correction); the HUD still shows the true number. */
export function faceForCharge(charge: Charge): FaceId {
  return charge <= 1 ? "frown" : charge === 2 ? "neutral" : charge === 3 ? "happy" : "grin";
}

/** Faces that interrupt the calm charge progression. They fade in quickly so the hit reads on the frame it arrives. */
export function isReaction(face: FaceId): boolean {
  return face === "hit" || face === "fear" || face === "release" || face === "winner";
}

interface ActiveReaction { readonly kind: ReactionKind; readonly startMs: number; readonly untilMs: number }

export class ExpressionState {
  private chargeValue: Charge = 0;
  private reactions: ActiveReaction[] = [];
  private release: { startMs: number; untilMs: number | null } | null = null;
  private winnerFlag = false;
  private readonly seen: (string | number)[] = [];
  /** A ghost never changes: one permanent evil face, independent of any living-expression stack. */
  private readonly ghost: boolean;
  constructor(ghost = false) { this.ghost = ghost; }

  get charge(): Charge { return this.chargeValue; }

  /** Authoritative charge. A change that happens AFTER a release began ends the release; a change in the same instant is part of it. */
  setCharge(charge: Charge, nowMs: number): void {
    if (charge !== this.chargeValue && this.release && nowMs > this.release.startMs) this.release = null;
    this.chargeValue = charge;
  }

  /**
   * Starts (or restarts) a 2 s weaker-contact or Burst-hit override for THIS head. Returns false and changes nothing for a repeated `key`
   * (resync, duplicate frame). Call only for the victim of an authoritative event, never for every touch.
   */
  cue(kind: ReactionKind, nowMs: number, key?: string | number): boolean {
    if (!this.remember(key)) return false;
    this.reactions.push({ kind, startMs: nowMs, untilMs: nowMs + REACTION_MS });
    return true;
  }

  /**
   * An authoritative 4/4 release. The server/skill leave its visual length OPEN, so none is invented: the face lasts until `endRelease()`,
   * until an explicit `untilMs` the caller derived from real data, or until the authoritative charge changes afterwards.
   */
  beginRelease(nowMs: number, key?: string | number, untilMs?: number): boolean {
    if (!this.remember(key)) return false;
    this.release = { startMs: nowMs, untilMs: untilMs ?? null };
    return true;
  }
  endRelease(): void { this.release = null; }

  /** Result-confirmed winner (ties: call for every tied winner). Persists until cleared, e.g. on rematch. */
  setWinner(on: boolean): void { this.winnerFlag = on; }

  /** Forget every override and remembered key: rematch, new match, history seek. The charge is kept (the caller sets it next). */
  reset(): void {
    this.reactions = [];
    this.release = null;
    this.winnerFlag = false;
    this.seen.length = 0;
  }

  /** The face to show at `nowMs`. Expired overrides are dropped here, which is what returns the head to the current charge face. */
  face(nowMs: number): FaceId {
    if (this.ghost) return "evil";
    this.reactions = this.reactions.filter((r) => nowMs < r.untilMs);
    if (this.release && this.release.untilMs !== null && nowMs >= this.release.untilMs) this.release = null;
    if (this.winnerFlag) return "winner";
    if (this.reactions.length > 0) {
      // Latest real event wins locally; at the same instant the Burst hit (the harder one) beats weaker-contact fear.
      let best = this.reactions[0]!;
      for (const r of this.reactions) if (r.startMs > best.startMs || (r.startMs === best.startMs && r.kind === "hit")) best = r;
      return best.kind;
    }
    if (this.release) return "release";
    return faceForCharge(this.chargeValue);
  }

  /** True while a release is the face (or is hidden under a stronger override): the halo is emphasised for the length of the release. */
  get releasing(): boolean { return this.release !== null; }

  private remember(key: string | number | undefined): boolean {
    if (key === undefined) return true;
    if (this.seen.includes(key)) return false;
    this.seen.push(key);
    if (this.seen.length > KEY_MEMORY) this.seen.shift();
    return true;
  }
}

/** Confirmed winners of a FINALIZED match: every row holding rank 1 (ties share the rank). Pending/aborted results must not be passed in. */
export function winnerSlots(rows: readonly { readonly slot: number; readonly rank: number }[]): number[] {
  return rows.filter((r) => r.rank === 1).map((r) => r.slot);
}

/** Pupil target from an effective movement direction. Keeps the last non-zero direction while stationary; diagonals are normalised. */
export function gazeVector(x: -1 | 0 | 1, y: -1 | 0 | 1, previous: { x: number; y: number }): { x: number; y: number } {
  if (x === 0 && y === 0) return previous;
  const len = Math.hypot(x, y);
  return { x: x / len, y: y / len };
}
