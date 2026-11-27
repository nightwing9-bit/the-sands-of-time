# 05 — Pixi scene, snowy arena, grey walls, candle lighting

## 1. Arena and camera (CONFIRMED intentions, VERIFY dimensions)

- Source ground: `game-assets/backgrounds/snow_arena.webp` (absolute user path in `01`), pale icy snow with dramatic massive monster-shadow silhouette in lower area. The shadow is **baked background atmosphere**, not a real enemy, moving NPC, collision zone or dynamic lighting source.
- World is one fixed whole-arena **16:9** presentation. Use `object-fit: contain` / equivalent Pixi aspect-preserving layout with letterboxing. Never stretch snow image horizontally or vertically to adjust for aspect ratio. Do not crop important arena boundaries for 'cinematic' effect.
- Runtime image recorded as approximately **1672×941** pixels and lossless WebP from selected PNG. It is already suitable; no fake 4K upscaling or over-sharpening. High-DPI canvas resolution can be capped by profiling rather than inflating source texture.
- **VERIFY server coordinates** (world width/height, origin, Y-up/down, collision transforms). One central `WorldTransform` maps server positions and wall geometry to rendered space. Don't assume server coordinate extent equals 1672×941 or the 1600×900 UI mockup.
- Keep snow texture static and loaded once. All other objects are **separate scene sprites**, not part of that image.

## 2. Wall assets (CONFIRMED Kenney source; approved grey conversion)

Source: `$GAME_ASSETS/kenney_pixel-platformer-blocks/` — Kenney Pixel Platformer Blocks CC0, 18×18 source tile pattern, initially brown/orange. User confirmed re-colour to stone grey; approved no longer to use AI-generated replacement walls.

**Preparation:** inspect actual downloaded transparent PNG/atlas and tile variants; choose appropriate rectangular top, corner, end cap/edge assets; derive a **grey atlas** once from source without painting over edge highlights and shade. Suggested operation on RGB of nontransparent pixels: luminance `Y=0.2126R+0.7152G+0.0722B`; map to a neutral-to-slightly-blue-grey range while preserving contrast and alpha. Aim midgrey comparable to mockups, e.g. mid `#758090`, highlight `#9da8b4`, edge `#434e60` (DIRECTOR BASELINE; tune against snow). No hard uniform gray overpaint; protect texture/bevel.

Prefer offline preprocessing (a single generated runtime texture) over applying `ColorMatrixFilter` separately to every wall. Keep source pack untouched and generated file in `public/game-assets/generated/grey-walls.*`. The agent can create a script after inspecting real sheet coordinates and alpha. Runtime filter is acceptable **only for prototyping**; never let filter pass become a performance tax for 30 identical static tiles.

**Placement:** receive an authoritative map/maze specification from backend or main agent. Rectangles/segments/tiles/colliders come from backend, not image detection. For each wall piece choose repeated center tile + end caps + corners based on actual lengths and orientations. Use 9-slice/tiling for long continuous segments only if it preserves texture seam quality and correct widths; avoid random stretched pixels. Visual segments may be more numerous than collider segments, but must represent the SAME playable obstruction. Build `WallRenderer.updateFromGeometry(...)` to reflect verified layout. If maze layout fixed, use its real data; if dynamic, subscribe to its actual configuration; **never invent a symmetrical maze from the user's mockup**. Reference images are not geometry truth.

**Drawing:** walls above snow, below players/candles by default; if game requires character under/behind wall projection, add separate small foreground wall lip layer, not fake collisions. Soft contact shadow allowed to lift walls from pale snow. Avoid overly heavy outer glow, random procedural wall thickness or giant 3D extrusion. Pixel-block art and painterly snow differ in style; render/test at game scale and prefer smooth coherent grey material over raw brown.

## 3. Candles — same asset, two lighting presentations

