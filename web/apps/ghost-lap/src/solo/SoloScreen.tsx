// The Solo journey (Spec 13, task 13.9): Home -> choose a head -> a real 90-second Original -> saving -> saved and verified -> My Histories.
// ONE screen owns one real hub connection per run. The phase is derived from what the server said, never stored:
//
//   choose        the four approved heads on the calm snow; the choice is sent as StartSoloRequest.MascotId (a solo-only feature)
//   starting      StartRun in flight. The server starts the 30 Hz run the moment it answers; there is no countdown to pretend otherwise
//   playing       the authoritative arena + a HUD built only from the Original frame's delivered fields
//   finished      RunEnded(final): the recording is complete. FinishRun by the durable key decides Saving / Saved / Pending / Failed
//   interrupted   RunEnded(interrupted) or a lost connection: never an original, never resumable
//
// Nothing is saved, scored or "recorded" by this screen. A score on the finish panel is labelled the last live score until the server
// returns the verified original.
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { BackLink, FrostPanel, GameButton, StatusPill } from "../ui/primitives.tsx";
import type { ArenaWorld } from "../stage/ArenaWorld.ts";
import { CharacterTextures } from "../characters/CharacterAssets.ts";
import { CandleTextures } from "../candles/CandleAssets.ts";
import { HourglassMark } from "../ui/HourglassMark.tsx";
import { PlaySession, type PlayHud } from "../play/PlaySession.ts";
import { PlayScene } from "../play/PlayScene.ts";
import { bindKeyboard, type KeyboardBinding } from "../play/keyboardIntent.ts";
import { attachPlayAudio } from "../audio/playAudio.ts";
import { getAudio } from "../audio/instance.ts";
import { useAudioMode } from "../audio/react.ts";
import { clock, Soulfire } from "../screens/PlayScreen.tsx";
import { LobbyHeads, LOBBY_CARD_X } from "../mp/LobbyHeads.ts";
import { SoloTransport } from "./SoloTransport.ts";
import { hubFailureText, SoloHubError, type SoloRunEndedWire, type SoloRunStartedWire } from "./soloWire.ts";
import {
  AUTO_RETRY_DELAYS_MS, cap, clearPending, interpretSave, interpretSaveError, MASCOT_COUNT, mascotName, newOperationKey, readMascot, saveFailureText,
  shortHash, shortId, writeMascot, writePending, type SaveState,
} from "./model.ts";
import { useRecovery, type Recovery } from "./recovery.ts";

export type SoloScene = "menu" | "day";
type Phase = "choose" | "starting" | "playing" | "finished" | "interrupted";

interface Run { readonly transport: SoloTransport; readonly session: PlaySession; readonly scene: PlayScene; readonly keys: KeyboardBinding; readonly abort: AbortController; readonly audioOff: () => void }

const worldPct = (x: number) => `${(x / 1600) * 100}%`;
const sleep = (ms: number, signal: AbortSignal) => new Promise<void>((resolve) => {
  if (signal.aborted) return resolve();
  const timer = setTimeout(resolve, ms);
  signal.addEventListener("abort", () => { clearTimeout(timer); resolve(); }, { once: true });
});

