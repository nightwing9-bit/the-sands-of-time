// PREVIEW (development only): the 13.3 expression viewer. NOT a shipping character picker and NOT gameplay.
// It draws the real CharacterView into the real stage (actors layer of the one Pixi world, on the real snow) so heads are seen at the true
// play scale of the current window. Every state here is a hand-driven fixture; nothing is server truth.
//   left   matrix: four heads x (charge 0..4, weaker-contact fear, Burst hit, release, winner, ghost). Fear/hit/ghost/release/winner are held
//          still so they can be inspected (the live cast below shows the real 2 s timing).
//   right  live cast: the same four heads driven by the controls through the real ExpressionState (timed overrides expire to the charge).
//          Shown at play size and magnified x3 for anatomy.
//   P1     P1 comparison: C (approved) and the unselected A and B beside the P3 diamond.
import { useEffect, useMemo, useRef, useState } from "react";
import { BackLink } from "../ui/primitives.tsx";
import type { ArenaWorld } from "../stage/ArenaWorld.ts";
import { CharacterTextures } from "../characters/CharacterAssets.ts";
import { CharacterView } from "../characters/CharacterView.ts";
import { HEAD_SIZE_OPTIONS, HEAD_UNITS, P1_OPTIONS, SLOT_SPECS } from "../characters/characterSpec.ts";
import type { FaceId } from "../characters/expression.ts";
import type { ActorSlot, Charge } from "../presentation/types.ts";

const SLOTS: readonly ActorSlot[] = [0, 1, 2, 3];
const MATRIX_X = [250, 330, 410, 490];
const MATRIX_Y0 = 150;
const MATRIX_DY = 72;
type RowKind = { label: string; sub: string } & ({ charge: Charge; pin?: FaceId; release?: boolean; winner?: boolean; ghost?: boolean });
const ROWS: readonly RowKind[] = [
  { label: "Charge 0", sub: "frown · no halo", charge: 0 },
  { label: "Charge 1", sub: "same as 0", charge: 1 },
  { label: "Charge 2", sub: "neutral · faint", charge: 2 },
  { label: "Charge 3", sub: "happy · faint+", charge: 3 },
  { label: "Charge 4", sub: "grin · armed", charge: 4 },
  { label: "Weaker contact", sub: "fear (2 s, held)", charge: 2, pin: "fear" },
  { label: "Burst hit", sub: "X eyes (2 s, held)", charge: 2, pin: "hit" },
  { label: "4/4 release", sub: "effort (open-ended)", charge: 4, release: true },
  { label: "Winner", sub: "confirmed result", charge: 3, winner: true },
  { label: "Ghost", sub: "permanent evil", charge: 0, ghost: true },
];
const LIVE_X = [760, 950, 1140, 1330];
const LIVE_Y = 140;
const BIG_Y = 285;
const BIG_SCALE = 3;
const CMP_X = [760, 920, 1080, 1240];
const CMP_Y = 480;
const CMP_SCALE = 2;
const pct = (x: number, y: number) => ({ left: `${(x / 1600) * 100}%`, top: `${(y / 900) * 100}%` });
const KEY_DIR: Record<string, readonly [number, number]> = {
  ArrowLeft: [-1, 0], KeyA: [-1, 0], ArrowRight: [1, 0], KeyD: [1, 0], ArrowUp: [0, -1], KeyW: [0, -1], ArrowDown: [0, 1], KeyS: [0, 1],
};

interface Live { charge: Charge; gx: -1 | 0 | 1; gy: -1 | 0 | 1 }