**Asset:** `$GAME_ASSETS/haunted_candle.png`, orange-flame variant from ParticleFX. Use `AnimatedSprite` / parsed frame atlas with actual sequence and published nominal 90 frames at 30fps / 3 seconds (**VERIFY actual sheet**). Each candle shares decoded textures; create cheap independent instances, optionally phase-offset for non-synchronized flicker without modifying source sequence. Candle appears/vanishes **only when backend candle state says so**. Play `candle_eat.wav` once on verified collection.

**Daytime:** same flame loop; extremely faint *warm yellow* light pool, only a subtle focus. Daylight snow still visually dominates. **Nighttime:** same flame loop and no change to candle physics or flicker pattern; larger, softer, warmer diffused contribution that gently colours adjacent snow, grey walls, maybe just a little living-head reflection. Not a spotlight/torch/tower; no hard circular edge.

### Rendering solution (DIRECTOR BASELINE)

- One **shared light accumulation layer**, not a heavy filter on each candle. Low-resolution `RenderTexture` or grouped radial sprites combine candle light pools into an ambient lightmap. Can use `pixi-filters/simple-lightmap` with verified API or an equivalent lightweight custom compositing pass if implementation is simpler. The lightmap is **not self-generating**: populate it from candle positions/radii/strengths and reuse resources.
- Environment (`snow + walls`) first receives night colour grade; warm light is composited onto these environmental layers. The living player and historical ghost retain their intended colours. A tiny additive warm halo may sit around each candle sprite. UI is separate React above everything.
- For a room with many candles, use shared textures and one batch/composite rather than a nested full-world filter for each. Cull offscreen candles; update lightmap only when candle set/mode changes or flicker needs gentle change — do not allocate new render textures per frame.
- Ambient night remains legible *everywhere* even outside lights. Darkened snow should preserve giant monster shadow; not black screen. Do not add vignette or simulated physical shadow casting by walls.
- Candle radius derives from appearance tuning and viewport scale, **not authoritative pickup radius**. Never allow light radius to define candle collision.

### Quality presets, configurable (not physics)

| Setting | Day | Night |
|---|---|---|
| Main animated flame | same orange 30fps nominal | same exact orange loop |
| Light tint | warm amber/yellow | same warm amber/yellow |
| Environment brightness | original snow/day | graded cool dark snowy world |
| Light contribution | barely visible | clearly visible but diffused and weak |
| Light edge | fully soft | fully soft |
| Lit objects | nearby snow and walls primarily | nearby snow/walls; living reflection optional |
| Ghost | absent in regular day | **not a light source** |

Set numerical strength/radius only after screenshot test and backend dimensions; keep in a theme preset module (`LightingPresets.ts`). Don't add spark showers, procedural candle smoke or flame recolouring unless separately approved.

## 4. Layer priority / blending

Recommended scene hierarchy:

```text
WorldContainer (sized with authoritative world transform)
├─ BackgroundSnow (1 sprite)
├─ EnvironmentWalls (grey tile sprites)
├─ EnvironmentNightGrade (applies ONLY environmental composite)
├─ CandleLightmap (warm ambient light over graded environment)
├─ GroundDecorativeShadows (player contact shadows only)
├─ Candles (animated orange sprites, distinct light source sprites)
├─ GhostBehindPlayers (cyan ghost with evil face, alpha + glow)
├─ LivingChargeHalos (behind corresponding living heads)
├─ LivingPlayerHeads (crisp shapes + expressions)
├─ TransientWorldFx (Burst world distortion target only)
└─ ScreenTransitions (CRT during transition, smoke overlay)
```

Actual `Container` layering may need an effect-specific sub-`RenderTexture`: a ShockwaveFilter should visibly distort the intended world portion **without distorting DOM UI**, and a `ColorMatrixFilter` on the environment container should not unintentionally darken a candle or head. Verify clip bounds and world-to-filter coordinates.

## 5. Arena visual acceptance

Day: snow remains nearly white and monster shadow visibly atmospheric; grey walls align gameplay collision precisely; four vivid heads readable at speed; orange candles noticeable but understated. Night: snow clearly darker and cool, walls visible, monster shadow retained, candles create warm pools with gradual falloff, living heads unaffected by night grade, ghost readable; no visible flashlight edges or full-screen perf penalty.
