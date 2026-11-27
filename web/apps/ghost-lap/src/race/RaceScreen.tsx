// The Ghost Challenge and the cinematic historical Fork (Spec 13, task 13.11): the one pivotal journey of the game.
//
//   Fork:       replay -> Fork here (T) -> [genuine rewind through RECORDED frames, ending exactly at T] -> truthful "Preparing history" until the
//               server says Ready -> CRT + smoke, night swapped in under cover -> the world at T re-entered, ghost and head on the same recorded
//               state -> the player presses Begin -> StartChallenge -> a real, independent future from T+1 beside the immutable ghost -> save.
//   Challenge:  My Histories / replay -> Ghost Challenge -> Preparing -> CRT + smoke -> the arena at T0 -> Begin -> the whole round -> save.
//
// What cannot be faked, and is not:
//   * The server's clock starts with the StartChallenge reply. Nothing here pauses or rewinds a running game: the build-up happens BEFORE it, on
//     recorded history and on the server's own Ready reply, and the player's press of Begin is what starts the clock.
//   * The cross from day to night needs the server's verified Ready (mayCross). A smoke cover or a timer can never stand in for it; if Ready is slow
//     the player sees "Preparing history" on the recorded frame, and if it fails they see why.
//   * The rewind shows only recorded states that are really held (montageStartTick), at integer ticks, never interpolated, never a video.
//   * The ghost is the server's own ChallengeFrame.ghost. It is never offset to prove divergence and never interacts.
// Not here: the Marrow Timeline, any backend history, the rules, scoring, collisions, Burst, ghost replay or fork-tick semantics.
import { createPortal } from "react-dom";
import { useCallback, useEffect, useMemo, useReducer, useRef, useState } from "react";
import { BackLink, FrostPanel, GameButton, StatusPill } from "../ui/primitives.tsx";
import { HourglassMark } from "../ui/HourglassMark.tsx";
import type { ArenaWorld } from "../stage/ArenaWorld.ts";
import { CharacterTextures } from "../characters/CharacterAssets.ts";
import { CandleTextures } from "../candles/CandleAssets.ts";
import { SmokeTextures } from "../fx/SmokeAssets.ts";
import { SceneTransition } from "../fx/SceneTransition.ts";
import { prefersReducedMotion } from "../fx/motion.ts";
import { PlaySession, type PlayHud } from "../play/PlaySession.ts";
import { PlayScene } from "../play/PlayScene.ts";
import type { RaceReadout } from "../play/playTypes.ts";
import { bindKeyboard, type KeyboardBinding } from "../play/keyboardIntent.ts";
import { attachPlayAudio } from "../audio/playAudio.ts";
import { getAudio } from "../audio/instance.ts";
import { useAudioMode } from "../audio/react.ts";
import type { AudioMode } from "../audio/cues.ts";
import { clock, Soulfire } from "../screens/PlayScreen.tsx";
import type { ActorSlot } from "../presentation/types.ts";
import { HistoryScene } from "../history/HistoryScene.ts";
import { HistoryDock } from "../history/HistoryDock.tsx";
import { describeLoadError, historyKey, openHistory, type OpenedHistory } from "../history/historyStore.ts";
import { fetchOriginalChunk } from "../history/historyWire.ts";
import { clockText, signed } from "../history/model.ts";
import { cap, mascotName, shortHash, shortId } from "../solo/model.ts";
import { SoloHubError } from "../solo/soloWire.ts";
import { RaceTransport } from "./RaceTransport.ts";
import {
  checkStart, clearPendingRace, firstOwnTick, interpretFinish, interpretFinishError, mayCross, montageDurationMs, montageStartTick, montageTick, newRaceKey, raceTitle,
  RACE_RETRY_MS, writePendingRace, type RacePlan, type RaceSave, type StartCheck,
} from "./raceModel.ts";
import { raceFailureText, type RacePreparedWire } from "./raceWire.ts";

export type RaceScene = "history" | "night";
type Phase = "opening" | "rewinding" | "preparing" | "crossing" | "ready" | "starting" | "racing" | "finished" | "failed" | "interrupted";
interface Run { readonly session: PlaySession; readonly scene: PlayScene; readonly keys: KeyboardBinding; readonly abort: AbortController; readonly audioOff: () => void }
interface Failure { readonly text: string; readonly where: "prepare" | "start" | "assets" | "lost" }

export interface RaceScreenProps {
  readonly world: ArenaWorld | null;
  readonly plan: RacePlan;
  /** The element below the stage the history dock is portalled into (a Fork keeps the dock for its rewind, then it leaves under the smoke). */
  readonly dockHost: HTMLElement | null;
  readonly onBack: () => void;
  readonly onScene: (scene: RaceScene) => void;
  readonly onDock: (open: boolean) => void;
  readonly onHistories: () => void;
  readonly onCompare: (childId: string, originalId: string) => void;
  /** Starts the same race again from the top (a fresh operation key, a fresh preparation). */
  readonly onAgain: () => void;
}

