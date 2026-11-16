import { useId, type ReactNode } from "react";
import { bufferDisclosure, bufferLabel, bufferPolicy, type BufferReference } from "./bufferReference.ts";
import { compactMoney, hourMinute, money } from "./format.ts";
import { useSize } from "./shared.tsx";

export type Tone = "source" | "alternative" | "posted" | "reserved" | "projection";
export type Point = { at: number; minor: string };
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

const MIN_SPAN_MS = 60_000;
const steps = [5, 10, 15, 30, 60, 120, 240].map(m => m * 60_000);

/**
 * The one hand-written SVG time chart behind Tower, Liquidity and Rewind. It measures its box and draws at 1:1 pixels,
 * so glyphs and strokes are never stretched. The y-domain comes from the plotted series only: the illustrative buffer
 * reference never rescales real data, it is drawn only when it falls inside the observed range.
 */
export function TimeChart({ label, series, reference, showNote = true, fork, now, markers, wedge, callouts, yLabel, compact, children }: {
  label: string; series: Series[]; reference?: BufferReference; showNote?: boolean; fork?: { at: number; label: string } | null;
  now?: { at: number; label: string } | null; markers?: LaneMarker[]; wedge?: { a: string; b: string; from: number } | null;
  callouts?: Callout[]; yLabel?: string; compact?: boolean; children?: ReactNode;
}) {
  const [ref, { width, height }] = useSize<HTMLDivElement>();
  const uid = useId().replace(/:/g, "");
  const samples = series.flatMap(s => s.values);
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
  const forkY = (() => {
    if (!fork) return null;
    const s = series[0]?.values ?? [];
    const near = s.reduce<Point | null>((best, p) => (!best || Math.abs(p.at - fork.at) < Math.abs(best.at - fork.at) ? p : best), null);
    return near && Math.abs(near.at - fork.at) <= 300_000 ? y(BigInt(near.minor)) : null;
  })();
  const forkBox = fork ? Math.min(Math.max(x(fork.at) - 66, m.left), m.left + pw - 132) : 0;
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
          <pattern id={`${uid}-hatch`} width="7" height="7" patternUnits="userSpaceOnUse" patternTransform="rotate(45)">
            <rect width="7" height="7" className="r8-hatch-bg" /><line x1="0" y1="0" x2="0" y2="7" className="r8-hatch-line" /></pattern>
        </defs>
        {width > 0 && <>
          <rect className="r8-plot" x={m.left} y={m.top} width={pw} height={ph} />
          {yticks.map((v, i) => <g key={i}><line className="r8-grid" x1={m.left} x2={m.left + pw} y1={y(v)} y2={y(v)} />
            <text className="r8-axis" x={m.left - 9} y={y(v) + 4} textAnchor="end">{compactMoney(v)}</text></g>)}
          {xticks.map(t => <g key={t}><line className="r8-grid r8-grid-v" x1={x(t)} x2={x(t)} y1={m.top} y2={m.top + ph} />
            <text className="r8-axis" x={x(t)} y={m.top + ph + lane + (compact ? 16 : 21)} textAnchor="middle">{hourMinute(t)}</text></g>)}
          <line className="r8-baseline" x1={m.left} x2={m.left + pw} y1={m.top + ph} y2={m.top + ph} />
          {yLabel && !compact && <text className="r8-axis r8-ylabel" x={fork && forkBox < m.left + 260 ? m.left + pw : m.left} textAnchor={fork && forkBox < m.left + 260 ? "end" : "start"} y={m.top - 12}>{yLabel}</text>}
          {series.filter(s => s.area && s.values.length > 1).map(s => <path key={"a" + s.name} fill={`url(#${uid}-g-${s.tone})`}
            d={`M${s.values.map(pt).join(" L")} L${x(s.values.at(-1)!.at).toFixed(1)},${m.top + ph} L${x(s.values[0]!.at).toFixed(1)},${m.top + ph} Z`} />)}
          {wedgePath && <path className="r8-wedge" d={wedgePath} fill={`url(#${uid}-hatch)`} />}
          {placement === "in" && referenceMinor !== null && <g data-testid="r8-buffer-line" aria-hidden="true" data-scale-bp={Math.round(fraction(referenceMinor) * 10000)}>
            <line className="r8-buffer" x1={m.left} x2={m.left + pw} y1={y(referenceMinor)} y2={y(referenceMinor)} />
            <text className="r8-buffer-label" x={m.left + 8} y={y(referenceMinor) - 7}>ILLUSTRATIVE BUFFER · {compactMoney(referenceMinor)}</text>
          </g>}
          {series.map(s => s.values.length === 1
            ? <circle key={s.name} className={`r8-line r8-tone-${s.tone}`} cx={x(s.values[0]!.at)} cy={y(BigInt(s.values[0]!.minor))} r="3.5" />
            : <polyline key={s.name} className={`r8-line r8-tone-${s.tone}${s.dash ? " is-dashed" : ""}`} points={s.values.map(pt).join(" ")} />)}
          {series.filter(s => s.hollow).flatMap(s => s.values.map(p => <circle key={s.name + p.at} className={`r8-hollow r8-tone-${s.tone}`} cx={x(p.at)} cy={y(BigInt(p.minor))} r="4.5" />))}
          {now && <g className="r8-now"><line x1={x(now.at)} x2={x(now.at)} y1={m.top - (compact ? 0 : 8)} y2={m.top + ph + lane} />
            {!compact && <><rect x={x(now.at) - 26} y={m.top - 24} width="52" height="20" rx="4" /><text x={x(now.at)} y={m.top - 10} textAnchor="middle">{now.label}</text></>}</g>}
          {lane > 0 && <g className="r8-lane"><rect x={m.left} y={m.top + ph + 4} width={pw} height={lane - 6} rx="3" />
            {markers!.map((mk, i) => {
              const cx = x(mk.at), cy = m.top + ph + 4 + (lane - 6) / 2;
              if (mk.kind === "ObservationSeal") return <line key={i} className="r8-lane-seal" x1={cx} x2={cx} y1={cy - 4} y2={cy + 4}><title>{mk.label}</title></line>;
              if (mk.kind === "Screening") return <path key={i} className="r8-lane-screen" d={`M${cx} ${cy - 6} l6 10 h-12 Z`}><title>{mk.label}</title></path>;
              return <circle key={i} className="r8-lane-dot" cx={cx} cy={cy} r="4"><title>{mk.label}</title></circle>;
            })}</g>}
          {fork && <g className="r8-fork"><line x1={x(fork.at)} x2={x(fork.at)} y1={m.top - 4} y2={m.top + ph + lane} />
            <rect x={forkBox} y={m.top - 26} width="132" height="22" rx="4" /><text x={forkBox + 66} y={m.top - 11} textAnchor="middle">{fork.label}</text>
            {forkY !== null && <rect className="r8-fork-diamond" x={x(fork.at) - 6} y={forkY - 6} width="12" height="12" transform={`rotate(45 ${x(fork.at)} ${forkY})`} />}</g>}
          {series.map(s => { const last = s.values.at(-1); return last && (s.tone === "source" || s.tone === "alternative")
            ? <circle key={"e" + s.name} className={`r8-endpoint r8-tone-${s.tone}`} cx={x(last.at)} cy={y(BigInt(last.minor))} r="4.5" /> : null; })}
          {placedCallouts.map(c => <g key={c.series} className={`r8-callout r8-tone-${c.tone}`} transform={`translate(${m.left + pw + 14} ${c.y})`}>
            <rect width={m.right - 22} height="54" rx="5" /><text className="r8-callout-k" x="12" y="19">{c.label}</text>
            <text className="r8-callout-v" x="12" y="41">{money(c.value)}</text></g>)}
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
