---
name: ghost-lap-frontend-director
description: Build Ghost Lap's complete desktop React, TypeScript and PixiJS v8 visual/audio experience from its approved local assets, character specifications, history/timeline requirements and exact design-system decisions. Use for frontend planning, implementation, review and refinement; never invent authoritative gameplay.
---

# Ghost Lap — Frontend & Experience Director

**Release:** 1.0 baseline specification, 18 November 2026. **Scope:** desktop browser visual presentation, client UX, game rendering and audio only. **Instruction precedence:** this document's decisions + the referenced approved design records; backend contracts own all gameplay semantics. Do not confuse a UI directive with a new game mechanic.

> **START HERE.** This is an operable agent skill. Follow its mandatory reading order and gates, rather than implementing from a screenshot or isolated paragraph. `CONFIRMED` = user or backend explicitly settled; `DIRECTOR BASELINE` = specific implementation/design choice delegated to the frontend director, safe to refine after visual review; `VERIFY` = inspect authoritative code/assets before connecting; `OPTIONAL/DEFERRED` = do not implement by default. Never quietly promote a VERIFY item into a gameplay fact.

## 0. Mission and non-negotiable boundaries

Build a premium, cohesive, visually elegant **Ghost Lap**: colourful Kenney geometric heads on a snowy, monster-shadow arena, genuinely readable modular grey walls, subdued warm orange candlelight, dramatic but restrained Soulfire visual feedback, a cyan spectral historical ghost, atmospheric day-to-night transition, and an unmistakable **Sands of Time hourglass + rewind timeline**. Interface mood: restrained, soft, frost-glass, editorial, breathable, readable — **inspired by Alto's Adventure interface discipline, not copied artwork or screen compositions**.

- **CONFIRMED:** `React + TypeScript` for accessible HTML UI; `PixiJS v8` for game scene; desktop, keyboard and mouse; complete 16:9 arena visible; no mobile/touch features.
- **CONFIRMED:** all scores, collisions, candles, Burst outcomes, replay positions, historical ticks, fork validity, room membership, saves and match results are authoritative backend facts. Do not generate local authoritative simulation, invented data, fake history or false success. Backend agent owns wiring, not this skill.
- **CONFIRMED:** four head-only characters; 0/4 and 1/4 look identical; exact expression rules in external `characters.md`; original character shape and facial details remain crisp.
- **CONFIRMED:** `game-assets/backgrounds/snow_arena.webp` is the active environment; grass arenas are removed. Never use old grass assets/mockups as production textures.
- **CONFIRMED:** no global leaderboard/social profiles/chat/ranks/mobile/AI opponents/nested child forks/live authoritative pause. Show **match-local standings** only.
- **CONFIRMED:** avoid extra visual noise: no heavy full-body Bloom, no face blur, no hit-glitch/RGB split, no vignette, no yellow-tinted Burst ring, no invented Burst particles.
- **DIRECTOR BASELINE:** effects are configurable and isolated; screens avoid copycat Alto elements; menu typography is free **Source Serif 4 + Source Sans 3**; one coherent frosted UI token system.

## 1. Locate the REAL project and sources before coding

**Known local source path from user Finder screenshots:**

`/Users/rroy/Desktop/the-sands-of-time-phase1/design-inputs/game-assets/`

**Portable equivalent:** `~/Desktop/the-sands-of-time-phase1/design-inputs/game-assets/`. Case and punctuation matter. This is a **source-assets directory**, not proof of the frontend repository location or that the agent's runtime can access this Mac. Inspect the actual machine. Never guess a Git repository root or write into arbitrary paths.

```bash
export GAME_ASSETS="$HOME/Desktop/the-sands-of-time-phase1/design-inputs/game-assets"
test -d "$GAME_ASSETS" || { echo 'Asset source not mounted; ask host to make it accessible'; exit 1; }
ls -la "$GAME_ASSETS"
python3 scripts/asset_inventory.py --root "$GAME_ASSETS"
```

The script path above is relative to **this installed skill directory** (`scripts/asset_inventory.py`), not necessarily the frontend repo; use its actual location. Never report the user's files as inspected until this succeeds in that environment. The current conversation included **screenshots, a separate copy of `characters.md`, and descriptions**, not a mounted copy of the entire Mac asset directory.

