// Validates: Spec 13 task 13.5 (candles, day/night, historical ghost). Pure logic and shipped-data checks only: no Pixi, no DOM.
// The visual result is judged by a person in the running app (HUMAN REVIEW PENDING); these tests guard the facts the visuals depend on.
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import { frameAt, loopPosition, phaseOffsetFrames, poolGain } from "../src/candles/candleTiming.ts";
import { FIXTURE_TICKS, GHOST_ROUTE, LIVING_ROUTE, divergenceTick, routeAt } from "../src/ghost/previewFixture.ts";
import { ARENA_V1_RENDER } from "../src/arena/arenaV1Descriptor.ts";

const here = (p: string) => fileURLToPath(new URL(p, import.meta.url));
const atlas = JSON.parse(readFileSync(here("../src/config/candleAtlas.json"), "utf8")) as {
  meta: {
    frameCount: number; cell: number; columns: number; rows: number; fps: number; flashFrames: [number, number]; lightGain: number[];
    loops: { source: { frames: number[] }; steady: { frames: number[] } };
    evidence: { rowMajorMeanStep: number; columnMajorMeanStep: number; randomPairMean: number; rowMajorLoopClose: number; successorAmongTwoNearest: number; of: number };
  };
  frames: Record<string, { x: number; y: number; w: number; h: number }>;
};

test("candle atlas: 90 frames, in reading order, inside the shipped PNG, small enough for any GPU", () => {
  const m = atlas.meta;
  assert.equal(m.frameCount, 90);
  assert.equal(Object.keys(atlas.frames).length, 90);
  const png = readFileSync(here("../public/game-assets/generated/candle-flame.png"));
  assert.equal(png.subarray(1, 4).toString(), "PNG");
  const width = png.readUInt32BE(16), height = png.readUInt32BE(20);
  assert.equal(width, m.columns * m.cell);
  assert.equal(height, m.rows * m.cell);
  assert.ok(width <= 4096 && height <= 4096, "the derived atlas must not repeat the master's over-4096 problem");
  for (let i = 0; i < 90; i++) {
    const f = atlas.frames[String(i)]!;
    assert.deepEqual([f.x, f.y, f.w, f.h], [(i % 10) * m.cell, Math.floor(i / 10) * m.cell, m.cell, m.cell], `frame ${i} follows row-major order`);
  }
});

test("candle order evidence: reading order is far smoother than column order or random pairs, and the loop closes", () => {
  const e = atlas.meta.evidence;
  assert.ok(e.rowMajorMeanStep * 4 < e.columnMajorMeanStep, "reading order must be several times smoother than column order");
  assert.ok(e.rowMajorMeanStep * 4 < e.randomPairMean);
  assert.ok(e.rowMajorLoopClose < e.rowMajorMeanStep, "frame 89 must close to frame 0");
  assert.ok(e.successorAmongTwoNearest >= 0.75 * e.of);
});

test("candle loops: source is all 90 frames; steady drops exactly the measured flash run", () => {
  const { source, steady } = atlas.meta.loops;
  const [a, b] = atlas.meta.flashFrames;
  assert.deepEqual(source.frames, Array.from({ length: 90 }, (_, i) => i));
  assert.equal(steady.frames.length, 90 - (b - a + 1));
  assert.ok(steady.frames.every((f) => f < a || f > b));
  assert.deepEqual(steady.frames, [...steady.frames].sort((x, y) => x - y), "steady keeps the sheet's order");
  assert.equal(atlas.meta.lightGain.length, 90);
});

test("candle timing: deterministic opaque phase, wraps, reverses, and never reads a site number", () => {
  assert.equal(phaseOffsetFrames("c-0007", 66), phaseOffsetFrames("c-0007", 66));
  const offsets = new Set(Array.from({ length: 24 }, (_, i) => phaseOffsetFrames(`c-${String(i).padStart(4, "0")}`, 66)));
  assert.ok(offsets.size >= 12, "neighbouring ids must not all share one flicker phase");
  for (const o of offsets) assert.ok(o >= 0 && o < 66);
  assert.equal(phaseOffsetFrames("x", 0), 0);
  // 30 fps: 1000 ms is exactly 30 frames; the loop wraps both ways.
  assert.equal(loopPosition(1000, 0, 90, 30), 30);
  assert.equal(loopPosition(3000, 0, 90, 30), 0);
  assert.equal(loopPosition(1000, 0, 90, 30, true), 60);
  const loop = atlas.meta.loops.steady.frames;
  assert.equal(frameAt(loop, 0, 0, 30), loop[0]);
  assert.ok(poolGain(2.2) <= 1.4 && poolGain(0.35) < 1 && poolGain(1) === 1);
});

test("preview fixture: ghost and living head overlap EXACTLY until the first real divergence, then separate", () => {
  const d = divergenceTick();
  assert.ok(d > 100 && d < FIXTURE_TICKS, `divergence at ${d}`);
  for (let t = 0; t < d; t += 7) {
    const g = routeAt(GHOST_ROUTE, t), l = routeAt(LIVING_ROUTE, t);
    assert.deepEqual([g.x, g.y], [l.x, l.y], `tick ${t} must be an exact overlap`);
  }
  const g = routeAt(GHOST_ROUTE, d + 30), l = routeAt(LIVING_ROUTE, d + 30);
  assert.ok(Math.hypot(g.x - l.x, g.y - l.y) > 100);
});

test("preview fixture lanes keep a 48x48 head clear of all nine ArenaV1 walls", () => {
  const half = 24;
  for (const route of [GHOST_ROUTE, LIVING_ROUTE]) {
    for (let t = 0; t <= FIXTURE_TICKS; t++) {
      const { x, y } = routeAt(route, t);
      for (const w of ARENA_V1_RENDER.obstacles) {
        const overlaps = x + half > w.x0 && x - half < w.x1 && y + half > w.y0 && y - half < w.y1;
        assert.equal(overlaps, false, `tick ${t} at (${x},${y}) hits wall ${JSON.stringify(w)}`);
      }
    }
  }
});
