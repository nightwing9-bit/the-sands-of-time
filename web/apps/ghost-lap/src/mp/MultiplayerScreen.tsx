// The multiplayer journey (Spec 13, task 13.8): Home -> Create / Join -> Lobby -> Match -> Results -> Rematch or Leave, in ONE screen that owns
// one real room connection for its whole life. The phase is derived, never stored: it comes from what the server said about the room
// (phase, match status, sealed round) and about the connection. Nothing here decides a score, a slot, a winner or a start.
//
//   setup      Create / Join / Watch        MultiplayerTransport.start (CreateRoom or JoinRoom{AsSpectator})
//   lobby      members, ready, host start   SetReady, StartCountdown; the room is polled (the hub pushes no room events)
//   countdown  server-sealed slots appear   CancelCountdown (host); heads are drawn only for slots the server named
//   match      live arena + HUD             Snapshot stream; spectators and superseded tabs are read-only (You.CanControl)
//   results    GetMatchResult               pending / finalized / aborted; rematch returns to the lobby, next start is a new match
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { BackLink, FrostPanel, GameButton, StatusPill } from "../ui/primitives.tsx";
import type { ArenaWorld } from "../stage/ArenaWorld.ts";
import { CharacterTextures } from "../characters/CharacterAssets.ts";
import { CandleTextures } from "../candles/CandleAssets.ts";
import { SHAPE_BY_SLOT } from "../config/assetRegistry.ts";
import { PlaySession, type PlayHud } from "../play/PlaySession.ts";
import { PlayScene } from "../play/PlayScene.ts";
import { bindKeyboard, type KeyboardBinding } from "../play/keyboardIntent.ts";
import { MultiplayerTransport } from "../play/MultiplayerTransport.ts";
import { failureText, HubCallError, type JoinedRoom } from "../play/multiplayerWire.ts";
import { attachPlayAudio } from "../audio/playAudio.ts";
import { getAudio } from "../audio/instance.ts";
import { useAudioMode } from "../audio/react.ts";
import { clock, Soulfire } from "../screens/PlayScreen.tsx";
import { LobbyHeads } from "./LobbyHeads.ts";
import { LobbyView, ResultsPanel, SetupPanel, type SetupTab } from "./panels.tsx";
import {
  countdownSecondsLeft, lobbyCards, matchHasEnded, matchIsSettled, noticeText, normalizeCodeInput, resultModel, roomStage, RESULT_BACKOFF_MS,
  slotLabel, startAdvice, type ResultModel,
} from "./model.ts";

type Phase = "setup" | "connecting" | "lobby" | "countdown" | "match" | "results" | "closed" | "failed";
/** Which side of the stage the arena or the lobby backdrop should show: the live arena only while a match is being played or its result read. */
export type MultiplayerScene = "menu" | "day";
export type MultiplayerEntry = "create" | "join" | "spectate";

interface Run { readonly transport: MultiplayerTransport; readonly session: PlaySession; readonly scene: PlayScene; readonly keys: KeyboardBinding; readonly abort: AbortController; readonly audioOff: () => void }
interface ResultState { readonly kind: "loading" | "ready" | "denied" | "stalled"; readonly model: ResultModel | null; readonly text: string; readonly busy: boolean }
const NO_RESULT: ResultState = { kind: "loading", model: null, text: "", busy: false };

/** The server forfeits a disconnected competitor after 450 authoritative ticks (13.0 contract map A4): 15 s at 30 Hz. */
const GRACE_SECONDS = 15;

const sleep = (ms: number, signal: AbortSignal) => new Promise<void>((resolve) => {
  if (signal.aborted) return resolve();
  const timer = setTimeout(resolve, ms);
  signal.addEventListener("abort", () => { clearTimeout(timer); resolve(); }, { once: true });
});

