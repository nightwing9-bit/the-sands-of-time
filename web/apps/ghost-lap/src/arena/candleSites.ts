// PREVIEW FIXTURE ONLY. The 24 candle SITES of layout "arena-v1", copied from ArenaV1.CandleSites (12 authored + their point mirrors) and
// checked against src/GhostLap/Rules/ArenaV1.cs by scripts/check-arena-descriptor.mjs.
//
// This is NOT the candle plan and never feeds gameplay or production rendering. The real game draws candles from the server frame
// (ActiveCandleIds resolved through the verified CandlePlan: MultiplayerTransport -> PlayFrame.candles). Candle ids are NOT site indices;
// the study invents its own clearly marked fixture ids and only borrows these coordinates so the placement looks like the real arena.
export interface CandleSiteUnits { readonly x: number; readonly y: number }
const AUTHORED: readonly (readonly [number, number])[] = [
  [300, 120], [250, 300], [480, 320], [640, 200], [640, 340], [400, 450],
  [620, 450], [250, 600], [480, 580], [640, 700], [560, 780], [700, 600],
];
export const ARENA_V1_CANDLE_SITES: readonly CandleSiteUnits[] = [
  ...AUTHORED.map(([x, y]) => ({ x, y })),
  ...AUTHORED.map(([x, y]) => ({ x: 1600 - x, y: 900 - y })),
];
