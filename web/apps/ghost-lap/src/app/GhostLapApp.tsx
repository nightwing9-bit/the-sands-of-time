// Ghost Lap game shell. One persistent Stage; the active screen only chooses its layers. Server contact in 13.1 is limited to the
// existing contract handshake, shown as a quiet status. No guest session, hub or game endpoint is touched (that boundary is pending).
import { useEffect, useState } from "react";
import { createMarrowTransport, describeHandshake, type ApiResult, type HandshakeVerdict } from "@sands/contracts";
import { Stage } from "../stage/Stage.tsx";
import { WorldLayer } from "../stage/WorldLayer.tsx";
import { HomeScreen } from "../screens/HomeScreen.tsx";
import { ArenaStudyHud } from "../screens/ArenaStudy.tsx";
import { CharacterStudyHud } from "../screens/CharacterStudy.tsx";
import { PlayScreenHud } from "../screens/PlayScreen.tsx";
import { NightStudyHud } from "../screens/NightStudy.tsx";
import { BurstStudyHud } from "../screens/BurstStudy.tsx";
import type { ArenaWorld } from "../stage/ArenaWorld.ts";
import { HistoryDockStudy, HistoryStudyHud } from "../screens/HistoryStudy.tsx";
import { MultiplayerScreen, type MultiplayerEntry, type MultiplayerScene } from "../mp/MultiplayerScreen.tsx";
import { SoloScreen, type SoloScene } from "../solo/SoloScreen.tsx";
import { HistoriesScreen, type HistoriesFocus } from "../solo/HistoriesScreen.tsx";
import { ReplayScreen } from "../history/ReplayScreen.tsx";
import { RaceScreen, type RaceScene } from "../race/RaceScreen.tsx";
import type { RacePlan } from "../race/raceModel.ts";
import { parseReplayParams, targetFromParams, type ReplayTarget } from "../history/target.ts";
import { HowToPlayScreen } from "../screens/HowToPlayScreen.tsx";
import { SettingsScreen } from "../screens/SettingsScreen.tsx";
import { SoundStudyHud } from "../screens/SoundStudy.tsx";
import { SoundPill } from "../ui/SoundPill.tsx";
import { StatusPill } from "../ui/primitives.tsx";
import type { ScreenId } from "../presentation/types.ts";
import { SCREENS } from "./screens.ts";

const transport = createMarrowTransport({ apiBaseUrl: "", realtimeBaseUrl: "" });
const PREVIEW = import.meta.env.DEV; // preview fixtures never ship in a production build

function initialScreen(): ScreenId {
  const requested = new URLSearchParams(window.location.search).get("screen");
  if (requested === "dayPlay") return "dayPlay";
  if (requested === "settings" || requested === "howto") return requested;
  if (requested === "multiplayer") return "multiplayer";
  if (requested === "solo" || requested === "histories") return requested;
  if (requested === "replay" && parseReplayParams(window.location.search)) return "replay";
  return PREVIEW && (requested === "soundStudy" || requested === "historyStudy" || requested === "arenaStudy" || requested === "characterStudy" || requested === "dayPlay" || requested === "nightStudy" || requested === "burstStudy") ? requested : "home";
}

/** A refresh on a replay URL reopens the same authenticated history by id; the library row is not needed. */
function initialReplayTarget(): ReplayTarget | null {
  const p = parseReplayParams(window.location.search);
  return p ? targetFromParams(p) : null;
}

function ContractStatus() {
  const [handshake, setHandshake] = useState<ApiResult<HandshakeVerdict> | null>(null);
  useEffect(() => {
    const controller = new AbortController();
    void transport.negotiate(controller.signal).then((result) => !controller.signal.aborted && setHandshake(result));
    return () => controller.abort();
  }, []);
  const tone = handshake === null ? "neutral" : handshake.ok && handshake.value.compatible ? "ok" : "warn";
  return <StatusPill tone={tone} role="status" data-testid="contract-status">{describeHandshake(handshake)}</StatusPill>;
}