export function MultiplayerScreen({ world, entry, onBack, onScene }: { world: ArenaWorld | null; entry: MultiplayerEntry; onBack: () => void; onScene: (scene: MultiplayerScene) => void }) {
  const [textures, setTextures] = useState<CharacterTextures | null>(null);
  const [candleTextures, setCandleTextures] = useState<CandleTextures | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [tab, setTab] = useState<SetupTab>(entry === "create" ? "create" : "join");
  const [code, setCode] = useState("");
  const [spectator, setSpectator] = useState(entry === "spectate");
  const [run, setRun] = useState<Run | null>(null);
  const runRef = useRef<Run | null>(null);
  const [room, setRoom] = useState<JoinedRoom | null>(null);
  const [roomReadAt, setRoomReadAt] = useState(0);
  const [hud, setHud] = useState<PlayHud | null>(null);
  const [closed, setClosed] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState("");
  const [leaving, setLeaving] = useState(false);
  const [result, setResult] = useState<ResultState>(NO_RESULT);
  const [recheck, setRecheck] = useState(0);
  const [arenaOnly, setArenaOnly] = useState(false);
  const lobbyHeads = useRef<LobbyHeads | null>(null);

  // ---- assets (same shared textures every other screen uses) ----------------------------------------------------------------------
  useEffect(() => {
    const abort = new AbortController();
    let loaded: CharacterTextures | null = null;
    let candles: CandleTextures | null = null;
    CharacterTextures.load(abort.signal).then(
      (t) => { if (abort.signal.aborted) return t.destroy(); loaded = t; setTextures(t); },
      (e: unknown) => { if (!abort.signal.aborted) setLoadError(String(e)); },
    );
    CandleTextures.load(abort.signal).then(
      (t) => { if (abort.signal.aborted) return t.destroy(); candles = t; setCandleTextures(t); },
      (e: unknown) => { if (!abort.signal.aborted) setLoadError(String(e)); },
    );
    return () => { abort.abort(); loaded?.destroy(); candles?.destroy(); setTextures(null); setCandleTextures(null); };
  }, []);

  useEffect(() => {
    if (!world || !textures) return;
    const heads = new LobbyHeads(world, textures);
    lobbyHeads.current = heads;
    return () => { heads.dispose(); lobbyHeads.current = null; };
  }, [world, textures]);

  const teardown = useCallback(() => {
    const r = runRef.current;
    if (!r) return;
    runRef.current = null;
    r.keys.dispose(); // sends the neutral intent before the transport goes
    r.audioOff();
    r.abort.abort();
    r.scene.dispose();
    r.session.dispose(); // disposes the transport: neutral input, LeaveRoom (best effort), connection stop
    setRun(null); setRoom(null); setHud(null); setClosed(null); setResult(NO_RESULT); setBusy(null); setLeaving(false); setArenaOnly(false);
  }, []);
  useEffect(() => teardown, [teardown]);
  useEffect(() => () => onScene("menu"), [onScene]);

  // ---- one real connection ---------------------------------------------------------------------------------------------------------
  const begin = useCallback((mode: MultiplayerEntry, joinCode?: string) => {
    if (!world || !textures || !candleTextures) return;
    teardown();
    setError("");
    const transport = new MultiplayerTransport({ pollRoom: true, ...(mode === "create" ? {} : { code: joinCode ?? "", spectator: mode === "spectate" }) });
    transport.onRoom = (joined) => { setRoom({ ...joined }); setRoomReadAt(performance.now()); };
    transport.onRoomClosed = (reason) => setClosed(reason);
    const session = new PlaySession(transport);
    const scene = new PlayScene(world, textures, session, candleTextures);
    const keys = bindKeyboard((intent, reason) => session.intent(intent, reason, performance.now()), () => !!session.frame?.you.canControl && session.hud(performance.now()).connection.kind === "live");
    const abort = new AbortController();
    session.subscribe(() => {
      const next = session.hud(performance.now());
      if (!next.you?.canControl || next.connection.kind !== "live") keys.reset();
      setHud(next);
    });
    const audioOff = attachPlayAudio(session, getAudio());
    const next: Run = { transport, session, scene, keys, abort, audioOff };
    runRef.current = next;
    setRun(next);
    setHud(session.hud(performance.now()));
    void transport.start(abort.signal);
  }, [world, textures, candleTextures, teardown]);

  // ---- derived phase ---------------------------------------------------------------------------------------------------------------
  const view = room?.view ?? null;
  const failed = hud?.connection.kind === "failed";
  const phase: Phase = !run ? "setup" : !room || !view ? (failed ? "setup" : "connecting")
    : closed || roomStage(view) === "closed" ? "closed"
    : failed ? "failed"
    : (() => {
      const stage = roomStage(view);
      if (stage === "match") return matchHasEnded(view.matchStatus, !!hud?.final) ? "results" : "match";
      return stage;
    })();
  const me = room && view ? view.members.find((m) => m.memberId === room.memberId) ?? null : null;
  const isSpectator = room?.role === "spectator";
  const isHost = !!me?.isHost;
  const matchId = hud?.identity?.split(":")[0] ?? view?.round?.matchId ?? null;

  useEffect(() => { onScene(phase === "match" || phase === "results" ? "day" : "menu"); }, [phase, onScene]);
  useAudioMode(phase === "match" && !!hud?.identity && !hud.final && hud.connection.kind !== "failed" ? "day" : "menu");
  useEffect(() => { if (phase === "match") (document.activeElement as HTMLElement | null)?.blur?.(); }, [phase]);

  // The room went back to its lobby (a rematch by anyone): the finished match leaves the arena, the event feed, the cues and the result.
  useEffect(() => {
    if (phase !== "lobby") return;
    run?.session.clear();
    setResult(NO_RESULT); setArenaOnly(false); setLeaving(false);
  }, [phase, run]);

  // Heads appear only for slots the server has sealed.
  const cards = useMemo(() => (view && room ? lobbyCards(view, room.memberId) : []), [view, room]);
  const sealed = !!view?.round && view.round.slots.length > 0;
  const headSig = cards.map((c) => (c.member && c.slot !== null ? `${c.index}:${c.slot}:${c.member.connected ? 1 : 0}` : "-")).join("|");
  useEffect(() => {
    const heads = lobbyHeads.current;
    if (!heads) return;
    const showing = phase === "lobby" || phase === "countdown";
    heads.set(showing ? cards.flatMap((c) => (c.member && c.slot !== null ? [{ index: c.index, slot: c.slot, present: c.member.connected }] : [])) : []);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [phase, headSig, textures, world]);

  // ---- result: read GetMatchResult with bounded backoff -----------------------------------------------------------------------------
  useEffect(() => {
    if (phase !== "results" || !run || !room || !matchId) return;
    const abort = new AbortController();
    const memberId = room.memberId;
    setResult((r) => ({ ...r, busy: true }));
    void (async () => {
      let last = "";
      for (const delay of RESULT_BACKOFF_MS) {
        await sleep(delay, abort.signal);
        if (abort.signal.aborted) return;
        try {
          const outcome = await run.transport.getResult(matchId);
          if (abort.signal.aborted) return;
          const model = resultModel(outcome, memberId);
          setResult({ kind: "ready", model, text: "", busy: model.status === "pending" });
          if (model.status !== "pending") return;
        } catch (e) {
          if (abort.signal.aborted) return;
          if (e instanceof HubCallError && e.code === "result_not_found") {
            setResult({ kind: "denied", model: null, text: isSpectator ? "A spectator can read the result only while still in the room." : failureText(e), busy: false });
            return;
          }
          last = failureText(e);
        }
      }
      if (abort.signal.aborted) return;
      setResult((r) => ({ kind: r.model ? "ready" : "stalled", model: r.model, text: last || "The server hasn't recorded the result yet.", busy: false }));
    })();
    return () => abort.abort();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [phase, matchId, recheck, run]);

  useEffect(() => {
    // A confirmed winner's head shows the winner face; anything not finalized names no winner.
    run?.scene.setWinners(result.model?.status === "finalized" ? result.model.winners : []);
  }, [run, result.model]);

  // ---- actions -----------------------------------------------------------------------------------------------------------------------
  const act = async (name: string, fn: () => Promise<unknown>) => {
    if (busy) return;
    setBusy(name); setError("");
    try { await fn(); } catch (e) { setError(failureText(e)); } finally { setBusy(null); }
  };
  const leaveNow = () => { teardown(); onBack(); };

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== "Escape") return;
      if (leaving) setLeaving(false); else if (phase === "match") setLeaving(true);
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [leaving, phase]);

  const setupError = !room && failed && hud ? (hud.connection.kind === "failed" ? hud.connection.reason : "") : "";
  const submitSetup = () => {
    if (tab === "create") begin("create");
    else begin(spectator ? "spectate" : "join", code);
  };

  // ---- render ------------------------------------------------------------------------------------------------------------------------
  const nowMs = performance.now();
  const countdownLeft = view ? countdownSecondsLeft(view, roomReadAt, nowMs) : null;
  const connectionNote = hud?.connection.kind === "degraded" ? "Connection trouble. Reconnecting…" : null;
  const youReady = !!me?.ready;
  const settled = matchIsSettled(view?.matchStatus) || (result.model !== null && result.model.status !== "pending");
  const rematchWhy = settled ? null : "Waiting for the server to record the result.";

  return (
    <div className="gl-mp" data-testid="mp-screen" data-phase={phase}>
      {phase === "setup" || phase === "connecting" ? (
        <SetupPanel tab={tab} onTab={setTab} code={code} onCode={(c) => setCode(normalizeCodeInput(c))} spectator={spectator} onSpectator={setSpectator}
          pending={phase === "connecting"} ready={!!world && !!textures && !!candleTextures} loadError={loadError} error={setupError || error}
          onSubmit={submitSetup} onBack={() => { teardown(); onBack(); }} />
      ) : null}

      {(phase === "lobby" || phase === "countdown") && room && view ? (
        <LobbyView room={room} cards={cards} sealed={sealed} countdownLeft={countdownLeft} notice={noticeText(view.notice)} error={error} busy={busy}
          advice={startAdvice(view)} isHost={isHost} youReady={youReady} isSpectator={!!isSpectator} connectionNote={connectionNote}
          onReady={() => void act("ready", () => run!.transport.setReady(!youReady))}
          onStart={() => void act("start", () => run!.transport.startCountdown())}
          onCancel={() => void act("cancel", () => run!.transport.cancelCountdown())}
          onLeave={leaveNow} />
      ) : null}

      {phase === "match" && run && hud && room ? (
        <MatchHud hud={hud} role={room.role} onLeave={() => setLeaving(true)} onResync={() => void run.transport.resync().catch((e) => setError(failureText(e)))} error={error} />
      ) : null}

      {phase === "results" && run && hud ? (
        <>
          <MatchHud hud={hud} role={room?.role ?? "competitor"} onLeave={leaveNow} onResync={() => undefined} error="" ended />
          {arenaOnly ? (
            <div className="gl-mp-arena-bar">
              <GameButton variant="primary" onClick={() => setArenaOnly(false)} data-testid="mp-show-results">Show results</GameButton>
            </div>
          ) : (
            <ResultsPanel
              state={{ kind: result.kind, model: result.model, text: result.text, busy: result.busy }}
              canRematch={!isSpectator && settled} rematchWhy={rematchWhy} rematchBusy={busy === "rematch"} error={error} spectator={!!isSpectator}
              onRematch={() => void act("rematch", async () => { await run.transport.rematch(); run.session.clear(); setResult(NO_RESULT); })}
              onHome={leaveNow} onRetry={() => setRecheck((n) => n + 1)} onViewArena={() => setArenaOnly(true)} />
          )}
        </>
      ) : null}

      {phase === "closed" ? (
        <div className="gl-mp-setup-wrap" data-testid="mp-closed">
          <FrostPanel className="gl-mp-setup">
            <h2>Room closed</h2>
            <p className="gl-mp-help">{closed ?? "This room no longer exists."}</p>
            <GameButton variant="primary" onClick={leaveNow}>Home</GameButton>
          </FrostPanel>
        </div>
      ) : null}

      {phase === "failed" && room ? (
        <div className="gl-mp-setup-wrap" data-testid="mp-failed">
          <FrostPanel className="gl-mp-setup" role="alert">
            <h2>Disconnected</h2>
            <p className="gl-mp-help">{hud?.connection.kind === "failed" ? hud.connection.reason : "The connection to the game server was lost."}</p>
            <p className="gl-mp-help">The server keeps a competitor's seat for about {GRACE_SECONDS} seconds in a running match, then forfeits it.</p>
            <GameButton variant="primary" onClick={() => begin(isSpectator ? "spectate" : "join", room.roomCode)}>Rejoin room {room.roomCode}</GameButton>
            <GameButton onClick={leaveNow}>Home</GameButton>
          </FrostPanel>
        </div>
      ) : null}

      {leaving ? (
        <div className="gl-play-leave gl-panel" role="alertdialog" aria-label="Leave the match?" data-testid="mp-leave">
          <p>{isSpectator
            ? "Stop watching? You can join again with the room code."
            : `Leave this match? The server keeps playing. After about ${GRACE_SECONDS} seconds disconnected your seat is forfeited and cannot be taken back.`}</p>
          <div>
            <GameButton variant="primary" onClick={() => { setLeaving(false); leaveNow(); }} data-testid="mp-leave-confirm">Leave</GameButton>
            <GameButton onClick={() => setLeaving(false)}>Keep playing</GameButton>
          </div>
        </div>
      ) : null}
    </div>
  );
}

