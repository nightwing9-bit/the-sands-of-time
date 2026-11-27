// The Sands of Time dock (Spec 13, task 13.10): the contextual history instrument. It exists only on the replay screen, below the stage, and
// the full 16:9 arena re-fits above it. Hourglass LEFT, then one horizontal rail, then the transport and the one fork action.
//
//   rail ─ shared run          one line: before the split there is only one history
//        ─ after the split     the original stays the pale line; the saved future is drawn from the server's own score curve as its gap
//        ─ pips                real recorded candle pickups and Bursts only (loaded ticks), never a story beat that did not happen
//        ─ gold diamond        the SELECTED tick (what Fork Here would use)       ─ ice playhead   the tick being VIEWED during playback
//        ─ small notch         where a saved future split from its source (the future's own fork tick)
//
// Presentation only. It never fetches: it is handed what the store holds and reports where the person pointed.
import { useRef, type KeyboardEvent, type PointerEvent as ReactPointerEvent } from "react";
import { GameButton } from "../ui/primitives.tsx";
import { HourglassMark } from "../ui/HourglassMark.tsx";
import { clockText, fractionOf, keyTick, shortClock, tickFromFraction, type ForkVerdict, type Ribbon } from "./model.ts";
import type { HistoryMarker } from "./historyStore.ts";

export interface DockProps {
  readonly kind: "original" | "child";
  readonly terminal: number;
  /** The tick Fork Here would use (set by click, drag, keys, a pip, or a pause). */
  readonly selected: number;
  /** The tick the arena currently shows. Equals `selected` unless local playback is running. */
  readonly cursor: number;
  readonly playing: boolean;
  /** True while playback or a seek is waiting for a part of the history that has not arrived. */
  readonly buffering: boolean;
  readonly loadedRuns: readonly [number, number][];
  readonly markers: readonly HistoryMarker[];
  /** The tick a saved future split from its source, or null for an Original. 0 for a Challenge. */
  readonly splitTick: number | null;
  readonly ribbon: Ribbon | null;
  readonly chip: string;
  readonly loadingText: string | null;
  readonly fork: { readonly verdict: ForkVerdict; readonly available: boolean; readonly note: string | null; readonly onFork?: () => void };
  readonly onSeek: (tick: number) => void;
  readonly onPlayPause: () => void;
  readonly onStep: (delta: number) => void;
  /**
   * 13.11: while a Fork rewinds the dock is an instrument you watch, not one you drive. Locked, the rail ignores pointer and keys, the transport
   * buttons are inert and `lockedNote` replaces the fork action's reason. The playhead and hourglass still follow `cursor`.
   */
  readonly locked?: boolean;
  readonly lockedNote?: string;
}

const pct = (f: number) => `${(f * 100).toFixed(3)}%`;
const RIBBON_H = 44;
const RIBBON_AMP = 13;

function Glyph({ d }: { d: string }) {
  return <svg viewBox="0 0 16 16" width="16" height="16" aria-hidden="true" focusable="false"><path d={d} fill="currentColor" /></svg>;
}
const BACK = "M3 3h2v10H3zM13 3v10L6 8z";
const FWD = "M11 3h2v10h-2zM3 3l7 5-7 5z";
const PLAY = "M4 2.5v11l9-5.5z";
const PAUSE = "M4 3h3v10H4zM9 3h3v10H9z";

