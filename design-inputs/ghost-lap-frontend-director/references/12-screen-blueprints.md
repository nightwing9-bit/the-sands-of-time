# 12 — UI screen blueprints: practical layout and click design

**Use together with `03-screens-and-interactions.md` and design tokens `02`.** These are **DIRECTOR BASELINE visual layouts**, not a request to build new routes or data contracts. Illustrative reference canvas **1600×900 CSS-design units**; the actual game world comes from the backend and always preserves 16:9. For smaller desktop windows use fluid sizing and scrolling in UI lists; never distort the arena. Use real HTML buttons/labels. No phone view.

## Global shell: every UI context

- `GameShell` fills browser viewport with near-snow atmosphere. If active gameplay, entire 16:9 world image must remain visible within a letterboxed frame. Non-game pages may use a **subtle static snow background** or cold gradient (no animated giant hero scene necessary).
- The frost panels use 14–20px radius, white/blue tint, faint outline, lightly blurred **only behind the panel**; no giant full-screen frosted rectangle. Keyboard focus must be high contrast and safe on white snow.
- Margins: for 1600×900 artboard, safe gutters approx 40–60px; shrink down to 20–24px on smaller desktop. Default vertical rhythm 8/16/24/32/48 px; do not stack margins unpredictably.
- Back navigation is **top left** on focused subviews or bottom left on historical dock where it better matches UI; don't change icon meaning from page to page. Settings/help appears in home and can overlay a non-active view; no faux pause during live server gameplay.
- Typography: all primary screen titles Source Serif 4, all controls/numbers Source Sans 3; sentence case except short micro labels; no decorative calligraphy.
- Content grid only when it helps e.g. lobby slots, result scoreboard or two-column history info; avoid trading-screen dashboards and multiple nested containers.

## A. HOME (six supported destinations)

```text
┌──────────────────────────────────────────────────────────────────┐
│                                                     [ Settings ] │
│                                                                  │
│                     G H O S T   L A P                            │
│                   the Sands of Time                              │
│                                                                  │
│                       Multiplayer                                │
│                       Solo Run                                   │
│                       My Histories                               │
│                       Spectate                                   │
│                       How to Play                                │
│                       Settings                                   │
│                                                                  │
│      (baked monster shadow remains softly visible below)        │
└──────────────────────────────────────────────────────────────────┘
```

Design: pale snowy image behind a small concentrated menu column; avoid putting dense text over the monster shadow. Primary button (`Multiplayer`) strongest; the rest subtle. `gameLoading.wav` continues. Don't show public leaderboard or store. If redundant settings icon + menu entry feels cluttered, prefer just one settings access while keeping the action logically reachable.

## B. CREATE/JOIN DIALOG AND LOBBY

```text
┌────── Room Setup ───────────────────┐  ┌─────────────────────────────────┐
│ [Create Room]  [Join Room]         │  │ ROOM <actual real room code>   │
│ [Room code field if joining]       │  │ [Copy]                 [Leave] │
│ [Join] / [Create] (real pending)   │  │                                 │
└────────────────────────────────────┘  │ [P1] [P2] [P3] [P4] (real slots)│
                                      │ [Ready / Not Ready]             │
                                      │          [Start if authorized]  │
                                      └─────────────────────────────────┘
```

Use shape icons, truthful ready chips, room identity. If character selection is verified supported, clicking one assigned character card opens a restrained selectable strip of four heads; otherwise the card is read-only. An absent slot is faded/empty, not assigned a random player. `Start` disabled with message until permitted. Add `Join as spectator` only in valid join flow.

## C. LIVE MULTIPLAYER HUD (DOM over Pixi stage, minimal)

```text
┌─────────────────── 16:9 arena fully visible ─────────────────────┐
│ P1 score   P2 score        T: actual         P3 score  P4 score  │
│                                                                  │
│                                                                  │
│                     [PIXIJS WORLD]                              │
│         4 characters + candles + grey geometry                   │
│                                                                  │
│                                                                  │
│       [My Soulfire □ □ □ □ ]  [Overlit/readiness if active]        │
└──────────────────────────────────────────────────────────────────┘
```

These are **semantic anchor zones**, not a license to cover the walls: measure actual top/bottom obstacle bounds and place HUD over empty safe zones or a thin translucent edge tray. On smaller desktops reduce HUD labels, not truth. Avoid corner mega-panels. All visual states read actual backend. The HUD does not include global leaderboard, chat or unverified pause.

## D. SOLO ORIGINAL

Same snow world full-screen, one living character, small timer/score/charge. No ghost. Use day multiplayer soundtrack. End: a single calm modal `Original Recorded` only if verified; before that say `Saving your original…` and show progress/status. Optional `View My Histories`, `Another Run`, `Home` according to actual available actions.

## E. MATCH RESULTS / SCORE PANEL

```text
                      Match Results
        ┌────────────────────────────────────────┐
        │  [Head] P?    Placement         Score │
        │  [Head] P?    Placement         Score │
        │  [Head] P?    Placement         Score │
        │  [Head] P?    Placement         Score │
        │ ────────────────────────────────────── │
        │ Movement  Pickups  Penalties (if real) │
        │ Burst / Contacts (if returned)        │
        └────────────────────────────────────────┘
                  [Rematch]     [Home]
```

Alto-inspired clean scores and single typographic table, no fake confetti. `Pending`, `Aborted`, `Forfeit`, `Tie` truthful statuses. Winner face only when result confirms. Horizontal score comparison with large readable typography; no public share button.