// ---- the live HUD: slim edge zones over the one arena; every number is the server's -------------------------------------------------------------
function MatchHud({ hud, role, onLeave, onResync, error, ended = false }: { hud: PlayHud; role: string; onLeave: () => void; onResync: () => void; error: string; ended?: boolean }) {
  const spectating = role === "spectator";
  const youSlot = hud.you?.slot ?? -1;
  const you = youSlot >= 0 ? hud.players.find((p) => p.slot === youSlot) : undefined;
  const youForfeited = you?.status === "forfeited";
  const superseded = !spectating && !!hud.you && !hud.you.canControl && !youForfeited && hud.connection.kind === "live" && !ended;
  const conn = hud.connection;
  const live = conn.kind === "live" || conn.kind === "ended";
  return (
    <div className="gl-play gl-mp-hud" data-testid="mp-hud">
      <div className="gl-play-top">
        <BackLink onClick={onLeave}>{ended ? "Home" : "Leave"}</BackLink>
        {!ended ? <StatusPill tone={live ? "ok" : conn.kind === "connecting" ? "neutral" : "warn"} role="status" data-testid="hud-connection" data-state={conn.kind}>
          {conn.kind === "live" ? "Live" : conn.kind === "connecting" ? "Connecting…" : conn.kind === "degraded" ? "Connection trouble" : conn.kind === "failed" ? "Disconnected" : "Ended"}
        </StatusPill> : null}
        {spectating ? <StatusPill tone="neutral" data-testid="hud-spectating">Spectating · read only</StatusPill> : null}
        {spectating && !ended ? <GameButton onClick={onResync} data-testid="mp-resync">Resync</GameButton> : null}
      </div>

      {hud.identity ? <div className="gl-play-clock" data-testid="hud-clock" data-tick={hud.tick} aria-label="Time remaining">{clock(hud.secondsLeft)}</div> : null}

      {you && !ended ? (
        <div className="gl-play-me" data-testid="hud-you">
          <div className="gl-play-score" data-testid="hud-score" data-score={you.totalScore}>{you.totalScore}</div>
          <Soulfire charge={you.charge} />
          {you.overlit && you.overlitDeadlineTick !== null ? (
            <span className="gl-play-overlit" data-testid="hud-overlit">Overlit · auto-Burst in {Math.max(0, (you.overlitDeadlineTick - hud.tick) / hud.tickRate).toFixed(1)} s</span>
          ) : null}
          <span className="gl-play-parts">move {you.movementPoints} · candles {you.pickupPoints} · hits {you.penaltyPoints}</span>
        </div>
      ) : null}

      {hud.players.length > 0 ? (
        <ul className="gl-play-rail gl-mp-rail" aria-label="Players" data-testid="mp-rail">
          {hud.players.map((p) => {
            const grace = p.status === "grace" && p.graceEndsTick != null ? Math.max(0, (p.graceEndsTick - hud.tick) / hud.tickRate) : null;
            return (
              <li key={p.slot} data-you={youSlot === p.slot ? "1" : "0"} data-status={p.status ?? "active"} data-testid="mp-rail-item">
                <img src={SHAPE_BY_SLOT[p.slot]?.file} alt="" width={22} height={22} data-slot={p.slot} />
                <span className="gl-mp-rail-name">{slotLabel(p.slot)}{youSlot === p.slot ? " · you" : ""}</span>
                <b>{p.totalScore}</b>
                <Soulfire charge={p.charge} />
                {p.status === "forfeited" ? <em data-tone="warn">forfeited</em> : grace !== null ? <em data-tone="warn">reconnecting · {grace.toFixed(0)} s</em> : null}
              </li>
            );
          })}
        </ul>
      ) : null}

      {!ended ? (
        <ul className="gl-play-feed" aria-live="polite" data-testid="hud-feed">
          {hud.feed.map((l) => <li key={l.key} data-tone={l.tone}>{l.text}</li>)}
        </ul>
      ) : null}
      {hud.burst && !ended ? <div className="gl-play-burst" data-stage={hud.burst.stage} data-testid="hud-burst">{hud.burst.detail}</div> : null}

      {!hud.identity && !ended ? (
        <div className="gl-mp-wait gl-panel" role="status" data-testid="mp-waiting">The match is starting…</div>
      ) : null}
      {conn.kind === "degraded" || conn.kind === "failed" ? (
        <div className="gl-play-banner" role="alert" data-kind={conn.kind} data-testid="hud-banner">
          <strong>{conn.kind === "failed" ? "Disconnected" : "Connection trouble"}</strong>
          <span>{conn.reason}</span>
          {conn.kind === "degraded" && !spectating ? <span>The picture is the last confirmed state, not live play. After about {GRACE_SECONDS} s offline the server forfeits your seat.</span> : null}
        </div>
      ) : youForfeited && !ended ? (
        <div className="gl-play-banner" role="alert" data-kind="degraded" data-testid="hud-forfeit">
          <strong>You forfeited this match</strong>
          <span>The server ended your seat. You can keep watching; the result will list the forfeit.</span>
        </div>
      ) : superseded ? (
        <div className="gl-play-banner" role="alert" data-kind="degraded" data-testid="hud-superseded">
          <strong>Another tab is controlling your seat</strong>
          <span>This window is read-only.</span>
        </div>
      ) : null}
      {!hud.eventsComplete && !ended ? <span className="gl-play-note" data-testid="hud-events-note">Event history is incomplete for this delivery.</span> : null}
      {error ? <p className="gl-mp-error gl-mp-hud-error" role="alert">{error}</p> : null}
    </div>
  );
}
