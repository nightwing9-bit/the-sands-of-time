# Spec 13 — Design: One Ghost Lap World, One Experience Director

**Purpose:** implementation architecture for `requirements.md`, informed by the **entire** `ghost-lap-frontend-director` skill package. This design specifies **boundaries, dependency order, technical behaviour and quality gates** while giving Opus 5.5 High substantial creative authority **inside approved art, audio, gameplay and interaction constraints**. It is not a claim that any frontend file, library, asset or real browser test already exists.

## 1. Executive design decision

**Build one exceptional playable world with one unified visual/audio/interaction system, then expose every certified journey through it.** Do not approach the project as individually styled screens, randomly assembled shaders or a giant terminal-only checklist. The client's emotional progression is: crisp bright snow and expressive characters → satisfying real play → eerie but legible spectral night → recognizable real past → precise hourglass rewind → authentic alternate future → clear meaningful comparison. The same stage, UI grammar and audible world must accompany that progression.

The project's prepared assets and detailed skill are the creative foundation. **Opus must demonstrate intelligent authorship**, not passive obedience: consider legibility, hierarchy, timing, compositing, ease and cohesion at every step; revise director-baseline aesthetic parameters when actual play evidence demands it. Do not replace settled assets, disregard exclusions or introduce unapproved art/sounds to show creativity. **Creative synthesis, not feature invention.**

### Hard inheritance

| Source | Authority | Implementation consequence |
|---|---|---|
| Actual Ghost Lap backend, Spec 10 evidence | `ghost-mp-v2` authoritative room, gameplay, results, spectator and rematch | Consume real hub and `MatchOutcomeView`; no fake multiplayer or Elo |
| Spec 11 acceptance (`docs/architecture/16-ghostlap-spec11-final-acceptance.md`) | verified saved originals/children, immutable forks, history trace, timing and signal semantics | Use correct private API and tick identity; no frontend Timeline reducer |
| Spec 12 `kiro-log.md` inspection/handoff | `JoinRoomRequest.AsSpectator`, `SnapshotFrame.You.CanControl`, result access, rematch | Render truthful bounded multiplayer choices; no made-up spectator privileges |
| Skill `SKILL.md`, refs `00`–`12` | exact approved aesthetic/assets/choreography plus labeled director defaults | Required reading before creative work; do not implement from screenshots only |
| Actual `$GAME_ASSETS/characters.md` | individual character shapes/eyes/mouth/gaze/reactive face rules | Read *actual file* before composing faces; 0/1 user correction applies |
| User's latest instructions | desktop-only, ambitious Opus-owned frontend, dedicated backend/technical verification, timeline hourglass left | No mobile or speculative backend tasks; front-end focus remains intensive |

**Known source location:** `~/Desktop/the-sands-of-time-phase1/design-inputs/game-assets/`; **skill location expected:** `design-inputs/ghost-lap-frontend-director/`. These are source/art-location hints, not proof a code agent can access the path or that the frontend lives there. **Existing frontend root must be discovered.** Old Spec 13 references to `web/apps/ghost-lap` are hypotheses until inspection, not a license to generate a parallel app.

**Status taxonomy:** `CONFIRMED`/`DIRECTOR BASELINE`/`VERIFY`/`OPTIONAL/DEFERRED` from skill; plus technical `IMPLEMENTED`/`TESTED`/`PENDING`/`BLOCKED` in evidence. Never conflate these labels.

## 2. Responsibilities and build ownership

```text
                       VERIFIED AUTHORITATIVE SERVER
  ghost-v1 / arena-v1          ghost-mp-v2 / arena-v1       Marrow Timeline
  SoloOriginals, SoloBranches  GhostLapHub & room outcome  frozen states/records
             |                       |                           |
             +----------- Auth + HTTPS/WSS (actual contracts) -----+
                                        |
              CLIENT/TRANSPORT INTEGRATION BOUNDARY
             actual DTOs | guest cookies | hubs | input seq | retries
             resync | stable event keys | trace paging | typed errors
                                        |
                 FRONTEND-OWNED READ-ONLY PRESENTATION MODEL
    view frame index | authoritative cues | world geometry | mode status
                                        |
                         OPUS EXPERIENCE COORDINATOR
      scene mode | visual clock | HUD state | FX cue dispatch | audio state
                        /                |                  \
             PIXI WORLD             REACT UI             AUDIO DIRECTOR
       snow/walls/heads/candle    menus/HUD/history    music/SFX/ambience
       ghost/filters/transitions  hourglass/dialogs    focus/mute/cleanup
```

This is a conceptual dependency diagram—not mandatory new framework or file layout. Build on the real repo's existing React/TypeScript conventions. **No local frontend owns authoritative simulation/score/event results.**

### Agent division, without expensive duplication

**Opus High owns:** the design system, all Pixi scene composition, expression logic, client visual motion policy, presentation-mode state machine, VFX/audio interactions, navigation/HUD/history/time dock, complete creative integration and all aesthetic/play-feel corrections. Opus should actively *see* running visuals and listen to real playback. A small focused creative check is part of the job.

**Technical integration scope:** code-first audits, actual hub/HTTP typed clients, guest auth/session handling, safe transport retries and resync, narrow backend corrections, binary/atlas metadata and legality audits, repeatable build/test/debug, Playwright-style browser flow/trace/performance measurement, OS/device/deployment gates. Client transport work must **not restyle screens, choose visual recipes or rewrite the approved scene architecture**.

**You/main agent:** decide scope, assess true trade-offs and delegate exceptional architectural blockers. Escalate a consequential authority/semantics change to Kiro Sonnet High or Opus High only if a narrow compatible correction cannot preserve frozen contracts.

**Shared-file ownership rule:** 13.1 must agree on an **actual typed semantic boundary**; the technical integration track owns `net/transport` and verified DTO mapping; the creative track owns `experience/render/ui/audio`. Do not simultaneously refactor the same integration files. Exchange short source-grounded handoff and test/capture evidence on each boundary. Commit focused changes; preserve unrelated working-tree edits.

