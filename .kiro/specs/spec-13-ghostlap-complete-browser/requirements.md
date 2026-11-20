# Spec 13 — Ghost Lap: Complete Frontend & Experience Director

**Status:** Replacement implementation specification; not implementation evidence.  
**Product:** The Sands of Time / Ghost Lap. **Platform:** desktop browser **only**.  
**Frontend creative authority:** `design-inputs/ghost-lap-frontend-director/SKILL.md` plus **all thirteen** references `00`–`12` and the actual `$GAME_ASSETS/characters.md`.  
**Backend authority:** frozen Marrow Timeline, `ghost-v1` solo, `ghost-mp-v1` historical compatibility, `ghost-mp-v2` new multiplayer, `arena-v1`, completed Specs 10–12 contracts.  
**Primary implementer:** Kiro **Claude Opus 5.5 High** for creative frontend Tasks **13.1–13.12**; **Codex Medium** for 13.0, objective transport/backend adapters, heavy tests, browser measurements and deployment evidence.  
**Dependencies:** verified Spec 10 multiplayer, Spec 11 original/ghost/fork/compare, Spec 12 results/spectators/rematch handoff. **No mobile, Elo, chat, public replay sharing or social profile.**

## Product outcome: one extraordinary, truthful game

Ghost Lap shall feel like a finished premium arcade game: instantly legible, responsive, characterful, sonically coherent and original. Its snow arena and giant baked monster shadow, four Kenney geometric heads, expressive Soulfire faces, orange candles, pure distortion Burst, dramatic but readable night, cyan historical ghost and iconic **hourglass physically left of the history timeline** shall form **one authored experience**, not a collection of separate effects/screens. The frontend must simultaneously demonstrate that the history, rewind, independent futures and same-tick comparisons are **real, immutable and verifiable**.

**Quality ambition is not optional:** faithful asset composition, strong individual creative judgment, emotionally satisfying interaction timing, polished states and repeated visual play review are first-class requirements. Opus must interpret the complete skill intelligently and refine the composition, but must not independently replace approved source assets, invent gameplay, contradict explicit exclusions or shift the selected artistic direction. Deliver an exceptionally cohesive product, not merely a completed checkbox grid.

### Authority, source and decision precedence

1. **Authoritative game fact:** actual backend code, certified APIs, real game events and frozen codecs decide positions, input admission, contact, candle collection, charge, Overlit, Burst, scores, ticks, winner, save, timeline and ownership. No frontend physics fork or pretend replay.
2. **Appearance/audio corrections:** latest explicit user decisions take precedence over older mockups and director defaults (snow instead of grass; head-only; stage 0/1 identical expression/no halo; no coloured Burst ring; approved sound filenames; ghost without body blur; no mobile).
3. **Approved authored character and media sources:** real `game-assets/characters.md` governs face identity/expressions; exact approved assets and names in skill `01`; do not crop from market screenshots or substitute invented material.
4. **Skill creative contract:** `SKILL.md` + `references/00`–`12` supply aesthetic, choreography, screen and acceptance baselines. Distinguish `CONFIRMED`, `DIRECTOR BASELINE`, `VERIFY`, `OPTIONAL/DEFERRED` as the skill does. Director-baseline numbers may be visually tuned coherently; confirmed features/exclusions may not be quietly overridden.
5. **Illustrations and old specs:** Revenant banking screenshot, Alto-inspired restraint, pre-snow mockups, the old Spec 13 and illustrative data are **not** game authority or copyable production art.