const sleep = (ms: number, signal: AbortSignal) => new Promise<void>((resolve) => {
  if (signal.aborted) return resolve();
  const timer = setTimeout(resolve, ms);
  signal.addEventListener("abort", () => { clearTimeout(timer); resolve(); }, { once: true });
});

const audioModeOf = (phase: Phase): AudioMode => (phase === "crossing" || phase === "ready" || phase === "starting" ? "transition" : phase === "racing" ? "night" : "menu");

export function RaceScreen({ world, plan, dockHost, onBack, onScene, onDock, onHistories, onCompare, onAgain }: RaceScreenProps) {
  const isFork = plan.kind === "Fork";
  const slot = Math.min(3, Math.max(0, plan.mascotId)) as ActorSlot;
  const [, bump] = useReducer((n: number) => n + 1, 0);
  const [phase, setPhaseState] = useState<Phase>("opening");
  const phaseRef = useRef<Phase>("opening");
  const setPhase = useCallback((next: Phase | ((p: Phase) => Phase)) => {
    const value = typeof next === "function" ? next(phaseRef.current) : next;
    phaseRef.current = value;
    setPhaseState(value);
  }, []);
  const [attempt, setAttempt] = useState(0);
  const [prepared, setPrepared] = useState<RacePreparedWire | null>(null);
  const [failure, setFailure] = useState<Failure | null>(null);
  const [characters, setCharacters] = useState<CharacterTextures | null>(null);
  const [candles, setCandles] = useState<CandleTextures | null>(null);
  const [smoke, setSmoke] = useState<SmokeTextures | null>(null);
  const [smokeSettled, setSmokeSettled] = useState(false);
  const [history, setHistory] = useState<OpenedHistory | null>(null);
  const [sceneReady, setSceneReady] = useState(false);
  const [transitionReady, setTransitionReady] = useState(false);
  const [montage, setMontage] = useState(plan.forkTick);
  const [montageDone, setMontageDone] = useState(false);
  const [dockOpen, setDockOpen] = useState(isFork);
  const [run, setRun] = useState<Run | null>(null);
  const [hud, setHud] = useState<PlayHud | null>(null);
  const [readout, setReadout] = useState<RaceReadout | null>(null);
  const [check, setCheck] = useState<StartCheck | null>(null);
  const [save, setSave] = useState<RaceSave | null>(null);
  const [leaving, setLeaving] = useState(false);
  const [arenaOnly, setArenaOnly] = useState(false);

  const transportRef = useRef<RaceTransport | null>(null);
  const keyRef = useRef<string>("");
  const histRef = useRef<HistoryScene | null>(null);
  const runRef = useRef<Run | null>(null);
  const transitionRef = useRef<SceneTransition | null>(null);
  const montageRaf = useRef(0);
  const montageStarted = useRef(false);
  const lastLiveScore = useRef<{ you: number; source: number; tick: number } | null>(null);
  const callbacks = useRef({ onScene, onDock });
  callbacks.current = { onScene, onDock };

  useAudioMode(audioModeOf(phase));

  // ---- the original's recorded states: the same cache the replay screen uses ------------------------------------------------------------
  useEffect(() => {
    const hash = plan.finalStateHash ?? "?", vk = plan.versionKey ?? "?";
    const opened = openHistory(
      historyKey({ kind: "original", id: plan.originalId, finalStateHash: hash, versionKey: vk }),
      { kind: "original", id: plan.originalId, ...(plan.finalStateHash ? { finalStateHash: plan.finalStateHash } : {}), ...(plan.versionKey ? { versionKey: plan.versionKey } : {}) },
      (id) => (from, count, signal) => fetchOriginalChunk(id, from, count, signal),
    );
    setHistory(opened);
    const off = opened.loader.subscribe(bump);
    opened.loader.want(plan.forkTick);
    opened.loader.start();
    return () => { off(); opened.loader.abort(); };
  }, [plan]);

  // ---- assets: the shared textures every other screen uses ---------------------------------------------------------------------------------
  useEffect(() => {
    const abort = new AbortController();
    let c: CharacterTextures | null = null, k: CandleTextures | null = null, s: SmokeTextures | null = null;
    const fail = (e: unknown) => { if (!abort.signal.aborted) { setFailure({ where: "assets", text: `The scene could not load: ${String(e)}` }); setPhase("failed"); } };
    CharacterTextures.load(abort.signal).then((t) => { if (abort.signal.aborted) return t.destroy(); c = t; setCharacters(t); }, fail);
    CandleTextures.load(abort.signal).then((t) => { if (abort.signal.aborted) return t.destroy(); k = t; setCandles(t); }, fail);
    // Without the smoke sheet the transition falls back to its plain cover (SceneTransition): still honest, still gated on Ready.
    SmokeTextures.load(abort.signal).then((t) => { if (abort.signal.aborted) return t.destroy(); s = t; setSmoke(t); setSmokeSettled(true); }, () => { if (!abort.signal.aborted) setSmokeSettled(true); });
    return () => { abort.abort(); c?.destroy(); k?.destroy(); s?.destroy(); setCharacters(null); setCandles(null); setSmoke(null); setSmokeSettled(false); };
  }, [setPhase]);

  useEffect(() => {
    if (!world || !smokeSettled) return;
    const t = new SceneTransition(world, smoke);
    transitionRef.current = t;
    setTransitionReady(true);
    return () => { t.dispose(); if (transitionRef.current === t) transitionRef.current = null; setTransitionReady(false); };
  }, [world, smoke, smokeSettled]);

  // ---- the day scene: the head of the original, drawn from recorded states --------------------------------------------------------------------
  useEffect(() => {
    if (!world || !characters) return;
    const scene = new HistoryScene(world, characters, slot);
    histRef.current = scene;
    setSceneReady(true);
    return () => { scene.dispose(); if (histRef.current === scene) histRef.current = null; setSceneReady(false); };
  }, [world, characters, slot]);

  useEffect(() => { callbacks.current.onScene("history"); callbacks.current.onDock(isFork); }, [isFork]);

  // ---- prepare: ask the server; Ready or a typed failure. Nothing starts. ----------------------------------------------------------------------
  useEffect(() => {
    const key = newRaceKey();
    keyRef.current = key;
    const transport = new RaceTransport({ kind: plan.kind, originalId: plan.originalId, forkTick: plan.forkTick, mascotId: plan.mascotId, operationKey: key });
    transportRef.current = transport;
    setPrepared(null); setFailure(null);
    const abort = new AbortController();
    transport.onLost = (reason, started) => {
      if (abort.signal.aborted) return;
      setFailure({ where: "lost", text: reason });
      if (started) clearPendingRace(); // an interrupted race is never a child, so there is nothing to recover
      setPhase(started ? "interrupted" : "failed");
    };
    // A deferred start: React StrictMode mounts, unmounts and mounts again in development, and only the last mount may prepare on the server.
    const timer = setTimeout(() => {
      transport.prepare(abort.signal).then(
        (p) => { if (!abort.signal.aborted) setPrepared(p); },
        (e: unknown) => { if (!abort.signal.aborted) { setFailure({ where: "prepare", text: raceFailureText(e) }); setPhase("failed"); } },
      );
    }, 0);
    return () => {
      clearTimeout(timer);
      abort.abort();
      if (runRef.current?.session && transportRef.current === transport) return; // a running race is torn down by teardownRun, not by a re-prepare
      transport.dispose();
      if (transportRef.current === transport) transportRef.current = null;
    };
  }, [plan, attempt, setPhase]);

  // ---- the rewind: recorded frames, newest first, ending exactly at T. Starts as soon as T itself is held. -----------------------------------------
  const frames = history?.frames ?? null;
  const rev = frames?.version ?? 0;
  useEffect(() => {
    if (phaseRef.current !== "opening" || !sceneReady || !frames || montageStarted.current) return;
    const scene = histRef.current;
    if (!scene || !frames.has(plan.forkTick)) return;
    montageStarted.current = true;
    const show = (tick: number) => { const a = frames.get(tick); if (a) scene.setPose({ a, b: null, frac: 0 }); };
    const start = isFork ? montageStartTick(plan.forkTick, frames.terminalTick, (t) => frames.has(t)) : plan.forkTick;
    const duration = isFork && !prefersReducedMotion() ? montageDurationMs(start - plan.forkTick) : 0;
    if (duration === 0) {
      show(plan.forkTick); setMontage(plan.forkTick); setMontageDone(true); setPhase((p) => (p === "opening" ? "preparing" : p));
      return;
    }
    show(start); setMontage(start); setPhase((p) => (p === "opening" ? "rewinding" : p));
    const t0 = performance.now();
    let last = start;
    const step = (now: number) => {
      const u = Math.min(1, (now - t0) / duration);
      const tick = montageTick(u, start, plan.forkTick);
      if (tick !== last) { last = tick; show(tick); setMontage(tick); }
      if (u >= 1) { show(plan.forkTick); setMontage(plan.forkTick); setMontageDone(true); setPhase((p) => (p === "rewinding" ? "preparing" : p)); return; }
      montageRaf.current = requestAnimationFrame(step);
    };
    montageRaf.current = requestAnimationFrame(step);
  }, [phase, sceneReady, frames, rev, plan.forkTick, isFork, setPhase]);
  useEffect(() => () => cancelAnimationFrame(montageRaf.current), []);

  // ---- the cross: day -> night, only on the server's verified Ready ---------------------------------------------------------------------------
  const teardownRun = useCallback(() => {
    const r = runRef.current;
    if (!r) return;
    runRef.current = null;
    r.keys.dispose();
    r.audioOff();
    r.abort.abort();
    r.scene.dispose();
    r.session.dispose(); // disposes the transport: neutral input, stream end, connection stop
    setRun(null);
  }, []);
  useEffect(() => () => { histRef.current?.dispose(); histRef.current = null; teardownRun(); transportRef.current?.dispose(); }, [teardownRun]);

  const saveRace = useCallback(async (transport: RaceTransport, signal: AbortSignal) => {
    const ask = { kind: plan.kind, originalId: plan.originalId, forkTick: plan.forkTick, key: transport.operationKey };
    let tries = 0;
    while (!signal.aborted) {
      setSave({ kind: "saving", attempt: tries });
      let state: RaceSave;
      try {
        const view = await transport.finish();
        tries += 1;
        state = interpretFinish(view, ask, tries);
      } catch (error) {
        state = error instanceof SoloHubError ? interpretFinishError(error.code, raceFailureText(error)) : interpretFinishError(null, raceFailureText(error));
      }
      if (signal.aborted) return;
      setSave(state);
      if (state.kind === "saved" || state.kind === "interrupted") clearPendingRace();
      if (state.kind !== "saving") return;
      await sleep(RACE_RETRY_MS[Math.min(tries - 1, RACE_RETRY_MS.length - 1)]!, signal);
    }
  }, [plan]);

  /** Under full smoke cover: the day scene goes, the night scene (the same PlayScene the race will be played in) comes, on the recorded state at T. */
  const swapToNight = useCallback(() => {
    const transport = transportRef.current;
    const rec = frames?.get(plan.forkTick);
    if (!world || !characters || !candles || !transport || !rec || runRef.current) return;
    histRef.current?.dispose();
    histRef.current = null;
    const session = new PlaySession(transport);
    const scene = new PlayScene(world, characters, session, candles);
    scene.setMode("night");
    const keys = bindKeyboard((intent, reason) => session.intent(intent, reason, performance.now()), () => !!session.frame?.you.canControl && session.hud(performance.now()).connection.kind === "live");
    const abort = new AbortController();
    session.subscribe(() => {
      const next = session.hud(performance.now());
      if (!next.you?.canControl || next.connection.kind !== "live") keys.reset();
      const f = session.frame;
      if (f?.race && f.players[0]) lastLiveScore.current = { you: f.players[0].totalScore, source: f.race.sourceScore, tick: f.tick };
      setHud(next);
      setReadout(f?.race ?? null);
    });
    const audioOff = attachPlayAudio(session, getAudio());
    transport.onEnded = (e) => {
      keys.reset();
      if (e.reason === "final") { setPhase("finished"); void saveRace(transport, abort.signal); }
      else { clearPendingRace(); setFailure({ where: "lost", text: e.code ? `The server interrupted the race (${e.code}). It was not saved.` : "The server interrupted the race. It was not saved." }); setPhase("interrupted"); }
    };
    const next: Run = { session, scene, keys, abort, audioOff };
    runRef.current = next;
    setRun(next);
    setHud(session.hud(performance.now()));
    transport.showRecorded(rec);
    callbacks.current.onScene("night");
    callbacks.current.onDock(false);
    setDockOpen(false);
  }, [world, characters, candles, frames, plan.forkTick, saveRace, setPhase]);

  useEffect(() => {
    if (phase !== "preparing" || !transitionReady || !candles || !characters) return;
    const rec = frames?.get(plan.forkTick);
    if (!mayCross({ prepared: prepared !== null, recordedAtStart: !!rec, needsMontage: true, montageDone })) return;
    const t = transitionRef.current;
    if (!t) return;
    setPhase("crossing");
    t.play({ crt: true, smoke: true, onCovered: swapToNight, onDone: (how) => { if (how === "complete") setPhase((p) => (p === "crossing" ? "ready" : p)); } });
  }, [phase, prepared, montageDone, transitionReady, candles, characters, frames, rev, plan.forkTick, swapToNight, setPhase]);

  // ---- begin: the ONE moment the server's clock starts --------------------------------------------------------------------------------------
  const begin = useCallback(async () => {
    const transport = transportRef.current;
    if (phaseRef.current !== "ready" || !transport) return;
    setPhase("starting");
    (document.activeElement as HTMLElement | null)?.blur?.();
    try {
      const started = await transport.begin();
      setCheck(checkStart(started, frames?.get(plan.forkTick)));
      writePendingRace({ key: transport.operationKey, kind: plan.kind, originalId: plan.originalId, forkTick: plan.forkTick, startedAtMs: Date.now() });
      setPhase((p) => (p === "starting" ? "racing" : p));
    } catch (error) {
      setFailure({ where: "start", text: raceFailureText(error) });
      setPhase("failed");
    }
  }, [frames, plan, setPhase]);

  // ---- leaving -------------------------------------------------------------------------------------------------------------------------------
  const leaveNow = useCallback(() => {
    const transport = transportRef.current;
    const started = phaseRef.current === "racing" || phaseRef.current === "finished";
    if (!started) void transport?.cancel(); // nothing was started: tell the server to drop its prepared session now
    cancelAnimationFrame(montageRaf.current);
    transitionRef.current?.cancel();
    teardownRun();
    onBack();
  }, [teardownRun, onBack]);
  const requestLeave = useCallback(() => { if (phaseRef.current === "racing" || phaseRef.current === "starting") setLeaving(true); else leaveNow(); }, [leaveNow]);
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== "Escape") return;
      if (leaving) setLeaving(false); else if (phaseRef.current !== "finished") requestLeave();
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [leaving, requestLeave]);

  const retry = useCallback(() => {
    // Still on the recorded day frame (nothing crossed yet): ask the server again, on a fresh connection, without replaying the rewind.
    if (failure?.where === "prepare" && !runRef.current) { setPhase(montageDone ? "preparing" : montageStarted.current ? "rewinding" : "opening"); setAttempt((n) => n + 1); }
    else onAgain();
  }, [failure, montageDone, onAgain, setPhase]);

  // ---- words ----------------------------------------------------------------------------------------------------------------------------------
  const terminal = frames?.terminalTick ?? 2700;
  const loadFailure = history?.loader.status.kind === "failed" ? history.loader.status.failure : null;
  const blocked = !!frames && !frames.has(plan.forkTick) && !!loadFailure;
  const chip = `Original · ${cap(mascotName(plan.mascotId))} · #${shortId(plan.originalId)}`;
  const you = useMemo(() => (hud?.you ? hud.players.find((p) => p.slot === hud.you!.slot) : undefined), [hud]);
  const transport = transportRef.current;
  const title = raceTitle(plan.kind);
  const racingHud = (phase === "racing" || phase === "finished" || phase === "interrupted") && hud?.identity;
  const lastScore = lastLiveScore.current;
  // The cards sit in a side rail, on the side AWAY from the head at the chosen tick, so the re-entered world is never hidden behind them.
  const atStart = frames?.get(plan.forkTick);
  const side: "left" | "right" = atStart && atStart.xFixed / 1024 < 800 ? "right" : "left";

  return (
    <div className="gl-race" data-testid="race-screen" data-phase={phase} data-kind={plan.kind} data-fork-tick={plan.forkTick} data-side={side} data-prepared={prepared ? "1" : "0"} data-montage={montage}>
      {phase !== "crossing" && phase !== "racing" && phase !== "finished" ? (
        <div className="gl-study-top"><BackLink onClick={requestLeave}>{phase === "ready" || phase === "preparing" || phase === "opening" || phase === "rewinding" ? "Cancel" : "My Histories"}</BackLink></div>
      ) : null}

      {phase === "opening" && !blocked ? <FrostPanel className="gl-hd-state gl-race-card" role="status" data-testid="race-opening">Opening your original…</FrostPanel> : null}
      {blocked && phase !== "failed" ? (
        <FrostPanel className="gl-hd-state gl-race-card" role="alert" data-testid="race-load-failed">
          <p>{describeLoadError(loadFailure).text}</p>
          <div className="gl-race-actions">
            {loadFailure?.retryable ? <GameButton variant="primary" onClick={() => history?.loader.retry()}>Try again</GameButton> : null}
            <GameButton onClick={leaveNow}>Back</GameButton>
          </div>
        </FrostPanel>
      ) : null}

      {phase === "rewinding" ? (
        <FrostPanel className="gl-race-rewind" role="status" aria-live="off" data-testid="race-rewind">
          <p className="gl-eyebrow">Fork · going back</p>
          <p className="gl-race-rewind-tick" data-testid="race-rewind-tick">T{montage}</p>
          <p className="gl-hd-parts">{clockText(montage)} · back to T{plan.forkTick} through your recorded run</p>
        </FrostPanel>
      ) : null}

      {phase === "preparing" ? (
        <FrostPanel className="gl-hd-state gl-race-card" role="status" data-testid="race-preparing">
          <p className="gl-eyebrow">{title}{isFork ? ` · T${plan.forkTick}` : ""}</p>
          <h2 className="gl-race-title">Preparing history</h2>
          <p className="gl-race-body">{isFork ? `You're back at T${plan.forkTick} (${clockText(plan.forkTick)}). ` : ""}The server is verifying your original and holding its ghost. The race starts only when it says Ready.</p>
          <div className="gl-race-wait" aria-hidden="true"><HourglassMark elapsed={Math.min(1, montage / terminal)} flowing label="Preparing" /><i /></div>
          <div className="gl-race-actions"><GameButton onClick={leaveNow} data-testid="race-cancel">Cancel</GameButton></div>
        </FrostPanel>
      ) : null}

      {phase === "ready" ? (
        <FrostPanel className="gl-race-ready" role="region" aria-label="Ready to begin" data-testid="race-ready">
          <p className="gl-eyebrow">{title} · ready</p>
          <h2 className="gl-race-title">{isFork ? `Back at T${plan.forkTick}` : "The arena, from the start"}</h2>
          <p className="gl-race-body" data-testid="race-ready-text">
            {isFork && plan.forkTick >= 2699
              ? `Only one tick is left. The world is exactly as you left it at ${clockText(plan.forkTick)}, and the cyan ghost repeats your original to the end. Your first decision is for T${firstOwnTick(plan)}, the last moment of the round.`
              : isFork
              ? `The world is exactly as you left it at ${clockText(plan.forkTick)}. The cyan ghost repeats your original from here. Everything from T${firstOwnTick(plan)} on is yours.`
              : "The same arena at T0. The cyan ghost repeats your whole original. Everything you do now is new."}
          </p>
          <p className="gl-race-fine">The clock starts the moment you press Begin, and it cannot be paused.</p>
          {transport?.delivery === "basic" ? <p className="gl-race-fine gl-race-limit" data-testid="race-candle-limit">This server doesn't send candle positions for races yet, so no candles are drawn. Your head, the ghost, scores and Burst are live.</p> : null}
          <div className="gl-race-actions">
            <GameButton variant="primary" onClick={() => void begin()} autoFocus data-testid="race-begin">{isFork ? `Begin from T${plan.forkTick}` : "Begin the race"}</GameButton>
          </div>
        </FrostPanel>
      ) : null}

      {phase === "starting" ? <FrostPanel className="gl-race-ready" role="status" data-testid="race-starting">Starting…</FrostPanel> : null}

      {racingHud && hud ? (
        <RaceHud hud={hud} you={you} readout={readout} plan={plan} phase={phase} onLeave={requestLeave} check={check} candleLimit={transport?.delivery === "basic"} />
      ) : null}

      {phase === "finished" && run ? (
        arenaOnly ? (
          <div className="gl-mp-arena-bar"><GameButton variant="primary" onClick={() => setArenaOnly(false)} data-testid="race-show-finish">Show result</GameButton></div>
        ) : (
          <FinishPanel plan={plan} save={save} last={lastScore} onRetry={() => { if (transport) { setSave({ kind: "saving", attempt: 0 }); void saveRace(transport, run.abort.signal); } }}
            onCompare={(id) => onCompare(id, plan.originalId)} onHistories={onHistories} onAgain={onAgain} onViewArena={() => setArenaOnly(true)} />
        )
      ) : null}

      {phase === "failed" || phase === "interrupted" ? (
        <FrostPanel className="gl-hd-state gl-race-card" role="alert" data-testid={phase === "failed" ? "race-failed" : "race-interrupted"}>
          <p className="gl-eyebrow">{title}</p>
          <h2 className="gl-race-title">{phase === "interrupted" ? "Race not saved" : failure?.where === "start" ? "It didn't start" : failure?.where === "prepare" ? "Couldn't prepare this history" : "Couldn't continue"}</h2>
          <p className="gl-race-body" data-testid="race-failure-text">{failure?.text ?? "Something went wrong."}</p>
          <p className="gl-race-fine">{phase === "interrupted" ? "An unfinished race can't be resumed and is never saved. Your original and your other futures are untouched." : "Nothing was started and your original is untouched."}</p>
          <div className="gl-race-actions">
            <GameButton variant="primary" onClick={retry} data-testid="race-retry">{failure?.where === "prepare" ? "Try again" : "Start over"}</GameButton>
            <GameButton onClick={leaveNow}>Back</GameButton>
          </div>
        </FrostPanel>
      ) : null}

      {leaving ? (
        <div className="gl-play-leave gl-panel" role="alertdialog" aria-label="Leave the race?" data-testid="race-leave">
          <p>Leave this race? It can't be paused or resumed, and a race that isn't finished is never saved.</p>
          <div>
            <GameButton variant="primary" onClick={() => { setLeaving(false); leaveNow(); }} data-testid="race-leave-confirm">Leave</GameButton>
            <GameButton onClick={() => setLeaving(false)}>Keep playing</GameButton>
          </div>
        </div>
      ) : null}

      {dockOpen && dockHost && frames ? createPortal(
        <HistoryDock
          kind="original" terminal={terminal} selected={plan.forkTick} cursor={montage} playing={false} buffering={phase === "preparing"}
          loadedRuns={frames.loadedRuns()} markers={frames.markers} splitTick={null} ribbon={null} chip={chip}
          loadingText={phase === "rewinding" ? `Going back to T${plan.forkTick}` : phase === "preparing" ? "Preparing history…" : phase === "crossing" ? "Re-entering the world…" : null}
          fork={{ verdict: { allowed: true, reason: null }, available: false, note: null }}
          onSeek={noop} onPlayPause={noop} onStep={noop} locked lockedNote={phase === "rewinding" ? `Rewinding to T${plan.forkTick}…` : "Forking this moment…"}
        />, dockHost) : null}
    </div>
  );
}