## 3. Preflight: source-based integration and asset provenance

### 13.0 factual discovery gate (before production composition)

Record actual paths, versions, build script, package manager/lockfile, existing app routing, real assets present on the coding machine and all server contracts needed by Opus. Read `SKILL.md` plus refs `00`, `01`, `09`, `11` and inspect code and existing frontend. Read `characters.md` **when actually available** and flag inaccessible source rather than guessing its content.

Inspect exact Kenney transparent PNGs/atlases, wall corners/endcaps, hero head/face elements, orange candle PNG and smoke PNG: pixel/frame counts, transparency, frame order, texture limits, legal provenance. Skill quotes *nominal* 90 candle / 84 smoke frames; physical source can differ. `scripts/asset_inventory.py` is read-only; run it against actual verified root. Inventory audio durations/samples/codecs and licenses; don't assume WAVs are CC0 or available on Mac just because a screenshot exists. Confirm `snow_arena.webp` exists and preserves monster-shadow layout; don't serve master PNG.

For backend, record exact room creation/join/ready/start/rematch/result and spectator semantics, owner privacy, world dimensions/origin/maze/collider geometry, player-slot and character identity, input key/movement vector/Burst edge semantics, state field names, tick timing, stable cue identifiers, solo save/prepare/start/finish keys and history routes. **If geometry or character selection action is unknown, block just that consumer**, never invent a map or clickable selector. Map missing decision/contract to specific owner and gate; don't reopen frozen rules for a rendering shortcut.

### Approved media mapping (from skill `01`, exact source names)

| Purpose | Source, expected under `game-assets/` | Required treatment |
|---|---|---|
| Snow/monster shadow | `backgrounds/snow_arena.webp` | runtime full-world static source, aspect preserved |
| Source master | `backgrounds/snow_arena_master.png` | retain only in source inputs, not duplicate runtime |
| Walls | `kenney_pixel-platformer-blocks/` | derived greyscale/slate atlas from verified alpha/tiles; real server placement |
| Heads/faces | `kenney_shape-characters/`, `characters.md` | four exact assembled, head-only, no guessed atlas cropping |
| Orange candles | `haunted_candle.png` | verified frame slice/transparent atlas; shared AnimatedSprite textures |
| Smoke | `smoke_transition.png` | verified animated transition sheet, no perpetual fog |
| Audio | `sounds/` with exact 9 approved names | central registry, legal/publication checks |
| Fonts | Source Serif 4 / Source Sans 3 | compatible self-hosted WOFF/WOFF2 with OFL provenance |

Approved source files remain immutable. Generate *derived* atlases and runtime copies in the real frontend's served namespace, keyed through one registry; never use `file:///Users/...` browser URLs. Verify production base paths, type/mime, preloading, high-DPI texture sizes and actual GPU `MAX_TEXTURE_SIZE` before shipping. Permission for game use does not automatically prove raw asset redistribution rights: log license source and treat unverified public-repo rights as a real release blocker, not creative permission to substitute unauthorized files.

### Libraries and resource policy

The skill proposes PixiJS **v8**, community `pixi-filters` **v6**, `@pixi/sound` **v6**, Source Serif 4 and Source Sans 3 via Fontsource. Use actual installed package versions/exports and existing package manager; do not blindly paste version-skewed filter examples. Vanilla Pixi within React mount is sufficient; do not install `@pixi/react`, GSAP, particle engine, or heavy 3D hourglass stack without concrete need/compatibility. Official Pixi v8 guides and the skill's verified package references govern API calls. **No dependency upgrade merely to make a visual prototype convenient.**

## 4. The unified frontend model (one scene, not three apps)

### State ownership

| State category | Owner | Examples | Never do |
|---|---|---|---|
| Authoritative game fact | backend | tick, positions, walls, charge, scoring, candles, Burst/collision, terminal | recompute it in Pixi |
| Transport/session | typed client adapter | auth, seq, run ID, subscriptions, resync, retries, pending/unavailable | infer completion from frontend timer |
| Historical read data | existing backend + verified loader | verified trace frames, hash/version, comparison samples, child kind/fork tick | synthesize inaccessible replay |
| Presentation projection | Opus read-only view adapter | screen position, pupil direction, buffered visual time, readable HUD | change canonical state |
| Transient artistic state | Opus | expression reaction time, halo pulse, CRT/smoke, sound mix, highlighted selection | emit new canonical gameplay event |
| Local user preference | frontend | music/SFX levels, reduced motion | claim server-side game pause |

A `PresentationAdapter` consumes source-verified DTOs and produces typed **view** states and semantic cues for Opus. These types are not new backend contracts. Confirm real field names during 13.0; do not invent `ServerTickTime` or player-choice APIs because a UI type would be convenient.

### Example interface responsibilities (illustrative ONLY)

```ts
// Illustrative frontend semantic seam; names are not certified backend DTOs.
type VisualMode = 'menu' | 'day-play' | 'history' | 'preparing'
                | 'rewinding' | 'night-play' | 'results';

type TrustedFrame = {
  runIdentity: string;                  // real match/solo run identity
  absoluteTick: number;                 // real authoritative tick
  world: Readonly<WorldProjection>;     // authenticated server values
  sourceGhost?: Readonly<GhostProjection>;
  receivedAtClientMonotonicMs: number; // local transport arrival metric only
};

type VisualCue = {
  runIdentity: string;
  tick: number;
  index: number;                       // real index or verified derived stable ordinal
  type: string;                        // mapped from actual event payload
  payload: unknown;
};
```

Do **not** invent authoritative `WorldProjection` or derive stable ordinal from unverified assumptions. Copy exact identifiers, versioning and state-diff semantics from actual adapters. App-local types should be *readonly* and discriminate modes/errors so ghost source and living player cannot accidentally share mutable candle/score state.

