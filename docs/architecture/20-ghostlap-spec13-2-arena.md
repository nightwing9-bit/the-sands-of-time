# Spec 13.2: the snowy arena (HUMAN REVIEW PENDING)

Art preview only. It is **not** runtime integration: walls are drawn from a static `arena-v1` render descriptor, not from a live production geometry feed.

## Try it
In `web/apps/ghost-lap` run `npx vite --port 5174`, then open `http://localhost:5174/?screen=arenaStudy` (dev only). The preview strip has "Geometry overlay" (magenta = the nine ArenaV1 rectangles, cyan = the 1600x900 world frame).

## What was built
- `src/stage/ArenaWorld.ts`: one long-lived Pixi v8 Application. Layers `ground / walls / actors / debug` under one `world` container. Transform: scale = min(w/1600, h/900), centred (contain/letterbox). Snow (1672x941) is contained, not stretched, inside the world. Frame textures are destroyed and the two source textures unloaded on `destroy()`; React StrictMode double mount ran with no console errors.
- `src/stage/WorldLayer.tsx`: mounts it once for the stage; the menu shows snow only, `arenaStudy` and `historyStudy` show walls.
- `src/arena/arenaV1Descriptor.ts`: static descriptor, `authority: "static-fixture-not-production"`. `scripts/check-arena-descriptor.mjs` compares it with `ArenaV1.ObstacleUnits` in C#: OK, 9 identical.
- `scripts/build-wall-atlas.py` writes `public/game-assets/generated/grey-walls.png` and `src/config/greyWallsAtlas.json`. Source is the pack's own **Rock** material (the pack's `Stone` folder is brown, `Marble` near-white, `Rock` blue-grey). Only four source colours exist; they are remapped one-to-one to a deeper slate, so bevel, alpha and pixel structure are untouched. 2x nearest upscale with edge-extruded gutters. Source files are read only.
- Walls: each rectangle becomes a whole number of 20-unit cells (all nine are exact multiples): corner, edge and interior tiles, deterministic speckled variant, soft contact shadow from stacked translucent rounded rects. No filters or shaders.

## Needed from the main agent (geometry gate, still OPEN)
1. A read projection of `ArenaV1`: `{ layout: "arena-v1", width: 1600, height: 900, obstacles: [{x0,y0,x1,y1}] }` in world units (fixed-point already divided by 1024), carrying the same layout identity/fingerprint (`MpRulesProfile.LayoutFingerprint`) as the room or solo run, so the client can reject a mismatch.
2. For 13.4/13.5 candles: the seed plus a `CandlePlan` projection (`CandleSpec` Id, Site x/y, TelegraphTick, ActivateTick, ExpireTick), or the 24 `CandleSites` plus seed with a verified client twin.
Until then the descriptor is a fixture and the preview tag says so.

## Not done / not claimed
No GPU/FPS measurement (screenshots used software GL). Not checked in Firefox/Safari. Walls do not yet carry the night grade (13.5).
