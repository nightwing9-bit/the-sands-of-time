import { useLayoutEffect, useRef } from "react";

/**
 * Particle layer for the Rewind reveal. It never invents data: every dot is placed from the pixel geometry of the polylines
 * that TimeChart has already drawn (same scales, same layout), so the swarm resolves onto the existing curves exactly.
 * The dissolve/emerge choreography follows the supplied dot-graph reference: diagonal stagger (x wave + per-dot rank),
 * pop-in easing, columns of dots standing in for the filled area.
 */
type Pt = [number, number];
type Dot = { x0: number; y0: number; x1: number; y1: number; delay: number; dur: number; r: number; a: number; tone: "source" | "alternative"; sway: number; phase: number; v0: number; g: number };
type Geometry = { left: number; right: number; top: number; base: number; source: Pt[]; alternative: Pt[] };

const clamp = (v: number) => Math.min(1, Math.max(0, v));
const easeOutCubic = (p: number) => 1 - Math.pow(1 - p, 3);
const easeOutBack = (p: number) => { const c1 = 0.7, c3 = c1 + 1; return 1 + c3 * Math.pow(p - 1, 3) + c1 * Math.pow(p - 1, 2); };
const rnd = (() => { let s = 0x9e3779b9; return () => { s ^= s << 13; s ^= s >>> 17; s ^= s << 5; return ((s >>> 0) % 100000) / 100000; }; })();

function readPoints(svg: SVGSVGElement, tone: string): Pt[] {
  const el = svg.querySelector(`polyline.r8-tone-${tone}`);
  const raw = el?.getAttribute("points")?.trim();
  if (!raw) return [];
  return raw.split(/\s+/).map(p => p.split(",").map(Number) as Pt).filter(p => p.length === 2 && p.every(Number.isFinite));
}
function readGeometry(svg: SVGSVGElement): Geometry | null {
  const plot = svg.querySelector("rect.r8-plot");
  if (!plot) return null;
  const left = Number(plot.getAttribute("x")), top = Number(plot.getAttribute("y"));
  const right = left + Number(plot.getAttribute("width")), base = top + Number(plot.getAttribute("height"));
  const source = readPoints(svg, "source");
  return source.length < 2 ? null : { left, right, top, base, source, alternative: readPoints(svg, "alternative") };
}
function yAt(line: Pt[], x: number) {
  if (x <= line[0]![0]) return line[0]![1];
  for (let i = 1; i < line.length; i++) {
    const [xa, ya] = line[i - 1]!, [xb, yb] = line[i]!;
    if (x <= xb) return xb === xa ? yb : ya + (yb - ya) * ((x - xa) / (xb - xa));
  }
  return line[line.length - 1]![1];
}
function along(line: Pt[], step: number): Pt[] {
  const out: Pt[] = [];
  let carry = 0;
  for (let i = 1; i < line.length; i++) {
    const [xa, ya] = line[i - 1]!, [xb, yb] = line[i]!;
    const len = Math.hypot(xb - xa, yb - ya);
    for (let d = carry; d < len; d += step) out.push([xa + ((xb - xa) * d) / len, ya + ((yb - ya) * d) / len]);
    carry = ((carry - len) % step + step) % step;
  }
  out.push(line[line.length - 1]!);
  return out;
}

/** Everything standing on the curves: dense line dots plus sparse dot columns for the shaded areas. */
function targets(geo: Geometry, alt: boolean) {
  const span = geo.right - geo.left || 1;
  const out: { x: number; y: number; r: number; a: number; tone: "source" | "alternative"; line: boolean; xf: number; xc: number }[] = [];
  const sx0 = geo.source[0]![0], sx1 = geo.source[geo.source.length - 1]![0];
  for (const [x, y] of along(geo.source, 3.1)) out.push({ x, y, r: 1.6, a: 1, tone: "source", line: true, xf: (x - geo.left) / span, xc: 0 });
  for (let x = sx0 + 2; x < sx1; x += 11) {
    const top = yAt(geo.source, x);
    for (let y = top + 10 + rnd() * 4; y < geo.base - 3; y += 11) out.push({ x: x + (rnd() - 0.5) * 2, y, r: 1.25, a: 0.34, tone: "source", line: false, xf: (x - geo.left) / span, xc: 0 });
  }
  if (alt && geo.alternative.length > 1) {
    const ax0 = geo.alternative[0]![0], ax1 = geo.alternative[geo.alternative.length - 1]![0], aspan = ax1 - ax0 || 1;
    for (const [x, y] of along(geo.alternative, 3.1)) out.push({ x, y, r: 1.85, a: 1, tone: "alternative", line: true, xf: (x - geo.left) / span, xc: (x - ax0) / aspan });
    for (let x = ax0 + 3; x < ax1; x += 11) {
      const top = yAt(geo.alternative, x), bottom = yAt(geo.source, x);
      for (let y = top + 9 + rnd() * 3; y < bottom - 5; y += 10) out.push({ x: x + (rnd() - 0.5) * 2, y, r: 1.3, a: 0.5, tone: "alternative", line: false, xf: (x - geo.left) / span, xc: (x - ax0) / aspan });
    }
  }
  return out;
}

