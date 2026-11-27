// The nine approved cues and their routing facts (Spec 13, task 13.7; skill ref 07). Pure data, no DOM.
//
// LOUDNESS. `trimDb` is a FIRST PASS, derived from the measured integrated loudness of the actual files (ffmpeg ebur128, 19 Nov 2026, see
// docs/architecture/25-ghostlap-spec13-7-audio.md) and NOT a listened mix. Themes are normalised to the night theme (-14.4 LUFS); wind and
// knock sit at the skill's ~80 / ~90 perceptual ratio under it (wind ~2 dB, knock ~1 dB below the theme after normalising); SFX are set by
// character (Burst tamed, click quiet). Every number is HUMAN REVIEW PENDING: the dev sound study has per-cue trim sliders so the listener
// reports numbers instead of the code guessing them.
import { SOUND_FILES } from "../config/assetRegistry.ts";

export const CUE_IDS = ["gameLoading", "multiplayerTheme", "ghostMainTheme", "candleEat", "playerHit", "blast", "menu", "wind", "knocking"] as const;
export type CueId = (typeof CUE_IDS)[number];
export type BusId = "music" | "sfx";

export interface CueSpec {
  readonly id: CueId;
  readonly url: string;
  /** Music bus follows the Music slider and mute (themes AND night ambience); SFX bus follows the Effects slider and mute. */
  readonly bus: BusId;
  /** First-pass level trim in dB, applied per source on top of the bus level. */
  readonly trimDb: number;
  /** What was measured in the source file: integrated LUFS (null: too short to gate) and peak dBFS. */
  readonly measured: { readonly lufs: number | null; readonly peakDb: number };
  readonly role: string;
}

export const CUES: Readonly<Record<CueId, CueSpec>> = {
  gameLoading: { id: "gameLoading", url: SOUND_FILES.gameLoading, bus: "music", trimDb: -2.1, measured: { lufs: -12.3, peakDb: 0.0 }, role: "menu / lobby / histories / settings loop" },
  multiplayerTheme: { id: "multiplayerTheme", url: SOUND_FILES.multiplayerTheme, bus: "music", trimDb: -2.4, measured: { lufs: -12.0, peakDb: -4.1 }, role: "active day multiplayer and original Solo loop" },
  ghostMainTheme: { id: "ghostMainTheme", url: SOUND_FILES.ghostMainTheme, bus: "music", trimDb: 0, measured: { lufs: -14.4, peakDb: -1.0 }, role: "active night loop (reference = 100)" },
  wind: { id: "wind", url: SOUND_FILES.wind, bus: "music", trimDb: -6.3, measured: { lufs: -10.1, peakDb: 0.0 }, role: "night ambience segment (~80 relative); the file is hot, so it is trimmed hard" },
  knocking: { id: "knocking", url: SOUND_FILES.knocking, bus: "music", trimDb: 2.0, measured: { lufs: -17.4, peakDb: -0.5 }, role: "night ambience segment (~90 relative); the file is quiet, so it is lifted" },
  candleEat: { id: "candleEat", url: SOUND_FILES.candleEat, bus: "sfx", trimDb: 0, measured: { lufs: null, peakDb: -0.2 }, role: "living candle collected" },
  playerHit: { id: "playerHit", url: SOUND_FILES.playerHit, bus: "sfx", trimDb: 1, measured: { lufs: -17.8, peakDb: -1.0 }, role: "weaker player takes the contact drain" },
  blast: { id: "blast", url: SOUND_FILES.blast, bus: "sfx", trimDb: -6, measured: { lufs: -6.2, peakDb: -0.2 }, role: "confirmed Soulfire Burst (the loudest file; tamed)" },
  menu: { id: "menu", url: SOUND_FILES.menu, bus: "sfx", trimDb: -4, measured: { lufs: null, peakDb: -0.2 }, role: "real UI activation" },
};

/** Which looping music a presentation mode plays. `transition` is deliberately silent music-wise: no new theme, no invented transition sound. */
export type AudioMode = "menu" | "day" | "transition" | "night";
export const MUSIC_FOR_MODE: Readonly<Record<AudioMode, CueId | null>> = {
  menu: "gameLoading",
  day: "multiplayerTheme",
  transition: null,
  night: "ghostMainTheme",
};

/** Night ambience, strictly in this order and repeating: two complete winds, then one complete knock. */
export const AMBIENCE_SEQUENCE: readonly CueId[] = ["wind", "wind", "knocking"];

/** Real semantic game events that own a sound. A key press, a hover or a ghost cue is not one of them. */
export type SfxKind = "candle" | "hit" | "burst";
export const SFX_CUE: Readonly<Record<SfxKind, CueId>> = { candle: "candleEat", hit: "playerHit", burst: "blast" };

/** Decode/fetch order after unlock: what the menu needs first, then the small SFX, then the day theme, then the night set. */
export const PRELOAD_ORDER: readonly CueId[] = ["gameLoading", "menu", "candleEat", "playerHit", "blast", "multiplayerTheme", "wind", "knocking", "ghostMainTheme"];
