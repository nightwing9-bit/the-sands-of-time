import { useEffect, useRef, useState, type ReactNode } from "react";
import { demoMillions, BANKS } from "./format.ts";
import type { Payment, Snapshot } from "./shared.tsx";

/**
 * Demo Mode presentation of the six-bank network: an original, hand-drawn SVG city plan with six bank buildings,
 * five calm teal routes and one coral loop MRD → OST → CRV → MRD. Positions are normalised (0–1) so the whole
 * illustration scales to the card; building sizes are in pixels so labels stay legible.
 */
const spot: Record<string, [number, number]> = {
  "BANK-A": [0.12, 0.53], "BANK-B": [0.29, 0.17], "BANK-C": [0.70, 0.17],
  "BANK-D": [0.47, 0.41], "BANK-E": [0.89, 0.52], "BANK-F": [0.36, 0.77],
};
/** Teal routes: source → destination, with a bend (fraction of length, signed) for a hand-drawn curve. */
const teal: [string, string, number][] = [
  ["BANK-B", "BANK-A", 0.14], ["BANK-B", "BANK-C", -0.1], ["BANK-D", "BANK-C", 0.12], ["BANK-C", "BANK-E", -0.12], ["BANK-E", "BANK-D", 0.2],
];
const loop = ["BANK-A", "BANK-D", "BANK-F"] as const;
type Pt = [number, number];
const quad = (a: Pt, c: Pt, b: Pt, t: number): Pt => [(1 - t) ** 2 * a[0] + 2 * (1 - t) * t * c[0] + t * t * b[0], (1 - t) ** 2 * a[1] + 2 * (1 - t) * t * c[1] + t * t * b[1]];
const f = (n: number) => n.toFixed(1);
/** A quadratic route between two building anchors, bulging by `bend` × length (sign picks the side). */
function route(a: Pt, b: Pt, bend: number, away?: Pt) {
  const mx = (a[0] + b[0]) / 2, my = (a[1] + b[1]) / 2, dx = b[0] - a[0], dy = b[1] - a[1], len = Math.hypot(dx, dy) || 1;
  let nx = -dy / len, ny = dx / len;
  if (away && (mx - away[0]) * nx + (my - away[1]) * ny < 0) { nx = -nx; ny = -ny; }
  const c: Pt = [mx + nx * len * bend, my + ny * len * bend];
  return { a, b, c, d: `M${f(a[0])} ${f(a[1])}Q${f(c[0])} ${f(c[1])} ${f(b[0])} ${f(b[1])}` };
}

