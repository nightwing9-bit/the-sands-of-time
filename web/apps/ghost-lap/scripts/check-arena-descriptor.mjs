// Source-backed coordinate check: the render descriptor's nine obstacles must equal ArenaV1.ObstacleUnits in src/GhostLap/Rules/ArenaV1.cs.
// Run from web/apps/ghost-lap:  node scripts/check-arena-descriptor.mjs   (Node 22+ strips TypeScript types on import)
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { ARENA_V1_RENDER } from "../src/arena/arenaV1Descriptor.ts";

const cs = readFileSync(fileURLToPath(new URL("../../../../src/GhostLap/Rules/ArenaV1.cs", import.meta.url)), "utf8");
const block = /ObstacleUnits\s*=\s*\[([\s\S]*?)\];/.exec(cs)?.[1];
if (!block) throw new Error("ObstacleUnits not found in ArenaV1.cs");
const source = [...block.matchAll(/\(\s*(\d+),\s*(\d+),\s*(\d+),\s*(\d+)\s*\)/g)].map((m) => m.slice(1).map(Number));
const mine = ARENA_V1_RENDER.obstacles.map((r) => [r.x0, r.y0, r.x1, r.y1]);
const world = /WorldWidthUnits\s*=\s*(\d+)/.exec(readFileSync(fileURLToPath(new URL("../../../../src/GhostLap/Rules/GameRulesV1.cs", import.meta.url)), "utf8"))?.[1];
if (JSON.stringify(source) !== JSON.stringify(mine)) {
  console.error("MISMATCH\n source:", JSON.stringify(source), "\n render:", JSON.stringify(mine));
  process.exit(1);
}
// Candle sites (13.5 preview fixture): the 12 authored sites + point mirrors must equal ArenaV1.CandleSites.
import { ARENA_V1_CANDLE_SITES } from "../src/arena/candleSites.ts";
const siteBlock = /SiteHalfUnits\s*=\s*\[([\s\S]*?)\];/.exec(cs)?.[1];
if (!siteBlock) throw new Error("SiteHalfUnits not found in ArenaV1.cs");
const authored = [...siteBlock.matchAll(/\(\s*(\d+),\s*(\d+)\s*\)/g)].map((m) => [Number(m[1]), Number(m[2])]);
const expectedSites = [...authored, ...authored.map(([x, y]) => [1600 - x, 900 - y])];
const mySites = ARENA_V1_CANDLE_SITES.map((s) => [s.x, s.y]);
if (JSON.stringify(expectedSites) !== JSON.stringify(mySites)) {
  console.error("CANDLE SITE MISMATCH\n source:", JSON.stringify(expectedSites), "\n fixture:", JSON.stringify(mySites));
  process.exit(1);
}
console.log(`OK: ${mySites.length} candle sites identical to ArenaV1.CandleSites (fixture coordinates only)`);
console.log(`OK: ${source.length} obstacles identical to ArenaV1.cs; world width in rules = ${world}, descriptor = ${ARENA_V1_RENDER.width}`);
