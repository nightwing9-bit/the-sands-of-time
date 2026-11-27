# Spec 13 task 13.6: Burst, CRT and smoke transitions

Status: **CREATIVE IMPLEMENTATION DONE · focused technical gate passed · HUMAN REVIEW PENDING.** Existing `web/apps/ghost-lap` only. No backend, Marrow, `ghost-v1` / `ghost-mp-v2` / `arena-v1`, scoring, collision or Burst semantics changed. No sound added (no Burst sound is fabricated; `blast.wav` belongs to 13.7). The historical rewind montage is not built (13.11).

## Try it

Existing dev server, same URL, hot reload: `http://localhost:5174/?screen=burstStudy` (or the "Burst study" button in the preview strip).
Everything on this screen is a labelled **PREVIEW FIXTURE**: the frames come from `src/play/burstFixtureTransport.ts`, which produces the exact `PlayFrame` shape of a real transport and imitates the server's Burst rule (reach 160, 2 charge and 90 points per target). The path it drives is real: `PlaySession` -> one-shot cues -> `PlayScene` -> faces, halo pulse, distortion.

1. **Press Burst (Space)**: requested, admitted (fixture answers after 90 ms), then confirmed only when the fixture emits `BurstManual`. P2 and P3 are inside 160 units and get X-eyes; P4 is outside and is untouched.
2. **Overlit auto-Burst**: `BurstAuto`, no press, nothing to confirm in the HUD. Same effect (the server effect is identical).
3. **Weaker contact**: `ContactDrain` gives only the weaker victim the fear face; no distortion.
4. **Day -> Night (CRT + smoke)**: about 2.4 s in total, swap under full cover at 0.73 s after the smoke starts, clean return. Then Night -> Day, **CRT only**, **Cancel**, **New match**, **Reduced motion**.
5. The readout line under the buttons and the `data-*` attributes show the number of filters on the game view; it is 0 whenever nothing plays.

## What was built

| Piece | File | Notes |
|---|---|---|
| Presets | `src/fx/effectPresets.ts` | the one place for every effect number; `BURST_REACH_UNITS = 160` is the only authority-derived value (GameRulesV1.BurstRadiusFixed / 1024) |
| Pure timing | `src/fx/timeline.ts` | shockwave radius, halo pulse, CRT envelope, smoke frame/swap schedule, reduced-motion cover; unit tested |
| Filter owner | `src/fx/FilterStack.ts` | the only writer of the game view's `filters`; empty list means no filter and no filterArea |
| Burst | `src/fx/ShockwaveFx.ts` | ONE `pixi-filters/shockwave` filter, reused; brightness stays 1; world pixels only |
| CRT + smoke | `src/fx/SceneTransition.ts` | reusable `play({crt, smoke, onCovered, onDone})`, `cancel()`, `dispose()`; no night or history knowledge |
| Smoke textures | `src/fx/SmokeAssets.ts`, `scripts/build-smoke-atlas.py` | derived pages, ref-counted like the candle atlas |
| Halo pulse | `CharacterView.pulseHalo` | alpha and scale of the existing halo sprite, layered over the eased charge halo |
| Session cue | `PlaySession` `VisualCue.burst` | `{origin: manual or auto, x, y, tick}` from the server event and the authoritative emitter position |
| Reduced motion | `src/fx/motion.ts` | OS preference, with a settable override for the later Settings screen |
| Study | `src/screens/BurstStudy.tsx` | dev only |

`ArenaWorld` gained `fxStack`, `screenLayer` (a screen-space layer above the world, for overlays) and `screenSize`. `package.json` gained `pixi-filters@6.1.5` (peer `pixi.js >=8`, MIT; the exports `pixi-filters/shockwave` and `pixi-filters/crt` are used, not the whole package).

