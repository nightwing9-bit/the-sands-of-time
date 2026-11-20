# 03 — Screen-by-screen experience and clickable UI inventory

**Source of functional truth:** backend agent's 2026-11-18 Frontend Feature Inventory. **Presentation choices below: DIRECTOR BASELINE** unless explicitly marked CONFIRMED. Use Source Serif 4 for headings / Source Sans 3 for every control and data line. Use frost UI tokens from `02`. Desktop-only; never fabricate unavailable functionality.

## Navigation map (not a mandate for separate routes)

```text
ENTRY / HOME
├─ Multiplayer -> [Create Room | Join Room] -> Lobby -> Live Match -> Results -> [Rematch | Home]
├─ Solo -> Start Original -> Live Original -> [Save Pending | Verified] -> [My Histories | Another Original | Home]
├─ My Histories -> [Originals | Saved Futures]
│  ├─ Original -> [Inspect | Replay | Ghost Challenge | Rewind & Fork]
│  └─ Future -> [Replay | Compare Original | Back to Source]
├─ Spectate -> Join eligible room -> Read-only Arena -> [Leave | Result if authorized]
├─ How to Play
└─ Settings
```

Shared: back, confirmation, loading and error overlays. Do not add a global leaderboard, store, social tab, accounts, chat, friend list or sharing. There is no server pause control.

## 1. Start/home/menu (CONFIRMED needs; DIRECTOR BASELINE composition)

- Ambient background: show approved snow/monster image softly and unobstructed where possible. **Do not create a new hero illustration**; user's chosen art is the visual identity. Floating giant monster shadow should remain visible.
- Upper/central title **Ghost Lap** in Source Serif 4; small restrained support line **The Sands of Time** below/near it as brand linkage (DIRECTOR BASELINE, not a required literal logo). No copied Alto title/branding.
- One main vertical action stack, max 6 entries, in this order: **Multiplayer**, **Solo Run**, **My Histories**, **Spectate**, **How to Play**, **Settings**. One subtle back/escape if needed; no fake resume button. Keyboard focus arrow/Tab/Enter + Escape/back where appropriate.
- Persistent `gameLoading.wav` starts after first permitted user gesture. It does not restart on every menu change. On game entry it gives way to correct gameplay theme; after game it resumes.
- If connecting/auth not ready, show a discreet truthful status; disable actions that truly require connection, not entire menu indiscriminately.

## 2. Multiplayer create/join (DIRECTOR BASELINE: one frost dialog)

- Two tabs/actions: **Create Room** and **Join Room**. `Create` triggers verified room contract once, shows pending, success with actual room identity and error retries. `Join` accepts actual supported room code/id; validate only rules the server supports.
- Join-as-spectator affordance where server supports it, not a public room browser. Keep spectator flow distinct in the resulting presentation.
- Copy room identifier button may use browser clipboard (only on returned real room value); label `Copied` only on success.
- Do not fake matchmaking, random room suggestions or public browsing.

## 3. Lobby and character presentation

- Top row: room identifier, copy/action where valid, leave. Main central section: **2–4 player slots** based on actual room state, with Kenney character head, actual player identity/slot, readiness and host/authorized action cues. Empty slots visually quieter, never shown as joined.
- Place **Ready / Not Ready**, authorized **Start** and **Leave** where backend permits; every click needs pending/disabled/confirm state. Countdown is authoritative, not set by arbitrary React timer.
- **Character selection:** display named shape previews; show clickable selectable cards **ONLY IF** actual room/character API supports changing the active character. If not supported, present assigned identity as a preview, not a fake toggle. Character artistic reference is official Kenney shapes, not AI mockup.
- Avoid a forced separate character-select page unless main agent says it already exists. Inline lobby selection is the default treatment if verified.

## 4. Multiplayer live arena / HUD

- All gameplay world drawn in Pixi; HUD in DOM aligned with arena bounds. **Do not crop the arena**. UI should occupy slim edge zones.
- **Top center:** match time remaining or authoritative tick/time, strong tabular numerals. `90s` is established for solo history; do not assume multiplayer duration from that.
- **Top/side small player rail:** each real participant's 4-shape icon, colour/slot identity, current score, relevant state; current player distinctly identified without huge glow. No global leaderboard; live score ordering is match-local only.
- **Near the player's own HUD identity:** Soulfire 4-segment meter (0–4 numerically truthful); stage face changes as specified; Overlit/automatic Burst state explicit with small text/readable indicator; manual Burst cue only if actual command available.
- **Event feedback:** actual `candle_eat.wav`, `blast.wav`, `player_hit.wav` on verified relevant events. Visual reactions use character expressions (no hit-glitch); short-lived pop/score delta only if backed by genuine score event. Avoid overlapping screen-covering notifications.
- **Network/forfeit state:** unobtrusive status tag, blocking panel only for real terminal/connection issues. `Escape` may expose a local leave/settings menu but **never say 'Paused'** when game continues server-side.
- Preserve readable arena floor and all walls, no large floating score banner covering players.

## 5. Multiplayer results / rematch

- Distinct soft, large serif heading **Match Results** or **Victory** only when authoritative outcome identifies winner; show **winner(s)/ties/forfeit** precisely. Use match-local placements, score totals and actual counters.
- Alto-like horizontal rows, not a giant trophy/shopping UI. Show each player icon/name and final score, with an optional expanded truthful breakdown: movement, pickups, penalties, pickup/Burst/contact counts if returned.
- Status `pending/finalized/aborted`; **never** show a finalized winner before `GetMatchResult(matchId)` returns finalized state.
- Bottom actions: authorized **Rematch**, **Leave / Home**. Rematch returns to room lobby; clear prior match animation and HUD events. New MatchId/seed/config is a completely new match, not continuation.
- No social share button, currency or global ranking.

