// Validates: Spec 13 task 13.6 (Burst + transition effects). Pure logic only: no Pixi, no DOM. The atlas test reads the generated JSON and the
// source rules from disk, so the numbers the runtime relies on are checked against their evidence instead of re-stated.
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";
import { HALO_ALPHA, HALO_SCALE } from "../src/characters/characterSpec.ts";
import { BURST_REACH_UNITS, CRT, HALO_PULSE, SHOCKWAVE, SMOKE } from "../src/fx/effectPresets.ts";
import { crtEnvelope, haloPulseAt, haloPulseLength, reducedCoverAlpha, shockwaveAt, shockwaveEndRadius, smokeFrameAt, smokeSchedule, type SmokeMeta } from "../src/fx/timeline.ts";
import { PlaySession } from "../src/play/PlaySession.ts";
import type { ConnectionState, InputAnswerView, PlayEvent, PlayFrame, PlayTransport, PlayerFrame } from "../src/play/playTypes.ts";
import type { ControlIntent } from "../src/presentation/types.ts";

const atlas = JSON.parse(readFileSync(new URL("../src/config/smokeAtlas.json", import.meta.url), "utf8"));
const meta: SmokeMeta = { firstVisible: atlas.meta.firstVisibleFrame, lastVisible: atlas.meta.lastVisibleFrame, opaqueFrom: atlas.meta.cover.fullyOpaqueFrom, opaqueTo: atlas.meta.cover.fullyOpaqueTo };

test("the Burst visual never reaches past the server's 160-unit reach, and starts outside the emitting head", () => {
  const rules = readFileSync(new URL("../../../../src/GhostLap/Rules/GameRulesV1.cs", import.meta.url), "utf8");
  assert.match(rules, /BurstRadiusFixed = 160 \* FixedOne/);
  assert.equal(BURST_REACH_UNITS, 160);
  let widest = 0;
  for (let t = 0; t <= SHOCKWAVE.durationMs; t += 5) widest = Math.max(widest, shockwaveAt(t).front);
  assert.ok(widest <= BURST_REACH_UNITS + 1e-9, `front ${widest}`);
  const first = shockwaveAt(0);
  assert.ok(first.radius - SHOCKWAVE.wavelength / 2 >= 32, "the ring's inner edge starts beyond the 64-unit head's half width");
  assert.equal(shockwaveAt(SHOCKWAVE.durationMs).active, false);
  assert.equal(shockwaveEndRadius(), BURST_REACH_UNITS - SHOCKWAVE.wavelength / 2);
  let last = -1;
  for (let t = 0; t < SHOCKWAVE.durationMs; t += 10) { const r = shockwaveAt(t).radius; assert.ok(r >= last); last = r; }
  assert.equal(SHOCKWAVE.brightness, 1, "the ring stays colourless");
});

test("halo pulse rises fast, falls to exactly zero, and never exceeds the full halo alpha", () => {
  assert.equal(haloPulseAt(0), 0);
  assert.equal(haloPulseAt(HALO_PULSE.attackMs), 1);
  assert.ok(haloPulseAt(HALO_PULSE.attackMs + 100) < 1);
  assert.equal(haloPulseAt(haloPulseLength()), 0);
  assert.ok(HALO_PULSE.peak <= 1);
});

test("CRT interference is a smooth envelope that is zero outside the transition", () => {
  assert.equal(crtEnvelope(0), 0);
  assert.equal(crtEnvelope(CRT.durationMs), 0);
  assert.equal(crtEnvelope(CRT.durationMs + 1000), 0);
  let peak = 0, step = 0, prev = 0;
  for (let t = 0; t <= CRT.durationMs; t += 5) { const e = crtEnvelope(t); peak = Math.max(peak, e); step = Math.max(step, Math.abs(e - prev)); prev = e; }
  assert.equal(peak, 1);
  assert.ok(step < 0.2, `largest 5 ms step ${step}`);
  assert.equal(CRT.durationMs, 1420, "13.6 refinement: CRT presence 420 -> 1420 ms");
});

