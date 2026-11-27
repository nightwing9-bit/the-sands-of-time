// Replay and compare (Spec 13, task 13.10): a saved Original or a saved future (Challenge / Fork) seen through the Sands of Time dock.
//
// What is real here: every position, charge, score and event is a state the server verified and served from /originals/{id}/trace or
// /children/{id}/trace, held in memory by identity (src/history/historyStore.ts). Scrubbing reads memory. Play/pause is LOCAL viewing: it moves
// no clock on the server. Nothing in this file computes a score, a collision or a pickup, and nothing here invents a state for a tick that has
// not arrived (the last real state stays, dimmed, with the dock saying it is loading).
//
// Not here, by design: live play. Fork here and Ghost Challenge only hand a plan to the race journey (src/race, task 13.11); the Fork button reports
// eligibility, and is offered only for a tick whose recorded state is loaded, because the rewind ends exactly there.
import { useCallback, useEffect, useMemo, useReducer, useRef, useState, type ReactNode } from "react";
import { createPortal } from "react-dom";
import { BackLink, FrostPanel, GameButton } from "../ui/primitives.tsx";
import { Soulfire } from "../screens/PlayScreen.tsx";
import { useAudioMode } from "../audio/react.ts";
import { CharacterTextures } from "../characters/CharacterAssets.ts";
import { CandleTextures } from "../candles/CandleAssets.ts";
import { prefersReducedMotion } from "../fx/motion.ts";
import type { ArenaWorld } from "../stage/ArenaWorld.ts";
import type { ActorSlot } from "../presentation/types.ts";
import { cap, mascotName, shortId } from "../solo/model.ts";
import { ensureGuestSession, getOriginal, httpFailureText, SoloHttpError } from "../solo/soloWire.ts";
import { fetchChildChunk, fetchChildSummary, fetchComparison, fetchOriginalChunk, type ComparisonWire } from "./historyWire.ts";
import { describeLoadError, historyKey, openHistory, type LoadFailure, type OpenedHistory } from "./historyStore.ts";
import { HistoryScene, type ScenePose } from "./HistoryScene.ts";
import { HistoryDock } from "./HistoryDock.tsx";
import { advancePlayback, clampTick, clockText, compareAt, forkVerdict, futureLabel, litCandles, ribbonOf, signed } from "./model.ts";
import { metaFromOriginal, metaFromSummary, metaOfTarget, originalVersionKey, type ReplayMeta, type ReplayTarget } from "./target.ts";
import { challengePlan, forkPlan, type RacePlan } from "../race/raceModel.ts";

type Load<T> = { readonly kind: "loading" } | { readonly kind: "error"; readonly failure: LoadFailure } | { readonly kind: "ready"; readonly value: T };

export interface ReplayProps {
  readonly world: ArenaWorld | null;
  readonly target: ReplayTarget;
  /** The element below the stage the dock is portalled into, so the 16:9 arena re-fits above it. */
  readonly dockHost: HTMLElement | null;
  readonly onBack: () => void;
  /** Opens the replay of the Original a saved future grew from. */
  readonly onOpenOriginal: (id: string) => void;
  /** 13.11: starts a Fork at the selected tick, or a Ghost Challenge from T0, of the Original being viewed. */
  readonly onRace: (plan: RacePlan) => void;
}

const EVENT_TEXT: Readonly<Record<string, (value: number) => string>> = {
  CandleCollected: (v) => `Candle collected · +${v}`,
  BurstManual: () => "Burst",
  BurstAuto: () => "Burst (automatic)",
  OverlitStarted: () => "Overlit began",
  OverlitCancelled: () => "Overlit ended",
  RoundFinished: () => "Round finished",
};

// The curve is immutable for a given saved future: fetch it once per session, whichever way the screen is opened.
const curveCache = new Map<string, ComparisonWire>();

const slotOf = (mascotId: number): ActorSlot => (Math.min(3, Math.max(0, mascotId)) as ActorSlot);
const failureOf = (error: unknown): LoadFailure => describeLoadError(error);

