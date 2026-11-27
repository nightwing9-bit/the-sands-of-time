// Day play (Spec 13, task 13.4): the first screen where a person controls a real character. HUD only; the world is the one Pixi arena.
//
// Existing presentation seam, production multiplayer and separate DEV bridge.
import { useCallback, useEffect, useRef, useState } from "react";
import { BackLink, GameButton, StatusPill } from "../ui/primitives.tsx";
import type { ArenaWorld } from "../stage/ArenaWorld.ts";
import { CharacterTextures } from "../characters/CharacterAssets.ts";
import { CandleTextures } from "../candles/CandleAssets.ts";
import { HEAD_SIZE_OPTIONS } from "../characters/characterSpec.ts";
import { SHAPE_BY_SLOT } from "../config/assetRegistry.ts";
import { PlaySession, type PlayHud } from "../play/PlaySession.ts";
import { PlayScene } from "../play/PlayScene.ts";
import { bindKeyboard, type KeyboardBinding } from "../play/keyboardIntent.ts";
import { LOCAL_PRESETS, type LocalPresetId } from "../play/visualSmoothing.ts";

import { attachPlayAudio } from "../audio/playAudio.ts";
import { getAudio } from "../audio/instance.ts";
import { useAudioMode } from "../audio/react.ts";
import { MultiplayerTransport } from "../play/MultiplayerTransport.ts";
import type { JoinedRoom } from "../play/multiplayerWire.ts";
import type { PlayTransport } from "../play/playTypes.ts";

const DEV = import.meta.env.DEV;

interface Run { readonly session: PlaySession; readonly scene: PlayScene; readonly keys: KeyboardBinding; readonly abort: AbortController; readonly audioOff: () => void }

