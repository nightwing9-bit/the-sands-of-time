// PREVIEW (development only): the 13.6 Burst and transition study. NOT live play, NOT a saved history.
//
// It runs the REAL presentation path (PlaySession -> cues -> PlayScene: reaction faces, halo pulse, world distortion) from a labelled fixture
// transport that produces frames in the exact PlayFrame shape (src/play/burstFixtureTransport.ts). The same scene then plays one smoke + CRT
// transition and swaps day/night under full cover. What is real and what is a fixture, on screen and here:
//   REAL      arena, heads, halos, faces, candle art, the shockwave and CRT filters, the smoke sheet (verified slices), every cue/de-dup rule.
//   FIXTURE   the frames: positions, charges, scores, candle ids and the "server" that answers a Burst press. Marked "PREVIEW FIXTURE".
import { useCallback, useEffect, useRef, useState } from "react";
import { BackLink } from "../ui/primitives.tsx";
import type { ArenaWorld } from "../stage/ArenaWorld.ts";
import { CharacterTextures } from "../characters/CharacterAssets.ts";
import { CandleTextures } from "../candles/CandleAssets.ts";
import { PlaySession, type PlayHud } from "../play/PlaySession.ts";
import { PlayScene } from "../play/PlayScene.ts";
import { bindKeyboard, type KeyboardBinding } from "../play/keyboardIntent.ts";
import { BurstFixtureTransport } from "../play/burstFixtureTransport.ts";
import { HEAD_UNITS } from "../characters/characterSpec.ts";
import { SmokeTextures, SMOKE_TIMELINE } from "../fx/SmokeAssets.ts";
import { SceneTransition } from "../fx/SceneTransition.ts";
import { prefersReducedMotion, setReducedMotionOverride } from "../fx/motion.ts";
import { smokeSchedule } from "../fx/timeline.ts";
import type { EnvironmentMode } from "../lighting/lightingPresets.ts";

interface Run { readonly transport: BurstFixtureTransport; readonly session: PlaySession; readonly scene: PlayScene; readonly keys: KeyboardBinding; readonly abort: AbortController }
interface Readout { wave: boolean; filters: number; transition: string; smoke: boolean; cover: boolean; hud: PlayHud | null }