## 6. Spectator

- Same arena, same HUD scores/time; small persistent **Spectating**/read-only indicator (no character owned by spectator). Hide/disable all gameplay commands/Burst controls. Resync and connection status available.
- Leave action always visible; results only while authorization retained. No spectator chat/emotes or controlled camera not backed by contract.

## 7. Solo original recording and save

- `Solo Run` begins a **real 30Hz/2700-transition** original, with daylight snow world. Use the same `multiplayer_theme.wav` as day multiplayer. Show solo timer/score, Soulfire/Overlit and **actual Burst events** if received.
- One living Kenney character; no historical ghost in original recording. Candle/score changes from server only.
- Completion presentation: status **Recording complete / Saving...** then **Verified / Saved** only after actual confirmation. Error/unavailable states offer valid retry/return, not false persistence. Actions: view history, start another supported original, home.

## 8. My Histories — one coherent library with two categories

- Large serif `My Histories` title; toggle/tabs **Originals** and **Saved Futures**, with counts only if authentic.
- Alto Workshop-like restrained list rows: large identification label, small type badge (`Original`, `Challenge`, `Fork`), final score, verified status, timestamp if provided, source original, fork tick if present. Use no fake poster thumbnails or filenames as user-facing IDs when better server labels exist.
- Originals: `Inspect`, `Replay`, `Ghost Challenge`, `Rewind & Fork` (the latter only if eligible). Saved futures: `Replay`, `Compare with Original`, `Return to Source`. **No nested child fork** action.
- Empty original list: explain create a Solo Original first; simple `Start Solo Run` call to action. Empty futures list: explain ghost races/forks appear after verified completion. Unauthorized/missing/incompatible rows use clear error states.

## 9. Original historical inspection/replay

- Same arena view at selected historical tick, ideally dominant visual area. Bottom **hourglass-left** timeline system per `08`; history type, source ID and real selected tick/score.
- Play/pause governs **historical viewing only**. Step/seek controls and keyboard navigation. `Fork here` only on eligible original T0..T2699. `Ghost Challenge` launches separate prepare/start from T0; don't conflate with Fork at arbitrary tick.
- If viewing a saved child, show its source identity and fork boundary but disable child-to-child Fork.

## 10. Ghost Challenge and Fork preparation / play

- Authentic prepare state with Source Serif 4 heading, hourglass/snow motif, clear progress indicator **indeterminate** unless backend provides real %; cancel if supported. Backend may take ~5–6 seconds or longer; never use an invented percent.
- Ghost Challenge begins from T0; Fork begins from selected original tick T. Transition presentation from `08`/`06`: optional truthful rewind montage for fork, short CRT, smoke, night reveal, **then start night BGM**.
- Night arena: living character kept in natural colour; ghost cyan behind; living/ghost position only as returned by actual source history; ghost has evil permanent face, does not interact with new candles.
- HUD shows actual score, source-score-at-same-tick and signed delta. Indicate whether this is **Ghost Challenge** or **Fork**; no numeric source score replaced by final score.
- At completion, save child with original/Challenge/Fork relationship; pending/verified/error feedback.

## 11. Compare saved future with original

- Use same timeline at absolute tick, a **dual-lane original-versus-child** presentation with muted blue-grey for original and selective sand-gold or cyan for child. Must show shared history until fork and separate true futures after.
- Header: source and child identities + type + fork tick. Main comparison: aligned original score, child score and signed delta at *selected tick*; optional small authentic score curve only when samples are supplied.
- Primary comparison view may be one arena with a toggle `Original / Future` at selected tick (DIRECTOR BASELINE) rather than two tiny simulators. If backend supports concurrent compare and user can read it, dual view can be a later refinement.
- Child's correct absolute timeline range is source-defined; don't invent missing pre-fork data or recalculate scores. Return to source / sibling navigation when supported.

## 12. Settings / How to Play / local controls

- Settings in a compact frosted panel: **Music** volume/mute, **Sound Effects** volume/mute (ambient wind/knock follows music), **Reduced Motion** toggle, **Controls** explainer, optional Fullscreen only if truly supported. Mute functionality **CONFIRMED as required scope**; volume sliders are **DIRECTOR BASELINE** because the client audio router can implement them, not backend features.
- Settings labels in Source Sans 3; Source Serif heading. Do not invent language selector, paid skins, social notifications, game account settings or authoritative pause toggle.
- `How to Play`: readable concise sections for movement, collecting candles, Soulfire/Overlit/Burst, collisions, scoring, original, historical ghost, rewind/fork; **actual control keys and scoring values must come from backend owner**. No fake tutorial missions required.

## 13. Uniform contextual states and input standards

All user-facing screens must have handling for: initial loading, waiting, disabled/unavailable actions, verified success, retryable failure, terminal failure, return/back, keyboard focus and Escape semantics. Specific cases: connecting/auth, room join, countdown, disconnect/reconnect/forfeit, save pending/verified/failed, history empty/missing/unauthorized/incompatible, invalid fork tick, preparing/cancelling, spectator resync, pending/finalized/aborted results, transient delivery truncation. **Never display invented last-known score as a new authoritative score.**

For keyboard: Tab order follows visual order; Enter/Space activates real buttons, Escape closes safe dialogs; Arrow/Home/End used on timeline slider with labelled tick; focus visibly strong. Confirm on leaving an active session. No pseudo-buttons styled as text without semantic `<button>`.

## 14. Reuse and coherence

Every screen uses the same `FrostPanel`, `Heading`, `GameButton`, `InfoRow`, `StatusPill`, modal and typography tokens. Difference in mood between day and night comes from *arena and light*, not an unrelated UI theme. UI must remain readable on both. No screen should look like an imitation bank console or generic fantasy asset marketplace.
