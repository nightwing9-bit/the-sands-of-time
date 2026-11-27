// PREVIEW (development only): the 13.5 day/night, candle and ghost study. NOT gameplay and NOT a live history.
//
// The same arena, the same candle flames and the real CharacterView / spectral ghost, with day and night one click apart, in the one Pixi world.
// What is real and what is a fixture, on screen and here:
//   REAL      the arena art + nine wall rectangles, the 90-frame candle animation (derived atlas of the actual sheet), the head art, the ghost shader.
//   FIXTURE   candle PLACEMENT (the 24 ArenaV1 site coordinates with made-up fixture ids; a real run draws the server's ActiveCandleIds through the
//             verified plan, see the Day play screen) and BOTH ROUTES (scripted lanes, not a saved Original). Marked "PREVIEW FIXTURE" below.
import { useEffect, useRef, useState } from "react";
import { Container } from "pixi.js";
import { BackLink } from "../ui/primitives.tsx";
import type { ArenaWorld } from "../stage/ArenaWorld.ts";
import { CharacterTextures } from "../characters/CharacterAssets.ts";
import { CharacterView } from "../characters/CharacterView.ts";
import { HEAD_UNITS, SLOT_SPECS } from "../characters/characterSpec.ts";
import { CandleTextures, CANDLE_META, type CandleLoopId } from "../candles/CandleAssets.ts";
import { CandleField, type CandleSite } from "../candles/CandleField.ts";
import { GhostTextures, GhostView } from "../ghost/SpectralGhost.ts";
import { FIXTURE_TICKS, GHOST_ROUTE, LIVING_ROUTE, divergenceTick, routeAt } from "../ghost/previewFixture.ts";
import { ARENA_V1_CANDLE_SITES } from "../arena/candleSites.ts";
import type { EnvironmentMode } from "../lighting/lightingPresets.ts";
import type { ActorSlot, Charge } from "../presentation/types.ts";

const SLOTS: readonly ActorSlot[] = [0, 1, 2, 3];
/** Fixture selections. Indices into the 24 ArenaV1 sites; the ids below are fixture ids, never plan ids. */
const SHOWN = [0, 2, 5, 8, 10, 13, 16, 19, 21, 23];
const TELEGRAPH = [4, 17];
const TICK_MS = 1000 / 30;
const DIVERGES_AT = divergenceTick();

interface Controls { paused: boolean; ghostOn: boolean; charge: Charge; restartAt: number | null }
const fixtureCandles = (all: boolean): CandleSite[] =>
  ARENA_V1_CANDLE_SITES.flatMap((s, i): CandleSite[] => {
    const phase = all ? "active" : TELEGRAPH.includes(i) ? "telegraph" : SHOWN.includes(i) ? "active" : null;
    return phase ? [{ id: `fixture-candle-${i}`, x: s.x, y: s.y, phase }] : [];
  });