### ExperienceCoordinator state machine

```text
ENTRY / MENU
  |--> ROOM_CONNECTING --> ROOM_LOBBY --> COUNTDOWN --> DAY_MULTIPLAYER
  |                                                  |--> MATCH_PENDING --> RESULTS
  |                                                             |--> REMATCH_LOBBY (new match identity)
  |--> SPECTATOR_JOIN --> SPECTATING --> RESULTS_IF_AUTHORIZED
  |--> SOLO_PREP --> DAY_SOLO --> SAVE_PENDING --> ORIGINAL_VERIFIED
  |--> HISTORIES --> ORIGINAL_OR_CHILD_HISTORY --> HISTORY_VIEW
                                    |--> CHALLENGE_PREP (T0) --+
                                    |--> FORK_PREP (T0..T2699) +--> READY
                                    |                              |
                                    |                   [TRUE montage/CRT/smoke]
                                    |                              |
                                    +------------------------> NIGHT_GAMEPLAY
                                                                     |
                                                            CHILD_SAVE/RECOVERY
                                                                     |
                                                             FUTURE / COMPARE
```

Each state has: legal actions, actual server readiness, keyboard ownership, scene mode, HUD visibility, audio bus/sequence, cancellation/abort semantics, error/recovery, effects cleanup, and exit destination. Visual transitions **never** mark backend transitions as complete. If smoke finishes while preparation remains Pending, hold an honest, visually coherent waiting state. If Ready is achieved before visuals finish, avoid racing while inputs are visually blocked: sequence actual start/enable only once presentation is able to show and control the authoritative first tick, using existing host lifecycle. Do not create a server-side artificial 5-second delay. If unexpected server start semantics contradict sequencing, stop and solve boundary explicitly.

Day multiplayer and day solo share scene architecture/audio mood; night Challenge/Fork use **same geometry**, environmental treatment and ghost, not a second copied game. History viewing reuses scene with immutable archived snapshots and does not start a live runner. Modal/blur does not pause authority.

## 5. World transform, scene layers and rendering lifetime

### Coordinate fidelity and arena fit

One `WorldTransform` maps **verified world bounds, origin, axis direction, map geometry and positions** to Pixi scene coordinates. Do not treat screenshot 1600×900 or source WebP ~1672×941 as world units. One resize path computes contain scaling/letterboxing and DOM HUD attachment, with correct pointer-to-tick mapping; preserve the **whole 16:9 arena**, no wall crop or intentional character disappearance behind lower UI. If a bottom history dock opens, **refit full arena above the dock** rather than overlaying/occluding the bottom wall. Numerical tick/history selection remains exact even on a small desktop window.

### Recommended logical scene graph (adapt to actual Pixi APIs)

```text
GhostLapStage — one Pixi Application per mounted arena, reused while modes change
  ├─ WorldViewport — one verified server-to-scene transform
  │   ├─ SnowBackground — stable `snow_arena.webp`, baked monster shadow
  │   ├─ GreyWallEnvironment — derived tiles, real collider layout
  │   ├─ EnvironmentGradeGroup — only snow/walls receive cool night grade
  │   ├─ SharedCandleLightmap — low-resolution pooled warm light for environment
  │   ├─ CandleSprites — shared validated AnimatedSprite textures
  │   ├─ GroundShadows — small bottom-right anchored ovals
  │   ├─ GhostLayer — cyan evil-face historical ghost, BEHIND living heads
  │   ├─ HeadHalos — shape-matching aura behind corresponding crisp head
  │   ├─ LivingCharacterHeads — four Kenney shapes + separate facial pieces
  │   ├─ TransientWorldFX — bounded Shockwave, no React DOM distortion
  │   └─ TransitionFX — CRT/smoke/history montage when active only
  └─ ReactOverlay — DOM HUD/dialogs/menu/history dock outside world grading
```

Actual filter graph may use cached environment RenderTexture(s), to avoid tinting players/ghost/candles/HUD. Test environment grade/light interaction, clip bounds, coordinate conversion for Shockwave center and alpha/transparency. Shared frozen textures, no per-player GlowFilter. Allocate/decode assets once, update sprite properties per frame, avoid `React setState` for all 60Hz sprite transforms. Maintain robust cleanup on result/leave/rematch/history navigation; no stranded filters or audio loops.

### Asset display truths

Snow and shadow belong to the background, not a monster actor; grey walls are modular visual obstruction tied to actual colliders; the flame sheet remains orange in day/night; night uses same maze and candles. Player shadows are small decorative only; no actual physics. Avoid fake perspective maze layout, per-wall dynamic shadow generation, artificial camera shake, vignette or enlarged fake Burst range.

## 6. Opus-owned character direction and game acting

### Four authoritative visual identities

| Player slot | Head geometry | Hue | Invariants |
|---|---|---|---|
| 1 | circle | orange | remains round, no rotation |
| 2 | softly squared shape | pink | distinct from green rounded square |
| 3 | pointed diamond | yellow | face elements correctly centered/clipped |
| 4 | broad rounded square | green | distinct silhouette with soft corners |

Actual server assignment must be verified; don't fabricate selection support. `CharacterView` contains a low-alpha ground shadow, shape-backed coloured halo, original head texture and crisp reusable eyes/brows/mouth; pupils move with valid actual facing (only pupils). The heads have **no body/arms/legs**. Render at *gameplay size*, not only enlarged sprite sheet preview.

### Emotional state machine

| True charge | Normal face | Halo |
|---:|---|---|
| 0 | frown | none |
| 1 | identical frown | none |
| 2 | neutral | very faint, correct silhouette/hue |
| 3 | small happy smile | restrained growth |
| 4 | big happy grin | strongest but below candle prominence |