export function SoloScreen({ world, onBack, onScene, onHistories }: { world: ArenaWorld | null; onBack: () => void; onScene: (scene: SoloScene) => void; onHistories: () => void }) {
  const [textures, setTextures] = useState<CharacterTextures | null>(null);
  const [candleTextures, setCandleTextures] = useState<CandleTextures | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [mascot, setMascot] = useState<number>(() => readMascot());
  const [run, setRun] = useState<Run | null>(null);
  const runRef = useRef<Run | null>(null);
  const [hud, setHud] = useState<PlayHud | null>(null);
  const [started, setStarted] = useState<SoloRunStartedWire | null>(null);
  const [ended, setEnded] = useState<SoloRunEndedWire | null>(null);
  const [save, setSave] = useState<SaveState | null>(null);
  const [leaving, setLeaving] = useState(false);
  const [arenaOnly, setArenaOnly] = useState(false);
  const heads = useRef<LobbyHeads | null>(null);
  const { recovery, retry: recheck, dismiss } = useRecovery();

  // ---- assets (the same shared textures every other screen uses) --------------------------------------------------------------------
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
    const h = new LobbyHeads(world, textures);
    heads.current = h;
    return () => { h.dispose(); heads.current = null; };
  }, [world, textures]);

  const teardown = useCallback(() => {
    const r = runRef.current;
    if (!r) return;
    runRef.current = null;
    r.keys.dispose(); // sends the neutral intent before the transport goes
    r.audioOff();
    r.abort.abort();
    r.scene.dispose();
    r.session.dispose(); // disposes the transport: neutral input, stream end, connection stop
    setRun(null); setHud(null); setStarted(null); setEnded(null); setSave(null); setLeaving(false); setArenaOnly(false);
  }, []);
  useEffect(() => teardown, [teardown]);
  useEffect(() => () => onScene("menu"), [onScene]);

  // ---- the save of a finished run: FinishRun by the durable key, bounded same-key retries, then the player decides -------------------
  const saveRun = useCallback(async (transport: SoloTransport, signal: AbortSignal) => {
    let tries = 0;
    while (!signal.aborted) {
      setSave({ kind: "saving", attempt: tries });
      let state: SaveState;
      try {
        const view = await transport.finishRun();
        tries += 1;
        state = interpretSave(view, tries);
      } catch (error) {
        state = error instanceof SoloHubError ? interpretSaveError(error.code, hubFailureText(error)) : interpretSaveError(null, hubFailureText(error));
      }
      if (signal.aborted) return;
      setSave(state);
      if (state.kind === "saved" || state.kind === "interrupted") clearPending();
      if (state.kind !== "saving") return;
      await sleep(AUTO_RETRY_DELAYS_MS[Math.min(tries - 1, AUTO_RETRY_DELAYS_MS.length - 1)]!, signal);
    }
  }, []);

  // ---- one real run -----------------------------------------------------------------------------------------------------------------
  const begin = useCallback(() => {
    if (!world || !textures || !candleTextures) return;
    teardown();
    writeMascot(mascot);
    const key = newOperationKey();
    const transport = new SoloTransport({ mascotId: mascot, operationKey: key });
    const session = new PlaySession(transport);
    const scene = new PlayScene(world, textures, session, candleTextures);
    const keys = bindKeyboard((intent, reason) => session.intent(intent, reason, performance.now()), () => !!session.frame?.you.canControl && session.hud(performance.now()).connection.kind === "live");
    const abort = new AbortController();
    transport.onStarted = (r) => { setStarted(r); writePending({ key, mascotId: mascot, startedAtMs: Date.now(), ended: "playing" }); };
    const rememberFinal = () => writePending({ key, mascotId: mascot, startedAtMs: Date.now(), ended: "final" });
    transport.onFinalFrame = rememberFinal;
    transport.onEnded = (e) => {
      setEnded(e);
      keys.reset();
      if (e.reason === "final") { rememberFinal(); void saveRun(transport, abort.signal); }
      else clearPending(); // the server interrupted it: it can never be an original, so nothing is left to recover
    };
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
  }, [world, textures, candleTextures, teardown, mascot, saveRun]);

  // ---- derived phase ----------------------------------------------------------------------------------------------------------------
  const failed = hud?.connection.kind === "failed";
  const phase: Phase = !run ? "choose"
    : ended?.reason === "final" ? "finished"
    : ended || (failed && !!started) ? "interrupted"
    : failed ? "choose" // StartRun itself failed: back to the choice with the server's reason
    : !started ? "starting"
    : "playing";
  const startError = run && !started && failed && hud ? (hud.connection.kind === "failed" ? hud.connection.reason : "") : "";

  useEffect(() => { onScene(phase === "choose" ? "menu" : "day"); }, [phase, onScene]);
  useAudioMode(phase === "playing" ? "day" : "menu");
  useEffect(() => { if (phase === "playing") (document.activeElement as HTMLElement | null)?.blur?.(); }, [phase]);

  // Heads: the four approved heads only while choosing; a run draws its own through PlayScene.
  useEffect(() => {
    const h = heads.current;
    if (!h) return;
    h.set(phase === "choose" ? Array.from({ length: MASCOT_COUNT }, (_, index) => ({ index, slot: index, present: true })) : []);
    h.setSelected(phase === "choose" ? mascot : null);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [phase, mascot, textures, world]);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== "Escape") return;
      if (leaving) setLeaving(false); else if (phase === "playing") setLeaving(true);
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [leaving, phase]);

  const leaveNow = () => { teardown(); onBack(); };
  const you = useMemo(() => (hud?.you ? hud.players.find((p) => p.slot === hud.you!.slot) : undefined), [hud]);
  const ready = !!world && !!textures && !!candleTextures;
  const rich = run?.transport.delivery === "rich";

  return (
    <div className="gl-solo" data-testid="solo-screen" data-phase={phase}>
      {phase === "choose" ? (
        <ChoosePanel mascot={mascot} onMascot={setMascot} ready={ready} loadError={loadError} error={startError} recovery={recovery}
          onRecheck={recheck} onDismiss={dismiss} onHistories={onHistories} onBegin={begin} onBack={() => { teardown(); onBack(); }} />
      ) : null}

      {phase === "starting" ? (
        <div className="gl-mp-wait gl-panel" role="status" data-testid="solo-starting">Starting your run…</div>
      ) : null}

      {phase === "playing" || phase === "finished" || phase === "interrupted" ? (
        hud ? <SoloHud hud={hud} onLeave={phase === "playing" ? () => setLeaving(true) : leaveNow} finished={phase !== "playing"} over={ended?.reason === "final"} rich={!!rich} you={you} /> : null
      ) : null}

      {phase === "finished" && run ? (
        arenaOnly ? (
          <div className="gl-mp-arena-bar"><GameButton variant="primary" onClick={() => setArenaOnly(false)} data-testid="solo-show-finish">Show result</GameButton></div>
        ) : (
          <FinishPanel save={save} liveScore={you?.totalScore ?? null} lastTick={hud?.tick ?? null}
            onRetry={() => { setSave({ kind: "saving", attempt: 0 }); void saveRun(run.transport, run.abort.signal); }}
            onHistories={() => { teardown(); onHistories(); }} onAgain={() => { teardown(); }} onHome={leaveNow} onViewArena={() => setArenaOnly(true)} />
        )
      ) : null}

      {phase === "interrupted" ? (
        <FrostPanel className="gl-mp-results gl-solo-done" role="alert" data-testid="solo-interrupted">
          <p className="gl-eyebrow">Solo run</p>
          <h2>{ended?.reason === "interrupted" ? "Run not saved" : "Run interrupted"}</h2>
          <p className="gl-mp-help">{hud?.connection.kind === "failed" ? hud.connection.reason : "The run was interrupted."}</p>
          <div className="gl-mp-results-actions">
            <GameButton variant="primary" onClick={() => teardown()} data-testid="solo-again">Start another run</GameButton>
            <GameButton onClick={leaveNow}>Home</GameButton>
          </div>
        </FrostPanel>
      ) : null}

      {leaving ? (
        <div className="gl-play-leave gl-panel" role="alertdialog" aria-label="Leave the run?" data-testid="solo-leave">
          <p>Leave this run? It can't be paused or resumed, and a run that isn't finished is never saved.</p>
          <div>
            <GameButton variant="primary" onClick={() => { setLeaving(false); leaveNow(); }} data-testid="solo-leave-confirm">Leave</GameButton>
            <GameButton onClick={() => setLeaving(false)}>Keep playing</GameButton>
          </div>
        </div>
      ) : null}
    </div>
  );
}