export function GhostLapApp() {
  const [screen, setScreen] = useState<ScreenId>(initialScreen);
  const [notice, setNotice] = useState("");
  const guides = false; // the Preview Fixtures strip (which toggled these) is removed; the overlays stay off.
  const geometry = false;
  const [arena, setArena] = useState<ArenaWorld | null>(null);
  // 13.8: the multiplayer journey is one screen whose backdrop follows the room (lobby on the calm snow, a match in the walled arena).
  const [mpScene, setMpScene] = useState<MultiplayerScene>("menu");
  const [mpEntry, setMpEntry] = useState<MultiplayerEntry>(() => (new URLSearchParams(window.location.search).get("entry") === "spectate" ? "spectate" : "create"));
  // 13.9: Solo follows its run the same way: the calm backdrop while choosing a head, the walled arena once a run exists.
  const [soloScene, setSoloScene] = useState<SoloScene>("menu");
  // 13.10: the replay screen's target, the element its dock is portalled into (below the stage), and what My Histories should re-select on return.
  const [replayTarget, setReplayTarget] = useState<ReplayTarget | null>(initialReplayTarget);
  const [dockEl, setDockEl] = useState<HTMLElement | null>(null);
  const [histFocus, setHistFocus] = useState<HistoriesFocus | null>(null);
  // 13.11: the Ghost Challenge / Fork journey. Its plan is never in the URL (an unfinished race can't be resumed, so a refresh starts at Home).
  const [racePlan, setRacePlan] = useState<RacePlan | null>(null);
  const [raceNonce, setRaceNonce] = useState(0);
  const [raceScene, setRaceScene] = useState<RaceScene>("history");
  const [raceDock, setRaceDock] = useState(false);
  const [raceFrom, setRaceFrom] = useState<"replay" | "histories">("histories");
  const def = SCREENS[screen];
  const scene = screen === "multiplayer" ? mpScene : screen === "solo" ? soloScene : screen === "race" ? raceScene : def.scene;
  const syncUrl = (next: ScreenId, target: ReplayTarget | null) => {
    const url = new URL(window.location.href);
    const current = url.searchParams.get("screen");
    const owned = (id: string | null) => id === "solo" || id === "histories" || id === "replay";
    if (owned(next)) url.searchParams.set("screen", next);
    else if (owned(current)) url.searchParams.delete("screen");
    else return;
    if (next === "replay" && target) { url.searchParams.set("replay", target.kind); url.searchParams.set("id", target.id); }
    else { url.searchParams.delete("replay"); url.searchParams.delete("id"); }
    window.history.replaceState(window.history.state, "", url);
  };
  const go = (next: ScreenId, entry?: MultiplayerEntry) => {
    syncUrl(next, null);
    setNotice("");
    if (entry) setMpEntry(entry);
    if (next !== "multiplayer") setMpScene("menu");
    if (next !== "solo") setSoloScene("menu");
    if (next !== "race") { setRaceDock(false); setRaceScene("history"); }
    setScreen(next);
  };
  const openReplay = (target: ReplayTarget) => {
    syncUrl("replay", target);
    setNotice("");
    setReplayTarget(target);
    setHistFocus({ kind: target.kind, id: target.id });
    setRaceDock(false);
    setRaceScene("history");
    setScreen("replay");
  };
  const openRace = (plan: RacePlan, from: "replay" | "histories") => {
    syncUrl("race", null);
    setNotice("");
    setRacePlan(plan);
    setRaceNonce((n) => n + 1);
    setRaceFrom(from);
    setRaceScene("history");
    setRaceDock(plan.kind === "Fork");
    setScreen("race");
  };
  /** Back from a race to where it was chosen: the original's replay (a Fork) or My Histories, on the same original. */
  const closeRace = () => {
    if (!racePlan) return go("histories");
    setRaceDock(false);
    setRaceScene("history");
    if (raceFrom === "replay") openReplay({ kind: "original", id: racePlan.originalId, original: null });
    else { setHistFocus({ kind: "original", id: racePlan.originalId }); go("histories"); }
  };
  return (
    <Stage
      scene={scene}
      world={<WorldLayer scene={scene} walls={(screen === "multiplayer" && mpScene === "day") || (screen === "solo" && soloScene === "day") || screen === "arenaStudy" || screen === "historyStudy" || screen === "replay" || screen === "race" || screen === "dayPlay" || screen === "nightStudy" || screen === "burstStudy"} geometryOverlay={geometry} onWorld={setArena} />}
      hud={
        screen === "home"
          ? <HomeScreen notice={notice} onNotice={setNotice} onGo={go} status={<><SoundPill /><ContractStatus /></>} />
          : screen === "arenaStudy"
            ? <ArenaStudyHud onBack={() => go("home")} />
            : screen === "characterStudy"
              ? <CharacterStudyHud world={arena} onBack={() => go("home")} />
              : screen === "burstStudy"
                ? <BurstStudyHud world={arena} onBack={() => go("home")} />
              : screen === "nightStudy"
                ? <NightStudyHud world={arena} onBack={() => go("home")} />
                : screen === "dayPlay"
                  ? <PlayScreenHud world={arena} onBack={() => go("home")} />
                  : screen === "multiplayer"
                    ? <MultiplayerScreen key={mpEntry} world={arena} entry={mpEntry} onBack={() => go("home")} onScene={setMpScene} />
                  : screen === "solo"
                    ? <SoloScreen world={arena} onBack={() => go("home")} onScene={setSoloScene} onHistories={() => go("histories")} />
                  : screen === "histories"
                    ? <HistoriesScreen onBack={() => go("home")} onSolo={() => go("solo")} onReplay={openReplay} onChallenge={(plan) => openRace(plan, "histories")} focus={histFocus} />
                  : screen === "replay" && replayTarget
                    ? <ReplayScreen key={`${replayTarget.kind}:${replayTarget.id}`} world={arena} target={replayTarget} dockHost={dockEl} onBack={() => go("histories")} onOpenOriginal={(id) => openReplay({ kind: "original", id, original: null })} onRace={(plan) => openRace(plan, "replay")} />
                  : screen === "race" && racePlan
                    ? <RaceScreen key={`${raceNonce}`} world={arena} plan={racePlan} dockHost={dockEl} onBack={closeRace} onScene={setRaceScene} onDock={setRaceDock}
                        onHistories={() => { setHistFocus({ kind: "original", id: racePlan.originalId }); go("histories"); }}
                        onCompare={(childId) => openReplay({ kind: "child", id: childId, child: null, source: null })}
                        onAgain={() => openRace(racePlan, raceFrom)} />
                  : screen === "howto"
                    ? <HowToPlayScreen onBack={() => go("home")} />
                  : screen === "settings"
                    ? <SettingsScreen onBack={() => go("home")} />
                    : screen === "soundStudy"
                      ? <SoundStudyHud onBack={() => go("home")} />
                      : <HistoryStudyHud onBack={() => go("home")} />
      }
      dock={screen === "replay" || (screen === "race" && raceDock) ? <div className="gl-dock-host" ref={setDockEl} data-testid="dock-host" /> : def.dock ? <HistoryDockStudy /> : null}
      showSafeGuides={PREVIEW && guides}
    />
  );
}
