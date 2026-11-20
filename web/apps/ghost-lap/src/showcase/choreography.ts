import { ARENA_V1_RENDER } from "../arena/arenaV1Descriptor.ts";
import { ARENA_V1_CANDLE_SITES } from "../arena/candleSites.ts";
import { BURST_REACH_UNITS, SHOCKWAVE } from "../fx/effectPresets.ts";
import { MAX_UNITS_PER_TICK } from "../play/visualSmoothing.ts";
import type { CandleFrame, PlayEvent, PlayFrame } from "../play/playTypes.ts";
import type { ActorSlot, Charge } from "../presentation/types.ts";

// Ghost Lap's fixed-point movement and scoring constants (GameRulesV1.cs).
export const SHOWCASE_HZ = 30;
export const SHOWCASE_MS = 24_000;
const TICKS = SHOWCASE_MS * SHOWCASE_HZ / 1000;
const FIXED = 1024;
const DIAGONAL = 7241;
const HEAD_HALF = 32; // Keep the approved 64-unit artwork clear of all walls, beyond the 24-unit collider.
const COLLIDER_HALF = 24;
const PICKUP_REACH = COLLIDER_HALF + 12;
const CONTACT_REACH = COLLIDER_HALF * 2;
const PICKUP_POINTS = 25;
const BURST_PENALTY = 90;
const BURST_TICK = 218;
const DUAL_TICK = 529;
const SLOTS = [0, 1, 2, 3] as const;

interface Point { readonly x: number; readonly y: number }
const HEADING: Record<string, readonly [number, number]> = {
  N: [0, -1], NE: [1, -1], E: [1, 0], SE: [1, 1],
  S: [0, 1], SW: [-1, 1], W: [-1, 0], NW: [-1, -1],
};

// Start in motion, during a staged round. Each run of inputs is a heading followed by its duration in ticks.
const ROUTES = [
  { start: [70, 285], inputs: [
    "NE23 E6 SW6 S14 SE2 E14 SW1 S13 NE1 E22 SE11 S8 NW4 W20 SE2 E11",
    "SE2 S17 NE9 N1 NE1 N10 NW5 N38 NE7 N5 SE1 E30 SW6 S7 SW9 S4",
    "SE9 S8 NE7 E8 SE7 E6 SE1 S14 NW1 W30 SW5 S13 SE11 E6 NE1 E29",
    "NE14 NW6 N11 W17 NW15 W12 NW9 N8 NE10 N12 NE6 E17 SE9 E17 SE2 S19",
    "SW8 S15 W19 NW2 W27",
  ] },
  { start: [500, 320], inputs: [
    "E10 NE7 N8 NW5 N7 SE1 E35 SE3 E11 SW4 W9 SW3 S9 SW7 S11 SE4",
    "S8 SW6 W3 NW3 W14 NW5 N3 NW16 N7 E8 SE1 S2 W8 SW1 S7 SE3",
    "S12 SE8 E6 NE1 E4 S19 SE2 E14 NE7 N14 NE7 N2 NE3 E13 SE7 S1",
    "SE5 E1 SE1 S13 SE9 E7 NE18 N1 NW10 N9 NE4 N20 NW4 W8 SW8 W30",
    "NW1 W7 NW5 N16 NW1 W18 SW12 SE2 S17 SE12 S12 SW3 S19 SW3 W22 NW6",
    "W10 NW12 N6 NE3 N14 NW1 N16 NW13 N3",
  ] },
  { start: [420, 790], inputs: [
    "NW24 N2 NW11 N12 NE11 N3 SE4 E20 SE3 E14 SE7 S20 SW6 S7 NW7 N5",
    "NW5 N4 NW1 N7 E6 SE5 S7 SW6 S1 NW7 W1 N8 NW5 SE8 S4 NW1",
    "W8 NW2 N14 NW10 SW3 W15 S25 SE6 E10 NE2 E22 NE12 N1 NE3 N13 NE4",
    "E17 NE7 N8 NW9 N4 NW2 W15 SW12 W4 SE6 S12 SW12 S1 SW4 W13 SW2",
    "S12 E19 SE12 E2 SW1 S13 E21 NE12 N3 NW2 N11 NE5 N16 NE4 N5 NE3",
    "E5 SE7 E8 SW5 S16 SW11 S2 NW1 W11 SW4 S9 SW9",
  ] },
  { start: [1410, 750], inputs: [
    "SW4 W8 NE7 N13 NE15 N3 NW10 N10 SW1 W3 NW10 N10 NW1 W23 SW20 S4",
    "SE8 S9 SE18 E3 NE2 E12 SE5 E14 SE1 S17 SW1 W13 SW8 W24 NW8 W4",
    "NE1 N9 NE8 E2 NE1 N17 NE8 N5 E16 NE5 N20 NE2 E12 SE12 E3 SW6",
    "S35 SW14 S1 SW12 W3 N17 NW9 N4 NE7 N11 NW1 W15 SW1 W16 SE1 S17",
    "SE2 S14 SE6 S8 SE4 E24 NE10 E7 NE12 E5 NW5 N17 NE5 N22",
  ] },
] as const;