// ---- choose a head --------------------------------------------------------------------------------------------------------------------
function RecoveryNote({ recovery, onRecheck, onDismiss, onHistories }: { recovery: Recovery; onRecheck: () => void; onDismiss: () => void; onHistories: () => void }) {
  if (recovery.kind === "none") return null;
  return (
    <div className="gl-solo-note" role="status" data-testid="solo-recovery" data-kind={recovery.kind}>
      {recovery.kind === "interrupted" ? <span>Your last run was interrupted when the page closed. Unfinished runs can't be resumed, so it wasn't saved.</span> : null}
      {recovery.kind === "checking" ? <span>Checking whether your last run was saved…</span> : null}
      {recovery.kind === "saved" ? <span>Your last run was saved and verified: {cap(mascotName(recovery.original.mascotId))} original #{shortId(recovery.original.id)}.</span> : null}
      {recovery.kind === "pending" ? <span>Your last run isn't confirmed as saved yet. {recovery.reason ?? ""}</span> : null}
      {recovery.kind === "failed" ? <span>Your last run could not be saved. {recovery.text}</span> : null}
      {recovery.kind === "saved" ? <button type="button" className="gl-mp-link" onClick={onHistories}>Open My Histories</button> : null}
      {recovery.kind === "pending" ? <button type="button" className="gl-mp-link" onClick={onRecheck}>Check again</button> : null}
      {recovery.kind !== "checking" ? <button type="button" className="gl-mp-link" onClick={onDismiss}>Dismiss</button> : null}
    </div>
  );
}

function ChoosePanel(p: {
  mascot: number; onMascot: (id: number) => void; ready: boolean; loadError: string | null; error: string; recovery: Recovery;
  onRecheck: () => void; onDismiss: () => void; onHistories: () => void; onBegin: () => void; onBack: () => void;
}) {
  return (
    <div className="gl-solo-choose" data-testid="solo-choose">
      <div className="gl-study-top"><BackLink onClick={p.onBack}>Home</BackLink></div>
      <header className="gl-mp-room gl-solo-room">
        <p className="gl-eyebrow">Solo Run</p>
        <h2 className="gl-solo-title">Choose your head</h2>
        <p className="gl-mp-sub">A real 90-second run on the server. Every move is recorded, and a verified run becomes a history you keep.</p>
      </header>

      <div className="gl-mp-cards gl-solo-cards" role="radiogroup" aria-label="Your head for this run">
        {Array.from({ length: MASCOT_COUNT }, (_, id) => {
          const selected = p.mascot === id;
          return (
            <label key={id} className="gl-mp-card gl-solo-card" style={{ left: worldPct(LOBBY_CARD_X[id] ?? 800) }} data-selected={selected ? "1" : "0"} data-testid="solo-card" data-mascot={id}>
              <input type="radio" name="solo-mascot" className="gl-solo-radio" checked={selected} onChange={() => p.onMascot(id)} aria-label={cap(mascotName(id))} />
              <span className="gl-mp-card-title">{cap(mascotName(id))}</span>
              <span className="gl-mp-chip" data-ready={selected ? "1" : "0"}>{selected ? "Selected" : "Choose"}</span>
            </label>
          );
        })}
      </div>

      <RecoveryNote recovery={p.recovery} onRecheck={p.onRecheck} onDismiss={p.onDismiss} onHistories={p.onHistories} />

      <div className="gl-mp-actions gl-solo-actions">
        <GameButton variant="primary" onClick={p.onBegin} data-testid="solo-begin" {...(p.ready ? {} : { unavailable: p.loadError ? "The characters could not load." : "Loading the characters…" })}>
          Begin run
        </GameButton>
        <p className="gl-mp-help">The run starts the moment you begin. It can't be paused or resumed. Move with WASD or the arrow keys; Space is Burst once Soulfire is 4/4.</p>
        {p.loadError ? <p className="gl-mp-error" role="alert">Characters could not load: {p.loadError}</p> : null}
        {p.error ? <p className="gl-mp-error" role="alert" data-testid="solo-error">{p.error}</p> : null}
      </div>
    </div>
  );
}

// ---- the live HUD: slim edge zones over the one arena; every number is the server's ------------------------------------------------------
function SoloHud({ hud, onLeave, finished, over, rich, you }: { hud: PlayHud; onLeave: () => void; finished: boolean; over: boolean; rich: boolean; you: PlayHud["players"][number] | undefined }) {
  const conn = hud.connection;
  const live = conn.kind === "live" || conn.kind === "ended";
  return (
    <div className="gl-play gl-solo-hud" data-testid="solo-hud">
      <div className="gl-play-top">
        <BackLink onClick={onLeave}>{finished ? "Home" : "Leave"}</BackLink>
        {!finished ? (
          <StatusPill tone={live ? "ok" : conn.kind === "connecting" ? "neutral" : "warn"} role="status" data-testid="hud-connection" data-state={conn.kind}>
            {conn.kind === "live" ? "Live" : conn.kind === "connecting" ? "Connecting…" : conn.kind === "degraded" ? "Connection trouble" : conn.kind === "failed" ? "Disconnected" : "Ended"}
          </StatusPill>
        ) : null}
      </div>

      {hud.identity ? <div className="gl-play-clock" data-testid="hud-clock" data-tick={hud.tick} aria-label="Time remaining">{clock(over ? 0 : hud.secondsLeft)}</div> : null}

      {you ? (
        <div className="gl-play-me" data-testid="hud-you">
          <div className="gl-play-score" data-testid="hud-score" data-score={you.totalScore}>{you.totalScore}</div>
          <Soulfire charge={you.charge} />
          {you.overlit && you.overlitDeadlineTick !== null && !finished ? (
            <span className="gl-play-overlit" data-testid="hud-overlit">Overlit · auto-Burst in {Math.max(0, (you.overlitDeadlineTick - hud.tick) / hud.tickRate).toFixed(1)} s</span>
          ) : null}
        </div>
      ) : null}

      {!finished && conn.kind === "degraded" ? (
        <div className="gl-play-banner" role="alert" data-kind="degraded" data-testid="hud-banner">
          <strong>Connection trouble</strong>
          <span>{conn.reason}</span>
          <span>The picture is the last confirmed state, not live play. A dropped solo run can't be resumed.</span>
        </div>
      ) : null}

      {!finished && hud.identity && (!rich || !hud.eventsComplete) ? (
        <span className="gl-play-note gl-solo-limit" data-testid="solo-limit">{rich ? "Some event cues were missed. The arena shows the latest confirmed state." : "This game server did not send candle positions."}</span>
      ) : null}
    </div>
  );
}

// ---- recording complete: saving -> saved and verified -------------------------------------------------------------------------------------
const TITLE: Readonly<Record<SaveState["kind"], string>> = { saving: "Saving your original…", pending: "Still saving", saved: "Original recorded", failed: "Couldn't save this run", interrupted: "Run not saved" };

function FinishPanel(p: {
  save: SaveState | null; liveScore: number | null; lastTick: number | null;
  onRetry: () => void; onHistories: () => void; onAgain: () => void; onHome: () => void; onViewArena: () => void;
}) {
  const s: SaveState = p.save ?? { kind: "saving", attempt: 0 };
  const verifiedScore = s.kind === "saved" ? s.original.score : null;
  const score = verifiedScore ?? p.liveScore;
  const retryable = s.kind === "pending" || (s.kind === "failed" && s.retryable);
  return (
    <FrostPanel className="gl-mp-results gl-solo-done" role="region" aria-label="Run complete" data-testid="solo-finish" data-save={s.kind}>
      <div className="gl-solo-done-head">
        {/* The 90 seconds have all run out: the sand is history. While the server verifies it the stream still runs; saved, the glass is still. */}
        <div className="gl-solo-glass" data-flowing={s.kind === "saving" ? "1" : "0"}><HourglassMark elapsed={s.kind === "saving" ? 0.96 : 1} label={s.kind === "saved" ? "The run is history" : "The run is being written into history"} /></div>
        <div>
          <p className="gl-eyebrow">Recording complete</p>
          <h2 data-testid="solo-headline">{TITLE[s.kind]}</h2>
        </div>
      </div>

      <div className="gl-solo-score" data-testid="solo-score" data-verified={verifiedScore !== null ? "1" : "0"}>
        <span className="gl-solo-score-n">{score ?? "–"}</span>
        <span className="gl-solo-score-cap">
          {verifiedScore !== null ? "Verified score" : p.liveScore !== null ? `Last live score${p.lastTick !== null ? ` · tick ${p.lastTick}` : ""}` : "No score received"}
        </span>
      </div>

      <p className="gl-mp-results-sub" role="status" data-testid="solo-save-status">
        <StatusPill tone={s.kind === "saved" ? "ok" : s.kind === "saving" ? "neutral" : "warn"}>
          {s.kind === "saved" ? "Saved · verified" : s.kind === "saving" ? "Saving" : s.kind === "pending" ? "Pending" : s.kind === "failed" ? "Failed" : "Not saved"}
        </StatusPill>
        {" "}
        {s.kind === "saving" ? (s.attempt > 0 ? "The server is still confirming. Trying again with the same key." : "The server is verifying the recording before it counts as an original.") : null}
        {s.kind === "saved" ? `${cap(mascotName(s.original.mascotId))} original #${shortId(s.original.id)} · final state ${shortHash(s.original.finalStateHash)}` : null}
        {s.kind === "pending" ? `The server hasn't confirmed the save yet.${s.reason ? ` ${s.reason}` : ""} Your run isn't lost: retrying asks again with the same key.` : null}
        {s.kind === "failed" ? saveFailureText(s.code) : null}
        {s.kind === "interrupted" ? "The server has no finished run for this key, so it isn't an original." : null}
      </p>

      {s.kind === "saved" ? (
        <p className="gl-mp-help" data-testid="solo-next">Private to you, in My Histories. Replay it there and pick a moment to fork; Ghost Challenge and the Fork itself arrive in the next build.</p>
      ) : s.kind === "saving" ? (
        <p className="gl-mp-help">You can leave: the server finishes the save either way, and My Histories will find it.</p>
      ) : null}

      <div className="gl-mp-results-actions">
        {s.kind === "saved" ? (
          <>
            <GameButton variant="primary" onClick={p.onHistories} data-testid="solo-histories">My Histories</GameButton>
            <GameButton onClick={p.onAgain} data-testid="solo-again">Another run</GameButton>
            <GameButton onClick={p.onHome}>Home</GameButton>
          </>
        ) : retryable ? (
          <>
            <GameButton variant="primary" onClick={p.onRetry} data-testid="solo-retry">Retry save</GameButton>
            <GameButton onClick={p.onHome}>Home</GameButton>
          </>
        ) : s.kind === "saving" ? (
          <GameButton onClick={p.onHome}>Home</GameButton>
        ) : (
          <>
            <GameButton variant="primary" onClick={p.onAgain} data-testid="solo-again">Start another run</GameButton>
            <GameButton onClick={p.onHome}>Home</GameButton>
          </>
        )}
      </div>
      <div className="gl-mp-results-foot">
        <button type="button" className="gl-mp-link" onClick={p.onViewArena} data-testid="solo-view-arena">View the arena</button>
      </div>
    </FrostPanel>
  );
}
