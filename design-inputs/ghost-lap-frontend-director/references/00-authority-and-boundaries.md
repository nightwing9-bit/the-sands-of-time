# 00 — Authority, truth, modes and no-invention boundary

**Read before implementing any scene.** Labels: **CONFIRMED** (user or backend inventory), **DIRECTOR BASELINE** (specific frontend design defaults), **VERIFY** (dependent on actual code/files), **DEFERRED** (must not add).

## Project concept and platform

- **CONFIRMED:** Ghost Lap is an arena game that demonstrates the same *Sands of Time* history/replay/fork engine used in another application. The game's signature interface should visually echo the engine's hourglass-to-horizontal-timeline structure, but the game should retain its own polished aesthetic.
- **CONFIRMED:** Desktop web browser, mouse + keyboard, 16:9 arena. No phone mode, touch controls, mobile joystick, controller infrastructure or portrait redesign.
- **CONFIRMED:** React owns menus and accessible UI; PixiJS owns moving world graphics. C# backend/real-time transport/replay/history is authoritative; do not write alternate browser-side physics or score truth.
- **CONFIRMED:** no profile/social system, public replay links, global leaderboard, chat, Elo, AI rival, child-of-child fork, in-game payments or general inventory/store. Spectate an eligible room via supported join flow; not a public searchable room directory.

## Functional experiences actually supported

| Context | World view | Player and history | UI actions that exist |
|---|---|---|---|
| Main/menu | atmospheric UI | no active round | Multiplayer, Solo, My Histories, Spectate, How to Play, Settings |
| Multiplayer | **daylight snow** | real 2/3/4 living players | create/join/lobby/readiness, play, match results, authorized rematch/leave |
| Multiplayer spectator | **daylight snow** | read-only of real room | join eligible room, inspect score/state, leave; never control |
| Solo original | **daylight snow** | single player, authentic complete original | play, completion, verified save, history navigation |
| Saved history inspect/replay | historical arena | authentic selected frames | scrub T0–T2700, play/pause *viewing*, select original/child |
| Ghost Challenge | **night snow** | living player + noninteractive source ghost from T0 | new run, score delta, finish/verified child save |
| Fork from original | **night snow** | selected original tick T and subsequent independent living future + historical ghost | select T0–T2699, prepare, start new decisions at T+1, save child |
| Child replay/comparison | historical arena | original and child aligned by absolute tick | inspect, aligned score and fork boundary, compare |

Historical original run: **30 Hz, 2700 transitions, nominal 90 seconds**. The historical timeline range is **T0..T2700**. The selected tick T2700 is inspectable but **not forkable**. A child history's children cannot be forked again. A fork inherits the **complete** original state at T; new decisions begin at T+1. A Challenge starts at T0. Parent history remains immutable. Neither ghost nor visual timeline creates physical effects. Never show save success before verified completion.

## Truth from the backend feature inventory

- Multiplayer lobby supports room identity/member slots/ready/start actions where authorized. Character selection UI is **conditional on verified room/character contracts**, not guaranteed by mention of four graphical identities.
- Multiplayer HUD needs authoritative timer/tick, identity, score, Soulfire level/Overlit, manual or automatic Burst availability/outcome, candles, hit/contact events, disconnect/forfeit/terminal states.
- Match result may be pending/finalized/aborted; authorized match-local placement, tie, forfeit, score breakdown, pickup/Burst/contact counters. **No global leaderboard**.
- Spectator `You.CanControl` is authoritative; `JoinRoomRequest` includes `AsSpectator=true` where authorized. Spectator commands are disabled.
- Original solo has movement scoring, candles, Soulfire and **backend-verified Burst**. Earlier user prose described solo as collection-only; this is a source conflict. Do not synthesize extra Bursts, but do render genuine server Burst events in solo. Escalate if current actual repo contradicts inventory.
- History library: verified originals; completed Challenge/Fork children; source/fork ticks; replay/compare; not public or shareable.
- History preparation previously measured roughly 5–6 seconds locally; treat as **observation**, not a fixed timeout or promise. UI must wait for actual ready/failure.
- The live match **does not have a confirmed server pause**. A local overlay must not claim the simulation paused.
- Documented errors include unauthorized/missing/incompatible history, incomplete save, invalid fork tick, room connect failure, spectate resync, match aborted, disconnect, truncated visual delivery. Represent truthfully.

## Explicit user corrections (higher priority for presentation)

1. `game-assets/backgrounds/snow_arena.webp` replaces all grass variants.
2. `0/4` and `1/4` share the **frown expression with zero charge halo**; collecting first candle changes gameplay charge but not this facial/halo state. **Do not change backend starting charge**.
3. User's `characters.md` specifies bodyless head-only shapes; ghost uses one permanent evil face; the main character's expression still responds to real state.
4. The Soulfire Burst is **pure ShockwaveFilter distortion + temporary enhancement of existing charge halo**. Absolutely **no yellow tint, additional coloured ring, additional decorative particles**.
5. Ghost is cyan and translucent **without body blur**; real historical positions only, rendered behind living character so edge emission may remain visible at overlap.
6. Daylight multiplayer and daylight solo share `multiplayer_theme.wav`; night gameplay uses `ghost_main_theme.wav`, starting **after** successful night transition. `gameLoading.wav` outside active gameplay.
7. Ghost ambient sequence: two complete `Gushy_wind.mp3` plays followed by one complete `Ghost_knocking.wav`; repeat under continuously looping ghost theme. 80/90 relative loudness references; not exact amplitude fractions.
8. UI style authority: sparse elegant Alto's Adventure-like interface philosophy, readable classic typography, soft frost materials; don't imitate copyrighted source screen/art one-to-one.
9. Hourglass left of historical horizontal timeline: visually related in structure to other Sands of Time application, distinct game treatment.

## Source precedence in an agent's working environment

1. Live backend code/contracts/tested actual semantics: game facts.
2. Most recent explicit user correction in this conversation and supplied docs: visual/audio decisions.
3. `$GAME_ASSETS/characters.md`: complete character expression details, **except** the explicit 0/1 visual mapping correction.
4. Backend-authored **Frontend Feature Inventory and Navigation Handoff** (supplied 2026-11-18): supported UI scope.
5. This skill and references: creative defaults and frontend structure, never new backend semantics.
6. AI-generated illustrations, Kenney marketplace screenshots, Pinterest/Alto screenshots and banking image: reference only, **never production artwork unless explicitly designated**.

## Required preflight answers from backend owner — don't guess

- Which repository/package/directory is the actual frontend? Framework/build tool, existing app routing, and how to expose approved assets.
- Exact server world size, origin, coordinate orientation and wall collider shape/layout; how candle visibility and pickups are represented.
- Real player-to-slot/character mapping and whether choice is supported; movement controls and Burst input key.
- Authoritative timestamps/event identifiers, timing of charge stage changes, expression reaction events and whether solo events include Burst in installed code.
- Concrete contract for room creation, join (including spectator), ready/start, rematch, leave and results requests.
- Actual history fetch/seek/replay snapshots, canonical event markers, fork command/eligibility, ready/failed save, score alignment and child comparison.
- Safe audio hooks and transition readiness signals; whether UI can begin preparation before/while animation runs.

**Not asking the frontend designer to implement backend.** The backend owner maps those real contracts to the semantic frontend adapter described in `09`.
