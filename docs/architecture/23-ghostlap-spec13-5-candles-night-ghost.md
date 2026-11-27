# Spec 13.5: candles and the historical ghost in a day/night world

**CREATIVE IMPLEMENTATION DONE · FOCUSED CORRECTNESS GATE PASSED · HUMAN REVIEW PENDING · TECHNICAL ACCEPTANCE PENDING (live ghost and Challenge night play).** 19 November 2026.
Existing `web/apps/ghost-lap` only. No Marrow, rules, simulation, scoring, collision, Burst, record, replay, authorization or fork change. No mobile, no new sound, no smoke sheet (13.6), no ghost body blur or trail.

## Try it

Vite is the existing dev server (`npm run dev -w @sands/ghost-lap` from `web/`, port 5174, hot reload).

* **Night study (preview fixtures):** `http://localhost:5174/?screen=nightStudy`, or the "Night study" button in the preview strip.
* **Real candles in real play:** `http://localhost:5174/?screen=dayPlay` against the paired host (steps in doc 22). The two production candle states (flame, telegraph ring) now use the final art.

## What exists

| Piece | File | Notes |
|---|---|---|
| Candle atlas build | `scripts/build-candle-atlas.py` | Reads `design-inputs/game-assets/haunted_candle.png` (never modified, SHA-256 recorded). Writes `public/game-assets/generated/candle-flame.png` (2240×2016, 1.4 MB) and `src/config/candleAtlas.json`. |
| Textures | `src/candles/CandleAssets.ts` | One atlas, 90 shared frame textures, same unload-race-safe loader as characters. The 4130×3501 master is never served. |
| Timing (pure) | `src/candles/candleTiming.ts` | Frame at time, per-candle phase offset from the opaque id, pool gain. |
| Candle field | `src/candles/CandleField.ts` | Flame sprites + warm pools for a server candle list. Active = flame + pool. Telegraph = static ring only. No Filter, no per-frame allocation. |
| Lighting presets | `src/lighting/lightingPresets.ts` | Day/night tint, pool radius/colour/blend, candle scale, telegraph ring, ghost palette. One module, not twenty sliders. |
| World | `src/stage/ArenaWorld.ts` | New `environment` container (ground + walls) with `setEnvironment('day'|'night')` (multiply tint), new `lights` layer, generated shared radial light texture, `renderer` getter. |
| Ghost | `src/ghost/SpectralGhost.ts` | Same `CharacterView` (kind `ghost`, permanent evil face) baked once through one spectral shader + edge into a texture; `GhostView` is a plain Sprite with `eventMode none`. |
| Play wiring | `src/play/PlayScene.ts`, `playTypes.ts` (`GhostFrame`, optional `PlayFrame.ghost`), `PlaySession.localTauMs`, `PlayScreen.tsx` | Candles use the new field. A frame with `ghost` draws the ghost beneath the heads through the same ease as the local head, so an exact overlap stays exact. No transport sets `ghost` yet. |
| Study | `src/screens/NightStudy.tsx`, `src/ghost/previewFixture.ts`, `src/arena/candleSites.ts` | Day/night toggle, loop choice, backwards play, 24 sites, four heads, charge, scripted routes. All marked PREVIEW FIXTURE. |
| Source check | `scripts/check-arena-descriptor.mjs` | Now also asserts the 24 fixture sites equal `ArenaV1.CandleSites`. |

Layer order (bottom to top): environment (snow, walls; the ONLY tinted container) → lights → candles → ghost → living heads → debug. React HUD is DOM and untouched.

## Candle frame boundaries and order: evidence and decision

No exporter manifest exists. The asset audit left boundaries and order unconfirmed. Measured here, recorded in `candleAtlas.json` `meta.evidence`:

* **Grid (fact):** alpha occupancy shows exactly 10 columns × 9 rows. Each content square is 360×360 at `(26 + 413·col, 17 + 389·row)`, pitch 413×389. All 90 are non-empty. The equal-subdivision guess from the audit was close; the real content boxes are now known.
* **Order (evidence, derived judgement):** reading order (left to right, top to bottom) has a mean neighbour distance of 1.896; column order 11.507; random pairs 12.914. 74 of 89 frames have their reading-order successor among their two nearest frames. Frame 89 closes to frame 0 at 0.829, below the mean step. The one large jump is frame 38 → 39 (10.8): a one-frame flash onset followed by a slow decay, which fixes the playing direction as reading order. **Direction and 30 fps remain a judgement for a human**; "Play backwards" is in the study to compare. 30 fps / 3 s is the skill's nominal description, not export evidence.
* **Sheet content (fact, visible in the study):** frames 0 to 37 and 62 to 89 are an ordinary flicker with a breathing dip (frames 63 to 73 nearly die). **Frames 38 to 61 are a large white-pink flash**, up to 9.5× the flame mass. In day it reads as a pink-red orb, not an orange candle.
* **Decision:** two loops are shipped in the metadata. `steady` (66 frames, flash run skipped, join step 3.96 versus mean 1.9) is the default. `source` (all 90) is selectable in the study. This is a presentation choice, provisional until a human judges it. The master is untouched either way.