Backend 0 and 1 stay numerically distinct in HUD; no change of gameplay initial charge. Halo seed alpha `0/0/0.08/0.14/0.28`, subtle expansion 5–8% from skill `06`, **DIRECTOR BASELINE** to tune after actual play. Use reusable halo textures/masks, not dynamic full-body blur.

Transient expression precedence: confirmed result winner (results only; correctly handle ties) > qualifying actual hit/unequal-contact (2s only on actual victim, weaker only for the special collision) > confirmed 4/4 release exertion while valid > normal current charge face. Replay duplicates do not restart timer; on expiry revert to **latest** charge, not stale pre-event expression. When backend explicitly provides a different reaction/expiry field, follow it. Ghost always displays approved mean eyebrows/narrow eyes/crooked smirk independently of recorded face. Validate true event mapping and do not infer weaker party by reading projected face colours.

Visual creative mandate: Opus should consider eye scale, relative mouth arc, feature silhouette, gaze clipping, small contact shadows, spacing, rhythm between face changes and power glows so each character **acts** rather than switches crude icon frames. Exact event semantics remain inviolable.

## 7. Rendering effects direction: emotionally strong, deliberately restrained

### Night environment and candle pooling

Environment-only cool slate grade, not `ColorMatrixFilter.night()` green preset; snow texture and baked monster shadow remain identifiable. Shared soft warm radial accumulative lightmap for flame positions, minimal daylight strength vs readable diffused night warmth; candle flame unchanged. Batch shared textures, update lightmap only as needed, no heavyweight filter per candle and no hard illumination edges. Ghost does **not** illuminate the environment; living character colors stay original. Keep contrast on pink/yellow and wall edges.

### SpectralGhostEffect

Compose approved Kenney head+permanent evil face first; then luminance-map natural source pixels into cyan/white while retaining facial contrast and alpha. Skill `06` director seeds: dark `#1d516a`, middle `#62b9c5`, pale `#e8ffff`, edge `#83e4eb`, alpha around `.55–.72`. These are **tunable visuals**. One shared Pixi v8-compatible shader/filter approach, no body blur, artificial jitter or readback. At precise living overlap, head still occupies true position with faint spectral edge behind it. Ghost historical events are allowed visual reference but never affect living candles/score/collision or produce living SFX.

### SoulfireShockwave

Confirmed action's position in single world transform; temporary scoped `ShockwaveFilter` with neutral brightness, subtle pulse of **preexisting** halo; no ring tint, extra powder particles, hit-glitch, bloom, shake or bright flash. Time and perceived radius are visual and must correspond respectfully to actual game effect; damage/radius/eligibility always authoritative. Handle multiple concurrent bursts by batching/reducing optional distortion without dropping the underlying confirmed cues; short resource lifetime and proper teardown.

### CRT, smoke, historical montage

Brief `CRTFilter` (skill baseline 250–500ms, no persistent strobe), actual transparent smoke atlas (nominal 84/30fps ≈2.8s, **verify physical sheet**), coverage-safe environment switch under mask/crossfade, then remove filters. Real backwards montage of **prepared** historical snapshots, approximately one second as skill baseline, ending at exact selected T. Never synthetic event footage or reverse a fake pre-rendered video. Reduced-motion path displays accurate source/tick/night state with minimal decorative motion. Backend preparation observed ~5.7s; sequence art timing is not a promise the race starts in 3s.

### Emotional consistency

Opus has authority to tune easing, dynamic opacity, clip region, spacing and sound timing **within constraints**, after seeing actual play. Every effect must have a single event trigger and a single cleanup owner. Authentic event sequencing is more important than sheer particle count. Distortion should feel powerful because of timing, clarity and audio coupling—not because more shaders were added.

## 8. The 30Hz authority / ~60FPS presentation architecture

A beautiful frontend with snap-to-30Hz sprites will fail the user experience even if server replay is perfect. Make the **motion pipeline a core rendering feature by 13.4**, not late polish.

### Three clocks, never conflated

1. **Authoritative logical tick**: simulation/game state; used for all gameplay data and historical alignment.
2. **Server commit/send diagnostics**: useful for measuring schedule lag but *not* equivalent to a browser wall-clock offset. No client clock assertion about one-way latency without synchronization.
3. **Local render monotonic time**: drives only visual interpolation, transient art and on-screen smoothness.

Prior Spec 11 local host evidence: commit p50 ~33.46ms, p99 ~50ms, max ~92.9ms; client reception max gap ~93.3ms, cumulative schedule lag up to ~332ms; no live input refusals in corrected acceptance run. These are **measurements**, not proof that production always meets timing. A visual approach must handle bursts of late frames without changing server 30Hz pacing.

### Frame timeline and interpolation

- Maintain a small **bounded** buffer of immutable recent authoritative frames per run/generation, keyed by ordered tick/sequence. Deduplicate and reject stale mismatched generation after rematch/reconnect; track true gaps.
- For remote players, ghost and other read-only entities, choose a controlled render-time delay of approximately one or a few 30Hz ticks (**tune from measured jitter**), then interpolate only continuous rendered coordinates between **known** valid server states. At missing intervals, hold briefly or use **bounded visual extrapolation** only when safe; never fabricate events, candle consumption, points or historical state. Rebase after resync.
- For local player, send valid requested inputs immediately and use **cautious visual-only responsiveness**: either render a tiny bounded displacement/facing preview or an input-aware predicted visual position whose maximum error is capped and can be corrected; if trustworthy movement constraints unavailable, use smaller anticipation plus server interpolation. Never execute a second canonical reducer, collide with imaginary walls, score or predict a confirmed Burst. Correct toward server without teleport-like drift while remaining within credible bounds; prioritize actual collision position and authoritative corrections.
- For historical replay, **do not interpolate past game state into new canonical states**; seek selects exact state/tick. Cosmetic motion interpolation between existing verified positions during view-only playback is acceptable only if clearly separated from the exact selected historical tick and event ledger.
- Never animate power/score/candle/Burst from visual predicted position. Trigger one-shot feedback only from confirmed semantic event IDs. Under `MissedTruncated` or true gap, hold/reconcile and request existing resync/history where supported; don't pretend missing 64+ cues were seen.
- Neutralize held inputs on blur, leave, connection loss and input mode switch; avoid key autorepeat manufacturing repeated one-shot Burst edges. Distinguish local key down, network acceptance, confirmed cue and charge consumption in presentation.

