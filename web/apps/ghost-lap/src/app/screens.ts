// The screen/scene ownership seam. A screen id decides which scene the persistent stage shows and whether the history dock exists.
// AppShell reads this table; screens never mount their own stage or canvas, and never create audio: they only claim a mode from the one AudioDirector (src/audio). Later tasks add one row per real screen:
// lobby/match (day), results, histories (menu), replay and compare (history + dock), challenge and fork (night).
import type { SceneMode, ScreenId } from "../presentation/types.ts";

export interface ScreenDef {
  readonly scene: SceneMode;
  /** True when the screen owns a bottom history dock (the stage re-fits above it). Ordinary live screens never do. */
  readonly dock: boolean;
}

export const SCREENS: Readonly<Record<ScreenId, ScreenDef>> = {
  home: { scene: "menu", dock: false },
  historyStudy: { scene: "history", dock: true },
  /** 13.2 art preview (development only): the whole arena with the nine walls and no HUD. */
  arenaStudy: { scene: "day", dock: false },
  /** 13.3 expression viewer (development only): the four heads on the snow. A fixture, not a character picker. */
  characterStudy: { scene: "day", dock: false },
  /** 13.4 day play: the live arena with the real HUD. The transport (production hub, or the dev-only test-host bridge) is chosen on the screen. */
  dayPlay: { scene: "day", dock: false },
  /** 13.5 day/night, candle and ghost study (development only): the same arena graded for night, with real candle art and the spectral ghost. */
  nightStudy: { scene: "night", dock: false },
  /** 13.6 Burst + transition study (development only): the real play path fed by a labelled fixture, then one CRT + smoke day/night switch. */
  burstStudy: { scene: "day", dock: false },
  /** 13.8 Multiplayer journey (production): setup, lobby, match, results. The scene follows the room phase: calm menu backdrop in the lobby, the live arena in a match; GhostLapApp overrides this default. */
  multiplayer: { scene: "menu", dock: false },
  /** 13.9 Solo Run (production): choose a head on the calm backdrop, then the live arena. The scene follows the run phase; GhostLapApp overrides this default. */
  solo: { scene: "menu", dock: false },
  /** 13.9 My Histories (production): the guest's own saved Originals and saved futures over the calm backdrop. No timeline dock here. */
  histories: { scene: "menu", dock: false },
  /** 13.10 Replay and compare (production): one saved history in the walled arena with the Sands of Time dock below it. The ONLY screen with a history dock besides the dev study. */
  replay: { scene: "history", dock: true },
  /** 13.11 Ghost Challenge and cinematic Fork (production): the rewind and preparation on the day arena (a Fork keeps the history dock for its rewind), then the night race. GhostLapApp overrides the scene and the dock. */
  race: { scene: "history", dock: false },
  /** 13.7 Settings: music and effects volume and mute. Real (production) screen over the calm menu backdrop. */
  settings: { scene: "menu", dock: false },
  /** How to Play (production): one static instruction page over the calm menu backdrop. */
  howto: { scene: "menu", dock: false },
  /** 13.7 sound study (development only): the listening path for day, night, candle, hit, Burst and wind-wind-knock. Labelled preview fixture. */
  soundStudy: { scene: "menu", dock: false },
};