function build(mode: "dissolve" | "emerge", geo: Geometry): Dot[] {
  const alt = mode === "emerge";
  return targets(geo, alt).map(t => {
    if (mode === "dissolve") {
      // The line lets go first, the fill crumbles after it. Light gravity with a small upward puff, then a soft landing on the floor.
      const delay = t.xf * 0.85 + rnd() * 0.16 + (t.line ? 0 : 0.1 + rnd() * 0.18);
      const g = 1350 + rnd() * 600, v0 = -(14 + rnd() * 46), land = geo.base - 1 - rnd() * 5;
      const d = Math.max(0, land - t.y);
      return { x0: t.x, y0: t.y, x1: t.x + (rnd() - 0.5) * 26, y1: land, delay, dur: (-v0 + Math.sqrt(v0 * v0 + 2 * g * d)) / g, r: t.r, a: t.a, tone: t.tone, sway: 3 + rnd() * 5, phase: rnd() * 6.28, v0, g };
    }
    // Rise from the floor and organise, blue first, then the alternate branches away from the fork.
    const coral = t.tone === "alternative";
    const delay = coral ? 0.6 + t.xc * 0.8 + rnd() * 0.22 : t.xf * 0.95 + rnd() * 0.24 + (t.line ? 0 : 0.12 + rnd() * 0.12);
    const dur = (coral ? 1.05 : 1.0) + rnd() * 0.5 + (t.line ? 0.1 : 0);
    return { x0: t.x + (rnd() - 0.5) * 46, y0: geo.base + 10 + rnd() * 52, x1: t.x, y1: t.y, delay, dur, r: t.r, a: t.a, tone: t.tone, sway: 4 + rnd() * 9, phase: rnd() * 6.28, v0: 0, g: 0 };
  });
}

export function RewindParticles({ mode, settle, onDone }: { mode: "dissolve" | "emerge"; settle?: boolean; onDone: () => void }) {
  const canvas = useRef<HTMLCanvasElement>(null);
  const done = useRef(onDone);
  done.current = onDone;

  useLayoutEffect(() => {
    const cv = canvas.current!, box = cv.parentElement!, svg = box.querySelector("svg");
    const geo = svg ? readGeometry(svg) : null;
    const ctx = cv.getContext("2d");
    if (!geo || !ctx) { done.current(); return; }
    const w = box.clientWidth, h = box.clientHeight, dpr = Math.min(2, window.devicePixelRatio || 1);
    cv.width = Math.round(w * dpr); cv.height = Math.round(h * dpr);
    const css = getComputedStyle(cv);
    const color = { source: css.getPropertyValue("--original").trim() || "#3F8F9A", alternative: css.getPropertyValue("--coral").trim() || "#FF6B4A" };
    const dots = build(mode, geo);
    const end = dots.reduce((m, d) => Math.max(m, d.delay + d.dur + (mode === "dissolve" ? 0.42 : 0)), 0);
    const emerge = mode === "emerge";
    let raf = 0, finished = false;
    const t0 = performance.now();

    const frame = (now: number) => {
      const t = Math.min(end, (now - t0) / 1000);
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
      ctx.clearRect(0, 0, w, h);
      if (emerge) { ctx.beginPath(); ctx.rect(geo.left - 6, geo.top - 12, geo.right - geo.left + 12, geo.base - geo.top + 12); ctx.clip(); }
      // Draw order keeps the alternate on top of the original.
      for (const tone of ["source", "alternative"] as const) {
        ctx.fillStyle = color[tone];
        for (const d of dots) {
          if (d.tone !== tone) continue;
          const local = t - d.delay;
          let x: number, y: number, a = d.a, r = d.r;
          if (emerge) {
            if (local <= 0) continue;
            const p = clamp(local / d.dur);
            const rise = easeOutBack(p), glide = easeOutCubic(p);
            x = d.x0 + (d.x1 - d.x0) * glide + Math.sin(p * 7 + d.phase) * d.sway * (1 - p) * (1 - p);
            y = d.y0 + (d.y1 - d.y0) * rise;
            a *= Math.min(1, p * 5); r *= 0.45 + 0.55 * Math.min(1, p * 2.4);
          } else if (local <= 0) { x = d.x0; y = d.y0; }
          else {
            const s = Math.min(local, d.dur);
            y = d.y0 + d.v0 * s + 0.5 * d.g * s * s;
            const fall = clamp(s / d.dur);
            x = d.x0 + (d.x1 - d.x0) * fall * fall + Math.sin(s * 9 + d.phase) * d.sway * fall;
            if (y > d.y1) y = d.y1;
            // Lighter as it nears the floor, then it dissolves into it.
            a *= 1 - 0.45 * fall * fall;
            if (local > d.dur) { const k = clamp((local - d.dur) / 0.42); a *= 1 - k; r *= 1 - 0.4 * k; }
          }
          if (a <= 0.01) continue;
          ctx.globalAlpha = a;
          ctx.beginPath(); ctx.arc(x, y, r, 0, 6.2832); ctx.fill();
        }
      }
      ctx.globalAlpha = 1;
      if (t < end) raf = requestAnimationFrame(frame);
      else if (!finished) { finished = true; done.current(); }
    };
    raf = requestAnimationFrame(frame);
    return () => cancelAnimationFrame(raf);
  }, [mode]);

  return <canvas ref={canvas} className={"r8-fx-canvas" + (settle ? " is-settle" : "")} aria-hidden="true" style={{ width: "100%", height: "100%" }} />;
}
