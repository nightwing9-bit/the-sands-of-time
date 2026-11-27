// PREVIEW FIXTURE (development only): a scripted pair of routes so the study can show the ghost at EXACT overlap and then at a genuine later
// divergence. It is NOT a saved history, NOT a recorded Original, and it cannot certify live Challenge play. The routes are plain waypoint
// lists walked at the game's cardinal speed (10 units per 30 Hz tick, GameRulesV1) through open lanes between the nine ArenaV1 walls, with
// every lane at least 24 units from a wall. The real ghost reads a verified saved trace (GET /originals/{id}/trace) instead.
//
// Pure functions, no Pixi: shared by the study screen and the unit test.
export interface Waypoint { readonly x: number; readonly y: number }
export const FIXTURE_UNITS_PER_TICK = 10;
export const FIXTURE_TICKS = 360;

const lap: readonly Waypoint[] = [
  { x: 200, y: 450 }, { x: 200, y: 300 }, { x: 680, y: 300 }, { x: 680, y: 600 }, { x: 200, y: 600 }, { x: 200, y: 450 },
];
const cycle = (laps: number): Waypoint[] => Array.from({ length: laps }, (_, i) => (i === 0 ? lap : lap.slice(1))).flat();

/** The "original run": the lap, repeated. */
export const GHOST_ROUTE: readonly Waypoint[] = cycle(3);
/** The "living run": the same first lap and the start of the second (so the two heads overlap exactly), then a real change of decision at the upper-middle corner. */
export const LIVING_ROUTE: readonly Waypoint[] = [
  ...lap, lap[1]!, lap[2]!,
  { x: 680, y: 110 }, { x: 1000, y: 110 }, { x: 1000, y: 450 },
];
/** First tick at which the living route differs from the ghost route (computed, not claimed). */
export function divergenceTick(): number {
  for (let t = 0; t <= FIXTURE_TICKS; t++) {
    const a = routeAt(GHOST_ROUTE, t), b = routeAt(LIVING_ROUTE, t);
    if (Math.abs(a.x - b.x) > 0.001 || Math.abs(a.y - b.y) > 0.001) return t;
  }
  return -1;
}

/** Position and direction after `tick` ticks along a polyline. Holds at the last point. Direction is the unit step (for gaze). */
export function routeAt(points: readonly Waypoint[], tick: number): { x: number; y: number; dx: -1 | 0 | 1; dy: -1 | 0 | 1 } {
  let remaining = Math.max(0, tick) * FIXTURE_UNITS_PER_TICK;
  for (let i = 1; i < points.length; i++) {
    const a = points[i - 1]!, b = points[i]!;
    const len = Math.abs(b.x - a.x) + Math.abs(b.y - a.y); // routes are axis-aligned
    const dx = Math.sign(b.x - a.x) as -1 | 0 | 1, dy = Math.sign(b.y - a.y) as -1 | 0 | 1;
    if (remaining <= len) return { x: a.x + dx * remaining, y: a.y + dy * remaining, dx, dy };
    remaining -= len;
  }
  const last = points[points.length - 1]!;
  return { x: last.x, y: last.y, dx: 0, dy: 0 };
}