**Do not prematurely introduce rollback netcode, WebRTC, a second local physics engine, huge prediction queues or new server tick protocol.** If real browser feel is insufficient, Review footage and measurement evidence, then tune bounded render policy against validated transport behavior.

### Semantic cue pipeline

Categorize incoming information as: (a) persistent authoritative snapshot (can replace previous), (b) sequenced one-shot cue (must process once), (c) connection/preparation/terminal status (state machine transition). Use `runId/matchId` + genuine generation + tick + event index/key for dedup. Multiplayer event `Tick*1024+Index` is **known from Spec 12** but still needs match/generation; solo historical/living cue order uses actual `Seq`/`Missed`/`MissedTruncated` and contracts; never invent a magic globally unique key from approximate position. Distinguish live living events from historical ghost cues to avoid dual sounds. Exactly-once *visual consumption* within received/resynced scope, with honest resync if delivery truncated—not a claim of reliable full event replay over an unbounded outage.

### Client network/recovery interface — technical boundary

The client integration layer supplies typed auth and SignalR lifecycle, room/spectator/resync/result methods, original/challenge/fork start/input/save/retry/resolve, children/original trace/comparison requests and error mapping. Preserve server owner scoping and restricted DB role; no client-supplied owner authority. Keep heartbeat and timeout verified from current hub. Frontend can consume `Ready`, `Pending`, `Unavailable`, `Conflict`, `Aborted`, `not_found` or actual typed statuses; no UI fake happy-path.

## 9. AudioDirector and sound as part of the game's acting

One `AudioDirector` persists across screen/mode changes, with stable handles and distinct **music** vs **SFX** buses. The night wind/knock ambience follows music volume/mute and has calibrated relative trim. Nine approved files, exact source filenames:

`gameLoading.wav` · `multiplayer_theme.wav` · `ghost_main_theme.wav` · `candle_eat.wav` · `player_hit.wav` · `blast.wav` · `menu.wav` · `Gushy_wind.mp3` · `Ghost_knocking.wav`.

| State | Required soundtrack | SFX/ambience notes |
|---|---|---|
| Entry, home, setup, lobby, settings, history menu | persistent `gameLoading.wav` | do not restart on every nav |
| Day multiplayer + Solo Original **active** | loop `multiplayer_theme.wav` | approved living cues |
| Ghost preparation / reverse montage / CRT / smoke | controlled transition fade/gap | **no ghost theme yet**, no invented CRT sound |
| Night Challenge / Fork, after visual reveal and ready | loop `ghost_main_theme.wav` **plus** sequential ambience | exactly wind, wind, knock, repeat |
| Results, failure, leaving night | release night voices; return to appropriate menu state | no stale or doubled loops |

The wind cycle **must** use `ended`/completion callbacks: `Gushy_wind.mp3` once, again, `Ghost_knocking.wav` once, repeat while night gameplay. Perceptual loudness direction ~80 wind / ~90 knock / 100 ghost theme, not literal fixed volume multipliers. Real candle collection, eligible weaker-player contact, confirmed Burst and actual click own SFX. Suppress duplicates on React rerender, resync and source ghost cues. Audio unlock after user gesture, codec fallback verified in desktop Chrome/Safari, focus/mute stops queued callback loops, no music stacking or clipping. Unapproved voiceovers/screams/CRT sounds remain excluded.

Opus must **listen** to transitions and combined night soundscape, not only run an audio unit test. Scale relative gains and fades as artistic work, preserve event semantics. Two sound control sliders (music and effects) plus mute, reduced-motion; no separate ambience slider required.

## 10. Navigation and modes: integrated functional journeys

This is **functional topology**, not a mandate for a route per node. Build UI from shared tokens/primitives and real calls. The skill `03` and `12` own screen/click details.

```text
MAIN MENU — Multiplayer / Solo Run / My Histories / Spectate / How to Play / Settings
  MULTIPLAYER --> CREATE/JOIN --> LOBBY (real slots/ready/authorized start)
                  --> DAY LIVE 2/3/4P --> FINALIZED/PENDING/ABORTED RESULTS
                  --> REMATCH to lobby --> new MatchId/seed/config
  SPECTATE --> eligible room --> same world HUD, readonly --> result while authorized
  SOLO RUN --> DAY ORIGINAL --> VERIFIED SAVE/PENDING/FAILURE --> ORIGINAL LIBRARY
  MY HISTORIES --> ORIGINALS [inspect | replay | challenge from T0 | rewind&fork]
               --> SAVED FUTURES [replay | compare | source]
  ORIGINAL HISTORY --> HOURGLASS LEFT + PRECISE TIMELINE --> FORK AT T0..2699
                        |--> READY --> REAL MONTAGE/CRT/SMOKE --> NIGHT FORK
  GHOST CHALLENGE FROM START --> READY --> NIGHT CHALLENGE
  SAVED CHILD --> REPLAY --> ORIGINAL-CHILD SAME-TICK COMPARISON
```

### UI primitives and fidelity

One `FrostPanel`, `Heading`, `GameButton`, `BackLink`, `InfoRow`, `StatusPill`, `SegmentedSoulfire`, `ScoreReadout`, `ScoreDelta`, `MatchStandingRow`, `HistoryRow`, `ConfirmDialog`, `ProgressOverlay`, `ToastArea`, `EmptyState`, `ErrorState`, `AudioSlider`, `KeyboardHint`, `HourglassMark` and `TimelineScrubber` family. Reuse instead of bespoke per-screen styling. Good typography and deliberate negative space, readable contrasts, minimal HUD around arena perimeter, 0–4 charge numeric and Overlit indicator separate, no fake decorative pause. Use Source Serif 4 for headings, Source Sans 3 for HUD and controls, `tabular-nums` for time/scores. Baseline tokens from skill `02` start the design; Opus may tune palette/spaces **holistically**, not per page at random.