test("smoke atlas: 84 timeline frames, 70 visible cells, an exactly opaque window, contiguous frames, texture pages below 2048", () => {
  assert.equal(atlas.meta.timelineFrames, 84);
  assert.equal(atlas.meta.visibleFrames, 70);
  assert.equal(Object.keys(atlas.frames).length, 70);
  assert.deepEqual([atlas.meta.firstVisibleFrame, atlas.meta.lastVisibleFrame], [6, 75]);
  assert.equal(atlas.meta.evidence.emptyLeading + 70 + atlas.meta.evidence.emptyTrailingInTimeline, 84);
  for (let i = 6; i <= 75; i++) assert.ok(atlas.frames[String(i)], `frame ${i}`);
  assert.ok(atlas.meta.cover.fullyOpaqueFrom >= atlas.meta.cover.firstFrame && atlas.meta.cover.fullyOpaqueTo <= atlas.meta.cover.lastFrame);
  for (const p of atlas.meta.pages) assert.ok(p.w <= 2048 && p.h <= 2048, `${p.file} ${p.w}x${p.h}`);
  for (const f of Object.values(atlas.frames) as { page: number; x: number; y: number; w: number; h: number }[]) {
    const page = atlas.meta.pages[f.page];
    assert.ok(f.x + f.w <= page.w && f.y + f.h <= page.h, "frame inside its page");
  }
  assert.equal(atlas.meta.frameSize.w / atlas.meta.frameSize.h, 16 / 9);
});

test("smoke playback skips the empty leading frames, swaps inside the opaque window, and ends after the last visible frame", () => {
  assert.equal(smokeFrameAt(-1, meta), null);
  assert.equal(smokeFrameAt(0, meta), meta.firstVisible);
  const sch = smokeSchedule(meta);
  const frameAtSwap = smokeFrameAt(sch.swapAtMs + 1, meta)!;
  assert.ok(frameAtSwap >= meta.opaqueFrom && frameAtSwap <= meta.opaqueTo);
  assert.ok(sch.clearAtMs > sch.swapAtMs);
  assert.equal(smokeFrameAt(sch.totalMs + 1, meta), null);
  // A 50 ms clamped step per update can never jump over the opaque window (it is 18 frames wide).
  const window = ((meta.opaqueTo - meta.opaqueFrom + 1) * 1000) / SMOKE.fps;
  assert.ok(window > 50 * 4, `window ${window} ms`);
  assert.ok(sch.totalMs > 3000 && sch.totalMs < 3300, `70 frames at ${SMOKE.fps} fps = ${sch.totalMs} ms`);
  const total = SMOKE.startDelayMs + sch.totalMs;
  assert.ok(total > 3200 && total < 3400, `whole transition ${total} ms`);
});

test("reduced-motion cover: one fade in, a hold at full cover, one fade out, nothing sudden", () => {
  assert.equal(reducedCoverAlpha(0, 160, 40), 0);
  assert.equal(reducedCoverAlpha(160, 160, 40), 1);
  assert.equal(reducedCoverAlpha(190, 160, 40), 1);
  assert.equal(reducedCoverAlpha(360, 160, 40), 0);
  let step = 0, prev = 0;
  for (let t = 0; t <= 400; t += 8) { const a = reducedCoverAlpha(t, 160, 40); step = Math.max(step, Math.abs(a - prev)); prev = a; }
  assert.ok(step <= 8 / 160 + 1e-9);
});