export function CharacterStudyHud({ world, onBack }: { world: ArenaWorld | null; onBack: () => void }) {
  const [textures, setTextures] = useState<CharacterTextures | null>(null);
  const [failed, setFailed] = useState<string | null>(null);
  const [headUnits, setHeadUnits] = useState<number>(HEAD_UNITS);
  const [charge, setCharge] = useState<Charge>(2);
  const [gaze, setGaze] = useState<{ x: -1 | 0 | 1; y: -1 | 0 | 1 }>({ x: 0, y: 0 });
  const [readout, setReadout] = useState<{ face: FaceId; pxPerUnit: number }>({ face: "neutral", pxPerUnit: 0 });
  const live = useRef<Live>({ charge: 2, gx: 0, gy: 0 });
  const liveViews = useRef<CharacterView[]>([]);
  const held = useRef(new Set<string>());
  const winner = useRef(false);
  const [winnerOn, setWinnerOn] = useState(false);
  const [releaseOn, setReleaseOn] = useState(false);

  live.current.charge = charge;
  live.current.gx = gaze.x;
  live.current.gy = gaze.y;

  useEffect(() => {
    const abort = new AbortController();
    let loaded: CharacterTextures | null = null;
    CharacterTextures.load(abort.signal).then(
      (t) => { if (abort.signal.aborted) return t.destroy(); loaded = t; setTextures(t); },
      (error: unknown) => { if (!abort.signal.aborted) { console.error("Character atlas failed", error); setFailed(String(error)); } },
    );
    return () => { abort.abort(); loaded?.destroy(); setTextures(null); };
  }, []);

  // Keyboard gaze: arrows / WASD while this study is open (only ever moves pupils).
  useEffect(() => {
    const dir = () => {
      let x = 0, y = 0;
      for (const code of held.current) { const d = KEY_DIR[code]; if (d) { x += d[0]; y += d[1]; } }
      return { x: Math.sign(x) as -1 | 0 | 1, y: Math.sign(y) as -1 | 0 | 1 };
    };
    const down = (e: KeyboardEvent) => { if (KEY_DIR[e.code] && !(e.target instanceof HTMLInputElement)) { held.current.add(e.code); setGaze(dir()); e.preventDefault(); } };
    const up = (e: KeyboardEvent) => { if (held.current.delete(e.code)) setGaze(dir()); };
    window.addEventListener("keydown", down);
    window.addEventListener("keyup", up);
    return () => { window.removeEventListener("keydown", down); window.removeEventListener("keyup", up); };
  }, []);

  const p1 = P1_OPTIONS;
  const compare = useMemo(() => [
    { slot: 0 as ActorSlot, body: { key: p1[0].body, hue: p1[0].hue }, label: "A · circle, unmodified (not selected)" },
    { slot: 0 as ActorSlot, body: { key: p1[1].body, hue: p1[1].hue }, label: "B · red circle, unmodified (not selected)" },
    { slot: 0 as ActorSlot, body: { key: p1[2].body, hue: p1[2].hue }, label: "C · hue-rotated -13° (APPROVED P1)" },
    { slot: 2 as ActorSlot, body: undefined, label: "P3 diamond (for reference)" },
  ], [p1]);

  useEffect(() => {
    if (!world || !textures) return;
    const layer = world.layers.actors;
    const views: CharacterView[] = [];
    const matrix: { view: CharacterView }[] = [];
    const liveSet: CharacterView[] = [];
    const bigSet: CharacterView[] = [];
    const cmpSet: CharacterView[] = [];
    const add = (view: CharacterView, x: number, y: number, scale = 1) => {
      view.setPosition(x, y);
      view.root.scale.set(scale);
      layer.addChild(view.root);
      views.push(view);
      return view;
    };
    ROWS.forEach((row, r) => SLOTS.forEach((slot, c) => {
      const view = add(new CharacterView(textures, { slot, kind: row.ghost ? "ghost" : "living", headUnits }), MATRIX_X[c]!, MATRIX_Y0 + r * MATRIX_DY);
      view.setCharge(row.charge, 0);
      if (row.release) view.expression.beginRelease(0);
      if (row.winner) view.expression.setWinner(true);
      if (row.pin) view.pinFace(row.pin);
      matrix.push({ view });
    }));
    SLOTS.forEach((slot, c) => {
      liveSet.push(add(new CharacterView(textures, { slot, headUnits }), LIVE_X[c]!, LIVE_Y));
      bigSet.push(add(new CharacterView(textures, { slot, headUnits }), LIVE_X[c]!, BIG_Y, BIG_SCALE));
    });
    compare.forEach((item, c) => cmpSet.push(add(new CharacterView(textures, { slot: item.slot, headUnits, ...(item.body ? { body: item.body } : {}) }), CMP_X[c]!, CMP_Y, CMP_SCALE)));
    liveViews.current = [...liveSet, ...bigSet];

    const animated = [...liveSet, ...bigSet, ...cmpSet, ...matrix.map((m) => m.view)];
    const driven = [...liveSet, ...bigSet, ...cmpSet];
    let last = performance.now();
    let lastReadout = 0;
    const tick = () => {
      const now = performance.now();
      const dt = Math.min(100, now - last);
      last = now;
      const { charge: ch, gx, gy } = live.current;
      for (const v of animated) {
        v.setGaze(gx, gy);
        v.update(now, dt);
      }
      for (const v of driven) v.setCharge(ch, now);
      if (now - lastReadout > 120) {
        lastReadout = now;
        const next = { face: liveSet[0]!.face, pxPerUnit: world.transform.scale };
        setReadout((prev) => (prev.face === next.face && Math.abs(prev.pxPerUnit - next.pxPerUnit) < 0.001 ? prev : next));
      }
    };
    world.ticker.add(tick);
    return () => {
      world.ticker.remove(tick);
      liveViews.current = [];
      for (const v of views) v.destroy();
    };
  }, [world, textures, headUnits, compare]);

  const everyLive = (fn: (v: CharacterView, now: number) => void) => { const now = performance.now(); for (const v of liveViews.current) fn(v, now); };
  const cueFear = () => everyLive((v, now) => v.expression.cue("fear", now));
  const cueHit = () => everyLive((v, now) => v.expression.cue("hit", now));
  const toggleRelease = () => {
    const next = !releaseOn;
    setReleaseOn(next);
    everyLive((v, now) => { if (next) v.expression.beginRelease(now); else v.expression.endRelease(); });
  };
  const toggleWinner = () => { const next = !winner.current; winner.current = next; setWinnerOn(next); everyLive((v) => v.expression.setWinner(next)); };
  const clearAll = () => { winner.current = false; setWinnerOn(false); setReleaseOn(false); everyLive((v) => { v.expression.setWinner(false); v.expression.endRelease(); }); };
  const dir = (x: -1 | 0 | 1, y: -1 | 0 | 1) => setGaze((g) => (g.x === x && g.y === y ? { x: 0, y: 0 } : { x, y }));
  const px = readout.pxPerUnit * headUnits;

  return (
    <div className="gl-cs" data-testid="character-study" data-ready={world && textures ? "true" : "false"}>
      <div className="gl-study-top">
        <BackLink onClick={onBack}>Home</BackLink>
        <span className="gl-fixture-tag" data-testid="fixture-tag">Expression viewer · dev fixture · not a picker</span>
      </div>
      {failed ? <p className="gl-world-failed" role="alert">The character atlas could not load: {failed}</p> : null}

      <div className="gl-cs-heads">{SLOTS.map((slot, c) => <span key={slot} style={pct(MATRIX_X[c]!, MATRIX_Y0 - 52)}>{SLOT_SPECS[slot].name.split(" · ")[0]}</span>)}</div>
      {ROWS.map((row, r) => (
        <div key={row.label} className="gl-cs-row" style={pct(8, MATRIX_Y0 + r * MATRIX_DY)}>
          <b>{row.label}</b><span>{row.sub}</span>
        </div>
      ))}

      <p className="gl-cs-cap" style={pct(LIVE_X[0]! - 90, 78)}>Live cast · real expression logic · {readout.face}</p>
      <p className="gl-cs-cap gl-cs-cap--sub" style={pct(LIVE_X[0]! - 90, LIVE_Y + 38)}>play size: {headUnits} units ≈ {px.toFixed(0)} px in this window</p>
      <p className="gl-cs-cap gl-cs-cap--sub" style={pct(LIVE_X[0]! - 90, BIG_Y + 82)}>same state ×3 for anatomy</p>
      <p className="gl-cs-cap" style={pct(CMP_X[0]! - 90, CMP_Y - 92)}>P1 comparison · C approved · follows the controls</p>
      {compare.map((item, c) => <span key={item.label} className="gl-cs-label" style={pct(CMP_X[c]!, CMP_Y + 58)}>{item.label}</span>)}

      <div className="gl-cs-panel" style={pct(668, 610)} aria-label="Preview controls">
        <div role="group" aria-label="Soulfire charge (fixture)">
          <span className="gl-cs-k">Charge</span>
          {([0, 1, 2, 3, 4] as const).map((n) => <button key={n} type="button" aria-pressed={charge === n} onClick={() => setCharge(n)} data-testid={`charge-${n}`}>{n}</button>)}
          <output className="gl-cs-hud" aria-label="HUD charge">HUD {charge}/4</output>
        </div>
        <div role="group" aria-label="Effective direction (pupils only)">
          <span className="gl-cs-k">Gaze</span>
          <button type="button" aria-pressed={gaze.x === -1 && gaze.y === 0} onClick={() => dir(-1, 0)} aria-label="Gaze left">←</button>
          <button type="button" aria-pressed={gaze.x === 0 && gaze.y === -1} onClick={() => dir(0, -1)} aria-label="Gaze up">↑</button>
          <button type="button" aria-pressed={gaze.x === 0 && gaze.y === 1} onClick={() => dir(0, 1)} aria-label="Gaze down">↓</button>
          <button type="button" aria-pressed={gaze.x === 1 && gaze.y === 0} onClick={() => dir(1, 0)} aria-label="Gaze right">→</button>
          <button type="button" aria-pressed={gaze.x === 1 && gaze.y === -1} onClick={() => dir(1, -1)} aria-label="Gaze up right">↗</button>
          <span className="gl-cs-note">or hold arrow keys / WASD</span>
        </div>
        <div role="group" aria-label="Authoritative cues (fixtures)">
          <span className="gl-cs-k">Cues</span>
          <button type="button" onClick={cueFear} data-testid="cue-fear">Weaker contact (2 s)</button>
          <button type="button" onClick={cueHit} data-testid="cue-hit">Burst hit (2 s)</button>
          <button type="button" aria-pressed={releaseOn} onClick={toggleRelease} data-testid="cue-release">4/4 release (hold)</button>
          <button type="button" aria-pressed={winnerOn} onClick={toggleWinner} data-testid="cue-winner">Winner</button>
          <button type="button" onClick={clearAll}>Clear</button>
        </div>
        <div role="group" aria-label="Head size">
          <span className="gl-cs-k">Head</span>
          {HEAD_SIZE_OPTIONS.map((o) => <button key={o.units} type="button" aria-pressed={headUnits === o.units} onClick={() => setHeadUnits(o.units)}>{o.label}</button>)}
          <span className="gl-cs-note">release has no invented length: it holds until cleared</span>
        </div>
      </div>
    </div>
  );
}
