import { useCallback, useEffect, useMemo, useRef, useState, type CSSProperties, type KeyboardEvent, type PointerEvent as ReactPointerEvent } from "react";
import { preparedHistory, preparedRewind } from "./demo/fixtures.ts";
import { SandGlass } from "./SandGlass.tsx";

/**
 * Revenant's demo Timeline dock. A continuous history track from 06:00 to 12:00 UTC: any minute can be previewed (drag or click) and
 * one click on "Fork here" fixes it. Graph playback is a separate, graph-only affordance. Presentation only: nothing here replays
 * a ledger, fetches, or changes the shared Timeline engine (the live path keeps TimelineScrubber).
 */
export const HISTORY_MINUTES = 360;
export const HISTORY_START = preparedRewind.horizon - HISTORY_MINUTES * 60_000;
export const DEFAULT_FORK_MIN = Math.round((preparedRewind.fork.atMs - HISTORY_START) / 60_000);
export const atMinute = (m: number) => HISTORY_START + m * 60_000;
const START_HOUR = new Date(HISTORY_START).getUTCHours();
const pad = (n: number) => String(n).padStart(2, "0");
export const hhmm = (m: number) => `${pad(START_HOUR + Math.floor(m / 60))}:${pad(m % 60)}`;
const clamp = (m: number) => Math.min(HISTORY_MINUTES, Math.max(0, Math.round(m)));

// The original history as a quiet silhouette behind the rail: the same prepared network curve the chart draws, never re-derived.
const SPARK_H = 28;
const spark = (() => {
  const pts = preparedHistory.filter(p => p.balance.bankId === preparedRewind.bank).map(p => ({ m: (p.atMs - HISTORY_START) / 60_000, v: Number(p.balance.availableMinor) }));
  const lo = Math.min(...pts.map(p => p.v)), hi = Math.max(...pts.map(p => p.v)), span = hi - lo || 1;
  const line = pts.map(p => `${((p.m / HISTORY_MINUTES) * 1000).toFixed(1)} ${(SPARK_H - 3 - ((p.v - lo) / span) * (SPARK_H - 8)).toFixed(2)}`);
  return { line: "M" + line.join(" L"), area: `M${line.join(" L")} L1000 ${SPARK_H} L0 ${SPARK_H} Z` };
})();
const SEALS = Array.from({ length: 19 }, (_, i) => i * 20);
const HOURS = Array.from({ length: 7 }, (_, i) => i * 60);

/** Graph-only playback: draws the displayed history left to right, pausable, restartable. It never touches the fork or the handle. */
const PLAY_MS = 3600;
const easeInOutSine = (p: number) => -(Math.cos(Math.PI * p) - 1) / 2;
export function useGraphPlayback() {
  const [progress, setProgress] = useState<number | null>(null);
  const [playing, setPlaying] = useState(false);
  const elapsed = useRef(0), raf = useRef(0), live = useRef(false);
  const cancel = () => { cancelAnimationFrame(raf.current); live.current = false; };
  useEffect(() => cancel, []);
  const run = useCallback(() => {
    const t0 = performance.now() - elapsed.current;
    const tick = (now: number) => {
      if (!live.current) return;
      elapsed.current = Math.min(PLAY_MS, now - t0);
      if (elapsed.current >= PLAY_MS) { live.current = false; elapsed.current = 0; setPlaying(false); setProgress(null); return; }
      setProgress(easeInOutSine(elapsed.current / PLAY_MS));
      raf.current = requestAnimationFrame(tick);
    };
    live.current = true;
    raf.current = requestAnimationFrame(tick);
  }, []);
  const toggle = useCallback(() => {
    if (live.current) { cancel(); setPlaying(false); return; }
    if (progress === null) { elapsed.current = 0; setProgress(0); }
    setPlaying(true);
    run();
  }, [progress, run]);
  const stop = useCallback(() => { cancel(); elapsed.current = 0; setPlaying(false); setProgress(null); }, []);
  return { progress, playing, toggle, stop };
}

export type DemoTimelineProps = {
  selected: number; confirmed: number; armed: boolean; locked: boolean; burst: number; playing: boolean; progress: number | null;
  onSelect: (minute: number) => void; onFork: () => void; onPlayPause: () => void;
};

const RING = 2 * Math.PI * 20;