Before work, the implementing agent must inspect actual frontend package and original asset directory at `~/Desktop/the-sands-of-time-phase1/design-inputs/game-assets/` (a known **Mac source path**, not proof of availability in Codex's environment); verify source file names, licences and actual backend contracts. Do not create a duplicate frontend app if one exists. Do not alter the skill package or user's source masters as a shortcut.

## A. Application scope and coherent identity

**R13.01 — Desktop-only product.** WHEN the game is opened in an intended desktop browser, THE FRONTEND SHALL present a keyboard-and-mouse accessible desktop experience, preserving the complete 16:9 world at every supported desktop viewport without stretched sprites, hidden walls or cropped playable boundaries. Mobile/touch/joystick/portrait/controller infrastructure is explicitly out of scope. UI panels may resize and scroll at smaller desktop sizes.

**R13.02 — One connected visual language.** WHEN switching between home, lobby, day gameplay, history, night, results and settings, THE FRONTEND SHALL preserve a coherent editorial/frost design: Source Serif 4 for display, Source Sans 3 for controls/HUD, restrained snow/slate palette, optional sand gold for historical focus, selective cyan for ghost, readable frost materials and consistent controls. Day/night change the **arena treatment**, not an unrelated UI design language. Original composition and creative refinement within skill boundaries are required.

**R13.03 — Real supported navigation.** WHEN a user enters the game, THE FRONTEND SHALL offer functional Multiplayer, Solo Original, My Histories, eligible Spectate, How to Play and Settings, with actual route/action outcomes and truthful waiting/error/return states. Logical destinations may share routes or panels. Do not invent profile, global leaderboard, shop, social, chat, share-token or fake character-choice screens.

**R13.04 — One experience coordinator.** WHEN a user changes mode, THE FRONTEND SHALL coordinate backend eligibility, input enablement, scene, HUD, loading, transition, sound and cleanup as one application lifecycle. It shall never show a live round paused simply because a local menu is open; server state determines live pause (not certified/supported).

**R13.05 — Honest state language.** WHEN data is loading, an original save is pending/unavailable, a result is pending/aborted, preparation is incomplete, a history is incompatible/foreign, or a connection is lost, THE FRONTEND SHALL show an accurate, actionable state rather than synthetic percentages, placeholder winners, phantom successful saves, fabricated histories or misleading controls. Contextual focus/keyboard handling is required.

## B. Real art, scene composition and character performance

**R13.06 — Exact snow environment.** WHEN rendering the arena, THE FRONTEND SHALL serve approved `backgrounds/snow_arena.webp` preserving its aspect and baked **giant monster shadow**. The shadow is atmosphere, not an enemy. The source `snow_arena_master.png` remains an untouched master, not a second shipped background. No grass imagery or bank/market screenshots as production textures.

**R13.07 — Real collision-aligned grey Kenney walls.** WHEN placing walls, THE FRONTEND SHALL use derived grey/slate Kenney Pixel Platformer Blocks tiles with visible edge/corner/bevel quality, positioned from **verified server maze/collider geometry** via one world transform. 18×18 source tile dimensions are texture resolution, not the game-world collider size. Do not draw a guessed symmetrical maze, invent collisions or stretch modular textures badly.

**R13.08 — Four exact bodyless characters.** WHEN players appear, THE FRONTEND SHALL compose approved transparent Kenney source assets into orange circle, pink square, yellow diamond and green rounded-square **head-only** figures, with crisply rendered facial parts, small contact shadows, authentic player/slot mapping and consistent proportions at real play scale. Do not add legs, limbs, weapon poses, body rotation, or mockup-derived redraws. Pupils convey direction; head stays upright.

**R13.09 — Complete Soulfire acting system.** WHEN the server reports charge 0–4, THE FRONTEND SHALL display **0 and 1 identically** (frown, no halo), 2 neutral/very faint shape-backed coloured halo, 3 small happy/faintly stronger, 4 super happy/modest armed halo, with numeric charge still truthful. Halos follow actual silhouettes and hues behind heads; no face/body bloom or universal yellow circle. Expression transitions must remain clear, subtle and cohesive.

**R13.10 — Special expressions from real events.** WHEN a qualifying authoritative event occurs, THE FRONTEND SHALL apply the actual 4/4-release exertion/scream, Burst-victim X-eye reaction (2s visual override), weaker-participant contact fear reaction (2s, weaker only), and results-confirmed winner face—including correct ties. The ghost retains **one permanent evil face**. Duplicated/resynced events shall not restart reactions; expired overrides return to **current** backend charge state. Where backend field/reaction timing is explicit, it wins over visual defaults.

**R13.11 — Authentic candle imagery.** WHEN a candle exists in the authoritative world, THE FRONTEND SHALL show the verified **orange** `haunted_candle.png` animation, with frames ordered and cropped from physically inspected source—not guessed 90-frame sheet geometry. Candles appear/disappear from server state; a real collection triggers its visual/sound cue exactly once. Shared decoded textures, verified GPU-sized atlases and proportional display are mandatory.

**R13.12 — Day/night lighting with unaffected characters.** WHEN in daylight Multiplayer or Solo Original, THE FRONTEND SHALL preserve bright readable snow and barely perceptible warm candle pools. WHEN in approved Ghost Challenge/Fork night gameplay, THE FRONTEND SHALL use cool dark **environment-only** grading, retained monster shadow/wall visibility and one soft shared accumulated warm candle lightmap. Characters, candle flames, ghost and React HUD shall not inherit the environment grade. No green night-vision preset, vignette, hard flashlights or costly permanent per-candle filters.

**R13.13 — Independent spectral ghost.** WHEN a verified source ghost accompanies a Challenge or Fork, THE FRONTEND SHALL render its exact same-tick historical location behind living players as a legible translucent cyan/whitish figure, with preserved facial contrast, faint edge and permanent evil face. At genuine overlap, do not forcibly offset it or invent a trail; it has no living collisions/pickups/score effect and is never a light source. No ghost body blur.

**R13.14 — Pure Soulfire Burst treatment.** WHEN an authoritative manual or automatic Burst cue occurs, THE FRONTEND SHALL deliver a short localized/world-limited `ShockwaveFilter` distortion and a restrained pulse of the **existing** emitting silhouette halo, with exact one-shot event ownership and cleanup. It shall not introduce a tinted ring, random burst particles, camera zoom/shake, body bloom or hit RGB glitch. Collision/power/radius/outcome stay server-owned.

**R13.15 — Purposeful transition effects.** WHEN authentic rewind/night transition proceeds, THE FRONTEND SHALL use real-history montage frames, short restrained CRT interference and the actual `smoke_transition.png` sequence, grading the world under smoke cover and disposing temporary filters afterward. Effect durations in skill are **tunable visual targets**, not proof that history is ready. No artificial gameplay state change during transitions; reduced-motion alternatives preserve meaning.

## C. Real-time control, client timing, audiovisual unity

**R13.16 — Responsive truthful controls.** WHEN the desktop player presses verified movement/Burst keys, THE FRONTEND SHALL send valid ordered input through actual authenticated game transport, release movement on blur/keyup/disconnect, and show an immediately responsive but **presentation-only** control response. It SHALL NOT determine hits, candle pickups, score, Soulfire, accepted Burst or history locally. Unknown key bindings must be verified before showing help.

**R13.17 — Smooth 30Hz-to-display presentation.** WHEN authoritative 30Hz frames arrive unevenly, THE FRONTEND SHALL render on a display-driven ticker targeted at smooth ~60 FPS, interpolate remote/world visuals over an appropriately buffered tick timeline, use bounded local movement prediction where reliable, and reconcile gently to confirmed server state without changing physics. Score, charge, candle membership and Burst outcomes are updated from authority, not fabricated through interpolation/extrapolation.

**R13.18 — Event fidelity under jitter.** WHEN frames arrive late, duplicated, coalesced or following resync, THE FRONTEND SHALL maintain stable per-run/per-match event identity and ordered delivery. Multiplayer uses verified `MatchEvent.Key = Tick*1024 + Index` with match/generation context; solo uses its actual `Seq`, `Missed`, `MissedTruncated` contracts and confirmed event identities. Critical one-shot candle/Burst/hit/terminal cues must never replay twice or disappear silently. An explicit truncated delivery triggers truthful resync/recovery, not fabricated missed event effects.

**R13.19 — Multiplayer liveness and boundaries.** WHEN a room client joins, disconnects, resumes or rematches, THE FRONTEND SHALL follow the real existing heartbeat/timeout, generation-aware resync, input sequence and result retrieval contracts (prior handoff mentions approximately 5s ping / 15s client timeout; confirm actual current implementation). A rematch clears old match presentation state and uses its new identity. No unsupported offline simulation or independent multiplayer authority.

**R13.20 — Audio is part of the baseline, not optional polish.** WHEN a browser gesture permits playback, THE FRONTEND SHALL use only the nine exact approved assets: `gameLoading.wav`, `multiplayer_theme.wav`, `ghost_main_theme.wav`, `candle_eat.wav`, `player_hit.wav`, `blast.wav`, `menu.wav`, `Gushy_wind.mp3`, `Ghost_knocking.wav`. One `AudioDirector` owns lifetime, mode routing, music/SFX buses, mute/volume, focus handling and one-shot deduplication. Source licences and actual codec compatibility must be verified before public release.

**R13.21 — Exact musical lifecycle.** WHEN on home/menu/lobby/histories/settings, `gameLoading.wav` is the persistent loop. WHEN active day multiplayer or original solo runs, `multiplayer_theme.wav` is active. WHEN the **night visual transition and verified ready gate** have finished, `ghost_main_theme.wav` starts together with night ambience; never during preparation or failed transition. All old layers stop/fade cleanly on exit, rematch, error and unmount; no stacked BGMs.

**R13.22 — Precise supernatural ambience.** DURING active night gameplay, THE FRONTEND SHALL play **two complete sequential** `Gushy_wind.mp3` segments followed by **one complete** `Ghost_knocking.wav`, then repeat under the continuously looping ghost theme. Sequence via actual completion callbacks, not guessed nine-second timers. Wind/knock have the skill's perceptual 80/90-relative-to-theme loudness intention, auditioned safely; ambience follows music mute. No unapproved screams, voices or new effects.

**R13.23 — Correct gameplay sounds.** WHEN a real relevant living candle is collected play `candle_eat.wav` once; WHEN a verified eligible weaker multiplayer player takes the specified contact reaction play `player_hit.wav` once; WHEN a real Burst cue occurs play `blast.wav` once; WHEN a real UI activation occurs play `menu.wav`. A button press alone does not earn Burst sound; source ghost historical cues do not become living pickup sound. Autoplay restriction or mute must not break gameplay.

## D. Complete connected multiplayer and solo journeys

**R13.24 — Live multiplayer end-to-end.** WHEN 2, 3 or 4 authenticated guests create/join a room, THE FRONTEND SHALL expose real room/slot/ready/start/countdown, the assigned head identities, authoritative arena/game HUD, match-local score, Soulfire/Overlit, contact, candle/Burst, current timer and honest disconnect/forfeit handling. Character choice is clickable ONLY if actual verified host contract supports changing it. No fake players or outcomes.

**R13.25 — Authorized results, spectators and rematch.** WHEN a match settles, THE FRONTEND SHALL call real `GetMatchResult(matchId)` and show `pending`, `finalized`, `aborted`, authoritative placings/ties/forfeits, movement/pickup/penalty scores and counters. Spectator join uses real `GhostLapHub.JoinRoom(JoinRoomRequest)` with `AsSpectator=true`; spectator has `SnapshotFrame.You.CanControl=false`, receives real snapshots/resync and cannot send gameplay. Result read requires room authorization. Real `Rematch` returns to lobby; next start seals **new** match ID/seed/config; old visuals/audio/cues cannot leak forward.

**R13.26 — Original solo recording and durable save.** WHEN a guest starts solo, THE FRONTEND SHALL present a daylight single-player real 2700-transition nominal 90-second original including actual Soulfire/Burst, then distinguish `Saving`, `Saved/Verified`, `Pending/Unavailable`, failure and retry. Only completed verified Originals appear as originals. The client never advances from a local fake victory/save state.

**R13.27 — Owner-owned history library.** WHEN a guest opens My Histories, THE FRONTEND SHALL browse actual completed Originals separately from saved `Challenge`/`Fork` children, with real score, kind, source, fork tick, verification and owner-scoped actions. It SHALL handle empty/foreign/unfinished/incompatible histories, offer eligible source/child navigation and never allow nested child forks or public share links.

**R13.28 — Full historical replay.** WHEN a saved Original or child is selected, THE FRONTEND SHALL load verified bounded HTTP trace chunks of 1–300 frames (source/child histories), retain stable source/version/hash identity, and present exact T0–T2700 inspection and local viewing playback. The history slider may seek loaded immutable frames without a database call per animation frame. No invented intermediate past, fake events, corrupted mixing or source final score substituted at earlier times.

**R13.29 — Signature Sands of Time timeline.** WHEN history/rewind/replay/compare is active, THE FRONTEND SHALL present an original **2.5D transparent hourglass physically left of a horizontal timeline**; precise tick/time readout, selected vs playback cursor, genuine event pips when available, source identity, fork marker and accessible pointer/keyboard seek. T0–T2700 inspectable; `Fork Here` enabled only for an owned verified original at T0–T2699, disabled at T2700. Timeline is **contextual**, not present on ordinary day live gameplay, lobby or home. The entire 16:9 arena remains visible above/outside the timeline dock, not cropped beneath it. Do not copy banking UI screens/art.

**R13.30 — Ghost Challenge from original start.** WHEN a user selects a completed verified original and starts Challenge, THE FRONTEND SHALL request real backend preparation, wait for verified Ready, transition coherently to night and start a new living run at T0 alongside the independent original ghost. Living and ghost share absolute tick; signed delta uses their **same-tick** scores. Completion persists a verified `Challenge` child and handles retry/recovery.

**R13.31 — Exact historical Fork.** WHEN a user selects eligible T on an owned verified original, THE FRONTEND SHALL show the real past at T and request genuine preparation; successful Fork inherits the **complete canonical state** at T, begins new living inputs at T+1, ends at absolute T2700, and saves a distinct verified `Fork` child. The original stays immutable, ghost uses the actual original future, a second operation creates a true sibling, same operation key remains idempotent. The frontend shall not falsely imply T2700 can fork, or child-to-child branching is available.

**R13.32 — Truthful cinematic rewind.** WHEN source data and preparation allow, THE FRONTEND SHALL transform the genuine selected history into a reverse-time montage from real archived frames, brief CRT, actual smoke, environmental night reveal, ghost and synchronized music, with chosen tick exact and no artificial ghost displacement. If backend Ready lags behind a ~1s montage/~3s scene-transition artistic target, hold an honest preparation state; do not start based on visual timer or display fake percent. Challenge and Fork have distinct valid flows.

**R13.33 — Meaningful original/child comparison.** WHEN comparing an owned saved child, THE FRONTEND SHALL use real aligned source/child historical tick, shared source prefix, actual fork boundary, truthful original/child score and signed child-minus-source delta, optionally compact genuine score curve. Replaying a child after refresh must work through the authenticated child trace; allow returning to source/another sibling. Not an invented two-player ghost fight or financial analytics dashboard.

## E. Performance, correctness and public acceptance

**R13.34 — Stable lifecycle and performance.** WHEN switching modes, replaying, restarting/rematching or resizing, THE FRONTEND SHALL reuse a single Pixi application per mounted scene, cache textures/atlases, avoid per-frame GPU allocation or heavyweight global React rerenders, release transient filters/audio handlers/listeners, bound interpolation/event buffers and preserve aspect ratio. Target approximately 60FPS on named reasonable desktop hardware with ~16.7ms frame-time **target**, not an unmeasured guarantee. Observe real Chrome and Safari, high-DPI behavior, scene/audio stress and memory over repeated journeys.

**R13.35 — Playable and accessible desktop UI.** WHEN using keyboard/mouse, THE FRONTEND SHALL provide semantically accessible buttons/dialogs, visible focus, readable contrast, correct Escape/back semantics, exact tick accessible timeline, and `Reduced Motion` behavior that preserves meaningful feedback. A Settings panel contains Music/SFX mute and volume, Reduced Motion and verified How to Play/Controls. No live authoritative pause, mobile controls, unapproved extras or inaccessible decorative pseudo-buttons.

**R13.36 — Browser+backend honesty.** WHEN a real browser plays, THE SYSTEM SHALL prove actual authenticated cookies, real WebSocket/session transport, real original/child REST APIs, correct owner boundaries, authentic multiplayer spectator/result/rematch and real history integrity without an offline alternate simulator. Backend corrections are isolated, source-verified and assigned to Codex unless a consequential game-authority boundary must be escalated. No speculative production test route or backend schema rewrite.

**R13.37 — Asset rights and production readiness.** WHEN preparing deployment, THE TEAM SHALL verify each approved source texture/audio file's actual presence, atlas metadata and applicable distribution rights; preserve Kenney/ParticleFX/audio provenance and font OFLs, use served build paths, and identify any uncleared public distribution as blocking release—not a reason to silently substitute unknown media. No user-specific absolute Mac paths in browser URLs.

**R13.38 — Real observed design quality.** WHEN marking a visual phase complete, THE IMPLEMENTER SHALL present and inspect actual running-game captures at realistic scale, including character expressions, day/night readability, cue synchronization and navigational flow. OPUS SHALL proactively harmonize art, timing, composition and interactions within skill constraints. Static screenshot-only, fake data, unchanged mock page or unplayed code does not satisfy completion.

**R13.39 — Cost-aware evidence ownership.** WHEN verifying a phase, Kiro/Opus SHALL perform only checks necessary to inspect its creative work and identify real defects; Codex Medium SHALL own costly builds, repeated regressions, real-host PostgreSQL/WebSocket sessions, timing/profiler measurements, browser automation and deployment acceptance. A failure discovered by Codex may return for focused Opus creative refinement. No green CI claims from absent feature-branch workflows.

**R13.40 — Full product closeout.** WHEN Spec 13 is declared accepted, THE TEAM SHALL provide an actual desktop browser journey covering: home→2/3/4 player gameplay→spectator/results/rematch; day solo original→verified save→refresh→full history→ghost Challenge; hourglass rewind→T1140 Fork→verified future→source/child replay and same-tick comparison, plus observed audio, exact faces, reduced motion, load/recovery, deployment/access and measured gameplay feel. Do not trade the signature artistic ambition for superficial task completion.

## Explicit exclusions (still binding)

No mobile or phone UX, touchscreen joystick, portrait overlay, controller system, global leaderboard, Elo/MMR, public profiles, social invites/chat/emotes, public replay/share token route, Redis, fake client authority, new multiplayer matchmaking, new rating/record store, AI opponent, nested saved-child fork, additional approved audio, new monster enemy, hit-glitch/RGB split, bloom-blurred bodies, tinted Burst ring, unsupported simulated live pause, guessed maze or invented historical data. Existing gameplay-led facial expressions are **not** social/network emotes.

## Phase-level acceptance map

| Requirements | Creative/implementation phase | Independent evidence owner |
|---|---|---|
| R13.01–05, 35 | 13.1 foundation; 13.8–13.12 contexts/polish | Codex B1/B4, Opus visual approval |
| R13.06–08 | 13.0 audit; 13.2 scene; 13.3 characters | Codex B0/B4, Opus visual approval |
| R13.09–10 | 13.3 expression performance | Codex focused cue regression; Opus visual approval |
| R13.11–15 | 13.5 environment; 13.6 effects | Codex asset/FX performance, Opus quality gate |
| R13.16–19 | 13.4 playable core; 13.8/13.11 integration | Codex B1/B2/B4, Opus feel review |
| R13.20–23 | 13.7 sound + integration | Codex audio lifecycle tests, Opus listening |
| R13.24–25 | 13.8 complete multiplayer | Codex real-client acceptance |
| R13.26–27 | 13.9 originals/library | Codex API/recovery evidence |
| R13.28–29,33 | 13.10 timeline/comparison | Codex trace/correctness/latency evidence |
| R13.30–32 | 13.11 challenge/fork/rewind | Codex real-host history journey |
| R13.34,36–40 | 13.12 whole-product refinement and acceptance | Codex B4/B5, Opus creative final |

**Creative exit criterion:** the approved assets, visual state system, social-free authoritative multiplayer, expressive soundscape, hourglass timeline and genuine alternatives must feel like one deliberately crafted game. **Technical exit criterion:** the same experience operates on actual backend data and survives the measurable browser/network/replay/recovery cases listed above. Both are necessary; neither can substitute for the other.