export function BurstStudyHud({ world, onBack }: { world: ArenaWorld | null; onBack: () => void }) {
  const [characters, setCharacters] = useState<CharacterTextures | null>(null);
  const [candles, setCandles] = useState<CandleTextures | null>(null);
  const [smoke, setSmoke] = useState<SmokeTextures | null>(null);
  const [failed, setFailed] = useState<string | null>(null);
  const [mode, setMode] = useState<EnvironmentMode>("day");
  const [reduced, setReduced] = useState<boolean>(prefersReducedMotion());
  const [collapsed, setCollapsed] = useState(false);
  const [readout, setReadout] = useState<Readout>({ wave: false, filters: 0, transition: "idle", smoke: false, cover: false, hud: null });
  const [matchCount, setMatchCount] = useState(0);
  const run = useRef<Run | null>(null);
  const transition = useRef<SceneTransition | null>(null);
  const modeRef = useRef<EnvironmentMode>("day");

  useEffect(() => {
    const abort = new AbortController();
    let c: CharacterTextures | null = null, k: CandleTextures | null = null, s: SmokeTextures | null = null;
    const fail = (e: unknown) => { if (!abort.signal.aborted) setFailed(String(e)); };
    CharacterTextures.load(abort.signal).then((t) => { if (abort.signal.aborted) return t.destroy(); c = t; setCharacters(t); }, fail);
    CandleTextures.load(abort.signal).then((t) => { if (abort.signal.aborted) return t.destroy(); k = t; setCandles(t); }, fail);
    SmokeTextures.load(abort.signal).then((t) => { if (abort.signal.aborted) return t.destroy(); s = t; setSmoke(t); }, fail);
    return () => { abort.abort(); c?.destroy(); k?.destroy(); s?.destroy(); setCharacters(null); setCandles(null); setSmoke(null); };
  }, []);

  // One fixture match: transport + session + scene. Torn down completely on remount and on "New match".
  const startMatch = useCallback(() => {
    if (!world || !characters || !candles) return;
    const old = run.current;
    run.current = null;
    old?.keys.dispose();
    old?.abort.abort();
    old?.scene.dispose();
    old?.session.dispose();
    const transport = new BurstFixtureTransport();
    const session = new PlaySession(transport);
    const scene = new PlayScene(world, characters, session, candles);
    scene.setHeadUnits(HEAD_UNITS);
    scene.setMode(modeRef.current);
    const keys = bindKeyboard((intent, reason) => session.intent(intent, reason, performance.now()), () => true);
    const abort = new AbortController();
    run.current = { transport, session, scene, keys, abort };
    void transport.start();
    setMatchCount((n) => n + 1);
  }, [world, characters, candles]);

  useEffect(() => {
    startMatch();
    return () => {
      const r = run.current;
      run.current = null;
      r?.keys.dispose();
      r?.abort.abort();
      r?.scene.dispose();
      r?.session.dispose();
    };
  }, [startMatch]);

  useEffect(() => {
    if (!world || !smoke) return;
    const t = new SceneTransition(world, smoke);
    transition.current = t;
    return () => { t.dispose(); if (transition.current === t) transition.current = null; };
  }, [world, smoke]);

  // Readout of what is live on the game view, so "clean return, no stale filter" is visible and not a claim.
  useEffect(() => {
    if (!world) return;
    const id = setInterval(() => {
      const r = run.current, t = transition.current;
      setReadout({
        wave: !!r?.scene.shockwave.active,
        filters: world.fxStack.count,
        transition: t?.active ? "running" : "idle",
        smoke: !!t?.smokeVisible,
        cover: !!t?.coverVisible,
        hud: r ? r.session.hud(performance.now()) : null,
      });
    }, 100);
    return () => clearInterval(id);
  }, [world]);

  const applyMode = (next: EnvironmentMode) => {
    modeRef.current = next;
    run.current?.scene.setMode(next);
    setMode(next);
  };
  const play = (target: EnvironmentMode | null, opts: { crt: boolean; smoke: boolean }) => {
    const t = transition.current;
    if (!t) return;
    t.play({ ...opts, onCovered: target ? () => applyMode(target) : undefined });
  };
  const setReducedMotion = (on: boolean) => { setReducedMotionOverride(on ? true : null); setReduced(prefersReducedMotion()); };
  const press = () => {
    const r = run.current;
    if (!r) return;
    r.session.intent({ moveX: 0, moveY: 0, requestBurst: true }, "change", performance.now());
    setTimeout(() => { if (run.current === r) r.session.intent({ moveX: 0, moveY: 0, requestBurst: false }, "change", performance.now()); }, 120);
  };

  const ready = !!world && !!characters && !!candles && !!smoke;
  const sched = smokeSchedule(SMOKE_TIMELINE);
  const hud = readout.hud;
  const burst = hud?.burst;
  const me = hud?.players.find((p) => p.slot === 0);
  return (
    <div className="gl-ns" data-testid="burst-study" data-ready={ready ? "true" : "false"} data-mode={mode} data-match={matchCount}
      data-wave={readout.wave ? "1" : "0"} data-filters={readout.filters} data-transition={readout.transition} data-smoke={readout.smoke ? "1" : "0"} data-cover={readout.cover ? "1" : "0"}>
      <div className="gl-study-top">
        <BackLink onClick={onBack}>Home</BackLink>
        <span className="gl-fixture-tag" data-testid="fixture-tag">PREVIEW FIXTURE · scripted frames and fixture candles · not a saved history · not live play</span>
      </div>
      {failed ? <p className="gl-world-failed" role="alert">Assets could not load: {failed}</p> : null}
      <output className="gl-ns-readout" data-testid="bs-readout">
        <b>{mode === "night" ? "Night" : "Day"}</b>
        <span>
          P1 {me ? `${me.charge}/4` : "–"}
          {burst ? ` · ${burst.stage}: ${burst.detail}` : ""}
        </span>
      </output>
      <div className="gl-ns-panel gl-cs-panel" aria-label="Burst and transition preview controls">
        <button type="button" className="gl-ns-head" aria-expanded={!collapsed} onClick={() => setCollapsed(!collapsed)}>Burst + transition study {collapsed ? "▸" : "▾"}</button>
        {collapsed ? null : (
          <>
            <div role="group" aria-label="Burst">
              <span className="gl-cs-k">Burst</span>
              <button type="button" onClick={press} data-testid="bs-manual">Press Burst (Space)</button>
              <button type="button" onClick={() => run.current?.transport.autoBurst(0)} data-testid="bs-auto">Overlit auto-Burst</button>
              <button type="button" onClick={() => run.current?.transport.contact()} data-testid="bs-contact">Weaker contact</button>
              <button type="button" onClick={() => run.current?.transport.rearm()} data-testid="bs-rearm">Re-arm</button>
              <button type="button" onClick={() => startMatch()} data-testid="bs-newmatch">New match</button>
            </div>
            <div role="group" aria-label="Transition">
              <span className="gl-cs-k">Switch</span>
              <button type="button" onClick={() => play(mode === "day" ? "night" : "day", { crt: true, smoke: true })} data-testid="bs-transition">{mode === "day" ? "Day → Night" : "Night → Day"} (CRT + smoke)</button>
              <button type="button" onClick={() => play(null, { crt: true, smoke: false })} data-testid="bs-crt">CRT only</button>
              <button type="button" onClick={() => transition.current?.cancel()} data-testid="bs-cancel">Cancel</button>
              <button type="button" aria-pressed={reduced} onClick={() => setReducedMotion(!reduced)} data-testid="bs-reduced">Reduced motion</button>
            </div>
            <p className="gl-cs-note gl-ns-note" data-testid="bs-note">
              Burst reach {160} u (server rule). Manual press: requested, admitted, confirmed only by the server's own event; auto-Burst has no press to confirm. Smoke: {SMOKE_TIMELINE.lastVisible - SMOKE_TIMELINE.firstVisible + 1} visible frames, swap at full cover (frame {SMOKE_TIMELINE.opaqueFrom}) after {(sched.swapAtMs / 1000).toFixed(2)} s, clear by {(sched.totalMs / 1000).toFixed(1)} s. Filters on the game view now: {readout.filters}.
            </p>
          </>
        )}
      </div>
    </div>
  );
}