export function NightStudyHud({ world, onBack }: { world: ArenaWorld | null; onBack: () => void }) {
  const [characters, setCharacters] = useState<CharacterTextures | null>(null);
  const [candles, setCandles] = useState<CandleTextures | null>(null);
  const [failed, setFailed] = useState<string | null>(null);
  const [mode, setMode] = useState<EnvironmentMode>("night");
  const [loop, setLoop] = useState<CandleLoopId>("steady");
  const [reverse, setReverse] = useState(false);
  const [allSites, setAllSites] = useState(false);
  const [slot, setSlot] = useState<ActorSlot>(0);
  const [ghostOn, setGhostOn] = useState(true);
  const [paused, setPaused] = useState(false);
  const [charge, setCharge] = useState<Charge>(2);
  const [collapsed, setCollapsed] = useState(false);
  const [readout, setReadout] = useState({ tick: 0, gap: 0 });
  const controls = useRef<Controls>({ paused: false, ghostOn: true, charge: 2, restartAt: null });
  const field = useRef<CandleField | null>(null);
  controls.current.paused = paused;
  controls.current.ghostOn = ghostOn;
  controls.current.charge = charge;

  useEffect(() => {
    const abort = new AbortController();
    let c: CharacterTextures | null = null;
    let k: CandleTextures | null = null;
    CharacterTextures.load(abort.signal).then((t) => { if (abort.signal.aborted) return t.destroy(); c = t; setCharacters(t); }, (e: unknown) => { if (!abort.signal.aborted) setFailed(String(e)); });
    CandleTextures.load(abort.signal).then((t) => { if (abort.signal.aborted) return t.destroy(); k = t; setCandles(t); }, (e: unknown) => { if (!abort.signal.aborted) setFailed(String(e)); });
    return () => { abort.abort(); c?.destroy(); k?.destroy(); setCharacters(null); setCandles(null); };
  }, []);

  // The scene: candles, one ghost, one living head. Rebuilt only when the world, the textures or the chosen head change.
  useEffect(() => {
    if (!world || !characters || !candles) return;
    const flames = new Container({ label: "study-flames" });
    const ghostLayer = new Container({ label: "study-ghost" });
    const headLayer = new Container({ label: "study-heads" });
    world.layers.actors.addChild(flames, ghostLayer, headLayer);
    const f = new CandleField(world, candles, flames);
    field.current = f;
    const ghostTextures = new GhostTextures(world.renderer, characters);
    const ghost = new GhostView(ghostTextures, slot, HEAD_UNITS);
    ghostLayer.addChild(ghost.root);
    const living = new CharacterView(characters, { slot, headUnits: HEAD_UNITS });
    headLayer.addChild(living.root);

    let tickF = 0;
    let last = performance.now();
    let lastReadout = 0;
    const onTick = () => {
      const now = performance.now();
      const dt = Math.min(100, now - last);
      last = now;
      const c = controls.current;
      if (c.restartAt !== null) { tickF = c.restartAt; c.restartAt = null; }
      if (!c.paused) tickF = (tickF + dt / TICK_MS) % FIXTURE_TICKS;
      const tick = Math.floor(tickF);
      const g = routeAt(GHOST_ROUTE, tick), l = routeAt(LIVING_ROUTE, tick);
      ghost.root.visible = c.ghostOn;
      ghost.setPosition(g.x, g.y);
      living.setPosition(l.x, l.y);
      living.setCharge(c.charge, now);
      living.setGaze(l.dx, l.dy);
      living.update(now, dt);
      f.update(now);
      if (now - lastReadout > 100) {
        lastReadout = now;
        const gap = Math.hypot(g.x - l.x, g.y - l.y);
        setReadout((p) => (p.tick === tick && p.gap === gap ? p : { tick, gap }));
      }
    };
    f.sync(fixtureCandles(false));
    world.ticker.add(onTick);
    return () => {
      world.ticker.remove(onTick);
      field.current = null;
      f.dispose();
      ghost.destroy();
      ghostTextures.destroy();
      living.destroy();
      flames.destroy({ children: true });
      ghostLayer.destroy({ children: true });
      headLayer.destroy({ children: true });
    };
  }, [world, characters, candles, slot]);

  // Mode, loop, direction and site set are applied to the live field without rebuilding the scene.
  useEffect(() => { world?.setEnvironment(mode); field.current?.setMode(mode); }, [world, mode, characters, candles, slot]);
  useEffect(() => { field.current?.setLoop(loop); }, [loop, characters, candles, slot]);
  useEffect(() => { field.current?.setReverse(reverse); }, [reverse, characters, candles, slot]);
  useEffect(() => { field.current?.sync(fixtureCandles(allSites)); }, [allSites, characters, candles, slot]);

  const ready = !!world && !!characters && !!candles;
  const meta = CANDLE_META;
  const overlap = readout.gap < 0.01;
  return (
    <div className="gl-ns" data-testid="night-study" data-ready={ready ? "true" : "false"} data-mode={mode}>
      <div className="gl-study-top">
        <BackLink onClick={onBack}>Home</BackLink>
        <span className="gl-fixture-tag" data-testid="fixture-tag">PREVIEW FIXTURE · scripted routes and fixture candle ids · not a saved history · not live play</span>
      </div>
      {failed ? <p className="gl-world-failed" role="alert">Assets could not load: {failed}</p> : null}

      <output className="gl-ns-readout" data-testid="ns-readout" data-overlap={overlap ? "1" : "0"}>
        <b>{mode === "night" ? "Night" : "Day"}</b>
        <span>tick {readout.tick} · ghost to living {readout.gap.toFixed(1)} u · {overlap ? "exact overlap" : `diverged (from tick ${DIVERGES_AT})`}</span>
      </output>

      <div className="gl-ns-panel gl-cs-panel" aria-label="Day and night preview controls">
        <button type="button" className="gl-ns-head" aria-expanded={!collapsed} onClick={() => setCollapsed(!collapsed)}>Day / night study {collapsed ? "▸" : "▾"}</button>
        {collapsed ? null : (
          <>
            <div role="group" aria-label="Environment">
              <span className="gl-cs-k">World</span>
              <button type="button" aria-pressed={mode === "day"} onClick={() => setMode("day")} data-testid="mode-day">Day</button>
              <button type="button" aria-pressed={mode === "night"} onClick={() => setMode("night")} data-testid="mode-night">Night</button>
              <span className="gl-cs-note">same arena, same flames; only the grade and the light change</span>
            </div>
            <div role="group" aria-label="Candle animation">
              <span className="gl-cs-k">Flame</span>
              <button type="button" aria-pressed={loop === "steady"} onClick={() => setLoop("steady")} data-testid="loop-steady">Steady flicker</button>
              <button type="button" aria-pressed={loop === "source"} onClick={() => setLoop("source")} data-testid="loop-source">Full source loop (with flash)</button>
              <button type="button" aria-pressed={reverse} onClick={() => setReverse(!reverse)} data-testid="loop-reverse">Play backwards</button>
              <button type="button" aria-pressed={allSites} onClick={() => setAllSites(!allSites)} data-testid="all-sites">All 24 sites</button>
            </div>
            <div role="group" aria-label="Head and ghost">
              <span className="gl-cs-k">Head</span>
              {SLOTS.map((s) => <button key={s} type="button" aria-pressed={slot === s} onClick={() => setSlot(s)}>{SLOT_SPECS[s].name.split(" · ")[0]}</button>)}
              <button type="button" aria-pressed={ghostOn} onClick={() => setGhostOn(!ghostOn)} data-testid="ghost-toggle">Ghost</button>
              <button type="button" aria-pressed={paused} onClick={() => setPaused(!paused)}>Pause</button>
              <button type="button" onClick={() => { controls.current.restartAt = 0; }}>Restart</button>
            </div>
            <div role="group" aria-label="Living charge (fixture)">
              <span className="gl-cs-k">Charge</span>
              {([0, 2, 3, 4] as const).map((n) => <button key={n} type="button" aria-pressed={charge === n} onClick={() => setCharge(n)}>{n}</button>)}
              <span className="gl-cs-note">halo and face stay un-graded</span>
            </div>
            <p className="gl-cs-note gl-ns-note" data-testid="ns-evidence">
              Candle order: reading order of the real 10×9 sheet. Neighbour step {meta.evidence.rowMajorMeanStep} (reading order) vs {meta.evidence.columnMajorMeanStep} (column order) vs {meta.evidence.randomPairMean} (random); frame 89 closes to 0 at {meta.evidence.rowMajorLoopClose}. Direction and the 30 fps rate are for you to judge, not export facts.
            </p>
          </>
        )}
      </div>
    </div>
  );
}