// ---- the real session turns server events into one-shot cues ------------------------------------------------------------------------------
const player = (over: Partial<PlayerFrame> = {}): PlayerFrame => ({ slot: 0, x: 620, y: 450, charge: 4, overlit: false, overlitDeadlineTick: null, totalScore: 0, movementPoints: 0, pickupPoints: 0, penaltyPoints: 0, pickups: 0, bursts: 0, appliedSeq: -1, ...over });
const ev = (key: string, kind: PlayEvent["kind"], slot = 0, over: Partial<PlayEvent> = {}): PlayEvent => ({ key, tick: 2, kind, raw: kind, slot, otherSlot: 0, value: 0, candleId: "", ...over });
class Fake implements PlayTransport {
  readonly label = "fake"; readonly production = false;
  private f?: (x: PlayFrame) => void;
  onFrame(l: (x: PlayFrame) => void) { this.f = l; return () => undefined; }
  onConnection(_l: (x: ConnectionState) => void) { return () => undefined; }
  onInputAnswer(_l: (x: InputAnswerView) => void) { return () => undefined; }
  setIntent(_i: ControlIntent) {}
  async start() {}
  dispose() {}
  push(x: PlayFrame) { this.f?.(x); }
}
const frame = (tick: number, events: PlayEvent[], players: PlayerFrame[]): PlayFrame => ({ identity: "r", tick, tickRate: 30, roundTicks: 2700, final: false, receivedAtMs: tick * 33, players, candles: [], events, you: { slot: 0, canControl: true }, eventsComplete: true });

test("a manual and an auto Burst each give ONE release cue with the emitter's authoritative position; a repeat gives none; a press gives none", () => {
  const t = new Fake();
  const s = new PlaySession(t);
  s.intent({ moveX: 0, moveY: 0, requestBurst: true }, "change", 0);
  assert.deepEqual(s.drainCues(), [], "a key press is not a Burst");
  t.push(frame(1, [], [player(), player({ slot: 1, x: 700, y: 380, charge: 3 })]));
  t.push(frame(2, [ev("a:2:0", "BurstManual", 0, { value: 1 }), ev("a:2:1", "BurstHit", 1, { otherSlot: 0, value: 90 })], [player({ charge: 0 }), player({ slot: 1, x: 700, y: 380, charge: 1 })]));
  const first = s.drainCues();
  assert.equal(first.length, 2);
  const release = first.find((c) => c.kind === "release")!;
  assert.deepEqual(release.burst, { origin: "manual", x: 620, y: 450, tick: 2 });
  assert.equal(first.find((c) => c.kind === "hit")!.slot, 1);
  t.push(frame(2, [ev("a:2:0", "BurstManual", 0, { value: 1 }), ev("a:2:1", "BurstHit", 1, { otherSlot: 0, value: 90 })], [player({ charge: 0 }), player({ slot: 1, charge: 1 })]));
  t.push(frame(3, [ev("a:3:0", "BurstAuto", 1, { value: 0, tick: 3 })], [player({ charge: 0 }), player({ slot: 1, x: 700, y: 380, charge: 4 })]));
  const auto = s.drainCues();
  assert.equal(auto.length, 1, "the duplicate of tick 2 made no new cue");
  assert.deepEqual(auto[0]!.burst, { origin: "auto", x: 700, y: 380, tick: 3 });
  // Only a manual Burst of the local player confirms the press; the auto-Burst of another slot never does.
  assert.equal(s.hud(0).burst?.stage, "confirmed");
});

test("a new match identity forgets old events: the same keys play again", () => {
  const t = new Fake();
  const s = new PlaySession(t);
  t.push(frame(2, [ev("k", "BurstAuto", 0)], [player()]));
  assert.equal(s.drainCues().length, 1);
  t.push({ ...frame(2, [ev("k", "BurstAuto", 0)], [player()]), identity: "other" });
  assert.equal(s.drainCues().length, 1);
});
test("armed 4/4 halo sits below the Burst peak; charges 0..3 keep their scale", () => {
  assert.ok(HALO_SCALE[4] > 1 && HALO_SCALE[4] < 1 + HALO_PULSE.scaleGain && HALO_ALPHA[4] <= HALO_PULSE.peak);
  assert.deepEqual(HALO_SCALE.slice(0, 4), [1, 1, 1, 1]);
  assert.deepEqual(HALO_ALPHA.slice(0, 4), [0, 0, 0.15, 0.26]);
});