**Contextual screens:** home/help/settings; create/join, lobby, day arena and match results/rematch; spectate; day solo and save result; owned originals/futures; original/child replay; prep/rewind/fork/ghost race; branch comparison. Character selector **conditional on actual backend contract**. No leaderboard, account profile, social invitation service, public replay-share or touch controls. Local fullscreen optional only if actually supported. Spectator result must be requested while roster/room access exists; don't broaden auth.

**Back/exit:** historical replay view can be paused locally; active live gameplay continues server-side. Confirm abandonment when leaving real running session; correctly call existing cancel/leave. `Escape` closes safe dialogs, never silently discards verified history or pretends to pause server. Browser refresh and reconnect return real statuses, not unauthenticated fake home-state success.

### Multiplayer specific data semantics

`GhostLapHub.GetMatchResult(matchId)` returns `MatchOutcomeView`: `Decision` one of `pending`, `finalized`, `aborted`; rows include match-local shared ranks/ties, forfeits below finishers, actual total/movement/pickup/penalty scores, contact/Burst/pickup counts, `MemberId`/`Slot`. No calculated Elo/rating, even if `ratedEligible` metadata exists. `JoinRoom(JoinRoomRequest)` with `AsSpectator` and `SnapshotFrame.You.CanControl=false`; resync uses real room generation and event/cursor fields; actions denied for spectators. `Rematch` returns room to lobby; next start introduces a new identity. Browser must not carry old event set, result, snapshot or audio into rematch. Existing multiplayer snapshots may publish slower than 30Hz simulation—renderer interpolates accordingly, never assumes each frame is a single logical tick.

### Solo/history service map (source-grounded from Spec 11 acceptance, recheck names in code)

- Hub: `/hubs/ghostlap-solo`, `PrepareChallenge`, `PrepareFork`, `StartChallenge`, `StreamChallengeInputs`, `FinishChallenge`, `FinishFork`, `CancelChallenge` (method signatures and actual streaming path must be source-verified).
- Original history HTTP: `/api/ghostlap/solo/originals`, `/{id}/inspect/{tick}`, `/{id}/trace?from=&count=&v=`.
- Child HTTP: `/api/ghostlap/solo/children?source=&after=&limit=`, `/{id}`, `/{id}/trace?from=&count=&v=`, `/{id}/comparison?from=&count=&stride=&v=`.
- Trace chunks **1–300**; completed verified original/child has T0..2700 **2701 frame indices**; `solo-history/1`; stable original/child IDs, hashes/versions, nullable continuation. Bounded comparison curve may return 271 genuine samples at stride 10; don't substitute intermediate fictional scores.
- Spec 11 host report measured original full trace ~5.706s, child ~6.133s, curve ~1.052s, preparation ~5.679s locally. Two uncached full traces fetched **sequentially** could take ~11.8s. These are observations; design progressively: show available score curve first where useful, fetch one-time verified original/child data intelligently, cache per verified identity, and never block the live 30Hz loop or slider movement on new historical reads.

Auth is guest-cookie scoped, original & child library owner-only, source corrupt/incompatible denied. The source history must not mutate; Challenge T0 and Fork(T0) have distinct saved types; Fork(T2700) invalid; parent child can't fork. Stable operation key idempotent across refresh. No `GET` from public history share route.

## 11. Timeline, hourglass and rewind: the hackathon centerpiece

### The hourglass is an identity, not a generic SVG placeholder

Opus creates the **original 2.5D glass-and-gold-sand hourglass** (skin direction in skill `08`), physically at the **left** of the **horizontal contextual history rail**. It must be compelling at gameplay size, with readable glass/sand materials, restrained falling sand and reduced-motion fallback. May use original SVG/Canvas/pre-rendered artwork, not source bank icon or a 3D engine. Hourglass animation is **not** the simulation clock; if tied visually to selected time, map accurately to the tick. Prototype its relationship to menu/frost language early in 13.1; only fully integrate history in 13.10.

### Exact tick and interaction behaviour

- History index **T0..T2700**, 30Hz; show elapsed `mm:ss.mmm` derived from tick/30 plus integer tick label. Authoritative tick wins over rounded display milliseconds.
- Pointer drag/click selects nearest available **actual integer recorded tick** in prepared frames; keyboard Arrow single tick, Shift+Arrow baseline 30 tick view step, Home/End endpoints, accessible ARIA slider. Play/pause is **viewing only**, no server pause.
- Fork diamond available only for **owned completed verified original** and T0..T2699. T2700 inspection-only and visibly disabled reason; no nested child fork.
- Selected cursor, running view-playback cursor and existing child's true fork marker have separate identities. Genuine event pips only for real recorded event cues. Clean rail if data absent; do not invent dramatic history beats.
- On comparing a saved child, show original and child shared prefix **as shared** through T, and authentic alternative future only after; same-tick source vs child score/delta; optional quiet true curve if loaded. Avoid a banking chart, fabricated branching graph or cartoon track where forks split before reality.
- When dock opens, **scale/re-fit the full arena above it**. Time rail is **not** placed on every screen.

### Genuine cinematic Fork/Challenge (contract order)

**Fork:** owned original history loaded → select T with exact frame → Fork action/confirmation → request real PrepareFork / maybe concurrently prefetch verified frames → optional truthful reverse montage (~1s director target), CRT (~250–500ms) and actual smoke sheet (~2.8s nominal) covering day-to-night environment swap → wait for real Ready even if visual finishes → start living from canonical state T, first input tick T+1 → aligned original ghost appears night behind living → synchronized night audio begins only once night gameplay actually visible/ready → persist verified `Fork` on complete → replay/compare.