const noop = () => undefined;

// ---- the live HUD: slim edge zones over the one arena; every number is the server's --------------------------------------------------------------
function RaceHud({ hud, you, readout, plan, phase, onLeave, check, candleLimit }: {
  hud: PlayHud; you: PlayHud["players"][number] | undefined; readout: RaceReadout | null; plan: RacePlan; phase: Phase; onLeave: () => void; check: StartCheck | null; candleLimit: boolean;
}) {
  const conn = hud.connection;
  const over = phase === "finished";
  const live = conn.kind === "live" || conn.kind === "ended";
  return (
    <div className="gl-play gl-solo-hud gl-race-hud" data-testid="race-hud">
      <div className="gl-play-top">
        <BackLink onClick={onLeave}>{over ? "My Histories" : "Leave"}</BackLink>
        {!over ? (
          <StatusPill tone={live ? "ok" : conn.kind === "connecting" ? "neutral" : "warn"} role="status" data-testid="hud-connection" data-state={conn.kind}>
            {conn.kind === "live" ? "Live" : conn.kind === "connecting" ? "Connecting…" : conn.kind === "degraded" ? "Connection trouble" : conn.kind === "failed" ? "Disconnected" : "Ended"}
          </StatusPill>
        ) : null}
        <span className="gl-race-kind" data-testid="race-kind">{plan.kind === "Fork" ? `Fork · from T${plan.forkTick}` : "Ghost Challenge"}</span>
      </div>

      <div className="gl-play-clock" data-testid="hud-clock" data-tick={hud.tick} aria-label="Time remaining">{clock(over ? 0 : hud.secondsLeft)}</div>

      {you ? (
        <div className="gl-play-me gl-race-me" data-testid="hud-you">
          <div className="gl-play-score" data-testid="hud-score" data-score={you.totalScore}>{you.totalScore}</div>
          <Soulfire charge={you.charge} />
          {you.overlit && you.overlitDeadlineTick !== null && !over ? (
            <span className="gl-play-overlit" data-testid="hud-overlit">Overlit · auto-Burst in {Math.max(0, (you.overlitDeadlineTick - hud.tick) / hud.tickRate).toFixed(1)} s</span>
          ) : null}
          {readout ? (
            <div className="gl-race-source" data-testid="race-source">
              <span className="gl-race-ghostmark" aria-hidden="true" />
              <span>Original <b data-testid="race-source-score">{readout.sourceScore}</b></span>
              <span className="gl-race-delta" data-testid="race-delta" data-sign={Math.sign(readout.delta)}>Δ {signed(readout.delta)}</span>
            </div>
          ) : null}
          <span className="gl-play-parts">at the same tick · your future from T{firstOwnTick(plan)}</span>
        </div>
      ) : null}

      {!over && conn.kind === "degraded" ? (
        <div className="gl-play-banner" role="alert" data-kind="degraded" data-testid="hud-banner">
          <strong>Connection trouble</strong><span>{conn.reason}</span>
          <span>The picture is the last confirmed state, not live play. A dropped race can't be resumed.</span>
        </div>
      ) : null}

      {check && !over ? (
        <span className="gl-race-check" data-testid="race-check" data-state={check.kind} role={check.kind === "differs" ? "alert" : "status"}>
          {check.kind === "matched" ? "✓ " : check.kind === "differs" ? "! " : ""}{check.detail}
        </span>
      ) : null}
      {!over && candleLimit ? <span className="gl-play-note gl-solo-limit" data-testid="race-limit">No candle positions from this server yet: none are drawn.</span> : null}
      {!over && hud.identity && !hud.eventsComplete && !candleLimit ? <span className="gl-play-note gl-solo-limit" data-testid="race-gap">Some event cues were missed. The arena shows the latest confirmed state.</span> : null}
    </div>
  );
}