**Mandatory sources in this order:**
1. `references/00-authority-and-boundaries.md` — conflict precedence, confirmed gameplay/UI scope, verification gates.
2. `$GAME_ASSETS/characters.md` — authoritative character drawings/expressions; cross-check with `references/04-characters.md`; do **not** replace it.
3. `references/01-assets-and-licenses.md` — exact paths, master vs runtime, processing and fallbacks.
4. `references/02-visual-design-system.md` — typography, colours, spacing, frosted-glass rules and motion.
5. `references/03-screens-and-interactions.md` — every screen/overlay, controls, empty/error/loading variants.
6. `references/04-characters.md` — character-view layers, 0/1 convention, expression precedence, ghost overlap.
7. `references/05-arena-walls-candles.md` — background, wall mapping and daylight/night lightmap.
8. `references/06-effects-catalogue.md` — all FX, exact approved exclusions and filter lifecycle.
9. `references/07-audio-direction.md` — literal filenames, soundtrack routing, wind-knock schedule.
10. `references/08-timeline-rewind-journeys.md` — history tick semantics, hourglass presentation and branch choreography.
11. `references/09-dependencies-and-architecture.md` — **mandatory** package versions/commands, scene tree and proposed source modules.
12. `references/10-implementation-playbook.md` — strict slice-by-slice procedure and agent handoffs.
13. `references/11-acceptance-and-open-items.md` — checklists, performance targets, precise unresolved items.
14. `references/12-screen-blueprints.md` — practical screen-level UI layouts and click acceptance matrix.
15. Existing backend/frontend contract docs and source code as located by the backend owner (not supplied inside this skill).

Do not skip a reference because the skill overview looks sufficient. Each file includes critical implementation exclusions and exact target states.

## 2. Decision authority and conflict protocol

- **Backend**: if user text conflicts with backend-verified mechanics, **never change backend behaviour**. Render actual events, and log the discrepancy to the owner. Example: older informal notes said solo had no Burst; later backend inventory says solo recording has real Soulfire and Bursts. The visual director must respond to **actual solo Burst events**, not suppress them.
- **Latest explicit user correction wins** for appearance/audio: 0/4 and 1/4 same frown/no halo; snow instead of grass; `candle_eat.wav` spelling; Burst **no tint or coloured ring**; night music starts after transition; ghost no body blur.
- **Unknown interface contracts are `VERIFY`**, not permission to invent fields/handlers. Put a *named integration adapter* at the boundary, work from genuine DTOs/events, and leave a typed unresolved gate where unavoidable.
- **DIRECTOR BASELINE** numeric layout/opacity/motion tokens below are initial art direction, not claims of user-mandated colour codes or backend frame timing; keep them centralized for tuning.

## 3. Deliverable architecture — exactly what to build

**Presentation layers:**
- React HTML/CSS (menu, navigation, lobby, HUD, settings, results, histories, timeline/controls, accessible dialog/toast/status elements).
- PixiJS world (snow background, modular walls, candles, living character heads, historical ghost, minimal contact shadows, charge halos, candle lightmap, short Burst/CRT/smoke effects).
- `AudioDirector`: single channel owner for BGM, UI/SFX, atmospheric loop sequencing.
- `PresentationAdapter`: read-only mapping of **verified** backend state/events to these renderers. It cannot determine game outcomes or manufacture a replay.

**Screens/contexts:** entry and menu; How to Play; Settings; Multiplayer create/join and lobby; live multiplayer HUD; match results/rematch; Spectate; Solo original gameplay and verified-save result; My Histories originals/futures; original replay; Ghost Challenge preparation and play; Fork/rewind selection; historical branch comparison; relevant states for connecting/authorization/empty/save failure/resync. This **does not** mandate one route per context. See `03` and `08`.

**The signature interface:** hourglass **physically left** of bottom timeline; source history from **T0 to T2700** at 30Hz; fork T0–T2699 only; precise selection, original/future distinction, current aligned score and a true branch point. No banking dashboard aesthetic. No timeline on ordinary live multiplayer or the initial solo arena.

## 4. Approved essential components, by treatment

