// STATIC RENDER DESCRIPTOR for layout "arena-v1". NOT PRODUCTION AUTHORITY.
// Copied from ArenaV1.cs and checked by scripts/check-arena-descriptor.mjs. Preview scenes use it as a fixture.
// Production multiplayer validates every rectangle against GetArena and compares its fingerprint with the sealed round before play.
// The server projection remains authority; this cached render descriptor is never used to simulate collisions.
export interface ArenaObstacle { readonly x0: number; readonly y0: number; readonly x1: number; readonly y1: number }
export interface ArenaRenderDescriptor {
  /** Explicit layout identity, compared with the identity a real feed would carry (solo: ghost-v1 / arena-v1; MP: ghost-mp-v2 / arena-v1). */
  readonly layout: "arena-v1";
  readonly authority: "static-fixture-not-production";
  /** World units. Origin top left, X right, Y down. */
  readonly width: 1600;
  readonly height: 900;
  /** Half-open rectangles in world units. */
  readonly obstacles: readonly ArenaObstacle[];
}
const o = (x0: number, y0: number, x1: number, y1: number): ArenaObstacle => ({ x0, y0, x1, y1 });
export const ARENA_V1_RENDER: ArenaRenderDescriptor = {
  layout: "arena-v1",
  authority: "static-fixture-not-production",
  width: 1600,
  height: 900,
  obstacles: [
    o(400, 200, 560, 240), // top-left bar
    o(1040, 200, 1200, 240), // top-right bar
    o(400, 660, 560, 700), // bottom-left bar
    o(1040, 660, 1200, 700), // bottom-right bar
    o(300, 380, 340, 520), // left post
    o(1260, 380, 1300, 520), // right post
    o(760, 410, 840, 490), // centre block
    o(780, 160, 820, 280), // top stub
    o(780, 620, 820, 740), // bottom stub
  ],
};
