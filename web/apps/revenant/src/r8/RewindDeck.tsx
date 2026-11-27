import { useCallback, useEffect, useId, useRef, useState } from "react";
import { Icon } from "./shared.tsx";

/**
 * Revenant's signature Rewind sequence, demo only. Scripted visual demonstration: no model runs here.
 * The thinking grid and shimmer label follow the supplied loading-state reference (wavefront cells, shimmering label,
 * mono timer); the colours, type and surfaces are Revenant's own.
 */
export const FRAUD_MS = 5100;
export const VELOCITY_MS = 6700;
export const TOTAL_MS = FRAUD_MS + VELOCITY_MS;

export type Stage = "original" | "analysing" | "hold" | "dissolve" | "void" | "rescale" | "emerge" | "settle" | "done";
const TIMED: Partial<Record<Stage, number>> = { hold: 900, void: 380, rescale: 520, settle: 800 };
const reducedMotion = () => typeof matchMedia === "function" && matchMedia("(prefers-reduced-motion: reduce)").matches;

/** Owns the stage machine. The particle layer reports dissolve/emerge completion through `advance`. */
export function useRewindSequence(alternate: boolean) {
  const [stage, setStage] = useState<Stage>("original");
  const [run, setRun] = useState(0);
  const advance = useCallback((from: Stage) => setStage(s => {
    if (s !== from) return s;
    if (reducedMotion() && from === "analysing") return "done";
    switch (from) {
      case "analysing": return "hold";
      case "hold": return "dissolve";
      case "dissolve": return "void";
      case "void": return alternate ? "rescale" : "emerge";
      case "rescale": return "emerge";
      case "emerge": return "settle";
      case "settle": return "done";
      default: return s;
    }
  }), [alternate]);
  useEffect(() => {
    const ms = TIMED[stage];
    if (ms === undefined) return;
    const timer = setTimeout(() => advance(stage), ms);
    return () => clearTimeout(timer);
  }, [stage, advance]);
  const start = useCallback(() => { setRun(r => r + 1); setStage("analysing"); }, []);
  const reset = useCallback(() => { setRun(r => r + 1); setStage("original"); }, []);
  return { stage, run, advance, start, reset };
}

const chevron = Array.from({ length: 9 }, (_, i) => (i % 3 + Math.abs(Math.floor(i / 3) - 1)) * 90);
const ORBIT_ORDER = [0, 1, 2, 5, 8, 7, 6, 3];
const orbit = Array.from({ length: 9 }, (_, i) => { const k = ORBIT_ORDER.indexOf(i); return k === -1 ? null : k * 110; });
const PATTERNS = { Dots: { delays: chevron, dur: 650, round: true }, Orbit: { delays: orbit, dur: 950, round: false } } as const;

function ThinkingGrid({ variant }: { variant: keyof typeof PATTERNS }) {
  const { delays, dur, round } = PATTERNS[variant];
  return <span aria-hidden="true" className="r8-think-grid">{delays.map((d, i) =>
    <i key={i} className={round ? "is-round" : ""} style={{ opacity: d === null ? 0.1 : 0.18, animation: d === null ? "none" : `r8-pixel-on ${dur}ms ease-in-out ${d}ms infinite` }} />)}</span>;
}

type Mode = "ready" | "running" | "done";
function Model({ index, name, variant, mode, remaining, progress }: { index: number; name: string; variant: keyof typeof PATTERNS; mode: Mode; remaining: number; progress: number }) {
  return <div className={"r8-model is-" + mode} style={{ "--p": progress } as React.CSSProperties} data-testid={`r8-model-${index}`}>
    <span className="r8-model-mark" aria-hidden="true">
      {mode === "running" ? <ThinkingGrid variant={variant} />
        : mode === "done" ? <svg viewBox="0 0 24 24" className="r8-check"><circle className="r8-check-disc" cx="12" cy="12" r="10.5" />
          <path className="r8-check-tick" d="M7.4 12.6l3.2 3.2 6-6.7" pathLength="1" /></svg>
          : <i className="r8-ready-ring" />}
    </span>
    <span className="r8-model-body">
      {mode === "running"
        ? <span className="r8-model-line" role="status"><span className="r8-think-label">Running {name === "Fraud Classifier" ? "Fraud Classifier" : "Velocity Anomaly Detection"}</span>
          <span className="r8-think-time">{(remaining / 1000).toFixed(1)}s</span></span>
        : <span className="r8-model-line"><span className="r8-model-name">{name}</span>{" "}<span className="r8-model-status">{mode === "done" ? "Complete" : "Ready"}</span></span>}
    </span>
    <span className="r8-model-bar" aria-hidden="true"><i /></span>
  </div>;
}