// Authored activation ticks at the existing sites. Negative ticks are candles already active on entry.
const CANDLE_STARTS = [
  [0, -34], [1, -25], [4, -16], [12, -11], [3, -3], [7, 0], [13, 10], [5, 29], [19, 41],
  [2, 52], [21, 74], [9, 109], [10, 118], [20, 130], [15, 149], [6, 153], [11, 171],
  [3, 210], [2, 214], [23, 269], [8, 281], [16, 348],
  [14, 350], [10, 359], [22, 391], [12, 438], [5, 453], [17, 479], [9, 494],
  [4, 544], [23, 546], [12, 610], [20, 612], [7, 651], [14, 654], [15, 674],
] as const;

function requireBeat(condition: unknown, message: string): asserts condition {
  if (!condition) throw new Error(`Showcase choreography: ${message}`);
}

function overlaps(a: Point, b: Point, reach: number): boolean {
  return Math.abs(a.x - b.x) < reach && Math.abs(a.y - b.y) < reach;
}

function withinBurst(a: Point, b: Point): boolean {
  return (a.x - b.x) ** 2 + (a.y - b.y) ** 2 <= BURST_REACH_UNITS ** 2;
}

/** Intersect the interpolation segment with the wall expanded by the full visual head's half-width. */
function clearStep(from: Point, to: Point): boolean {
  const { width, height, obstacles } = ARENA_V1_RENDER;
  if (to.x < HEAD_HALF || to.x > width - HEAD_HALF || to.y < HEAD_HALF || to.y > height - HEAD_HALF) return false;
  return obstacles.every((wall) => {
    let enter = 0;
    let exit = 1;
    for (const [a, b, low, high] of [
      [from.x, to.x, wall.x0 - HEAD_HALF, wall.x1 + HEAD_HALF],
      [from.y, to.y, wall.y0 - HEAD_HALF, wall.y1 + HEAD_HALF],
    ] as const) {
      if (a === b) {
        if (a <= low || a >= high) return true;
      } else {
        const t0 = (low - a) / (b - a), t1 = (high - a) / (b - a);
        enter = Math.max(enter, Math.min(t0, t1));
        exit = Math.min(exit, Math.max(t0, t1));
      }
    }
    return enter >= exit;
  });
}

function movement({ start, inputs }: typeof ROUTES[number], slot: ActorSlot) {
  let [x, y]: readonly [number, number] = start;
  const positions: Point[] = [{ x, y }];
  const movementPoints = [0];
  let distance = 0;
  requireBeat(clearStep(positions[0]!, positions[0]!), `P${slot + 1} has an illegal opening position`);
  for (const run of inputs.join(" ").split(" ")) {
    const [, heading, duration] = /^([A-Z]+)(\d+)$/.exec(run)!;
    const [dx, dy] = HEADING[heading!]!;
    const step = dx && dy ? DIAGONAL : MAX_UNITS_PER_TICK * FIXED;
    for (let i = 0; i < Number(duration) && positions.length <= TICKS; i++) {
      const next = { x: x + dx * step / FIXED, y: y + dy * step / FIXED };
      requireBeat(clearStep({ x, y }, next), `P${slot + 1} crosses a wall or edge at tick ${positions.length}`);
      positions.push(next);
      distance += Math.floor(Math.hypot(dx * step, dy * step));
      movementPoints.push(Math.floor(distance / (16 * FIXED)));
      ({ x, y } = next);
    }
  }
  requireBeat(positions.length === TICKS + 1, `P${slot + 1} must move through all ${TICKS} ticks`);
  return { positions, movementPoints };
}

type State = { charge: number; deadline: number; pickups: number; penalty: number; bursts: number };
type ScriptFrame = Pick<PlayFrame, "players" | "candles" | "events">;