**Challenge:** original selected → real PrepareChallenge from T0 → may use same atmospheric night reveal but **not** arbitrary history-fork moment → living starts T0 with source ghost → save distinct Challenge child. Do not conflate kinds or operation keys.

**Critical handoff:** If actual backend semantics require Start to occur earlier/later than animation, set a clear barrier so no authoritative ticks advance while the client hides gameplay unintentionally; never manufacture a fake server pause or delay a running tick stream to fit a cinematic. Preparation and animation can overlap; real Ready gates real Start and visible ability to control. An honest preparing screen is preferable to ghost playback with missing trace. Audio timing follows transition **complete + gameplay ready**, not completion of a promise-like timeout.

### Saved history and compare

Cache loaded histories keyed by (type, child/source ID, hash/version), not merely list position. On selection changes discard stale pending responses and old cue identities. Seek locally across verified frames with no restore-per-drag. Scores are same-tick, child minus original, not source final. A selected original/child can be replayed after browser refresh using authentic endpoints. Show loading/progress honest: actual frame count or indeterminate; never 87% based on elapsed seconds.

## 12. Deliberately cross-cutting quality and creative governance

### How Opus shall work: the six-part vertical slice

For **every** major Opus task: (1) read relevant skill refs and already agreed theme/adapter decisions; (2) inspect real approved asset and event contract; (3) make one cohesive implementation with carefully tuned creative baseline; (4) **observe it in a running game** at gameplay scale via screenshot/short video/audio; (5) refine the specific perceived weakness without drifting; (6) hand off a concise reproduction plus objective technical checks to the verification stage. A task is not complete simply because the TypeScript compiler passed or every source file exists. Opus must proactively reason about how its work affects earlier and later scenes.

**No high-credit terminal camp:** Opus may run a quick local preview/build if needed to see its own output, but do not ask Opus to wait on repeated PostgreSQL containers, long WebSocket simulations, broad Playwright suites, four-room load matrices or multi-minute regression loops. Those belong to focused technical verification. Conversely, *do not economize away* the critical short Opus visual observation and iterative creative corrections.

### Three intermediate creative holds and a final synthesis

| Hold | After | Opus must show | Technical verification confirms | Go/no-go |
|---|---|---|---|---|
| **A: The living game** | 13.4 | real snow, geometry, four crisp expressive heads, smooth responsive player motion, restrained HUD | world transform/input/liveness/frame profiling | fix art composition and feel **now** before extra effects |
| **B: Audiovisual identity** | 13.7 | day/night, orange candles, correct cyan ghost overlap, pure Burst, CRT/smoke, character reactions and nine-file soundscape | hot filter/audio disposal, event dedupe, FPS, browser codecs | coherent art and timing across scenes or revisit |
| **C: Time-travel showcase** | 13.11 | original→rewind→fork→true ghost future→saved child→compare; hourglass unmistakable | real host T1140 fork/auth/history/ready/save and resync | must be emotionally compelling **and** canonical |
| **Final** | 13.12 | complete polished connected product, interactions/errors/controls/sound | real browser multi-client, perf, deployment/rights | both artistic and technical exit gates met |

Owner visually reviews real footage, not only CI. `DIRECTOR BASELINE` values may be tuned as a coherent system, with short design-decision note. `CONFIRMED` exclusions remain hard. If a scene fails the visual hold, let Opus refine that scene before proceeding; don't hide defects under more VFX.

### Performance evidence (target, not manufactured achievement)

Goal: measured smooth ~60 FPS on specified normal desktop devices, ~16.7ms budget and no gameplay-obscuring hitching. Record median/p95/p99/max render frame time, long tasks, texture/memory residency, asset network sizes, scene switching/leaks, gameplay vs UI frame delivery, input admission/accepted Burst and network resync behavior in Chrome and Safari. Opus reviews actual high-DPI screen captures, 16:9 letterbox, changes in feel and tuning from data. Use shared candle lightmap, pooled filters/atlases and low-resolution FX fallback rather than deleting gameplay cues. Don't downgrade whole arena texture until demonstrated need. Reduced Motion keeps crucial historical tick, charge and Burst feedback, with alternate opacity changes.

### Explicit browser scenarios

- Real 2/3/4-player matches supported; one representative full game plus short 2/3/4 join/start/room correctness checks may reuse Spec 10 backend certification, avoiding another expensive matrix.
- 4-player spectator read-only, resync, result read while authorized, terminal match, pending/aborted and true rematch identity.
- Original day run 2700 ticks, verified save, refresh; load 2701 original frames; challenge T0; fork T0, T1140, T2699/T2700 eligibility; real completed T1140 fork; saved child load all 2701 frames; source/future aligned same-tick score and original unchanged.
- Real one-shot events under delay, solo `Missed/MissedTruncated`, multiplayer match generation, historical visual events without duplicated living sound.
- Browser input blur/reentry, audio permission, mute, menu/loop transitions, leave confirmation; three successive mode/rewind/rematch cycles without resource/audio leakage.
- Desktop Chrome/Safari and specified high-DPI/normal-DPI; focus/keyboard/screen reader labels, Reduced Motion, UI error/empty/retry.
- Production deployment check as applicable: valid asset URLs, guest auth cookies, WSS/proxy and no public unauthorized history; license provenance and release blocking items.

**Do not rerun full backend Spec 10/11 fairness or cold-replay batteries** just to reprove frozen truths. It should certify the new browser frontend and any actual regression introduced by its transport integration. No CI status claimed green if branch workflows have not run.

## 13. Technical integration and verification track — alongside the 12 creative tasks

These are **work packages**, scheduled against dependency moments; they are *not* invitations to rewrite backend architecture or introduce hidden frontend features.