export function HistoryDock(p: DockProps) {
  const track = useRef<HTMLDivElement>(null);
  const dragging = useRef(false);
  const tickAt = (clientX: number): number => {
    const r = track.current!.getBoundingClientRect();
    return tickFromFraction(r.width > 0 ? (clientX - r.left) / r.width : 0, p.terminal);
  };
  const locked = p.locked === true;
  const onDown = (e: ReactPointerEvent<HTMLDivElement>) => {
    if (locked || e.button !== 0) return;
    dragging.current = true;
    e.currentTarget.setPointerCapture(e.pointerId);
    e.currentTarget.focus({ preventScroll: true });
    p.onSeek(tickAt(e.clientX));
  };
  const onMove = (e: ReactPointerEvent<HTMLDivElement>) => { if (dragging.current) p.onSeek(tickAt(e.clientX)); };
  const onUp = (e: ReactPointerEvent<HTMLDivElement>) => { dragging.current = false; if (e.currentTarget.hasPointerCapture(e.pointerId)) e.currentTarget.releasePointerCapture(e.pointerId); };
  const onKey = (e: KeyboardEvent<HTMLDivElement>) => {
    if (locked || e.altKey || e.ctrlKey || e.metaKey) return;
    const next = keyTick(e.key, e.shiftKey, p.selected, p.terminal, p.markers);
    if (next === null) return;
    e.preventDefault();
    p.onSeek(next);
  };

  const sel = fractionOf(p.selected, p.terminal), cur = fractionOf(p.cursor, p.terminal);
  const viewing = p.cursor !== p.selected;
  const elapsed = p.cursor / p.terminal;
  const split = p.splitTick !== null ? fractionOf(p.splitTick, p.terminal) : null;
  const ribbonPath = (() => {
    const r = p.ribbon;
    if (!r || r.points.length < 2 || r.maxAbs === 0) return null;
    const scale = RIBBON_AMP / r.maxAbs;
    return r.points.map((q, i) => `${i === 0 ? "M" : "L"}${(q.x * 1000).toFixed(2)} ${(RIBBON_H / 2 - q.d * scale).toFixed(2)}`).join(" ");
  })();
  const flat = !!p.ribbon && p.ribbon.points.length > 1 && p.ribbon.maxAbs === 0;
  const verdict = p.fork.verdict;
  const forkUnavailable = locked ? p.lockedNote ?? "Not available right now" : !verdict.allowed ? verdict.reason ?? "Not available" : !p.fork.available ? p.fork.note ?? "Not available yet" : undefined;

  return (
    <section className="gl-panel gl-dock gl-dock--history" aria-label="History timeline" data-testid="history-dock" data-kind={p.kind} data-playing={p.playing ? "1" : "0"} data-locked={locked ? "1" : "0"}>
      <div className="gl-dock-hourglass" data-testid="dock-hourglass">
        <HourglassMark elapsed={elapsed} flowing={p.playing || p.buffering} label={`Hourglass: ${Math.round(elapsed * 100)} percent of the run has run out`} />
      </div>

      <div className="gl-dock-main">
        <div className="gl-dock-top">
          <span className="gl-chip" data-kind={p.kind}><i aria-hidden="true" />{p.chip}</span>
          {p.loadingText ? <span className="gl-hd-loading" role="status" data-testid="dock-loading">{p.loadingText}</span> : flat ? <span className="gl-hd-loading" data-testid="dock-flat">Same score at every sampled moment after the split</span> : <span />}
          <output className="gl-dock-time" data-testid="dock-time" aria-live="off">
            <b data-testid="dock-clock">{clockText(p.cursor)}</b>
            <span data-testid="dock-tick">T{p.cursor} of {p.terminal}</span>
            {viewing ? <span className="gl-dock-sel" data-testid="dock-selected">selected T{p.selected}</span> : null}
          </output>
        </div>

        <div
          className="gl-hd-rail" role="slider" tabIndex={locked ? -1 : 0} data-testid="dock-rail" aria-readonly={locked ? true : undefined}
          aria-label="History position" aria-orientation="horizontal" aria-valuemin={0} aria-valuemax={p.terminal} aria-valuenow={locked ? p.cursor : p.selected}
          aria-valuetext={`Tick ${locked ? p.cursor : p.selected} of ${p.terminal}, ${clockText(locked ? p.cursor : p.selected)}`}
          onPointerDown={onDown} onPointerMove={onMove} onPointerUp={onUp} onPointerCancel={onUp} onKeyDown={onKey}
        >
          <div className="gl-hd-track" ref={track}>
            <span className="gl-hd-line" />
            {p.loadedRuns.map(([a, b]) => <span key={a} className="gl-hd-ready" style={{ left: pct(fractionOf(a, p.terminal)), width: pct(fractionOf(b, p.terminal) - fractionOf(a, p.terminal)) }} />)}
            {split !== null && split > 0 ? <span className="gl-hd-shared" style={{ left: 0, width: pct(split) }} data-testid="dock-shared" /> : null}
            {ribbonPath ? (
              <svg className="gl-hd-ribbon" viewBox={`0 0 1000 ${RIBBON_H}`} preserveAspectRatio="none" aria-hidden="true" focusable="false" data-testid="dock-ribbon">
                <path d={ribbonPath} />
              </svg>
            ) : null}
            {p.markers.map((m) => (
              <button
                key={`${m.tick}:${m.index}`} type="button" tabIndex={-1} className="gl-hd-pip" data-kind={m.kind}
                style={{ left: pct(fractionOf(m.tick, p.terminal)) }} aria-label={`${m.kind === "burst" ? "Burst" : "Candle collected"} at tick ${m.tick}`}
                onPointerDown={(e) => { e.stopPropagation(); }} onClick={() => { if (!locked) p.onSeek(m.tick); }}
              />
            ))}
            {split !== null ? <span className="gl-hd-split" style={{ left: pct(split) }} title={p.splitTick === 0 ? "Started at T0" : `Split from the original at T${p.splitTick}`} data-testid="dock-split" /> : null}
            {viewing || p.playing ? <span className="gl-hd-cursor" style={{ left: pct(cur) }} data-testid="dock-cursor" /> : null}
            <span className="gl-hd-selected" style={{ left: pct(sel) }} data-testid="dock-selected-marker" />
          </div>
        </div>

        <div className="gl-dock-scale" aria-hidden="true">
          <span>{shortClock(0)}</span><span>{shortClock(Math.round(p.terminal / 3))}</span><span>{shortClock(Math.round((p.terminal * 2) / 3))}</span><span>{shortClock(p.terminal)}</span>
        </div>
      </div>

      <div className="gl-hd-actions">
        <div className="gl-hd-transport" role="group" aria-label="Viewing controls">
          <button type="button" className="gl-hd-icon" aria-label="Back one moment" title="Back one moment (Left)" disabled={locked} onClick={() => p.onStep(-1)} data-testid="dock-step-back"><Glyph d={BACK} /></button>
          <button type="button" className="gl-hd-icon gl-hd-icon--play" aria-label={p.playing ? "Pause viewing" : "Play viewing"} title={p.playing ? "Pause (Space)" : "Play (Space)"} aria-pressed={p.playing} disabled={locked} onClick={p.onPlayPause} data-testid="dock-play"><Glyph d={p.playing ? PAUSE : PLAY} /></button>
          <button type="button" className="gl-hd-icon" aria-label="Forward one moment" title="Forward one moment (Right)" disabled={locked} onClick={() => p.onStep(1)} data-testid="dock-step-forward"><Glyph d={FWD} /></button>
        </div>
        <GameButton
          variant="primary" data-testid="dock-fork" data-allowed={verdict.allowed ? "1" : "0"} data-available={p.fork.available ? "1" : "0"}
          {...(forkUnavailable ? { unavailable: forkUnavailable } : p.fork.onFork ? { onClick: p.fork.onFork } : {})}
        >
          <span className="gl-hd-fork-mark" aria-hidden="true" />Fork here <small>T{p.selected}</small>
        </GameButton>
        <p className="gl-hd-why" data-testid="dock-fork-why">{locked ? p.lockedNote : !verdict.allowed ? verdict.reason : p.fork.note}</p>
      </div>
    </section>
  );
}