/** Apply the relevant ghost-mp-v2 phases to authored inputs. Candle timing is staged and contested pickups are rejected. */
export function buildShowcase(): readonly ScriptFrame[] {
  const paths = ROUTES.map((route, i) => movement(route, SLOTS[i]!));
  const states: State[] = SLOTS.map(() => ({ charge: 0, deadline: 0, pickups: 0, penalty: 0, bursts: 0 }));
  const pool = CANDLE_STARTS.map(([site, start], i) => ({
    start, collected: false, id: `showcase-candle-${i}`, ...ARENA_V1_CANDLE_SITES[site]!,
  }));
  const contactReady = new Map<number, number>();
  const frames: ScriptFrame[] = [];

  for (let tick = 0; tick <= TICKS; tick++) {
    const events: PlayEvent[] = [];
    const candles: CandleFrame[] = [];
    const points = paths.map((p) => p.positions[tick]!);
    const move = paths.map((p) => p.movementPoints[tick]!);
    const total = (slot: number) => Math.max(0, move[slot]! + states[slot]!.pickups * PICKUP_POINTS - states[slot]!.penalty);
    const emit = (kind: PlayEvent["kind"], slot: number, value = 0, otherSlot = 0, candleId = "") => {
      events.push({ key: `${tick}:${events.length}`, tick, kind, raw: kind, slot, value, otherSlot, candleId });
    };
    const drain = (slot: number, amount: number) => {
      const state = states[slot]!;
      const before = state.charge;
      state.charge = Math.max(0, before - amount);
      if (before === 4) {
        state.deadline = 0;
        emit("OverlitCancelled", slot);
      }
      return before - state.charge;
    };
    for (const candle of pool) {
      if (candle.collected || tick < candle.start - 30 || tick >= candle.start + 210) continue;
      const active = tick >= candle.start;
      const claimants = active ? SLOTS.filter((slot) => overlaps(points[slot]!, candle, PICKUP_REACH)) : [];
      requireBeat(claimants.length < 2, `contested candle ${candle.id} at tick ${tick}`);
      const slot = claimants[0];
      if (slot === undefined) {
        candles.push({ id: candle.id, x: candle.x, y: candle.y, phase: active ? "active" : "telegraph" });
        continue;
      }
      requireBeat(tick > 0, `candle ${candle.id} picked up before motion`);
      candle.collected = true;
      const state = states[slot]!;
      state.pickups++;
      emit("CandleCollected", slot, PICKUP_POINTS, 0, candle.id);
      if (state.charge < 4 && ++state.charge === 4) {
        state.deadline = tick + 240;
        emit("OverlitStarted", slot, state.deadline);
      }
    }

    // Contacts compare the same pre-drain charge snapshot for every pair.
    const charges = states.map((p) => p.charge);
    for (const a of SLOTS) {
      for (const b of SLOTS) {
        const pair = a * SLOTS.length + b;
        if (a >= b || (contactReady.get(pair) ?? 0) > tick || charges[a] === charges[b]
          || !overlaps(points[a]!, points[b]!, CONTACT_REACH)) continue;
        const [weak, strong] = charges[a]! < charges[b]! ? [a, b] : [b, a];
        emit("ContactDrain", weak, drain(weak, 1), strong);
        contactReady.set(pair, tick + 45);
      }
    }

    // Both manual intents spend their charge before either wave resolves (ghost-mp-v2).
    const owners = tick === BURST_TICK ? [0] : tick === DUAL_TICK ? [0, 3] : [];
    for (const slot of owners) {
      const state = states[slot]!;
      requireBeat(state.charge === 4, `P${slot + 1} has ${state.charge}/4 at Burst tick ${tick}`);
      state.charge = state.deadline = 0;
      state.bursts++;
    }
    requireBeat(states.every((p) => !p.deadline || p.deadline > tick), `unscripted auto-Burst at tick ${tick}`);
    for (const owner of owners) {
      const targets = SLOTS.filter((slot) => slot !== owner && withinBurst(points[slot]!, points[owner]!));
      if (tick === BURST_TICK) requireBeat(targets.join() === "1,2", "signature must hit P2 and P3 and miss P4");
      emit("BurstManual", owner, targets.length);
      for (const slot of targets) {
        const penalty = Math.min(BURST_PENALTY, total(slot));
        emit("BurstHit", slot, penalty, owner);
        drain(slot, 2);
        states[slot]!.penalty += penalty;
      }
    }

    frames.push({
      candles, events,
      players: SLOTS.map((slot) => {
        const p = states[slot]!;
        return {
          ...points[slot]!, slot, charge: p.charge as Charge, totalScore: total(slot), overlit: p.charge === 4,
          overlitDeadlineTick: p.deadline || null, movementPoints: move[slot]!, pickupPoints: p.pickups * PICKUP_POINTS,
          penaltyPoints: p.penalty, pickups: p.pickups, bursts: p.bursts, appliedSeq: null,
        };
      }),
    });
  }
  requireBeat(frames.slice(270, 450).some((f) => f.events.some((e) => e.kind === "ContactDrain" && e.slot === 1
    && e.otherSlot === 3 && e.value === 1)), "P4 must drain one charge from P2 in the chase");
  const waveEnd = BURST_TICK + Math.ceil(SHOCKWAVE.durationMs * SHOWCASE_HZ / 1000);
  requireBeat(frames.slice(BURST_TICK, waveEnd + 1).every(({ players }) => {
    const pink = players[1]!, yellow = players[2]!;
    return Math.hypot(pink.x - yellow.x, pink.y - yellow.y) > 2 * HEAD_HALF;
  }), "both hit faces must stay visible during the signature wave");
  const [orange, , , green] = frames[DUAL_TICK]!.players;
  requireBeat(Math.hypot(orange!.x - green!.x, orange!.y - green!.y) < 2 * BURST_REACH_UNITS, "dual waves must overlap");
  return frames;
}