export function clock(seconds: number | null): string {
  if (seconds === null) return "–:––";
  const s = Math.ceil(seconds);
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, "0")}`;
}

export function Soulfire({ charge }: { charge: number }) {
  return (
    <span className="gl-play-soul" role="img" aria-label={`Soulfire ${charge} of 4`} data-testid="hud-charge" data-charge={charge}>
      {[1, 2, 3, 4].map((n) => <i key={n} data-on={charge >= n ? "1" : "0"} />)}
      <b>{charge}/4</b>
    </span>
  );
}

function ConnectionTag({ hud }: { hud: PlayHud }) {
  const c = hud.connection;
  const tone = c.kind === "live" ? "ok" : c.kind === "connecting" || c.kind === "ended" ? "neutral" : "warn";
  const text = c.kind === "live" ? "Live" : c.kind === "connecting" ? "Connecting…" : c.kind === "ended" ? "Ended" : c.kind === "degraded" ? "Connection trouble" : "Disconnected";
  return <StatusPill tone={tone} role="status" data-testid="hud-connection" data-state={c.kind}>{text}</StatusPill>;
}

export function PlayScreenHud({ world, onBack }: { world: ArenaWorld | null; onBack: () => void }) {
  const [room, setRoom] = useState<JoinedRoom | null>(null);
  const [roomCode, setRoomCode] = useState("");
  const [roomError, setRoomError] = useState("");
  const multiplayer = useRef<MultiplayerTransport | null>(null);
  const [textures, setTextures] = useState<CharacterTextures | null>(null);
  const [candleTextures, setCandleTextures] = useState<CandleTextures | null>(null);
  const [textureError, setTextureError] = useState<string | null>(null);
  const [run, setRun] = useState<Run | null>(null);
  const runRef = useRef<Run | null>(null);
  const [hud, setHud] = useState<PlayHud | null>(null);
  const [truth, setTruth] = useState(false);
  const [headIndex, setHeadIndex] = useState(0);
  const [preset, setPreset] = useState<LocalPresetId>("light");
  const [leaving, setLeaving] = useState(false);
  const [devCollapsed, setDevCollapsed] = useState(false);

  useEffect(() => {
    const abort = new AbortController();
    let loaded: CharacterTextures | null = null;
    let candles: CandleTextures | null = null;
    CharacterTextures.load(abort.signal).then(
      (t) => { if (abort.signal.aborted) return t.destroy(); loaded = t; setTextures(t); },
      (error: unknown) => { if (!abort.signal.aborted) setTextureError(String(error)); },
    );
    CandleTextures.load(abort.signal).then(
      (t) => { if (abort.signal.aborted) return t.destroy(); candles = t; setCandleTextures(t); },
      (error: unknown) => { if (!abort.signal.aborted) setTextureError(String(error)); },
    );
    return () => { abort.abort(); loaded?.destroy(); candles?.destroy(); setTextures(null); setCandleTextures(null); };
  }, []);

  const teardown = useCallback(() => {
    const r = runRef.current;
    if (!r) return;
    runRef.current = null;
    r.keys.dispose(); // sends the neutral intent before the transport is disposed
    r.audioOff();
    r.abort.abort();
    r.scene.dispose();
    r.session.dispose();
    multiplayer.current = null;
    setRoom(null);
    setRun(null);
    setHud(null);
  }, []);
  useEffect(() => teardown, [teardown]);

  const start = useCallback(async (mode: "dev" | "create" | "join" | "spectate" = "dev") => {
    if (!world || !textures || !candleTextures || (mode === "dev" && !DEV)) return;
    teardown();
    setRoomError("");
    let bridge: PlayTransport;
    if (mode !== "dev") {
      const mp = new MultiplayerTransport(mode === "create" ? {} : { code: roomCode.trim(), spectator: mode === "spectate" });
      mp.onRoom = joined => setRoom({ ...joined });
      multiplayer.current = mp;
      bridge = mp;
    } else if (DEV) {
      const { TestHostBridge } = await import("../play/testHostBridge.ts");
      const params = new URLSearchParams(window.location.search);
      const seed = params.has("seed") ? Number(params.get("seed")) : undefined;
      const mascot = params.has("mascot") ? Number(params.get("mascot")) : undefined;
      bridge = new TestHostBridge({
        ...(seed !== undefined && Number.isInteger(seed) ? { seed } : {}),
        ...(mascot !== undefined && Number.isInteger(mascot) ? { mascotId: mascot } : {}),
      });
    } else return;
    const session = new PlaySession(bridge);
    session.setPreset(preset);
    const scene = new PlayScene(world, textures, session, candleTextures);
    scene.setHeadUnits(HEAD_SIZE_OPTIONS[headIndex]!.units);
    scene.setShowTruth(truth);
    const keys = bindKeyboard((intent, reason) => session.intent(intent, reason, performance.now()), () => !!session.frame?.you.canControl && session.hud(performance.now()).connection.kind === "live");
    const abort = new AbortController();
    session.subscribe(() => {
      const nextHud = session.hud(performance.now());
      if (!nextHud.you?.canControl || nextHud.connection.kind !== "live") keys.reset();
      setHud(nextHud);
    });
    const audioOff = attachPlayAudio(session, getAudio());
    const next: Run = { session, scene, keys, abort, audioOff };
    runRef.current = next;
    setRun(next);
    setHud(session.hud(performance.now()));
    void bridge.start(abort.signal);
  }, [world, textures, candleTextures, teardown, preset, headIndex, truth, roomCode]);

  useEffect(() => {
    if (!room || hud?.identity) return;
    const timer = setInterval(() => { void multiplayer.current?.refreshRoom().catch(e => setRoomError(String(e))); }, 2000);
    return () => clearInterval(timer);
  }, [room?.view.roomId, hud?.identity]);
  const roomCommand = (method: "SetReady" | "StartCountdown") => { setRoomError(""); void multiplayer.current?.command(method).catch(e => setRoomError(String(e))); };

  const choosePreset = (id: LocalPresetId) => { setPreset(id); run?.session.setPreset(id); };
  const chooseHead = (i: number) => { setHeadIndex(i); run?.scene.setHeadUnits(HEAD_SIZE_OPTIONS[i]!.units); };
  const toggleTruth = () => { const v = !truth; setTruth(v); run?.scene.setShowTruth(v); };

  // 13.7: the day theme only while a run is actually being played; the lobby/start panel, a finished run and a failed one stay on the menu loop.
  const playing = !!run && !!hud?.identity && !hud.final && hud.connection.kind !== "failed" && hud.connection.kind !== "ended";
  useAudioMode(playing ? "day" : "menu");
  const active = !!run && !!hud && (hud.connection.kind === "live" || hud.connection.kind === "connecting" || hud.connection.kind === "degraded");
  const requestBack = () => (active ? setLeaving(true) : onBack());
  const you = hud?.you ? hud.players.find((p) => p.slot === hud.you!.slot) : undefined;
  const ready = !!world && !!textures && !!candleTextures;
  const terminal = hud && (hud.connection.kind === "failed" || hud.connection.kind === "ended");

  return (
    <div className="gl-play" data-testid="play-screen" data-run={run ? "1" : "0"}>
      <div className="gl-play-top">
        <BackLink onClick={requestBack}>Home</BackLink>
        {run && hud ? <ConnectionTag hud={hud} /> : null}
        <span className="gl-play-source" data-testid="play-source" data-production={run ? String(hud?.production ?? false) : "false"}>
          {run && hud ? hud.transportLabel : "No live source connected"}
        </span>
      </div>

      {run && hud ? (
        <>
          <div className="gl-play-clock" data-testid="hud-clock" data-tick={hud.tick} aria-label="Time remaining">{clock(hud.secondsLeft)}</div>
          {you ? (
            <div className="gl-play-me" data-testid="hud-you">
              <div className="gl-play-score" data-testid="hud-score" data-score={you.totalScore}>{you.totalScore}</div>
              <Soulfire charge={you.charge} />
              {you.overlit && you.overlitDeadlineTick !== null ? (
                <span className="gl-play-overlit" data-testid="hud-overlit">Overlit · auto-Burst in {Math.max(0, (you.overlitDeadlineTick - hud.tick) / hud.tickRate).toFixed(1)} s</span>
              ) : null}
              <span className="gl-play-parts">move {you.movementPoints} · candles {you.pickupPoints} · hits {you.penaltyPoints}</span>
            </div>
          ) : null}
          {hud.players.length > 1 ? (
            <ul className="gl-play-rail" aria-label="Players">
              {hud.players.map((p) => (
                <li key={p.slot} data-you={hud.you?.slot === p.slot ? "1" : "0"}>
                  <img src={SHAPE_BY_SLOT[p.slot]?.file} alt="" width={22} height={22} />
                  <b>{p.totalScore}</b><Soulfire charge={p.charge} />
                </li>
              ))}
            </ul>
          ) : null}
          <ul className="gl-play-feed" aria-live="polite" data-testid="hud-feed">
            {hud.feed.map((l) => <li key={l.key} data-tone={l.tone}>{l.text}</li>)}
          </ul>
          {hud.burst ? <div className="gl-play-burst" data-stage={hud.burst.stage} data-testid="hud-burst">{hud.burst.detail}</div> : null}
          {hud.connection.kind === "degraded" || hud.connection.kind === "failed" || hud.connection.kind === "ended" ? (
            <div className="gl-play-banner" role="alert" data-kind={hud.connection.kind} data-testid="hud-banner">
              <strong>{hud.connection.kind === "ended" ? "Run ended" : hud.connection.kind === "failed" ? "Disconnected" : "Connection trouble"}</strong>
              <span>{hud.connection.reason}</span>
              {hud.connection.kind === "degraded" ? <span>The picture below is the last confirmed state, not live play.</span> : null}
            </div>
          ) : null}
          {hud.final ? <div className="gl-play-final" data-testid="hud-final">Time. Final score {you?.totalScore ?? "–"}</div> : null}
          {!hud.eventsComplete ? <span className="gl-play-note" data-testid="hud-events-note">Event history is incomplete for this delivery.</span> : null}
        </>
      ) : null}

      {!run || !hud?.identity || terminal ? (
        <div className="gl-play-start gl-panel" data-testid="play-start">
          {!hud?.identity ? <p className="gl-play-blocked" data-testid="playability-blocked"><strong>Production play unavailable until a real room is running.</strong></p> : null}
          <p>Multiplayer integration controls · use separate browser profiles/private sessions for distinct guests.</p>
          {!room ? <div>
            <GameButton onClick={() => void start("create")} {...(ready ? {} : { unavailable: "Characters are loading" })}>Create real room</GameButton>
            <input aria-label="Room code" value={roomCode} onChange={e => setRoomCode(e.target.value)} />
            <GameButton onClick={() => void start("join")} {...(ready && roomCode.trim() ? {} : { unavailable: "Enter a room code" })}>Join real room</GameButton>
            <GameButton onClick={() => void start("spectate")} {...(ready && roomCode.trim() ? {} : { unavailable: "Enter a room code" })}>Watch as spectator</GameButton>
          </div> : <div data-testid="room-controls">
            <p>Room <b data-testid="room-code">{room.roomCode}</b> · {room.role} · {room.view.phase}</p>
            <p>{room.view.members.map(m => `${m.isHost ? "Host" : m.role}: ${m.ready ? "ready" : "waiting"}`).join(" · ")}</p>
            {room.role !== "spectator" ? <GameButton onClick={() => roomCommand("SetReady")}>Ready</GameButton> : null}
            {room.view.members.some(m => m.memberId === room.memberId && m.isHost) ? <GameButton onClick={() => roomCommand("StartCountdown")}>Start countdown</GameButton> : null}
          </div>}
          {roomError ? <p role="alert">{roomError}</p> : null}
          {hud?.connection.kind === "failed" ? <p role="alert">{hud.connection.reason}</p> : null}
          {DEV ? (
            <>
              <p>A <strong>test-host bridge</strong> can run a real authoritative solo game for a first feel check (dev only; it is not the production transport).</p>
              <GameButton variant="primary" {...(ready ? {} : { unavailable: "The arena is still loading." })} onClick={(e) => { e.currentTarget.blur(); void start("dev"); }} data-testid="play-start-button">
                {ready ? (terminal ? "Run again (test-host bridge)" : "Start test-host run") : "Loading arena…"}
              </GameButton>
              <p className="gl-play-keys">Move: WASD or arrows. Burst: Space (needs 4/4 Soulfire).</p>
            </>
          ) : null}
          {textureError ? <p role="alert">Characters could not load: {textureError}</p> : null}
        </div>
      ) : null}

      {leaving ? (
        <div className="gl-play-leave gl-panel" role="alertdialog" aria-label="Leave the run?">
          <p>{hud?.production ? "Leave this room? Input stops on disconnect; a competitor forfeits after the server’s grace period." : "Leave this run? It is not saved and cannot be resumed."}</p>
          <div>
            <GameButton variant="primary" onClick={() => { setLeaving(false); teardown(); onBack(); }}>Leave</GameButton>
            <GameButton onClick={() => setLeaving(false)}>Keep playing</GameButton>
          </div>
        </div>
      ) : null}

      {DEV ? (
        <div className="gl-play-dev" data-testid="play-dev">
          <button type="button" className="gl-play-dev-head" aria-expanded={!devCollapsed} onClick={() => setDevCollapsed(!devCollapsed)}>Feel review (dev) {devCollapsed ? "▸" : "▾"}</button>
          {devCollapsed ? null : <>
          <div>
            <span>Local smoothing</span>
            {(Object.keys(LOCAL_PRESETS) as LocalPresetId[]).map((id) => <button key={id} type="button" aria-pressed={preset === id} onClick={() => choosePreset(id)}>{LOCAL_PRESETS[id].label}</button>)}
          </div>
          <div>
            <span>Head size</span>
            {HEAD_SIZE_OPTIONS.map((o, i) => <button key={o.units} type="button" aria-pressed={headIndex === i} onClick={() => chooseHead(i)}>{o.units}</button>)}
            <button type="button" aria-pressed={truth} onClick={toggleTruth} title="Outline the newest authoritative 48x48 collider">Server position</button>
          </div>
          {hud?.production ? <button onClick={() => { void multiplayer.current?.resync().catch(e => setRoomError(String(e))); }}>Resync real match</button> : null}
          {hud ? (
            <div className="gl-play-metrics" data-testid="play-metrics">
              <span>input RTT {fmt(hud.metrics.inputRttMs)}</span>
              <span>key→server {fmt(hud.metrics.moveToAuthoritativeMs)}</span>
              <span>key→drawn {fmt(hud.metrics.moveToDrawnMs)}</span>
              <span>frame gap mean {hud.metrics.meanFrameGapMs.toFixed(0)} / worst {hud.metrics.worstFrameGapMs.toFixed(0)} ms</span>
              <span>visual lag {hud.metrics.visualLagUnits.toFixed(1)} u</span>
              <span data-testid="play-refusals">refused inputs {hud.metrics.rejectedInputs}{Object.entries(hud.metrics.rejectionReasons).map(([k, v]) => ` · ${k} ${v}`).join("")}</span>
            </div>
          ) : null}
          </>}
        </div>
      ) : null}
    </div>
  );
}

const fmt = (v: number | null) => (v === null ? "–" : `${v.toFixed(0)} ms`);
