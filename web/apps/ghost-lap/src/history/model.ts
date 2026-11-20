// Pure view rules of the history timeline (Spec 13, task 13.10). No React, no Pixi, no network.
// Nothing here is a game rule. It words and positions facts the server already verified: the tick, the score at that tick, whether a fork
// is allowed (T0..T2699 on an owned, verified Original), where a saved future split from its source. A value that was not delivered is
// reported as missing, never filled in.
import type { ChildKind, ComparisonPointWire, TraceKind } from "./historyWire.ts";
import { FINAL_TICK } from "./historyWire.ts";
import type { HistoryFrames } from "./historyStore.ts";

export const TICK_RATE = 30;
/** One-second VIEW step of Shift+Arrow (DIRECTOR BASELINE, skill ref 08). A view step, not a game rule. */
export const SHIFT_STEP = 30;

export const clampTick = (tick: number, terminal = FINAL_TICK): number => Math.min(terminal, Math.max(0, Math.round(tick)));

/** `mm:ss.mmm` derived from the integer tick (tick / 30). Milliseconds are floored so a tick never reads as the next second. The tick itself is the authority. */
export function clockText(tick: number): string {
  const ms = Math.floor((tick * 1000) / TICK_RATE);
  const m = Math.floor(ms / 60000), s = Math.floor((ms % 60000) / 1000);
  return `${String(m).padStart(2, "0")}:${String(s).padStart(2, "0")}.${String(ms % 1000).padStart(3, "0")}`;
}
/** `m:ss` for the scale beneath the rail. */
export function shortClock(tick: number): string {
  const s = Math.floor(tick / TICK_RATE);
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, "0")}`;
}
export const tickFromFraction = (fraction: number, terminal = FINAL_TICK): number => clampTick(Math.min(1, Math.max(0, fraction)) * terminal, terminal);
export const fractionOf = (tick: number, terminal = FINAL_TICK): number => (terminal <= 0 ? 0 : Math.min(1, Math.max(0, tick / terminal)));

/** Keyboard seek of the focused rail. Returns the new tick, or null when the key is not a seek key. `,` and `.` jump to the previous/next real event marker. */
export function keyTick(key: string, shift: boolean, tick: number, terminal: number, markers: readonly { tick: number }[]): number | null {
  switch (key) {
    case "ArrowLeft": case "ArrowDown": return clampTick(tick - (shift ? SHIFT_STEP : 1), terminal);
    case "ArrowRight": case "ArrowUp": return clampTick(tick + (shift ? SHIFT_STEP : 1), terminal);
    case "Home": return 0;
    case "End": return terminal;
    case ",": { let best: number | null = null; for (const m of markers) if (m.tick < tick) best = m.tick; return best ?? tick; }
    case ".": { for (const m of markers) if (m.tick > tick) return m.tick; return tick; }
    default: return null;
  }
}

// ---- fork eligibility ----------------------------------------------------------------------------------------------------------------
export interface ForkVerdict { readonly allowed: boolean; readonly reason: string | null }
/**
 * Fork Here is possible only on an OWNED, VERIFIED Original at ticks 0..2699. The final tick can be inspected but has no future to change, and a
 * saved future can never be forked again. This is the same rule the server enforces (PrepareFork); the browser only avoids offering what it refuses.
 */
export function forkVerdict(s: { kind: TraceKind; verified: boolean; tick: number; terminalTick: number }): ForkVerdict {
  if (s.kind === "child") return { allowed: false, reason: "A saved future can't be forked again. Fork the original instead." };
  if (!s.verified) return { allowed: false, reason: "The server hasn't confirmed this original yet." };
  if (s.tick >= s.terminalTick) return { allowed: false, reason: `Nothing is left to change at T${s.terminalTick}. Pick an earlier moment.` };
  return { allowed: true, reason: null };
}

// ---- local playback ------------------------------------------------------------------------------------------------------------------
export interface Playback { readonly cursor: number; readonly ended: boolean; /** True when playback had to stop short because the next state is not loaded yet. */ readonly waiting: boolean }
/**
 * Moves a fractional playback cursor forward by real elapsed time at the recorded 30 ticks per second, never past a state that is not loaded.
 * The cursor sits between two real states; the scene eases between them for smooth motion, but a stopped cursor is always on an exact tick.
 */
export function advancePlayback(cursor: number, dtMs: number, terminal: number, has: (tick: number) => boolean): Playback {
  if (cursor >= terminal) return { cursor: terminal, ended: true, waiting: false };
  const target = Math.min(terminal, cursor + (Math.max(0, dtMs) * TICK_RATE) / 1000);
  let reach = Math.floor(cursor);
  if (!has(reach)) return { cursor: Math.floor(cursor), ended: false, waiting: true };
  while (reach < Math.min(terminal, Math.floor(target) + 1) && has(reach + 1)) reach++;
  // `reach` is the last loaded state of an unbroken run starting at the cursor; the cursor may sit at most on it.
  if (target <= reach) return { cursor: target, ended: target >= terminal, waiting: false };
  return { cursor: reach, ended: false, waiting: reach < terminal };
}

// ---- same-tick comparison ------------------------------------------------------------------------------------------------------------
export interface CompareRow {
  /** exact: both scores belong to the shown tick. sampled: the server's nearest earlier curve point (every 10 ticks). none: not delivered yet. */
  readonly mode: "exact" | "sampled" | "none";
  readonly atTick: number | null;
  readonly original: number | null;
  readonly future: number | null;
  /** Always child minus original at `atTick`, as the server computed it or as two same-tick scores. */
  readonly delta: number | null;
}
/**
 * The original-versus-future numbers for the shown tick, from real data only:
 *  1. through the fork tick both worlds are ONE state (the server says AfterFork=false), so the future's own score is the original's too;
 *  2. after it, an exact original state at this tick (the optional source trace), or the curve point exactly at this tick;
 *  3. otherwise the nearest earlier curve point, labelled as sampled with its own tick.
 */
export function compareAt(args: {
  tick: number; forkTick: number; future: HistoryFrames; source: HistoryFrames | null; points: readonly ComparisonPointWire[] | null;
}): CompareRow {
  const { tick, forkTick } = args;
  const own = args.future.get(tick)?.totalScore ?? null;
  if (tick <= forkTick && own !== null) return { mode: "exact", atTick: tick, original: own, future: own, delta: 0 };
  const exactSource = args.source?.get(tick)?.totalScore ?? null;
  if (own !== null && exactSource !== null) return { mode: "exact", atTick: tick, original: exactSource, future: own, delta: own - exactSource };
  const pts = args.points;
  if (pts && pts.length > 0) {
    let best: ComparisonPointWire | null = null;
    for (const p of pts) { if (p.tick <= tick) best = p; else break; }
    if (best) {
      return best.tick === tick
        ? { mode: "exact", atTick: tick, original: best.sourceScore, future: best.childScore, delta: best.delta }
        : { mode: "sampled", atTick: best.tick, original: best.sourceScore, future: best.childScore, delta: best.delta };
    }
  }
  return { mode: "none", atTick: null, original: null, future: own, delta: null };
}

export interface Ribbon { readonly points: readonly { readonly x: number; readonly d: number }[]; readonly maxAbs: number }
/**
 * The score gap after the split, from the server's curve points only: x is the tick as a 0..1 fraction of the run, d is child minus original.
 * It starts at the fork tick with a gap of exactly 0 (one shared state), then follows each real sample with straight segments. No smoothing.
 */
export function ribbonOf(points: readonly ComparisonPointWire[] | null, forkTick: number, terminal: number): Ribbon {
  if (!points) return { points: [], maxAbs: 0 };
  const out: { x: number; d: number }[] = [{ x: fractionOf(forkTick, terminal), d: 0 }];
  let maxAbs = 0;
  for (const p of points) {
    if (p.tick <= forkTick) continue;
    out.push({ x: fractionOf(p.tick, terminal), d: p.delta });
    maxAbs = Math.max(maxAbs, Math.abs(p.delta));
  }
  return { points: out.length > 1 ? out : [], maxAbs };
}

// ---- wording -------------------------------------------------------------------------------------------------------------------------
export function signed(n: number): string { return n > 0 ? `+${n}` : n < 0 ? `−${Math.abs(n)}` : "±0"; }
export function futureLabel(kind: ChildKind, forkTick: number): string { return kind === "Fork" ? `Fork at T${forkTick}` : "Ghost challenge from T0"; }
/** How the source's pickups of a frame are described: the count of lit candles is exact; positions are not delivered by saved traces. */
export function litCandles(ids: readonly string[]): string { return ids.length === 1 ? "1 candle lit" : `${ids.length} candles lit`; }