// ---- recorded: saving -> saved and verified ---------------------------------------------------------------------------------------------------
const TITLE: Readonly<Record<RaceSave["kind"], string>> = { saving: "Saving your future…", pending: "Still saving", saved: "Future recorded", failed: "Couldn't save this race", interrupted: "Race not saved" };

function FinishPanel(p: {
  plan: RacePlan; save: RaceSave | null; last: { you: number; source: number; tick: number } | null;
  onRetry: () => void; onCompare: (childId: string) => void; onHistories: () => void; onAgain: () => void; onViewArena: () => void;
}) {
  const s: RaceSave = p.save ?? { kind: "saving", attempt: 0 };
  const verified = s.kind === "saved" ? s.result : null;
  const you = verified?.livingScore ?? p.last?.you ?? null;
  const source = verified?.sourceScore ?? p.last?.source ?? null;
  const delta = you !== null && source !== null ? you - source : null;
  const retryable = s.kind === "pending" || (s.kind === "failed" && s.retryable);
  return (
    <FrostPanel className="gl-mp-results gl-solo-done gl-race-done" role="region" aria-label="Race complete" data-testid="race-finish" data-save={s.kind}>
      <div className="gl-solo-done-head">
        <div className="gl-solo-glass" data-flowing={s.kind === "saving" ? "1" : "0"}><HourglassMark elapsed={s.kind === "saving" ? 0.96 : 1} label={s.kind === "saved" ? "The future is recorded" : "The future is being written into history"} /></div>
        <div>
          <p className="gl-eyebrow">{p.plan.kind === "Fork" ? `Fork from T${p.plan.forkTick} complete` : "Ghost Challenge complete"}</p>
          <h2 data-testid="race-headline">{TITLE[s.kind]}</h2>
        </div>
      </div>

      <dl className="gl-race-trio" data-testid="race-trio" data-verified={verified ? "1" : "0"}>
        <div><dt>Your future</dt><dd data-testid="race-final-you">{you ?? "–"}</dd></div>
        <div><dt>Original</dt><dd data-testid="race-final-source">{source ?? "–"}</dd></div>
        <div><dt>Difference</dt><dd data-testid="race-final-delta" data-sign={delta === null ? 0 : Math.sign(delta)}>{delta === null ? "–" : signed(delta)}</dd></div>
      </dl>
      <p className="gl-race-fine">{verified ? "Verified by the server at tick 2700." : p.last ? `Last live numbers, tick ${p.last.tick}. They become the verified result once the server confirms the save.` : "No score received."}</p>

      <p className="gl-mp-results-sub" role="status" data-testid="race-save-status">
        <StatusPill tone={s.kind === "saved" ? "ok" : s.kind === "saving" ? "neutral" : "warn"}>
          {s.kind === "saved" ? "Saved · verified" : s.kind === "saving" ? "Saving" : s.kind === "pending" ? "Pending" : s.kind === "failed" ? "Failed" : "Not saved"}
        </StatusPill>
        {" "}
        {s.kind === "saving" ? (s.attempt > 0 ? "The server is still confirming. Trying again with the same key." : "The server is verifying the finished race before it counts.") : null}
        {s.kind === "saved" ? `${p.plan.kind === "Fork" ? "Fork" : "Challenge"} #${shortId(s.result.challengeId)} · final state ${shortHash(s.result.childFinalStateHash)}` : null}
        {s.kind === "pending" ? `The server hasn't confirmed the save yet.${s.reason ? ` ${s.reason}` : ""} Your race isn't lost: retrying asks again with the same key.` : null}
        {s.kind === "failed" ? (s.code === "save_unconfirmed" ? "The server's answer did not match the race you played, so nothing is shown as saved." : raceFailureText(new SoloHubError("race", s.code))) : null}
        {s.kind === "interrupted" ? "The server has no finished race for this key, so nothing was saved." : null}
      </p>

      {s.kind === "saved" ? <p className="gl-mp-help" data-testid="race-next">Your original is unchanged. This future sits beside it, and you can compare the two at any tick.</p> : null}

      <div className="gl-mp-results-actions">
        {s.kind === "saved" ? (
          <>
            <GameButton variant="primary" onClick={() => p.onCompare(s.result.challengeId)} data-testid="race-compare">Compare with the original</GameButton>
            <GameButton onClick={p.onHistories} data-testid="race-histories">My Histories</GameButton>
            <GameButton onClick={p.onAgain} data-testid="race-again">Race again</GameButton>
          </>
        ) : retryable ? (
          <>
            <GameButton variant="primary" onClick={p.onRetry} data-testid="race-retry-save">Retry save</GameButton>
            <GameButton onClick={p.onHistories}>My Histories</GameButton>
          </>
        ) : s.kind === "saving" ? (
          <GameButton onClick={p.onHistories}>My Histories</GameButton>
        ) : (
          <>
            <GameButton variant="primary" onClick={p.onAgain}>Race again</GameButton>
            <GameButton onClick={p.onHistories}>My Histories</GameButton>
          </>
        )}
      </div>
      <div className="gl-mp-results-foot">
        <button type="button" className="gl-mp-link" onClick={p.onViewArena} data-testid="race-view-arena">View the arena</button>
      </div>
    </FrostPanel>
  );
}