/** A controlled data current: a trace that draws toward its target, a travelling head, then quiet packets while the target works. */
function Link({ on, live, future }: { on: boolean; live: boolean; future?: boolean }) {
  const id = useId().replace(/:/g, "");
  return <span className={"r8-link" + (on ? " is-on" : "") + (live ? " is-live" : "") + (future ? " is-future" : "")} aria-hidden="true">
    <svg viewBox="0 0 100 20" preserveAspectRatio="none">
      <defs><linearGradient id={id} gradientUnits="userSpaceOnUse" x1="0" x2="100" y1="0" y2="0">
        <stop offset="0" stopColor="var(--original)" /><stop offset="1" stopColor={future ? "var(--coral)" : "var(--navy)"} /></linearGradient></defs>
      <line className="r8-link-track" x1="0" x2="100" y1="10" y2="10" />
      <path className="r8-link-charge" pathLength="1" stroke={`url(#${id})`}
        d="M0 10 L8 9.5 L16 10.5 L24 9.6 L32 10.4 L40 9.55 L48 10.45 L56 9.6 L64 10.4 L72 9.55 L80 10.45 L88 9.7 L100 10" />
      <line className="r8-link-packets" x1="0" x2="100" y1="10" y2="10" />
    </svg>
    <b className="r8-link-head" />
  </span>;
}

export const POLICIES = [["policy-v7", "Policy v7"], ["policy-v8", "Policy v8 — default"]] as const;

export function RewindDeck({ stage, run, policy, onPolicy, onStart, onReset, onComplete }: {
  stage: Stage; run: number; policy: string; onPolicy: (p: string) => void; onStart: () => void; onReset: () => void; onComplete: () => void;
}) {
  const [ms, setMs] = useState(0);
  const complete = useRef(onComplete);
  complete.current = onComplete;
  useEffect(() => {
    if (stage === "original") { setMs(0); return; }
    if (stage !== "analysing") { setMs(TOTAL_MS); return; }
    setMs(0);
    const t0 = performance.now();
    let raf = 0;
    const tick = (now: number) => {
      const elapsed = Math.min(TOTAL_MS, now - t0);
      setMs(elapsed);
      if (elapsed >= TOTAL_MS) { complete.current(); return; }
      raf = requestAnimationFrame(tick);
    };
    raf = requestAnimationFrame(tick);
    return () => cancelAnimationFrame(raf);
  }, [stage, run]);

  const idle = stage === "original";
  const running = stage === "analysing";
  const fraud: Mode = idle ? "ready" : ms >= FRAUD_MS ? "done" : "running";
  const velocity: Mode = idle || ms < FRAUD_MS ? "ready" : ms >= TOTAL_MS ? "done" : "running";
  const pct = Math.min(1, ms / TOTAL_MS);
  const finished = !idle && ms >= TOTAL_MS;
  const C = 2 * Math.PI * 19;

  return <div className={"r8-deck" + (running ? " is-running" : "") + (finished ? " is-finished" : "")} role="group" aria-label="Rewind sequence">
    <label className="r8-deck-policy"><span className="r8-sr">Policy</span>
      <select aria-label="Policy" value={policy} disabled={running} onChange={e => onPolicy(e.target.value)}>
        {POLICIES.map(([value, label]) => <option key={value} value={value}>{label}</option>)}</select></label>

    <button type="button" className="r8-test" aria-busy={running} aria-disabled={running} onClick={() => { if (!running) onStart(); }} data-testid="r8-test-policy">
      <span className="r8-test-ring" aria-hidden="true">
        <svg viewBox="0 0 44 44"><circle className="r8-ring-track" cx="22" cy="22" r="19" />
          <circle className="r8-ring-arc" cx="22" cy="22" r="19" strokeDasharray={C} strokeDashoffset={C * (1 - pct)} /></svg>
        <span className="r8-ring-center">{finished ? <svg viewBox="0 0 24 24" className="r8-ring-check"><path d="M6.8 12.6l3.5 3.5 7-7.6" pathLength="1" /></svg>
          : running ? <b>{Math.floor(pct * 100)}<small>%</small></b> : <svg viewBox="0 0 24 24" className="r8-ring-play"><path d="M9 6.5v11l9-5.5z" /></svg>}</span>
      </span>
      <span className="r8-test-text"><strong>TEST NEW POLICY</strong><small><i />All Models Online</small></span>
    </button>

    <div className="r8-flow" key={run}>
      <Link on={!idle} live={fraud === "running"} />
      <Model index={1} name="Fraud Classifier" variant="Dots" mode={fraud} remaining={Math.max(0, FRAUD_MS - ms)} progress={Math.min(1, ms / FRAUD_MS)} />
      <Link on={!idle && ms >= FRAUD_MS} live={velocity === "running"} />
      <Model index={2} name="Velocity Anomaly Detector" variant="Orbit" mode={velocity} remaining={Math.max(0, TOTAL_MS - ms)} progress={Math.max(0, Math.min(1, (ms - FRAUD_MS) / VELOCITY_MS))} />
      <Link on={finished} live={false} future />
      <span className={"r8-future" + (finished ? " is-lit" : "")}><i /><span>New Future</span></span>
    </div>

    <button type="button" className="r8-reset" disabled={idle} onClick={onReset} aria-label="Reset to the original history" title="Reset to the original history">
      <Icon name="refresh" /></button>
  </div>;
}