### Derived frames (what changed from the master, and why)

The master is an additive-style effect sheet: every frame has a wide baked glow of nearly constant brown RGB (about 123,79,34) at varying alpha. Normal-blended on snow it is a dirty smudge, and additive on night it greys out. So the atlas keeps the flame and separates the light:

* Glow keyed out by luminance: `alpha *= smoothstep(0.40, 0.70, luminance)`. The flame body, orange rim and spark curl remain at their original colours and positions.
* Window 320×320 around the flame, circular edge fade over the outer 20 percent so every cell border is exactly transparent (asserted), 0.7× Lanczos on premultiplied colour (no dark fringe), 224 px cells, same frame index as the master's row-major index.
* Anchor `(112.0, 114.8)` in cell pixels is the flame heart (it sits on the candle site). Visual size: 0.36 world units per source pixel, so the flame is about 36 units tall (about half a head).
* A per-frame `lightGain` (flame luminous mass / median, clamped 0.35 to 2.2) couples the warm pool to the same frame, softened by a 0.5 exponent and capped at 1.4.

## Day and night

* **Night grade** is `environmentTint = 0x566a94` multiplied onto snow and walls only. It costs nothing per frame, keeps all ratios (the monster shadow survives), and avoids `ColorMatrixFilter.night()` (green night vision). Heads, flames, the ghost and the HUD are never touched.
* **Warm light:** one shared generated radial texture, one sprite per active candle. Day: normal blend, `#ffb347` at alpha 0.20, radius 64. Night: additive, colour = warm white minus the tint, alpha 0.95, radius 112. Same flames, same loop in both. Light radius is presentation; it never defines pickup range.
* **No perpetual filters.** There is no filter pass in the scene. The only shader (the ghost) runs once per slot/size at bake time.

## Ghost

Same actual head and permanent evil face as the living one. Luminance maps dark features to `#1d516a`, midtones to translucent cyan `#62b9c5`, highlights to `#e8ffff`; body alpha 0.66; outer edge `#83e4eb` plus a slightly brighter inner lip. No blur, no trail, no shimmer, no shadow, not lit, no gameplay hooks. Verified for all four slots in the study (same cold palette, silhouettes still distinguishable, evil brows readable). The ghost is drawn beneath the living head: at exact overlap only a thin cyan edge remains.

## Real data used and not used

* **Real:** candle frames from the real sheet; in Day play the candles are the server's `VisibleCandles` (active and telegraph phases from the verified plan, ids opaque). Verified against the real production hub with two guests (see Checks).
* **Opaque ids:** the flicker phase is a hash of the candle id for de-synchronisation only. It never reads a site number.
* **Ghost pickups:** nothing here consumes a ghost's collected candles. The ghost renders position only.
* **Fixtures (all labelled PREVIEW FIXTURE in the study):** candle placement uses the 24 ArenaV1 site coordinates with made-up `fixture-candle-N` ids; both routes are scripted lanes at the game's 10 units per tick, checked to stay 24+ units from all nine walls, with an exact overlap up to tick 220 and a real divergence after. They demonstrate composition only. They cannot certify Challenge play and are not a saved history.

## Pending and dependencies (exact)

| # | Need | Evidence | Required behaviour | Smallest success check | Creative slice can continue? |
|---|---|---|---|---|---|
| 1 | **Verified saved trace into `PlayFrame.ghost`** | `PlayFrame.ghost` exists but no transport sets it. Solo Challenge/Fork production transport is not integrated (doc 22 remaining work; `SoloRunRegistry.PushFrame` Original frames are thin; no `/ghost-lap/solo/*` proxy) | A Challenge adapter maps a saved Original's trace tick to `{slot (source MascotId), x, y}` for the current tick, plus `night` scene selection | A Challenge started from a saved Original shows the ghost at the Original's recorded position each tick; source and ghost head match | Yes (study + PlayScene path done) |
| 2 | **Real candle plan at night** | Candles already arrive via `VisibleCandles` for multiplayer; Challenge/Fork frames must carry `ActiveCandleIds` through the plan (contract map A2, A6) | Same list shape as Day play | A night Challenge shows the same flames the server lists | Yes |
| 3 | **Animation metadata** | No exporter manifest (audit B6). Order is derived, rate nominal | Owner confirms order/direction/30 fps, or a human accepts the derived order after review | User judgement of the study, or the export manifest | Yes (default `steady`, `source` kept) |
| 4 | **Candle redistribution grant** | Asset audit B8: no download-specific licence or manifest for `haunted_candle.png` | Owner provides the grant for the master and the derived atlas | Written grant on file | Does not block the visual slice; blocks public distribution |
| 5 | **Night HUD contrast** | HUD ink is dark on frost panels designed for day. Night play HUD is a later task (13.9) | Panels keep their own frost surface; check any bare text on the graded floor | Judge in a night Challenge | Yes |
| 6 | **Real-GPU numbers** | Headless software GL only | A real-browser frame-time reading from the user | Not claimed | n/a |

