# 10 — Strict implementation walkthrough for coding agents

**Rule:** follow numbered slices; each slice has inputs, work, verification, pass condition. Do not jump from code scaffold to a complete game with guessed contracts. Backend owner plugs in adapters where needed; frontend designer owns views and audio.

## Phase 0 — Non-destructive discovery; required before package installs

1. Locate `FRONTEND_ROOT` via real repository file tree. Identify existing React version, framework/bundler, routing, package manager, current UI component/library and unit/e2e test tools. Report them. No blind `create-vite`.
2. Confirm `$GAME_ASSETS` absolute path on active machine. Run installed skill's `scripts/asset_inventory.py --root ...`, list actual asset names and directories; open source `characters.md`. If inaccessible, **stop asset integration**, make precise request for mount/access, but continue planning/clean UI shell with clearly labeled placeholders if owner authorizes.
3. Inspect backend agent's inventory and exact code contracts: world size, map wall data, character mapping, authorized actions, history frames, event IDs, result states. Create a small `connection-map.md` listing source endpoint/event → intended view output, **without editing backend**.
4. Identify conflicts explicitly: solo Burst real events; start charge; selection action; history readiness; lobby ready/start availability; tie winner expression; exact collider geometry.
5. Audit sound licenses/rights and ParticleFX redistribution terms before bundling raw files into public repo; track creator/source/license/commit suitability.

**Gate 0 pass:** no fabricated source paths, no silent missing files, a real repo root identified, open integration questions logged.

## Phase 1 — Dependencies and preloading

1. Check lockfile first. Install only approved compatible packages in `09` using actual package manager, preserve React version. Add official PixiJS agent skills as docs if useful.
2. Define `assetRegistry.ts` with **literal filenames** from `01`, only after actual asset paths confirmed; set build-safe served base URL. Do not load local `/Users` paths in browser.
3. Inspect candlestick/smoke sheets, Kenney atlas; generate spritesheet metadata based on actual image layout. Repack large atlases, keep sources intact. Preprocess grey walls into derived atlas from originals (test alpha and seamless edges).
4. Self-host Source Serif 4 / Source Sans 3 via Fontsource, load before initial UI.

**Gate 1 pass:** smoke/candle frames transparent, correct order; grey wall tiles look natural; background same art; font audit has no missing resources; production bundler resolves URLs.

## Phase 2 — Design tokens / UI primitives

1. Implement exact theme tokens `02`, global reset, typography, `FrostPanel`, `GameButton`, `BackLink`, status, controls, dialog, toast and input wrappers.
2. Create light/dark frost variants and reduced-motion rules; buttons keyboard-activatable and visibly focused.
3. Build disposable *static* demo screen to screenshot variants: menu light, night comparison, result list, history row, timeline track. Ensure visual coherence before wiring complex mode flows.

**Gate 2:** Source fonts visible; no giant blocky arcade fonts, overly blurred background, cramped sub-14px labels or faux banking data grid.

## Phase 3 — Pixi stage, snow world and maze

1. Mount one PixiJS `Application` in React; fit complete scene with 16:9 letterboxing and central backend-to-render transform. Preload `snow_arena.webp`; verify monster shadow correct and static.
2. Implement `GreyWallRenderer` from authoritative geometry; use derived grey tiles/endcaps. Verify wall segment alignment from actual map. If no backend wall geometry exists, request it; **do not invent maze to match mockup**.
3. Add four Kenney shape components, grounded by small bottom-right shadows; render in correct game positions. Test source alpha and correct face assembly at gameplay size.

**Gate 3:** room geometry and art line up; scene resize without distortion; no baked-in walls, no repeated Pixi app mount.

## Phase 4 — Living expression state & Soulfire halos

1. Read `$GAME_ASSETS/characters.md` fully; create four shape identities and composited facial parts.
2. Implement numeric charge->normal-expression mapping with 0/1 visually identical; 2 neutral, 3 happy, 4 super happy. Keep actual charge 0 vs 1 in HUD.
3. Implement gaze-only movement direction; immutable head orientation. Implement one reusable slight shadow and silhouette halo component (without body filter).
4. Integrate hit 2s, weaker-contact 2s, release face, and authorized winner expression from backend events. De-duplicate and restore current normal expression properly.

**Gate 4:** screenshot matrix four identities × five charge values and special expressions; eyes sharp; stage 4 readably armed but candle still brighter.

## Phase 5 — Candle, day/night and spectral ghost

1. Parse orange `haunted_candle.png` frames, spawn/remove only for actual candle positions and pickup events; share texture sequences.
2. Implement shared lightmap: barely visible by day, soft warm environmental illumination by night; keep flame sequence unchanged. Avoid expensive per-candle filters.
3. Add environment-only night colour-grade. Test snow texture and monster shadow retained; player/HUD not affected.
4. Implement `SpectralGhostEffect`: one cyan luminance mapping filter, modest translucent alpha, soft edge glow, permanent evil face; exact historical positional truth, behind living player.
5. Test perfect overlap and genuine divergence; ghost never collides or consumes living candles.