export function CityMap({ state, focus, children }: { state: Snapshot; focus: Payment | null; children: (layout: { w: number; h: number; mid: Pt }) => ReactNode }) {
  const host = useRef<HTMLDivElement>(null);
  const [size, setSize] = useState({ w: 980, h: 400 });
  useEffect(() => {
    const el = host.current;
    if (!el) return;
    const read = () => setSize(s => (Math.abs(s.w - el.clientWidth) > 0.5 || Math.abs(s.h - el.clientHeight) > 0.5) && el.clientWidth > 0 ? { w: el.clientWidth, h: el.clientHeight } : s);
    read();
    const ro = new ResizeObserver(read);
    ro.observe(el);
    return () => ro.disconnect();
  }, []);
  const { w, h } = size;
  const s = Math.max(0.72, Math.min(1, h / 420));
  const at = (id: string): Pt => [spot[id]![0] * w, spot[id]![1] * h - 6 * s];
  const trim = (p: Pt, toward: Pt, r: number): Pt => { const dx = toward[0] - p[0], dy = toward[1] - p[1], l = Math.hypot(dx, dy) || 1; return [p[0] + (dx / l) * r, p[1] + (dy / l) * r]; };
  const r = 36 * s;
  const centroid: Pt = [loop.reduce((n, id) => n + at(id)[0], 0) / 3, loop.reduce((n, id) => n + at(id)[1], 0) / 3];
  const bends = [0.14, 0.22, 0.26];
  const loopRoutes = loop.map((id, i) => {
    const a0 = at(id), b0 = at(loop[(i + 1) % 3]!);
    const c0 = route(a0, b0, bends[i]!, centroid).c;
    return route(trim(a0, c0, r), trim(b0, c0, r), bends[i]!, centroid);
  });
  const tealRoutes = teal.map(([from, to, bend]) => {
    const a0 = at(from), b0 = at(to), c0 = route(a0, b0, bend).c;
    return { from, to, ...route(trim(a0, c0, r), trim(b0, c0, r), bend) };
  });
  const arrow = (ro: ReturnType<typeof route>) => {
    const p = quad(ro.a, ro.c, ro.b, 0.52), q = quad(ro.a, ro.c, ro.b, 0.56);
    return `translate(${f(p[0])} ${f(p[1])}) rotate(${f((Math.atan2(q[1] - p[1], q[0] - p[0]) * 180) / Math.PI)})`;
  };
  const suspect = focus !== null;
  const mid = quad(loopRoutes[1]!.a, loopRoutes[1]!.c, loopRoutes[1]!.b, 0.5);
  return <div className="r8-city" ref={host}>
    <CityBackdrop />
    <svg className="r8-city-svg" viewBox={`0 0 ${w} ${h}`} role="img" aria-label="Six banks across a city map. Committed payment routes in teal; one circular route MRD to OST to CRV and back to MRD is highlighted in coral.">
      <defs><filter id="r8-city-glow" x="-20%" y="-20%" width="140%" height="140%"><feGaussianBlur stdDeviation="5" /></filter></defs>
      <g className="r8-city-teal">
        {tealRoutes.map(t => <g key={t.from + t.to}>
          <path className="r8-city-teal-base" d={t.d} /><path className="r8-city-teal-flow" d={t.d} />
          <path className="r8-city-teal-arrow" d="M-4 -3.5L3 0-4 3.5" transform={arrow(t)} />
        </g>)}
      </g>
      <g className={"r8-city-loop" + (suspect ? " is-suspect" : "")}>
        {loopRoutes.map((l, i) => <g key={i}>
          <path className="r8-city-loop-glow" d={l.d} /><path className="r8-city-loop-base" d={l.d} /><path className="r8-city-loop-flow" d={l.d} />
          <path className="r8-city-loop-arrow" d="M-5 -4.5L4 0-5 4.5Z" transform={arrow(l)} />
          {[0, 1].map(k => <circle key={k} className="r8-city-spark" r="3.2"><animateMotion dur="3.6s" begin={`${-k * 1.8 - i * 0.6}s`} repeatCount="indefinite" path={l.d} /></circle>)}
        </g>)}
      </g>
      {suspect && <g className="r8-city-leader"><circle cx={mid[0]} cy={mid[1]} r="3.5" /></g>}
      {BANKS.map(b => {
        const bal = state.balances.find(x => x.bankId === b.id);
        const [x, y] = at(b.id), hot = suspect && (loop as readonly string[]).includes(b.id);
        return <g key={b.id} className={"r8-bank" + (hot ? " is-hot" : "")} transform={`translate(${f(x)} ${f(y)}) scale(${s.toFixed(3)})`}>
          <title>{b.name} ({b.id}) · available {bal ? demoMillions(bal.availableMinor) : "—"}</title>
          <ellipse className="r8-bank-shadow" cy="33" rx="46" ry="6" />
          <path className="r8-bank-roof" d="M-40 -14L0 -35 40 -14Z" /><path className="r8-bank-roof-hi" d="M-31 -16.5L0 -30.5 31 -16.5" />
          <rect className="r8-bank-band" x="-38" y="-14" width="76" height="17" rx="1.5" />
          <rect className="r8-bank-body" x="-35" y="3" width="70" height="20" />
          {[-24, -12, 0, 12, 24].map(cx => <rect key={cx} className="r8-bank-col" x={cx - 2.2} y="5" width="4.4" height="16" rx="1" />)}
          <rect className="r8-bank-step" x="-42" y="23" width="84" height="5" rx="1" /><rect className="r8-bank-step is-low" x="-46" y="28" width="92" height="5" rx="1" />
          <text className="r8-bank-code" textAnchor="middle" y="-1.8">{b.code}</text>
          <g className="r8-bank-tag"><rect x="-41" y="39" width="82" height="31" rx="8" />
            <text className="r8-bank-name" textAnchor="middle" y="51">{b.name}</text>
            <text className="r8-bank-amt" textAnchor="middle" y="64.5">{bal ? demoMillions(bal.availableMinor) : "—"}</text></g>
        </g>;
      })}
    </svg>
    {children({ w, h, mid })}
  </div>;
}

/** Pale cartographic backdrop: blocks, streets, parks and one faint canal. Pure vector, no labels. */
function CityBackdrop() {
  return <svg className="r8-city-bg" viewBox="0 0 1000 400" preserveAspectRatio="none" aria-hidden="true">
    <rect width="1000" height="400" fill="#F1F5F3" />
    <g fill="#F8FAF9">
      <path d="M0 0H70L88 118 0 132Z" /><path d="M110 0H300L318 80 128 118Z" /><path d="M330 0H610L632 74 340 96Z" /><path d="M650 0H820L836 100 668 78Z" /><path d="M852 0H1000V110L860 112Z" />
      <path d="M0 160 150 142 190 250 0 262Z" /><path d="M214 156 410 126 470 214 250 250Z" /><path d="M500 140 700 120 760 214 540 214Z" /><path d="M740 126 1000 140V236L790 214Z" />
      <path d="M0 292 130 280 176 400H0Z" /><path d="M210 284 400 270 430 400H236Z" /><path d="M470 262 660 262 700 400H500Z" /><path d="M730 250 1000 270V400H760Z" />
    </g>
    <g fill="#DFEBE1">
      <path d="M338 -4H520Q548 30 520 62 430 78 346 62 318 30 338 -4Z" />
      <path d="M800 286Q860 262 930 282 990 300 1000 340V404H836Q790 350 800 286Z" />
      <path d="M40 176 126 166 140 230 56 240Z" />
    </g>
    <path className="r8-city-canal" d="M-10 342C120 330 190 372 330 378S560 392 700 412" />
    <g className="r8-city-streets">
      <path className="is-major" d="M-20 330C250 290 520 190 1020 96" /><path className="is-major" d="M0 148C300 196 620 128 1020 238" />
      <path className="is-major" d="M84 -10C132 140 206 262 164 420" /><path className="is-major" d="M640 -10C598 120 712 262 770 420" />
      <path className="is-major" d="M392 -10C438 100 366 250 442 420" />
      <path d="M-10 90C240 110 480 80 1010 60" /><path d="M-10 260C260 232 480 262 1010 330" /><path d="M250 -10C262 120 300 250 312 410" />
      <path d="M520 -10C540 90 570 200 596 410" /><path d="M870 -10C860 120 900 260 930 410" /><path d="M-10 214C200 206 340 236 530 214" />
      <path d="M690 160C800 172 900 150 1010 170" />
    </g>
  </svg>;
}