### Burst
* Centre: the emitting slot's position in the frame that delivered the event, world units, times the world scale. The filter area is the world rectangle, so no other offset exists (pixi-filters measures centre, wavelength, amplitude and radius in CSS pixels of the filter input; verified in `FilterSystem._updateFilterUniforms`: `uInputSize` is the logical size).
* The ring's centre runs from 62 to 134 units with an ease-out over 640 ms; its far edge never passes 160 (a unit test reads the rule from `GameRulesV1.cs`). It starts outside the emitting head, which is therefore not distorted.
* Halo: the emitting head's own halo reaches alpha 1 and 1.16x scale in 45 ms, then falls to the new charge's halo (0 after a Burst) over 560 ms.
* Face: unchanged from 13.3/13.4 (release 450 ms PROVISIONAL, hit and fear 2 s). New: the Burst cue is one-shot per event key; a repeated key does not replay face, pulse or distortion.
* **Manual vs auto**: kept in the cue (`origin`), the feed text and the HUD confirmation (only the local player's own `BurstManual` confirms a press). The visual effect is deliberately identical, because the server effect is identical. They may differ in audio in 13.7.
* Multiple concurrent Bursts: one shared filter; a new Burst replaces the running one. Results and sound remain authoritative.
* Idle cost: none. The filter is only on the world while running; two warm-up frames compile the shader at arena start (an inert filter, amplitude 0).

### CRT
`CRTFilter` on the game view only, 420 ms, smooth ramp in 90 ms and out 170 ms, noise at most 0.16, line contrast 0.2, re-seeded 12 times a second at most, no curvature, no vignette. The filter's scan-line banding exists whenever `lineWidth > 0`, so it is off while the envelope is 0.

### Smoke: what was measured (no guessed atlas)
Master 7540 x 4806, SHA-256 in `smokeAtlas.json`. Evidence is computed by `build-smoke-atlas.py`, which fails if a measurement stops holding.
* Grid 10 x 9 cells of 754 x 534. Cells 0-5 and 76-89 are fully transparent; cells 6-75 (70) have content; no frame crosses a cell border.
* **84 frames = packing convention**: row-major from cell 0, 84 frames = 6 empty leading + 70 visible + 8 empty trailing, cells 84-89 unused. The audit's alpha-empty list (rows 0 and 7-8) matches this. It is a derivation, there is no exporter manifest. The empty frames stay in the timeline index; playback just starts at the first visible frame and stops after the last.
* Every frame carries one flat "card" rectangle, solid x 106..653, y 109..434 of its cell (2 px soft edge). The runtime maps its 16:9 crop (548 x 308) to the whole canvas with 2% overscan, so the card is the screen and the soft edge and atlas inset are off-screen.
* **Full cover**: inside that crop the minimum alpha is at least 250 for frames 27-46 (28-45 are exactly 255); mean alpha rises monotonically before and falls monotonically after. This also is the order evidence (reading order). The swap runs when frame 28 is displayed, in the same update, before the frame renders: no pop. A 50 ms per-update clamp makes it impossible to jump over the 18-frame (0.6 s) window.
* **Packaging**: three pages (1920x1890, 1920x1890, 1920x1080, all below 2048), 480 x 270 per frame, 4 per row, premultiplied LANCZOS, 1 px runtime inset. 70 textures share three sources. About 36 MB decoded, 7 MB PNG. The master is never served.
* 30 fps is the skill's nominal rate, not export data (`SMOKE.fps`, one number). Direction (cover then clear) is the natural reading; a human can judge.

## Lifecycle and reduced motion
* `PlayScene` owns its `ShockwaveFx`; it is cancelled on a new run identity and disposed with the scene. `SceneTransition` is owned by the screen. `ArenaWorld.destroy` clears the stack.
* Reduced motion (OS setting or the study's button): no shockwave, no CRT, no smoke. The halo pulse stays as a softer opacity-only swell; the transition becomes one slate cover that fades in over 160 ms, swaps, and fades out. The hook and the state are the same.
* If the smoke textures are missing, a play falls back to the same cover.

## Checks actually run
* `npm test -w @sands/ghost-lap`: 37 pass (29 earlier, 8 new in `test/fx.test.ts`: Burst front within the server reach read from `GameRulesV1.cs`, halo and CRT envelopes, atlas JSON consistency and swap window, reduced cover, one-shot manual/auto cues with authoritative position, press is not a Burst, new identity forgets keys).
* `tsc`, `vite build`, `check:bundles`, arena descriptor check: pass.
* Headless Chromium, software GL (SwiftShader) at 1600x900 and 1280x720: Burst (auto and manual), day -> night and night -> day (CRT + smoke), CRT only, cancel mid-way, new match during a Burst, reduced motion, 3 repeated cycles and a screen remount. After each: 0 filters on the game view, no smoke or cover, transition idle, no console errors. Screenshots show the distortion on the neighbouring heads, the halo swell, full cover with no seam, and the night world revealed without a pop.
* Not done: FPS or real-GPU numbers, other browsers, WebGPU, heavy leak or stress runs, live Burst from a real transport (the production events are the same `PlayEvent` kinds; no live run was driven in this task).

## Pending and exact needs
* **HUMAN REVIEW PENDING** (impact and clarity, not numbers). The three questions are in the task report.
* TECHNICAL ACCEPTANCE PENDING (external): real-GPU frame time with the filter on the world container at 1920x1080; a live multiplayer Burst through `MultiplayerTransport` (the cue path is the same `PlayEvent`s the 13.4 browser run already emitted, but no Burst occurred in that bounded run); smoke redistribution evidence (audit B8) and the exporter's frame rate if the owner has it.
* No backend need arose.
* Decisions provisional until reviewed: ring width 52, amplitude 15, duration 640 ms, CRT noise 0.16, smoke pace 30 fps (about 2.4 s with the 120 ms CRT lead), halo peak 1 at 1.16x.
* Not used from `skills2`: its ring, sparks, squash and wind-up (forbidden by the Director skill's negative catalogue).

## Refinement after human review (user approved the whole 13.6 implementation)
Four adjustments only; shockwave, expressions, candles, night tint, ghost, arena, HUD, physics, replay and contracts untouched. Values before → after:
* **Duration**: smoke playback 30 → 22 fps (70 frames, 2333 → 3182 ms, no frame skipped or held); CRT 420 → 1420 ms (attack 90 → 140, release 170 → 420). Whole transition 2453 → **3302 ms** (120 ms CRT lead + smoke). Swap under full cover at frame 28: 0.73 s → 1.12 s after play(). The 50 ms per-update clamp still cannot skip the 18-frame opaque window (now 818 ms).
* **Cleaner clouds**: `scripts/build-smoke-atlas.py` recolours the derived pages by luminance (58..120 dark slate → pale blue-white, shadow 192,204,224 to light 247,250,255); alpha, shading structure and the master are unchanged. Mean opaque colour (77,82,91) → (213,222,236). Reduced-motion cover colour follows (0x454a55 → 0xd5deec).
* **CRT strength**: noise 0.16 → 0.20, line contrast 0.20 → 0.25. No new layers.
* **4/4 halo**: alpha 0.86 → 0.95 and a steady scale 1 → 1.10 (eased like the alpha). This is about the Burst pulse at ~150 ms (alpha 0.66, scale 1.106). The Burst still peaks at alpha 1, scale 1.16. Charges 0..3 unchanged.
Final visual judgement stays with the user.