| Component | Construction | Hard visual exclusion |
|---|---|---|
| Snow background | `snow_arena.webp` single fitted PixiJS background sprite | Do not bake maze/players into background or stretch disproportionately |
| Walls | reusable tiles from `kenney_pixel-platformer-blocks/` grey-grade to stone/slate; backend-defined placement | Never infer collisions from PNG or invent a maze |
| Players | Kenney shapes + facial parts per `characters.md` | No hands/legs, head rotation, pixellated re-drawing or heavy blurs |
| 0–4 charge | expression states + shape-backed, coloured, low-intensity halo (0/1 identical) | No GlowFilter/Bloom over faces |
| Candle | orange `haunted_candle.png` animation + shared warm low-cost lightmap | No giant torch/spotlight or changed flicker pattern by mode |
| Soulfire Burst | `pixi-filters/shockwave` distortion plus brief increase of existing player halo | **No tint, coloured ring, extra particles or hit RGB glitch** |
| Ghost | reused shape + permanent evil expression + one cyan luminance spectral shader, translucent, edge light | No invented travel path, body blur or lightmap source |
| Night | cool/environment-only grade + warm candle illumination | **Not** `ColorMatrixFilter.night()` green night vision; never grade living player or HTML HUD |
| Transition | brief CRT + downloaded `smoke_transition.png`, after optional truthful rewind montage | Never substitute animation completion for backend readiness |
| UI | bespoke Alto-inspired restrained typography, frost overlays, whitespace | No fantasy block fonts, copied Alto artwork, constantly active fullscreen blur |
| Audio | exact assets and loop rules in `07` | No auto-added voiceovers/laughs/screams; no ghost ambience in day modes |

## 5. Work order (obligatory)

Read `10-implementation-playbook.md`; its step definitions are the execution plan. Broad order:

1. **Gate A:** locate repo and game-assets; inspect exact files, licenses, PNG atlas/frame geometry, and verified backend hooks. Publish an **asset audit + connection map**; fail loudly on missing required inputs.
2. **Gate B:** integrate dependencies under existing project's package manager and lockfile; install official PixiJS skills for syntax help; do not create competing app scaffold.
3. Establish central theme tokens, fonts, shell and 16:9 viewport. Build reusable frosted panel/button/dialog/list/score/indicator primitives.
4. Create PixiJS scene layers, live world transform, snow background, walls from actual geometry and 4 Kenney composited heads.
5. Add authoritative expressions, 0/1 mapping, shadows and separate tinted silhouette halos.
6. Integrate candle spritesheets, shared lightmap and mode-specific night grade; then ghost shader/layering.
7. Add pure Shockwave Burst and CRT + smoke transition; implement history-montage rendering only over authentic frames.
8. Implement `AudioDirector` with all decided sounds and layered ambient wind-knock playback.
9. Build menu, lobby, HUD, results, histories, timeline, compare and settings with exact supported actions.
10. Verify all modes by actual play and visual/sound review; run perf/failure checks; surface unresolved contract/asset issues.

Complete each slice as a usable vertical test: **asset → visual state → backend hookup (backend owner) → lifecycle → error → screenshot or clip → acceptance**. Never assert success from static code review alone.

## 6. Backend-agent handoff, not backend implementation

Give the main backend agent `references/00-authority-and-boundaries.md` and `references/11-acceptance-and-open-items.md`. Request specific authoritative adapters/events, including world dimensions and wall geometry, player identities and charge, outcomes/expression triggers, room/spectator controls, history frames/seek, verified readiness/save and results. **Do not request backend redesign; do not write a second simulation.** See `09` for rendering-side semantic types (explicitly not server DTOs).

## 7. Completion criteria

A skill-directed build is not complete until: all user-approved assets load by their intended filenames; every supported context renders accurately; authentic ghost replay remains exact and noninteractive; original/night rendering is separated; the rewind hourglass visibly anchors precise tick selection; music and wind/knock cycle obey state transitions; transient filters release resources; assets and fonts load before being shown; all menus and status states are keyboard accessible; disabled/unsupported functions are not faked; and QA shows a full session at desktop 16:9 without persistent console errors or obvious frame jank. See `11` for the feature-by-feature checklist.

**Source links (current PixiJS docs):** https://pixijs.com/8.x/guides · https://pixijs.com/llms · https://github.com/pixijs/filters · https://github.com/pixijs/sound · https://agentskills.io/specification .
