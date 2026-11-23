import { useId, type ReactNode } from "react";
import { bufferDisclosure, bufferLabel, bufferPolicy, type BufferReference } from "./bufferReference.ts";
import { compactMoney, hourMinute, money } from "./format.ts";
import { useSize } from "./shared.tsx";

export type Tone = "source" | "alternative" | "posted" | "reserved" | "projection";
export type Point = { at: number; minor: string };
export type Band = { label: string; lower: Point[]; upper: Point[]; opacity: number };
export type Series = { name: string; tone: Tone; values: Point[]; area?: boolean; dash?: boolean; hollow?: boolean };
export type LaneMarker = { at: number; kind: string; label: string };
export type Callout = { series: string; label: string };

/** Where the illustrative reference sits relative to the data-only y-domain of these series. */
export function placementOf(series: Series[], reference: BufferReference): "below" | "above" | "in" | undefined {
  const samples = series.flatMap(s => s.values).map(v => BigInt(v.minor));
  if (reference.status !== "Available" || samples.length === 0) return undefined;
  const r = BigInt(reference.minor), lo = samples.reduce((a, b) => (b < a ? b : a)), hi = samples.reduce((a, b) => (b > a ? b : a));
  return r < lo ? "below" : r > hi ? "above" : "in";
}

/** Exact linear interpolation of a series at any instant inside its range (null outside). Presentation only. */
export function interpolateAt(points: Point[], at: number): bigint | null {
  const first = points[0], last = points.at(-1);
  if (!first || !last || at < first.at || at > last.at) return null;
  for (let i = 1; i < points.length; i++) {
    const a = points[i - 1]!, b = points[i]!;
    if (at > b.at) continue;
    if (b.at === a.at) return BigInt(b.minor);
    return BigInt(a.minor) + ((BigInt(b.minor) - BigInt(a.minor)) * BigInt(Math.round(at - a.at))) / BigInt(b.at - a.at);
  }
  return BigInt(last.minor);
}
export type Mark = { at: number; label: string };

const MIN_SPAN_MS = 60_000;
const steps = [5, 10, 15, 30, 60, 120, 240].map(m => m * 60_000);

/**
 * The one hand-written SVG time chart behind Tower, Liquidity and Rewind. It measures its box and draws at 1:1 pixels,
 * so glyphs and strokes are never stretched. The y-domain comes from the plotted series only: the illustrative buffer
 * reference never rescales real data, it is drawn only when it falls inside the observed range.
 */