| ID | Owner | Needed before | Deliverable and boundary |
|---|---|---|---|
| **B0** | Technical verification | 13.1, assets in 13.2/3 | actual repo/asset/right/atlas and canonical world/API/UI truth map; no broad test matrix |
| **B1** | Technical verification | 13.4 first real playable | authenticated typed guest, room and solo HTTP/SignalR client, reliable method map and error/status channels; no scene styling |
| **B2** | Technical verification | 13.4 and 13.8 | real transport sequence, heartbeat, spectator permission, generation-aware resync, frame/event ordering, one-shot Burst rejection/admission visibility, idle/reconnect handling, short focused tests |
| **B3** | Technical verification | 13.9–13.11 | real original/child trace chunk client, ownership, verified hash/version, bounded curve, durable operation-key lookup, practical cache boundary and load measure; no new history engine |
| **B4** | Technical verification | holds A/B/C and 13.12 | production build/Playwright/real host browser latency/FPS/FX/audio lifecycle/security tests, focused correction suggestions to Opus |
| **B5** | Technical verification | final exit | public/near-prod deploy, desktop Chrome/Safari, WSS/cookie URLs, license/build/asset manifest, recorded final acceptance; separate infrastructure config owner if required |

**Backend exceptions only:** if a real missing world-geometry projection, event identity, auth route, saved child read or status contract prevents frontend correctness, first demonstrate the gap, then introduce the smallest safe compatible adapter and focused test. Avoid new schema/queue/Redis/Marrow ports/physics changes. If the fix meaningfully changes frozen authority, escalate for a specific architectural decision instead of letting either agent improvise.

### Gate ordering and concurrency rules

- B0 must complete before Opus makes atlas/world claims. Opus may establish design primitives with verified fonts while B0 is underway, but cannot close the relevant gate with guessed assets.
- B1 type contract is agreed **during 13.1**; B1 integration can proceed in parallel with 13.2/13.3, in separate files. **Do not start 13.4 as real-play gate without it.**
- B2 runs alongside 13.4 and 13.8. B3 may begin before 13.9, while Opus completes the artistic lighting/audio system. No overlapping edits to the experience coordinator.
- B4 is **incremental**, not a giant terminal test at the end. Objective failures are handed to Opus as a short reproducible creative issue or to the technical integration track for a transport/backend issue.
- B5 after integrated functionality. Don't claim deployed WSS or asset rights without evidence.

## 14. Risks, mitigations and exit package

| High-impact risk | Preventative gate | Owner |
|---|---|---|
| Beautiful mockups but no genuine playable game | real authenticated day game early (13.4), authentic full journeys | Opus + B1/B4 |
| Walls beautifully tiled in wrong maze | B0 reads authoritative collider geometry; 13.2 measured overlay | verified contract, Opus rendering |
| Character faces subtly wrong | read full actual `characters.md`, enlarged + game-size expression matrix | Opus |
| Off-the-shelf FX violate user direction | skill `06` negative catalogue, actual footage review | Opus |
| Ghost falsely impacts living world or moves off overlap | read-only ghost layer, same-tick source hashes, visual preview | integration B3 + Opus |
| Unresponsive keyboard or jumpy movement | real frame buffer, bounded visual prediction, A feel gate | Opus + integration B2/B4 |
| Missing one-shot Burst/audio after delayed push | stable event identity, dedupe/truncation/resync, focused sender delay | integration B2 + Opus cues |
| Unclear rewind while backend prepares | explicit ready/cinematic barrier, authentic frames, error recovery | Opus + integration B3 |
| 11-second cold traces block all UI | progressive score-curve/history load, immutable cache, no seeks-per-request | integration B3 + Opus time design |
| Multiple disconnected UI themes | one early shared token/scene grammar and deliberate whole-game polish | Opus |
| Audio plays wrong file/overlaps night menu | single persistent AudioDirector, completion-sequenced ambient loop | Opus + B4 |
| Overlarge atlases destroy GPU budget | pre-slice verified frames + shared textures + fallback | integration B0/B4 + creative review |
| Public release blocked by unknown audio/FX rights | source/licence audit early; explicit publish gate | integration B0/B5 + user approval |
| Opus credits wasted on slow tests | separate B0–B5 track and focused visual gate | Main agent |
| Old Spec 13 silently reintroduces mobile/social | requirements exclusions and approved skill precedence | All |

### Required evidence record per task

In existing `docs/kiro-log.md` or an equivalent lightweight log: task ID, source/skill sections used, actual changed files and build state if known, visual review captures and artistic decisions tuned, explicit open integration dependencies, relevant B-work package status, real verification verdict (pass/limitations/fail/pending), and observed credits if reported. Reuse approved design-input skill rather than copying 19k words into every task report. User/owner must be able to trace why each artistic decision was made.

### Spec 13 completion packet

- Fully playable installed desktop frontend in **actual** verified project (no duplicate app).
- Approved asset registry and true provenance/license manifest; built/served files and fonts verified.
- Cohesive day snow + night snow game, four correct faces (0/1/2/3/4 + reactions), orange candles, grey walls, spectral ghost, pure Burst and smoke/CRT/rewind.
- Functional genuine multiplayer/results/spectator/rematch and solo original/ghost/fork/histories/compare.
- One recognizable transparent golden-sand hourglass to **left** of authentic history timeline, accessible scrubbing and exact fork eligibility.
- Exact audio nine-file orchestration and wind/wind/knock night cycle.
- Responsive movement, authoritative correction, event fidelity, reconnect and score truth.
- Captured footage/screenshots and listening notes at Holds A/B/C/Final; named browser/hardware FPS and lifecycle evidence from technical verification, separate visual assessment from Opus.
- Known outstanding rights/deployment/compatibility issues clearly labeled; no false green/verified claims.

**No final substitute:** A technically passing server with a mediocre scene fails the creative standard. A gorgeous scene with fictional gameplay/events fails the canonical standard. Spec 13 is done only when **both** are true, with a real observed player journey rather than individual screenshots.