export function DemoTimeline({ selected, confirmed, armed, locked, burst, playing, progress, onSelect, onFork, onPlayPause }: DemoTimelineProps) {
  const inner = useRef<HTMLDivElement>(null);
  const dragging = useRef(false);
  const [drag, setDrag] = useState(false);
  const f = selected / HISTORY_MINUTES, c = confirmed / HISTORY_MINUTES;
  const minuteAt = (clientX: number) => {
    const r = inner.current!.getBoundingClientRect();
    return clamp(r.width > 0 ? ((clientX - r.left) / r.width) * HISTORY_MINUTES : 0);
  };
  const down = (e: ReactPointerEvent<HTMLDivElement>) => {
    if (locked || e.button !== 0) return;
    e.preventDefault();
    e.currentTarget.setPointerCapture(e.pointerId);
    e.currentTarget.focus({ preventScroll: true });
    dragging.current = true; setDrag(true);
    onSelect(minuteAt(e.clientX));
  };
  const move = (e: ReactPointerEvent<HTMLDivElement>) => { if (dragging.current) onSelect(minuteAt(e.clientX)); };
  const up = (e: ReactPointerEvent<HTMLDivElement>) => {
    dragging.current = false; setDrag(false);
    if (e.currentTarget.hasPointerCapture(e.pointerId)) e.currentTarget.releasePointerCapture(e.pointerId);
  };
  const key = (e: KeyboardEvent<HTMLDivElement>) => {
    if (locked || e.altKey || e.ctrlKey || e.metaKey) return;
    const step = e.shiftKey ? 10 : 1;
    const next = e.key === "ArrowLeft" || e.key === "ArrowDown" ? selected - step : e.key === "ArrowRight" || e.key === "ArrowUp" ? selected + step
      : e.key === "PageDown" ? selected - 60 : e.key === "PageUp" ? selected + 60 : e.key === "Home" ? 0 : e.key === "End" ? HISTORY_MINUTES : null;
    if (e.key === "Enter") { e.preventDefault(); onFork(); return; }
    if (next === null) return;
    e.preventDefault();
    onSelect(clamp(next));
  };
  const spent = useMemo(() => ({ clipPath: `inset(0 ${((1 - f) * 100).toFixed(3)}% 0 0)` }), [f]);
  const set = selected === confirmed;
  const replaying = progress !== null;

  return <section className={"r8-tl" + (locked ? " is-locked" : "") + (drag ? " is-dragging" : "") + (playing ? " is-playing" : "")} aria-label="Rewind timeline" data-testid="r8-timeline"
    style={{ "--f": f, "--c": c } as CSSProperties}>
    <div className="r8-tl-glass"><SandGlass elapsed={f} flowing={drag || playing} burst={burst} /></div>

    <div className="r8-tl-main">
      <div className="r8-tl-rail" role="slider" tabIndex={locked ? -1 : 0} aria-label="History moment" aria-orientation="horizontal" aria-disabled={locked || undefined}
        aria-valuemin={0} aria-valuemax={HISTORY_MINUTES} aria-valuenow={selected} aria-valuetext={`${hhmm(selected)} UTC`}
        onPointerDown={down} onPointerMove={move} onPointerUp={up} onPointerCancel={up} onKeyDown={key} data-testid="r8-timeline-rail">
        <div className="r8-tl-inner" ref={inner}>
          <div className="r8-tl-land" aria-hidden="true">
            <svg viewBox={`0 0 1000 ${SPARK_H}`} preserveAspectRatio="none"><path className="r8-tl-area" d={spark.area} /><path className="r8-tl-line" d={spark.line} /></svg>
            <svg viewBox={`0 0 1000 ${SPARK_H}`} preserveAspectRatio="none" className="is-lit" style={spent}><path className="r8-tl-area" d={spark.area} /><path className="r8-tl-line" d={spark.line} /></svg>
          </div>
          <div className="r8-tl-groove" aria-hidden="true">
            <i className="r8-tl-future" style={{ left: `calc(${c} * 100%)` }} />
            <i className="r8-tl-fill" />
            {SEALS.map(m => <b key={m} className={m % 60 === 0 ? "is-hour" : ""} style={{ left: `${(m / HISTORY_MINUTES) * 100}%` }} />)}
          </div>
          <div className={"r8-tl-pin" + (burst > 0 ? " is-landing" : "")} key={burst} aria-hidden="true"><i /><span /></div>
          <div className="r8-tl-handle" aria-hidden="true">
            <i className="r8-tl-stem" /><i className="r8-tl-knob" /><span className="r8-tl-bubble">{hhmm(selected)}</span>
          </div>
        </div>
      </div>
      <div className="r8-tl-hours" aria-hidden="true">{HOURS.map(m => <span key={m} style={{ left: `${(m / HISTORY_MINUTES) * 100}%` }}>{hhmm(m)}</span>)}</div>
    </div>

    <div className="r8-tl-side">
      <div className="r8-tl-read"><span className="r8-tl-k">Selected moment</span>
        <output className="r8-tl-clock" data-testid="r8-timeline-time">{hhmm(selected)}<small>UTC</small></output></div>
      <div className="r8-tl-chips">
        <span className={"r8-tl-chip is-fork" + (armed ? " is-armed" : "")} key={"c" + burst} data-testid="r8-timeline-fork"><i aria-hidden="true" />Fork <b>{hhmm(confirmed)}</b>{armed && <em>Ready</em>}</span>
        <span className="r8-tl-chip is-kept"><svg viewBox="0 0 12 12" aria-hidden="true"><path d="M2.4 6.4l2.3 2.3 4.9-5.2" /></svg>Original preserved</span>
      </div>
      <div className="r8-tl-actions">
        <button type="button" className={"r8-tl-play" + (playing ? " is-on" : "") + (replaying ? " is-replay" : "")} onClick={onPlayPause} disabled={locked} aria-pressed={playing}
          aria-label={playing ? "Pause history replay" : "Play history replay"} data-testid="r8-timeline-play">
          <svg className="r8-tl-ring" viewBox="0 0 44 44" aria-hidden="true"><circle cx="22" cy="22" r="20" strokeDasharray={RING} strokeDashoffset={RING * (1 - (replaying ? progress : 0))} /></svg>
          {playing ? <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M8 6h3.2v12H8zM12.8 6H16v12h-3.2z" /></svg>
            : <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M9 6.2v11.6l9.4-5.8z" /></svg>}
        </button>
        <button type="button" className={"r8-tl-forkbtn" + (set ? " is-set" : "")} onClick={onFork} disabled={locked} data-testid="r8-timeline-fork-here">
          {burst > 0 && <i className="r8-tl-sheen" key={burst} aria-hidden="true" />}
          <svg viewBox="0 0 20 20" aria-hidden="true" className={set ? "is-check" : undefined}>{set ? <path d="M5 10.4l3.2 3.2 6.6-7" /> : <path d="M10 2.4l7.6 7.6-7.6 7.6L2.4 10z" />}</svg>
          <span>Fork here</span><b>{hhmm(selected)}</b>
        </button>
      </div>
    </div>
  </section>;
}