export function ReplayScreen({ world, target, dockHost, onBack, onOpenOriginal, onRace }: ReplayProps) {
  useAudioMode("menu"); // histories keep the persistent menu loop (skill ref 07): no gameplay theme without gameplay
  const targetRef = useRef(target);
  targetRef.current = target;
  const [, bump] = useReducer((n: number) => n + 1, 0);
  const [meta, setMeta] = useState<Load<ReplayMeta>>({ kind: "loading" });
  const [characters, setCharacters] = useState<CharacterTextures | null>(null);
  const [textureError, setTextureError] = useState<string | null>(null);
  const [candles, setCandles] = useState<CandleTextures | null>(null);
  const [history, setHistory] = useState<OpenedHistory | null>(null);
  const [selected, setSelected] = useState(0);
  const [cursor, setCursor] = useState(0);
  const [playing, setPlaying] = useState(false);
  const [buffering, setBuffering] = useState(false);
  const [curve, setCurve] = useState<Load<ComparisonWire> | null>(null);
  const [curveRetry, setCurveRetry] = useState(0);
  const [showOriginal, setShowOriginal] = useState(false);
  const [source, setSource] = useState<OpenedHistory | null>(null);
  const cursorRef = useRef(0);
  const sceneRef = useRef<HistoryScene | null>(null);

  // ---- who is being opened --------------------------------------------------------------------------------------------------------
  useEffect(() => {
    const abort = new AbortController();
    setMeta({ kind: "loading" });
    setHistory(null); setSource(null); setShowOriginal(false); setCurve(null);
    setPlaying(false); setBuffering(false);
    const direct = metaOfTarget(targetRef.current);
    if (direct) { setMeta({ kind: "ready", value: direct }); return () => abort.abort(); }
    // Only an id is known (a refresh): ask the owner routes who it is. A foreign or unfinished id answers the same 404 as an unknown one.
    void (async () => {
      try {
        await ensureGuestSession(abort.signal);
        const t = targetRef.current;
        const value = t.kind === "original" ? metaFromOriginal(await getOriginal(t.id, abort.signal)) : metaFromSummary(await fetchChildSummary(t.id, abort.signal));
        if (!abort.signal.aborted) setMeta({ kind: "ready", value });
      } catch (error) {
        if (!abort.signal.aborted) setMeta({ kind: "error", failure: error instanceof SoloHttpError ? { text: httpFailureText(error), retryable: error.kind === "offline" || error.kind === "busy" || error.kind === "unavailable", busy: error.kind === "busy", integrity: false } : failureOf(error) });
      }
    })();
    return () => abort.abort();
  }, [target.kind, target.id]);

  const m = meta.kind === "ready" ? meta.value : null;
  const slot = m ? slotOf(m.mascotId) : 0;

  useEffect(() => {
    const abort = new AbortController();
    let loaded: CharacterTextures | null = null;
    let loadedCandles: CandleTextures | null = null;
    const fail = (error: unknown) => { if (!abort.signal.aborted) setTextureError(String(error)); };
    CharacterTextures.load(abort.signal).then(
      (t) => { if (abort.signal.aborted) return t.destroy(); loaded = t; setCharacters(t); },
      fail,
    );
    CandleTextures.load(abort.signal).then(
      (t) => { if (abort.signal.aborted) return t.destroy(); loadedCandles = t; setCandles(t); },
      fail,
    );
    return () => { abort.abort(); loaded?.destroy(); loadedCandles?.destroy(); setCharacters(null); setCandles(null); };
  }, []);

  // ---- the history itself: cached by identity, loaded in at most ten bounded chunks ---------------------------------------------------
  useEffect(() => {
    if (!m) return;
    const hash = m.finalStateHash ?? "?", vk = m.versionKey ?? "?";
    const opened = openHistory(
      historyKey({ kind: m.kind, id: m.id, finalStateHash: hash, versionKey: vk }),
      { kind: m.kind, id: m.id, ...(m.finalStateHash ? { finalStateHash: m.finalStateHash } : {}), ...(m.versionKey ? { versionKey: m.versionKey } : {}), ...(m.sourceId ? { sourceId: m.sourceId } : {}), ...(m.kind === "child" ? { forkTick: m.forkTick } : {}) },
      (id) => (m.kind === "child" ? (from, count, signal) => fetchChildChunk(id, from, count, signal) : (from, count, signal) => fetchOriginalChunk(id, from, count, signal)),
    );
    const start = m.kind === "child" ? m.forkTick : 0; // a saved future opens on the moment it split
    cursorRef.current = start; setSelected(start); setCursor(start);
    setHistory(opened);
    const off = opened.loader.subscribe(bump);
    opened.loader.want(start);
    opened.loader.start();
    return () => { off(); opened.loader.abort(); };
  }, [m]);

  // ---- the same-tick curve of a saved future: one request, never per movement ------------------------------------------------------------
  useEffect(() => {
    if (!m || m.kind !== "child") { setCurve(null); return; }
    const key = `${m.id}:${m.finalStateHash ?? ""}`;
    const hit = curveCache.get(key);
    if (hit) { setCurve({ kind: "ready", value: hit }); return; }
    const abort = new AbortController();
    setCurve({ kind: "loading" });
    fetchComparison(m.id, abort.signal).then(
      (value) => { if (abort.signal.aborted) return; curveCache.set(key, value); setCurve({ kind: "ready", value }); },
      (error: unknown) => { if (!abort.signal.aborted) setCurve({ kind: "error", failure: failureOf(error) }); },
    );
    return () => abort.abort();
  }, [m, curveRetry]);

  // ---- the optional original path: the SAME head's recorded route at the same tick, loaded only when asked -------------------------------------
  const sourceHash = m?.kind === "child" ? m.sourceKnown?.finalStateHash ?? (curve?.kind === "ready" ? curve.value.sourceFinalStateHash : undefined) : undefined;
  useEffect(() => {
    if (!showOriginal || !m || m.kind !== "child" || !m.sourceId || !sourceHash) { setSource(null); return; }
    const sourceId = m.sourceId;
    const opened = openHistory(
      historyKey({ kind: "original", id: sourceId, finalStateHash: sourceHash, versionKey: m.sourceKnown ? originalVersionKey(m.sourceKnown) : "*" }),
      { kind: "original", id: sourceId, finalStateHash: sourceHash, ...(m.sourceKnown ? { versionKey: originalVersionKey(m.sourceKnown) } : {}) },
      (id) => (from, count, signal) => fetchOriginalChunk(id, from, count, signal),
    );
    setSource(opened);
    const off = opened.loader.subscribe(bump);
    opened.loader.want(Math.floor(cursorRef.current));
    opened.loader.start();
    return () => { off(); opened.loader.abort(); };
  }, [showOriginal, m, sourceHash]);

  // ---- the arena: one head (and optionally the original's ghost) in the shared world --------------------------------------------------------
  useEffect(() => {
    if (!world || !characters || !candles || !m) return;
    const scene = new HistoryScene(world, characters, slot, candles);
    sceneRef.current = scene;
    return () => { if (sceneRef.current === scene) sceneRef.current = null; scene.dispose(); };
  }, [world, characters, candles, m, slot]);

  const frames = history?.frames ?? null;
  const terminal = frames?.terminalTick ?? 2700;
  const reduced = useMemo(() => prefersReducedMotion(), []);

  /** Draws tick `t` (fractional while playing) from memory. Returns false, drawing nothing new, when that state has not arrived. */
  const draw = useCallback((t: number): boolean => {
    const scene = sceneRef.current;
    if (!scene || !frames) return false;
    const fl = Math.floor(t);
    const a = frames.get(fl);
    if (!a) { scene.setPending(true); return false; }
    const frac = reduced ? 0 : t - fl;
    const b = frac > 0 ? frames.get(fl + 1) ?? null : null;
    const pose: ScenePose = { a, b, frac: b ? frac : 0 };
    scene.setPending(false);
    scene.setPose(pose);
    const src = source?.frames;
    const sa = src?.get(fl);
    scene.setGhostPose(showOriginal && sa ? { a: sa, b: frac > 0 ? src!.get(fl + 1) ?? null : null, frac: b ? frac : 0 } : null);
    return true;
  }, [frames, source, showOriginal, reduced]);

  // Paused or seeked: draw exactly the cursor tick. Re-runs when a chunk lands, so a pending seek resolves the moment its state arrives.
  const rev = frames?.version ?? 0, srcRev = source?.frames.version ?? 0;
  useEffect(() => { if (!playing) draw(cursorRef.current); }, [draw, cursor, rev, srcRev, playing, world, characters, candles, m]);

  // ---- local playback: 30 recorded ticks per real second, never past a state that has not arrived ------------------------------------------------
  useEffect(() => {
    if (!playing || !history || !frames) return;
    const loader = history.loader;
    let raf = 0, last = performance.now(), wasWaiting = false;
    const frame = (now: number) => {
      const dt = Math.min(250, now - last);
      last = now;
      const r = advancePlayback(cursorRef.current, dt, frames.terminalTick, (t) => frames.has(t));
      cursorRef.current = r.cursor;
      draw(r.cursor);
      const fl = Math.floor(r.cursor);
      setCursor((c) => (c === fl ? c : fl));
      if (r.waiting !== wasWaiting) { wasWaiting = r.waiting; setBuffering(r.waiting); }
      if (r.waiting) { loader.want(fl); loader.want(fl + 1); source?.loader.want(fl); }
      if (r.ended) { setPlaying(false); setBuffering(false); setSelected(frames.terminalTick); setCursor(frames.terminalTick); return; }
      raf = requestAnimationFrame(frame);
    };
    raf = requestAnimationFrame(frame);
    return () => cancelAnimationFrame(raf);
  }, [playing, history, frames, source, draw]);

  // ---- interaction ----------------------------------------------------------------------------------------------------------------------
  const seek = useCallback((tick: number) => {
    const t = clampTick(tick, terminal);
    cursorRef.current = t;
    setSelected(t); setCursor(t);
    history?.loader.want(t);
    source?.loader.want(t);
  }, [terminal, history, source]);
  const togglePlay = useCallback(() => {
    if (playing) {
      const t = Math.floor(cursorRef.current); // pausing lands on an exact recorded tick, and that tick becomes the selected one
      cursorRef.current = t; setCursor(t); setSelected(t); setPlaying(false); setBuffering(false);
      return;
    }
    if (cursorRef.current >= terminal) { cursorRef.current = 0; setCursor(0); setSelected(0); }
    setPlaying(true);
  }, [playing, terminal]);
  const step = useCallback((delta: number) => { if (playing) togglePlay(); seek((playing ? Math.floor(cursorRef.current) : selected) + delta); }, [playing, togglePlay, seek, selected]);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.altKey || e.ctrlKey || e.metaKey) return;
      const el = e.target instanceof HTMLElement ? e.target : null;
      const free = !el || el === document.body || el.getAttribute("role") === "slider";
      if (e.key === "Escape") { e.preventDefault(); onBack(); return; }
      if (e.key === " " && free) { e.preventDefault(); togglePlay(); }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onBack, togglePlay]);

  // ---- what to say ------------------------------------------------------------------------------------------------------------------------
  const frame = frames?.get(cursor) ?? null;
  const status = history?.loader.status;
  const pending = !!frames && !frames.has(cursor);
  const failure = status?.kind === "failed" ? status.failure : null;
  const confirmed = !!frames?.identity; // a chunk was served: the server verified the stored history before answering
  const verdict = forkVerdict({ kind: m?.kind ?? "original", verified: confirmed, tick: selected, terminalTick: terminal });
  const sourceFrames = source?.frames ?? null;
  const points = curve?.kind === "ready" ? curve.value.points : null;
  const compare = m?.kind === "child" && frames ? compareAt({ tick: cursor, forkTick: m.forkTick, future: frames, source: sourceFrames, points }) : null;
  const ribbon = m?.kind === "child" ? ribbonOf(points, m.forkTick, terminal) : null;
  const loadingText = pending ? `Loading T${cursor}…` : status?.kind === "loading" && frames ? `Loading… ${frames.heldCount} of ${terminal + 1} moments ready` : null;
  const chip = !m ? "Loading history…" : m.kind === "original" ? `Original · ${cap(mascotName(m.mascotId))} · #${shortId(m.id)}` : `${futureLabel(m.childKind ?? "Challenge", m.forkTick)} · ${cap(mascotName(m.mascotId))} · #${shortId(m.id)}`;
  // 13.11: Fork here and Ghost Challenge are offered only on a server-confirmed Original, and a Fork only at a tick whose recorded state is loaded
  // (the rewind ends exactly there and the night reveal starts from it).
  const forkTickNow = selected; // while viewing plays, the selected tick is the one Fork here names; pausing selects the paused tick
  const planBase = m && m.kind === "original" ? { originalId: m.id, mascotId: m.mascotId, ...(m.finalStateHash ? { finalStateHash: m.finalStateHash } : {}), ...(m.versionKey ? { versionKey: m.versionKey } : {}) } : null;
  const forkLoaded = !!frames?.has(forkTickNow);
  const startFork = () => { if (planBase && verdict.allowed && forkLoaded && !playing) onRace(forkPlan({ ...planBase, forkTick: forkTickNow })); };
  const startChallenge = () => { if (planBase && confirmed) onRace(challengePlan(planBase)); };
  const finalFrame = frames?.get(terminal);
  const finalScore = finalFrame?.totalScore ?? m?.finalScore ?? null;

  let hud: ReactNode;
  if (meta.kind === "loading") {
    hud = <FrostPanel className="gl-hd-state" role="status" data-testid="replay-loading">Opening this history…</FrostPanel>;
  } else if (meta.kind === "error") {
    hud = (
      <FrostPanel className="gl-hd-state" role="alert" data-testid="replay-error">
        <p>{meta.failure.text}</p>
        <GameButton variant="primary" onClick={onBack}>Back to My Histories</GameButton>
      </FrostPanel>
    );
  } else if (m) {
    hud = (
      <div className="gl-hd-col">
        <FrostPanel className="gl-hd-card" aria-label="Moment" data-testid="replay-card">
          <p className="gl-eyebrow">{m.kind === "original" ? "Original" : m.childKind === "Fork" ? "Saved future · Fork" : "Saved future · Ghost challenge"}</p>
          <div className="gl-hd-score" data-testid="replay-score" data-tick={cursor}>
            <span className="gl-hd-score-n">{frame ? frame.totalScore : "–"}</span>
            <span className="gl-hd-score-cap">{m.kind === "child" ? "Future score" : "Score"} at T{cursor} · {clockText(cursor)}</span>
          </div>
          {frame ? (
            <>
              <Soulfire charge={frame.charge} />
              <p className="gl-hd-parts" data-testid="replay-parts">Movement {frame.movementPoints} · Candles {frame.pickupPoints} · Penalty {frame.penaltyPoints}</p>
              <p className="gl-hd-parts" data-testid="replay-lit">{litCandles(frame.activeCandleIds)} · {frame.pickups} collected</p>
              {frame.events.length > 0 ? (
                <ul className="gl-hd-events" data-testid="replay-events">
                  {frame.events.map((e, i) => <li key={i}>{(EVENT_TEXT[e.kind] ?? (() => e.kind))(e.value)}</li>)}
                </ul>
              ) : null}
            </>
          ) : <p className="gl-hd-parts" role="status">{failure ? "This part of the history could not be loaded." : "Loading this part of the history…"}</p>}
          {finalScore !== null && cursor !== terminal ? <p className="gl-hd-parts" data-testid="replay-final">Final score {finalScore} (at T{terminal})</p> : null}
        </FrostPanel>

        {m.kind === "child" ? (
          <FrostPanel className="gl-hd-card gl-hd-compare" aria-label="Original and future at the same tick" data-testid="replay-compare">
            <p className="gl-eyebrow">Same tick · T{compare?.atTick ?? cursor}</p>
            {compare && compare.mode !== "none" ? (
              <dl className="gl-hd-rows" data-mode={compare.mode}>
                <div><dt>Original</dt><dd data-testid="cmp-original">{compare.original}</dd></div>
                <div><dt>Future</dt><dd data-testid="cmp-future">{compare.future}</dd></div>
                <div><dt>Δ</dt><dd data-testid="cmp-delta" data-sign={Math.sign(compare.delta ?? 0)}>{signed(compare.delta ?? 0)}</dd></div>
              </dl>
            ) : curve?.kind === "error" ? (
              <p className="gl-hd-parts" role="alert">{curve.failure.text} <button type="button" className="gl-mp-link" onClick={() => setCurveRetry((n) => n + 1)}>Try again</button></p>
            ) : <p className="gl-hd-parts" role="status">Loading the score curve…</p>}
            {compare?.mode === "sampled" ? <p className="gl-hd-parts" data-testid="cmp-sampled">Original and Δ are the server's sample at T{compare.atTick}. Show the original's path for the exact tick.</p> : null}
            {m.childKind === "Fork" && cursor <= m.forkTick ? <p className="gl-hd-parts">Before the split there is one history: both scores are the same.</p> : null}
            <GameButton
              aria-pressed={showOriginal} data-testid="show-original" onClick={() => setShowOriginal((v) => !v)}
              {...(!sourceHash ? { unavailable: curve?.kind === "error" ? "The original's identity comes from the score curve, which didn't load." : "Waiting for the score curve." } : {})}
            >
              {showOriginal ? "Hide the original's path" : "Show the original's path"}
            </GameButton>
            {showOriginal && source ? <p className="gl-hd-parts" data-testid="source-status">{source.loader.status.kind === "failed" ? source.loader.status.failure.text : source.frames.complete ? "Original path ready. The ghost is the original's recorded head." : `Loading the original… ${source.frames.heldCount} of ${source.frames.terminalTick + 1}`}</p> : null}
          </FrostPanel>
        ) : null}

        <div className="gl-hd-links">
          {m.kind === "child" && m.sourceId ? <GameButton onClick={() => onOpenOriginal(m.sourceId!)} data-testid="open-original">Open the original</GameButton> : null}
          {m.kind === "original" ? (
            <>
              <GameButton variant="primary" onClick={startChallenge} data-testid="challenge-start" {...(confirmed ? {} : { unavailable: "The server hasn't confirmed this original yet." })}>Ghost Challenge from the start</GameButton>
              <p className="gl-hd-parts">Races your whole original from T0. Not the same as a fork at a chosen moment.</p>
            </>
          ) : null}
        </div>
        {frame && frame.visibleCandles == null ? <p className="gl-hd-note" data-testid="replay-note">Candle positions aren't part of saved histories yet. Scores, charge and events are exact.</p> : null}
      </div>
    );
  }

  const dock = m && frames ? (
    <HistoryDock
      kind={m.kind} terminal={terminal} selected={selected} cursor={cursor} playing={playing} buffering={buffering}
      loadedRuns={frames.loadedRuns()} markers={frames.markers} splitTick={m.kind === "child" ? m.forkTick : null} ribbon={ribbon}
      chip={chip} loadingText={loadingText}
      fork={{ verdict, available: forkLoaded && !playing, note: playing ? "Pause on the moment you want, then fork it." : !forkLoaded ? `Loading T${forkTickNow}…` : "Rewinds to this moment, then lets you race a different future.", onFork: startFork }}
      onSeek={seek} onPlayPause={togglePlay} onStep={step}
    />
  ) : null;

  return (
    <div className="gl-hd" data-testid="replay-screen" data-kind={m?.kind ?? "loading"}>
      <div className="gl-study-top">
        <BackLink onClick={onBack}>My Histories</BackLink>
        <span className="gl-hd-stamp" data-testid="replay-stamp">{m?.kind === "child" ? "The original is unchanged" : "Original preserved"}</span>
      </div>
      {textureError ? <p className="gl-world-failed" role="alert">Assets could not load: {textureError}</p> : null}
      {hud}
      {failure ? (
        <FrostPanel className="gl-hd-banner" role="alert" data-testid="replay-failure">
          <p>{failure.text}</p>
          <div>
            {failure.retryable ? <GameButton variant="primary" onClick={() => history?.loader.retry()} data-testid="replay-retry">Try again</GameButton> : null}
            <GameButton onClick={onBack}>Back to My Histories</GameButton>
          </div>
        </FrostPanel>
      ) : null}
      {pending && !failure ? <p className="gl-hd-pending" role="status" data-testid="replay-pending">Loading T{cursor}… the last real moment stays on screen until it arrives.</p> : null}
      {dock && dockHost ? createPortal(dock, dockHost) : null}
    </div>
  );
}