## Questions for the human reviewer (HUMAN REVIEW PENDING; no approval claimed)

1. **Day vs night:** switch World Day / Night. Is night chilling but readable (snow, walls, the giant shadow in the lower area) and do the candle pools feel warm and precious without a hard edge? Is any head ever colour-shifted by the night?
2. **Flames:** compare "Steady flicker" and "Full source loop (with flash)", then "Play backwards". Does the steady flame look like the right orange candle and is the forward direction right? Are the flames the right size against the heads (Day play uses the same size)?
3. **Ghost:** with Restart, watch exact overlap (only a cyan edge shows) and the later divergence at about 7 s. Switch heads P1 to P4. Is the cyan ghost distinct from a living head in day and night, and is the evil face readable?

## Tuning surface (one place)

`src/lighting/lightingPresets.ts`: `LIGHTING.day|night` (tint, pool radius/colour/alpha/blend), `CANDLE_UNITS_PER_SOURCE_PX`, `TELEGRAPH_RING`, `SPECTRAL`. Rebuild the candle atlas: `python3 scripts/build-candle-atlas.py` (from `web/apps/ghost-lap`).

## Checks actually run

* `tsc` pass. `npm test -w @sands/ghost-lap` **29 pass** (23 existing + 6 new in `test/candles-ghost.test.ts`: atlas size/order inside the PNG and under 4096, order evidence, loops, timing/phase determinism, fixture exact-overlap then divergence, fixture lanes clear of all nine walls).
* `npm run build -w @sands/ghost-lap` and `check:bundles` pass; `scripts/check-arena-descriptor.mjs` pass (9 obstacles, 24 candle sites vs the C# source). Only the 1.4 MB derived atlas is in `dist`, not the master.
* Headless Chromium (software GL, so no FPS or feel claim) on the running dev server: the study in day and night, the diverged and overlapping ghost for P1 to P4, the flash loop in both modes, no console errors after fixing one shader precision error found there. A harness drove the real `PlayScene` with a fake transport: 2 active flames, 1 ring, 2 pools and 1 ghost appear; a new run identity clears candles, pools and ghost.
* Real production hub (existing paired host, 8081/8082): two distinct guests created, joined, readied and started a match from `?screen=dayPlay`; the real server candles drew as final flames and telegraph rings with no console error.

**Not run:** real GPU frame time, Firefox/Safari, Challenge/Fork night play, a real saved trace, reduced-motion in a browser, existing Playwright suites, audio. No CI claim.

## Correction pass: approval and tuning (after human review)
The user reviewed 13.5 and approved the candles, candle placement, ghost design, ghost/living separation, day/night composition, real-candle integration and the overall outcome. Those are locked. A small tuning pass followed (values only; no new effect, art, geometry, rule or HUD change):
* **Darker night grade (chosen):** `LIGHTING.night.environmentTint` `0x566a94` -> `0x384a7a` (deep midnight blue, still a multiply on snow/walls/environment only, no filter). Pool colour recomputed as warm white minus tint, `0xa86600` -> `0xc7871a`, so candle warmth and pool alpha/radius are unchanged. Day mode, heads, ghost, flames, HUD and the monster shadow are untouched.
* **Flame default LOCKED:** "Steady flicker" is the approved default and the only production motion. Both the real `PlayScene` and the study start on `steady` (`CandleField.loopId`, `NightStudy` state). "Full source loop (with flash)" is not approved for standard use and "Play backwards" is not a production mode; both remain as study-screen review controls only. Do not revisit.
* **Stronger charge-glow progression (chosen):** `HALO_ALPHA` in `characters/characterSpec.ts`, charge 0/1/2/3/4: `0/0/0.08/0.14/0.28` -> `0/0/0.10/0.175/0.43` (about +25%, +25%, +55%). Same halo sprite, hue, radius and ease; no pulsing or new glow.
This is a tuning pass. The user's approval covers 13.5 as reviewed; no subjective sign-off is claimed for the new night grade or glow values until the user looks at them. Look at `?screen=nightStudy` (Night, charge 2/3/4) and `?screen=dayPlay`.

Glow intensity increased again after visual review: it was still too weak at normal play zoom. `HALO_ALPHA` charge 0/1/2/3/4: `0/0/0.10/0.175/0.43` -> `0/0/0.15/0.26/0.86`. Intensity only (same sprite, hue, radius, ease); the release-emphasis alpha cap in `CharacterView` rose 0.5 -> 1 so release never dims below charge 4.