## F. MY HISTORIES

Top: `My Histories`, category tabs `Originals | Saved Futures`. Main: readable list (not random masonry cards); each row displays meaningful ID/title, type, actual verification/final score/source/fork tick, trailing context actions. Original row actions: `Inspect`, `Replay`, `Challenge from Start`, `Rewind & Fork`. Future row: `Replay`, `Compare`, `Source`. Rows whose action unsupported are not clickable; show reason. No social profile, community feeds or public replays.

When empty, light calm blank treatment with one CTA (`Record an Original`); never generate fake sample rows to fill whitespace. Rows may use the four character-head icons only when real source identity is available.

## G. HISTORY / REWIND INSPECTION

```text
┌────────────────── arena history preview ────────────────────────┐
│ Original / identity / read-only historical inspection           │
│                                                                  │
│                     actual archived world                         │
│                                                                  │
├──────────────────── bottom frost timeline dock ─────────────────┤
│ [glass hourglass] ────────◆───────────────→  00:47 / T1417       │
│                  [Prev] [Play/Pause view] [Next]   [Fork Here]   │
└──────────────────────────────────────────────────────────────────┘
```

**IMPORTANT:** while history dock is open, **re-fit the entire 16:9 arena into the remaining upper viewport** rather than hiding the bottom wall under a 130px overlay. The dock may occupy the bottom of the browser outside scaled arena; preserve complete map in smaller displayed region. In active multiplayer/solo no history dock exists so no reduction.

`T2700` can be viewed and replayed; `Fork Here` unavailable with message. `HourglassMark` is left of track, 2.5D glass + warm sand. This UI visually parallels the Sands of Time bank engine structure but uses independent original art and calm game design.

## H. GHOST CHALLENGE AND FORK PREPARATION

Use a focused translucent card over noninteractive snow preview: `Preparing history`, one clear step text and real ready/failure status. If no progress percentage from backend, show a gentle indeterminate indicator and hourglass; **do not claim 85%**. `Cancel` only if supported; `Back` if safe. After successful prep, optional authentic rewind montage, CRT/smoke, reveal night arena. The moment after reveal is when `ghost_main_theme.wav` plus wind/knock cycle begins. No ghost sound before the night scene.

## I. NIGHT GHOST RACE HUD

Same compact daytime HUD but one living charge/score rail, adjacent **Original score at same tick**, and a signed **Δ** value; label `Challenge` or `Fork`. Ghost face is permanently mean, cyan behind living. Current player natural colour. If at same coordinates, cyan edge may show but ghost does not forcibly move aside. No ghost-control button and no social challenge ranking.

## J. SAVED-FUTURE COMPARISON

Re-use dock from G with a second narrow rail branching after true fork mark. Top small selector `Original | Future` (stateful historical view only); right-side contextual 3-value summary `Original`, `Future`, `Difference` for **same absolute tick**. The fork diamond is warm gold; future trace cyan. Don't fill screen with a finance chart. `Compare` is available only for verified parent/child pair; no unsupported child-to-child fork.

## K. SETTINGS / HOW TO PLAY

Small center frost panel; Source Serif heading `Settings`; Source Sans labels `Music` slider/mute, `Sound effects` slider/mute, `Reduced motion` toggle, `Controls` help navigation, `Back`. Keep ambient wind/knocking under music bus. Do not invent language, profiles, cloud saves, joystick, accessibility preferences not planned.

How to Play: three/four readable short sections: movement, candles/Soulfire, multiplayer contact/Burst, history/ghost/rewind. Keybinding descriptions **must be fetched from actual backend/UI command mapping**; do not hardcode WASD/arrow/Burst key without verifying.

## L. ERROR AND RECOVERY STATES

Use one family of contexts: small status tag for recoverable connection; modal for blocking/terminal failure; empty card for history list with no data; indeterminate progress for preparing; clear confirmation for leaving live session. Messages must describe real failure, e.g. `This history is unavailable`, `Unable to prepare this fork`, `Reconnecting…`, `Match aborted`, not faux generic success. Returning/back must not destroy user history unexpectedly. Always give keyboard access and `aria-live` for state changes.

## Click acceptance matrix

| Visible interaction | Expected action | Visible success/failure |
|---|---|---|
| Multiplayer | enter real create/join flow | room pending/error/real lobby |
| Join / Create | invoke verified command exactly once | real room identifier or error |
| Ready/Start | invoke authorized action | wait/readiness/countdown from backend |
| Character choice | only if verified supported | actual selection confirmation, else read-only preview |
| Burst | verified key input only during eligible living play | actual Burst event drives shockwave/audio |
| Results Rematch | authorized rematch | new lobby and new MatchId |
| Spectate | join with real spectator flag | readonly HUD; no commands |
| Start Solo | authoritative original | day arena, real recording |
| Save result | server save lifecycle | pending -> verified or error |
| My Histories actions | invoke actual read/prepare/compare | real historical frames/status |
| Timeline scrub | choose nearest actual tick | exact matching historical frame |
| Fork Here | only T0–T2699 verified original | prep -> ready -> night start; T2700 disabled |
| Challenge | from verified original at T0 | prep -> night challenge |
| Music/SFX settings | local audio bus controls | correct sound changes, no backend state |
| Back/Leave | context-safe navigation | confirmation for session abandonment |

Do not display clickable actions that have no defined supported outcome. A polished button is not a substitute for a working contract.
