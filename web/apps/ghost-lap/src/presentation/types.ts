// The semantic render/control boundary of the Ghost Lap browser (Spec 13, task 13.1).
//
// THESE ARE PRESENTATION TYPES, NOT SERVER DTOs. Nothing here is parsed from the wire and nothing here is authoritative. A transport
// adapter (Codex B1/B2, not written yet) turns real hub frames into WorldPresentation and turns key state into ControlIntent; the
// renderer and the screens only ever see these two shapes. The mapping below was written after reading the DTOs that exist today:
//
//   WorldPresentation.players[i]    <- multiplayer PlayerSnapshot (XFixed/YFixed / 1024, Charge, MoveX/MoveY, overlit from
//                                      Charge == 4 and OverlitDeadlineTick) and challenge ChallengeFrameView.Living (HeldMoveX/Y)
//                                      and, for the test-host greybox only, greybox/types.ts PlayerView (already in world units).
//   WorldPresentation.ghost         <- the historical ghost position of a verified source trace (SoloGhostFrame). Never a living actor.
//   WorldPresentation.candles       <- ActiveCandleIds resolved through the verified CandlePlan. Not available in production yet.
//   ControlIntent                   -> multiplayer GameplayInput / solo SoloInputItem (adapter adds Seq, ClientTick, MatchId, epoch).
//                                      Same {moveX, moveY, requestBurst} triple the existing greybox keyboard hook already produces.
//
// Deliberately absent until a task needs them: expression/cue events (13.3, 13.6), history frames (13.10), results.

/** Which environment the world layer draws. `transition` is the CRT and smoke cover (13.6); `menu` is the calm pre-game backdrop. */
export type SceneMode = "menu" | "day" | "night" | "history" | "transition";

/** Screens that exist in this build. A later task adds an id here only together with a real screen. */
export type ScreenId = "home" | "historyStudy" | "arenaStudy" | "characterStudy" | "dayPlay" | "nightStudy" | "burstStudy" | "settings" | "howto" | "soundStudy" | "multiplayer" | "solo" | "histories" | "replay" | "race";

/** Zero-based server slot. Differs from the skill's illustrative 1..4: the audited server slots are zero-based (13.0 audit A3). */
export type ActorSlot = 0 | 1 | 2 | 3;

/** Numeric Soulfire charge, 0..4. 0 and 1 share a face and halo but the HUD still shows the true number. */
export type Charge = 0 | 1 | 2 | 3 | 4;

export interface WorldFrame {
  /** World units. Server authority is 1600x900 with 1024 fixed units per world unit (ArenaV1); fixed-point is converted by the adapter. */
  readonly width: number;
  readonly height: number;
}

export interface PlayerPresentation {
  readonly slot: ActorSlot;
  readonly x: number;
  readonly y: number;
  readonly charge: Charge;
  readonly overlit: boolean;
  /** Effective movement direction, used only for pupil gaze. Heads never rotate. */
  readonly gaze: { readonly x: -1 | 0 | 1; readonly y: -1 | 0 | 1 };
}

export interface WorldPresentation {
  readonly mode: Exclude<SceneMode, "menu" | "transition">;
  readonly world: WorldFrame;
  /** Absolute server tick the frame belongs to (0..2700 in solo). */
  readonly tick: number;
  readonly players: readonly PlayerPresentation[];
  /** Non-interactive historical ghost, drawn beneath the living actors. */
  readonly ghost?: { readonly x: number; readonly y: number };
  readonly candles: readonly { readonly id: string; readonly x: number; readonly y: number }[];
}

/** What the player is asking for, before any network detail. Burst is a held button; the server detects the rising edge. */
export interface ControlIntent {
  readonly moveX: -1 | 0 | 1;
  readonly moveY: -1 | 0 | 1;
  readonly requestBurst: boolean;
}