**Gate 5:** bright day and legible night both look intentional; no body blur; no 1px fake ghost offset; correct identity and expressive clarity.

## Phase 6 — Burst + transition FX

1. Connect `SoulfireShockwave` to real backend Burst event. Distort world briefly within authorized visual envelope. Temporarily pulse existing emitting halo. No yellow ring, extra particles or hit glitches.
2. Create brief CRT on scene for successful light→night theme switch; remove on completion or cancellation.
3. Animate downloaded smoke sheet as a complete transition mask; switch lightmap/night treatment underneath adequate coverage; remove resources after reveal.
4. Implement accurate backwards historical frame montage **only when real prepared frames available**, ending at exact chosen tick; keep proposed target ~1s; server fork ownership untouched.

**Gate 6:** 3 successive fork/night/return cycles without stale filter, stuck smoke or double event. No zoom jank, no false success on slow backend.

## Phase 7 — AudioDirector and ambience

1. Register exact 9 approved filenames (see `07`); browser gesture to unlock audio; stable singleton.
2. Implement `gameLoading.wav` between game sessions; `multiplayer_theme.wav` during active day multiplayer and solo; `ghost_main_theme.wav` from *night gameplay entry* only.
3. Implement wind twice, knocking once, repeat, using completion callbacks; stop on exit. Relative wind 80/knock 90 vs music 100 **by ear**, not hard-decibel assumption.
4. Route confirmed `candle_eat`, `player_hit`, `blast`, `menu` to correct UI/backend sources with dedupe. Add Music/SFX volume/mute controls in settings.

**Gate 7:** test complete audio sequences, no extra sounds or overlapping BGMs, no ghost ambience during menus/day, no double playback on React rerender.

## Phase 8 — Core UI functional journeys

Implement in this order using reusable primitives + real backend owner adapters:

1. **Home/Settings/Help:** correct six entry actions, tutorial text from verified controls, menu music persists.
2. **Room create/join/lobby:** accurate join status, readiness, authorized start, room identity; conditional character selection, no fake toggles.
3. **Live multiplayer HUD/result:** truthful participants, time, score, Soulfire, Overlit, hit feedback; `GetMatchResult` pending/finalized/aborted, ties/forfeits/rematch.
4. **Spectator:** readonly same arena, resync, leave, no gameplay commands.
5. **Solo original:** day arena, score/tick, real Burst events, verified save; save retry/failure.
6. **My Histories:** original/future list, source linkage, empty states, actual action availability.
7. **Inspect/replay/timeline:** hourglass left, T0–T2700 seek, exact tick, view-only playback, fork eligibility.
8. **Challenge and Fork:** prepare/ready/failed, night reveal, ghost, aligned score delta, child save, correct source identity.
9. **Branch compare:** same-tick original vs child, fork diamond, authentic difference, back to source.

**Gate 8:** every specified click produces a known action/real state or clearly explained disabled state; no decorative fake button. Keyboard works; no unsupported social/global ranking features.

## Phase 9 — Failure handling and quality validation

1. Test direct refresh in menu and in active room; replay/ghost lifecycle, disconnect/reconnect, result polling, cancel-preparation, malformed/missing event data.
2. Test slower preparation than 3-second transition: hold truthful state, prevent double start, expose retry/return; never continue gameplay on visual timer alone.
3. Test rematch reset: match IDs/positions/scores/effects belong to correct match; audio changes only once.
4. Test saved child vs source score alignment at early and late ticks, T0, T2699, T2700 and no nested fork. Timer and data are sourced from backend.
5. Profile typical desktop rendering with active candle count, multiple players, shockwave, ghost, opening of frost UI; record stutter and fix hot filter and unnecessary redrawing.
6. Capture screenshot and short clip for: menu, multiplayer lobby, day arena, 4 charge stages, Burst, night candle, overlapping ghost, rewind hourglass, history compare, results, settings, error/pending states.
7. Deliver a manifest listing asset origin/licence, runnable UI paths, install commands and known remaining VERIFY items. Do not say complete with only static mockups.

## Agency contract — what to do when blocked

- Missing frontend repo? Identify and request exact path; avoid creating alternate app.
- Missing backend world geometry? Build world renderer interface but do not fake obstacle positions as final.
- Missing character atlas or ambiguous expression? Read the actual pack, generate crops only from inspected coordinates and run screenshot validation.
- Missing history frames/replay API? Build timeline display and accessibility but leave fork action disabled with a truthful integration gate; do not invent history.
- Missing audio licence? Can prototype locally, but flag source-distribution and publication gates.
- A screenshot looks wrong? Adjust only visual configuration; don't alter collisions or history to make the art fit.