export function TimeChart({ label, series, reference, showNote = true, fork, now, markers, wedge, callouts, yLabel, compact, children,
  bands = [], referenceLabel = "ILLUSTRATIVE BUFFER", axisMoney = compactMoney, exactMoney = money, reveal = null, preview = null, ready = null, pulse = 0 }: {
  label: string; series: Series[]; reference?: BufferReference; showNote?: boolean; fork?: { at: number; label: string } | null;
  now?: { at: number; label: string } | null; markers?: LaneMarker[]; wedge?: { a: string; b: string; from: number } | null;
  callouts?: Callout[]; yLabel?: string; compact?: boolean; children?: ReactNode;
  bands?: Band[]; referenceLabel?: string; axisMoney?: (v: bigint) => string; exactMoney?: (v: string) => string;
  /** 0..1 left-to-right reveal of the curves (graph-only playback); null draws everything. */
  reveal?: number | null;
  /** Live scrub position, and a confirmed fork that has not been tested yet. Both are overlays: the curves never change. */
  preview?: Mark | null; ready?: Mark | null; pulse?: number;
}) {
  const [ref, { width, height }] = useSize<HTMLDivElement>();
  const uid = useId().replace(/:/g, "");
  const samples = [...series.flatMap(s => s.values), ...bands.flatMap(b => [...b.lower, ...b.upper])];
  const empty = samples.length === 0;
  const lo = empty ? 0n : samples.reduce((v, x) => (BigInt(x.minor) < v ? BigInt(x.minor) : v), BigInt(samples[0]!.minor));
  const hi = empty ? 0n : samples.reduce((v, x) => (BigInt(x.minor) > v ? BigInt(x.minor) : v), lo);
  const times = samples.map(x => x.at).concat(fork ? [fork.at] : [], now ? [now.at] : []);
  let t0 = empty ? 0 : Math.min(...times), t1 = empty ? 1 : Math.max(...times);
  if (t1 - t0 < MIN_SPAN_MS) { const mid = (t0 + t1) / 2; t0 = mid - MIN_SPAN_MS / 2; t1 = mid + MIN_SPAN_MS / 2; }
  const lane = markers && markers.length > 0 ? (compact ? 0 : 30) : 0;
  const m = { left: compact ? 52 : 64, right: callouts?.length ? 172 : 16, top: compact ? 14 : 30, bottom: (compact ? 22 : 30) + lane };
  const pw = Math.max(1, width - m.left - m.right), ph = Math.max(1, height - m.top - m.bottom);
  const x = (at: number) => m.left + ((at - t0) / (t1 - t0)) * pw;
  const fraction = (minor: bigint) => (hi === lo ? 0.5 : Number(((minor - lo) * 10000n) / (hi - lo)) / 10000);
  const y = (minor: bigint) => m.top + (1 - fraction(minor)) * ph;
  const pt = (p: Point) => `${x(p.at).toFixed(1)},${y(BigInt(p.minor)).toFixed(1)}`;
  const referenceMinor = reference?.status === "Available" ? BigInt(reference.minor) : null;
  const placement = referenceMinor === null || empty ? undefined : referenceMinor < lo ? "below" : referenceMinor > hi ? "above" : "in";
  const step = steps.find(s => (t1 - t0) / s <= (compact ? 5 : 7)) ?? steps.at(-1)!;
  const xticks: number[] = [];
  for (let t = Math.ceil(t0 / step) * step; t <= t1; t += step) xticks.push(t);
  const yticks = hi === lo ? [lo] : [0n, 1n, 2n, 3n, 4n].map(k => lo + ((hi - lo) * k) / 4n);
  const byName = (name: string) => series.find(s => s.name === name);
  const wedgePath = (() => {
    if (!wedge) return null;
    const a = byName(wedge.a)?.values.filter(p => p.at >= wedge.from) ?? [], b = byName(wedge.b)?.values.filter(p => p.at >= wedge.from) ?? [];
    if (a.length < 1 || b.length < 1) return null;
    return `M${a.map(pt).join(" L")} L${[...b].reverse().map(pt).join(" L")} Z`;
  })();
  const markY = (at: number) => { const v = interpolateAt(series[0]?.values ?? [], at); return v === null ? null : y(v); };
  const forkY = fork ? markY(fork.at) : null;
  const revealAt = reveal === null ? null : Math.min(1, Math.max(0, reveal));
  const revealX = revealAt === null ? null : m.left + revealAt * pw;
  const clip = revealX === null ? undefined : `url(#${uid}-reveal)`;
  const near = (a: number, b?: number) => b !== undefined && Math.abs(a - b) < 30_000;
  const overlay = (g: Mark | null, kind: "preview" | "ready") => {
    if (!g || (kind === "preview" && (near(g.at, fork?.at) || near(g.at, ready?.at)))) return null;
    const cx = x(g.at), cy = markY(g.at), w = kind === "ready" ? 100 : 54;
    const px = Math.min(Math.max(-w / 2, m.left - cx), m.left + pw - cx - w);
    const py = cy !== null && cy > m.top + ph / 2 ? m.top + 8 : m.top + ph - 30;
    return <g key={kind} className={`r8-mark is-${kind}`} style={{ transform: `translateX(${cx.toFixed(2)}px)` }}>
      <line x1="0" x2="0" y1={m.top - 4} y2={m.top + ph + lane} />
      {cy !== null && <rect className="r8-mark-diamond" x="-5.5" y="-5.5" width="11" height="11" style={{ transform: `translateY(${cy.toFixed(2)}px) rotate(45deg)` }} />}
      <g transform={`translate(${px.toFixed(1)} ${py})`}><rect width={w} height="20" rx="10" /><text x={w / 2} y="14" textAnchor="middle">{g.label}</text></g>
    </g>;
  };
  const forkBox = fork ? Math.min(Math.max(x(fork.at) - 66, m.left), m.left + pw - 132) : 0;
  const rightAlignYLabel = (fork && forkBox < m.left + 260) || (bands.length > 0 && now && x(now.at) < m.left + 160);
  const placedCallouts = (() => {
    const items = (callouts ?? []).flatMap(c => {
      const s = byName(c.series); const last = s?.values.at(-1);
      return s && last ? [{ ...c, tone: s.tone, value: last.minor, y: y(BigInt(last.minor)) }] : [];
    }).sort((a, b) => a.y - b.y);
    const h = 54;
    items.forEach((it, i) => {
      it.y = Math.min(Math.max(it.y - h / 2, m.top), m.top + ph - h);
      if (i > 0 && it.y < items[i - 1]!.y + h + 8) it.y = items[i - 1]!.y + h + 8;
    });
    return items;
  })();

  return <figure className={"r8-chart" + (compact ? " is-compact" : "")}>
    <div className="r8-chart-box" ref={ref}>
      <svg width={width} height={height} viewBox={`0 0 ${Math.max(width, 1)} ${Math.max(height, 1)}`} role="img" aria-label={label}>
        <defs>
          {series.map(s => <linearGradient key={s.name} id={`${uid}-g-${s.tone}`} x1="0" x2="0" y1="0" y2="1">
            <stop offset="0" className={`r8-stop-${s.tone}`} stopOpacity={s.tone === "alternative" ? 0.28 : 0.22} /><stop offset="1" className={`r8-stop-${s.tone}`} stopOpacity="0.02" /></linearGradient>)}
          {revealX !== null && <clipPath id={`${uid}-reveal`}><rect x={m.left - 6} y="0" width={Math.max(0, revealX - m.left + 6)} height={Math.max(height, 1)} /></clipPath>}
          <pattern id={`${uid}-hatch`} width="7" height="7" patternUnits="userSpaceOnUse" patternTransform="rotate(45)">
            <rect width="7" height="7" className="r8-hatch-bg" /><line x1="0" y1="0" x2="0" y2="7" className="r8-hatch-line" /></pattern>
        </defs>
        {width > 0 && <>
          <rect className="r8-plot" x={m.left} y={m.top} width={pw} height={ph} />
          {yticks.map((v, i) => <g key={i}><line className="r8-grid" x1={m.left} x2={m.left + pw} y1={y(v)} y2={y(v)} />
            <text className="r8-axis" x={m.left - 9} y={y(v) + 4} textAnchor="end">{axisMoney(v)}</text></g>)}
          {xticks.map(t => <g key={t}><line className="r8-grid r8-grid-v" x1={x(t)} x2={x(t)} y1={m.top} y2={m.top + ph} />
            <text className="r8-axis" x={x(t)} y={m.top + ph + lane + (compact ? 16 : 21)} textAnchor="middle">{hourMinute(t)}</text></g>)}
          <line className="r8-baseline" x1={m.left} x2={m.left + pw} y1={m.top + ph} y2={m.top + ph} />
          {yLabel && !compact && <text className="r8-axis r8-ylabel" x={rightAlignYLabel ? m.left + pw : m.left} textAnchor={rightAlignYLabel ? "end" : "start"} y={m.top - 12}>{yLabel}</text>}
          <g clipPath={clip}>
          {series.filter(s => s.area && s.values.length > 1).map(s => <path key={"a" + s.name} className="r8-area" fill={`url(#${uid}-g-${s.tone})`}
            d={`M${s.values.map(pt).join(" L")} L${x(s.values.at(-1)!.at).toFixed(1)},${m.top + ph} L${x(s.values[0]!.at).toFixed(1)},${m.top + ph} Z`} />)}
          {wedgePath && <path className="r8-wedge" d={wedgePath} fill={`url(#${uid}-hatch)`} />}
          </g>
          {bands.map(b => <path key={b.label} fill="var(--original)" fillOpacity={b.opacity}
            d={`M${b.upper.map(pt).join(" L")} L${[...b.lower].reverse().map(pt).join(" L")} Z`}><title>{b.label}</title></path>)}
          {placement === "in" && referenceMinor !== null && <g data-testid="r8-buffer-line" aria-hidden="true" data-scale-bp={Math.round(fraction(referenceMinor) * 10000)}>
            <line className="r8-buffer" x1={m.left} x2={m.left + pw} y1={y(referenceMinor)} y2={y(referenceMinor)} />
            <text className="r8-buffer-label" x={m.left + 8} y={y(referenceMinor) - 7}>{referenceLabel} · {axisMoney(referenceMinor)}</text>
          </g>}
          <g clipPath={clip}>
          {series.map(s => s.values.length === 1
            ? <circle key={s.name} className={`r8-line r8-tone-${s.tone}`} cx={x(s.values[0]!.at)} cy={y(BigInt(s.values[0]!.minor))} r="3.5" />
            : <polyline key={s.name} className={`r8-line r8-tone-${s.tone}${s.dash ? " is-dashed" : ""}`} points={s.values.map(pt).join(" ")} />)}
          </g>
          {revealX !== null && revealAt !== null && <g className="r8-scan" aria-hidden="true">
            <line x1={revealX} x2={revealX} y1={m.top} y2={m.top + ph} />
            {series.map(s => { const v = s.values.length > 1 ? interpolateAt(s.values, t0 + revealAt * (t1 - t0)) : null;
              return v === null ? null : <g key={s.name} className={`r8-head is-${s.tone}`} transform={`translate(${revealX.toFixed(1)} ${y(v).toFixed(1)})`}><circle className="r8-head-halo" r="10" /><circle r="4.5" /></g>; })}
          </g>}
          {series.filter(s => s.hollow).flatMap(s => s.values.map(p => <circle key={s.name + p.at} className={`r8-hollow r8-tone-${s.tone}`} cx={x(p.at)} cy={y(BigInt(p.minor))} r="4.5">
            {bands.length > 0 && <title>{[hourMinute(p.at) + " UTC · " + s.name + " " + exactMoney(p.minor), ...bands.flatMap(b => {
              const lower = b.lower.find(v => v.at === p.at), upper = b.upper.find(v => v.at === p.at);
              return lower && upper ? [`${b.label} ${exactMoney(lower.minor)} – ${exactMoney(upper.minor)}`] : [];
            })].join("\n")}</title>}
          </circle>))}
          {now && <g className="r8-now"><line x1={x(now.at)} x2={x(now.at)} y1={m.top - (compact ? 0 : 8)} y2={m.top + ph + lane} />
            {!compact && <><rect x={x(now.at) - 26} y={m.top - 24} width="52" height="20" rx="4" /><text x={x(now.at)} y={m.top - 10} textAnchor="middle">{now.label}</text></>}</g>}
          {lane > 0 && <g className="r8-lane"><rect x={m.left} y={m.top + ph + 4} width={pw} height={lane - 6} rx="3" />
            {markers!.map((mk, i) => {
              const cx = x(mk.at), cy = m.top + ph + 4 + (lane - 6) / 2;
              if (mk.kind === "ObservationSeal") return <line key={i} className="r8-lane-seal" x1={cx} x2={cx} y1={cy - 4} y2={cy + 4}><title>{mk.label}</title></line>;
              if (mk.kind === "Screening") return <path key={i} className="r8-lane-screen" d={`M${cx} ${cy - 6} l6 10 h-12 Z`}><title>{mk.label}</title></path>;
              return <circle key={i} className="r8-lane-dot" cx={cx} cy={cy} r="4"><title>{mk.label}</title></circle>;
            })}</g>}
          {fork && <g className="r8-fork-slide" style={{ transform: `translateX(${x(fork.at).toFixed(2)}px)` }}>
            <g className="r8-fork"><line x1="0" x2="0" y1={m.top - 4} y2={m.top + ph + lane} />
              <rect x={forkBox - x(fork.at)} y={m.top - 26} width="132" height="22" rx="4" /><text x={forkBox - x(fork.at) + 66} y={m.top - 11} textAnchor="middle">{fork.label}</text>
              {forkY !== null && <rect className="r8-fork-diamond" x="-6" y="-6" width="12" height="12" style={{ transform: `translateY(${forkY.toFixed(2)}px) rotate(45deg)` }} />}</g>
            {pulse > 0 && <circle key={pulse} className="r8-fork-ring" cx="0" cy={forkY ?? m.top} r="9" />}
          </g>}
          {overlay(ready, "ready")}{overlay(preview, "preview")}
          {series.map(s => { const last = s.values.at(-1); return last && (s.tone === "source" || s.tone === "alternative")
            ? <circle key={"e" + s.name} className={`r8-endpoint r8-tone-${s.tone}`} cx={x(last.at)} cy={y(BigInt(last.minor))} r="4.5"
              style={revealX !== null && revealX < x(last.at) - 0.5 ? { opacity: 0 } : undefined} /> : null; })}
          {placedCallouts.map(c => <g key={c.series} className={`r8-callout r8-tone-${c.tone}`} transform={`translate(${m.left + pw + 14} ${c.y})`}>
            <rect width={m.right - 22} height="54" rx="5" /><text className="r8-callout-k" x="12" y="19">{c.label}</text>
            <text className="r8-callout-v" x="12" y="41">{exactMoney(c.value)}</text></g>)}
        </>}
      </svg>
      {empty && <p className="r8-chart-empty">No completed observations for this selection yet.</p>}
      {children}
    </div>
    {reference && showNote && <BufferNote reference={reference} placement={placement} />}
  </figure>;
}

export function BufferNote({ reference, placement }: { reference: BufferReference; placement?: "below" | "above" | "in" }) {
  return <div className="r8-buffer-note" role="status" data-reference-status={reference.status}>
    <strong>{bufferLabel}</strong>
    {reference.status === "Available"
      ? <span>{money(reference.minor)}{placement === "above" || placement === "below" ? ` · ${placement} visible chart range` : ""}</span>
      : <span>Reference unavailable: a verified positive opening available balance is required.</span>}
    <small>{bufferDisclosure} <code>{bufferPolicy}</code></small>
  </div>;
}
