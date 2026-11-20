# Spec 13.1 — Ghost Lap frontend foundation

**CREATIVE IMPLEMENTATION DONE · HUMAN REVIEW PENDING · TECHNICAL ACCEPTANCE PENDING.**
Scope: `web/apps/ghost-lap` only. No server, Marrow, rules, scoring, collision or record change. No game transport was written.

## Where to look

Dev URL (hot reload): `cd web && npm run dev -w @sands/ghost-lap`, then <http://localhost:5174/>.

| View | URL |
|---|---|
| Entry/menu | `/` |
| Hourglass + timeline study (preview fixture) | `/?screen=historyStudy`, or "Hourglass study" in the preview strip |
| HUD-safe guides | "Safe guides" in the preview strip (development only) |
| Previous test-host greybox / generic timeline inspector | `/?greybox`, `/?timeline=<id>` (unchanged; used by the existing E2E specs) |

The preview strip and the study screen exist only when `import.meta.env.DEV`. A production build ships the menu only.

## The visual language (one place each)

| Concern | File |
|---|---|
| Colour, type, radius, blur, motion, stage rails, dock height | `src/styles/ghost-lap-tokens.css` |
| Page, 16:9 stage, layers, HUD-safe guides, fixture tag | `src/styles/stage.css` |
| FrostPanel, GameButton, StatusPill, BackLink | `src/styles/primitives.css`, `src/ui/primitives.tsx` |
| Home and dock compositions | `src/styles/screens.css` |
| Runtime asset paths | `src/config/assetRegistry.ts` |
| Hourglass mark | `src/ui/HourglassMark.tsx` |

Material rules: snow image is the ground; ink on snow; frost panels (blur only behind a panel) and slate panels for tool surfaces; warm sand-gold only for the hourglass and the selected-moment diamond; cyan only for memory (source history), never for living things; four shape hues appear only as the real Kenney shape PNGs. Fonts: Source Serif 4 (display) and Source Sans 3 (everything else), self-hosted from `@fontsource/*@5.3.1` (OFL-1.1), latin subset, weights 400/500 and 400/500/600/700.

## Ownership seam for later views

- `AppShell` (`src/app/GhostLapApp.tsx`) renders **one** persistent `Stage` (`src/stage/Stage.tsx`). Screens only fill layers: `world`, `hud`, `overlay`, and an optional `dock` that sits outside the 16:9 rectangle, so the whole arena re-fits above it.
- `src/app/screens.ts` maps `ScreenId` to `{ scene, dock }`. A task adds a row only together with a real screen.
- `world` is the single mount for the one long-lived PixiJS application in 13.2. Nothing else creates a canvas.
- All sizes inside the stage use container-query units, so the same composition holds at any desktop window size.
- The history dock is the frame the shared `@sands/timeline-ui` scrubber moves into in 13.10: hourglass left, track right, source chip, time readout. The study is static and does not use the scrubber.

## Render/control boundary (`src/presentation/types.ts`)

Presentation types, **not server DTOs**: `SceneMode`, `ScreenId`, `ActorSlot` (zero-based), `Charge`, `WorldFrame`, `PlayerPresentation`, `WorldPresentation`, `ControlIntent`. The file header lists the real DTO field each one is meant to be filled from (multiplayer `PlayerSnapshot`, challenge `ChallengeFrameView`, `SoloGhostFrame`, `GameplayInput`, `SoloInputItem`). `ControlIntent` has the same `{moveX, moveY, requestBurst}` shape the greybox keyboard hook already produces. Cue/event, audio and result types are deliberately absent until 13.3/13.6/13.7.

## Findings the user should know

1. **P1 and P3 share one colour in the pack.** `yellow_body_circle.png` and `yellow_body_rhombus.png` both have centre colour `#fab839`. 13.0 flagged the circle as an orange *candidate*; sampling shows it is identical in hue to the yellow diamond. Shape is the only distinguishing feature. I did not recolour anything. Decision needed from the owner (see questions in the report).
2. The faceless Kenney body shapes are used only as a small mark under the title. Faces are 13.3.
3. The existing app has no `/api/ghostlap/solo/*` proxy; the dev handshake request returns 500 when the API host is not running. The status pill shows this as "Could not check the server contract version." The menu still works.

## Transport boundary (pending, not coded)

Nothing in 13.1 contacts a game hub or a game HTTP route. The only server call is the existing `/meta` contract handshake. The creative slice can continue without any of the items below, until 13.4 needs real play.

| # | Source file | Observed mismatch | Required behaviour | Smallest success check | Creative slice can continue? |
|---|---|---|---|---|---|
| 1 | `web/apps/ghost-lap/vite.config.ts` | No proxy for `/api/ghostlap/solo/*` (belongs on the Realtime host); `/hubs` already forwards to Realtime | Same-origin forwarding of solo history reads to `VITE_REALTIME_ORIGIN` | `GET /api/ghostlap/solo/originals` through port 5174 returns 401 (no guest) instead of 404 | Yes (13.1 to 13.3). Needed by 13.9 |
| 2 | `src/Hosts.Realtime/GhostLap/Solo/SoloRunRegistry.cs` `PushFrame` (13.0 A3) | Original live frames fill only seven fields; events, active candle ids, ack and score parts stay default | Populate the declared rich fields the way `ChallengeMapping.ToWire` does | Original run frame carries `ActiveCandleIds` and ordered `Events` | Yes. Blocks 13.4 Original, 13.5, 13.9 |
| 3 | no browser file yet | No typed client for `/hubs/ghostlap` or `/hubs/ghostlap-solo`; `createRealtimeConnection` is fixed to `/hubs/realtime` | Typed hub client, ordered immutable frames feeding `WorldPresentation`, input stream feeding from `ControlIntent` | Browser steers a real character for 10 s with acked input | Yes. Blocks 13.4 |
| 4 | none (13.0 A2/A8) | No production read of maze geometry and seeded candle plan | Smallest read projection of `ArenaV1` + `CandlePlan` with layout fingerprint | Browser draws nine rectangles equal to the audited constants | Yes. Blocks 13.2 integration, 13.4/13.5 candles |

Items 2 to 4 were already recorded by 13.0; they are restated here as handoff rows, not new findings.

## Provenance and gates

- `public/game-assets/backgrounds/snow_arena.webp`: byte-identical copy (`cmp`) of the approved runtime WebP. Master PNG not copied.
- `public/game-assets/characters/`: four unchanged Kenney PNGs plus the Kenney CC0 licence text. The trailing-space source folder was read through its real path.
- Fonts: OFL texts are in `web/node_modules/@fontsource/*/LICENSE`. They must ship with any distribution (13.12 gate). No grant was inferred.
- The AI-generated snow image has no separate licence record; nothing about redistribution is claimed.
- `design-inputs/skills2 (1).md` (loaded per the game-authority rule) is the older dark "Candlelit Colosseum" direction with Bungee/Outfit fonts, chamfered stone, a leaderboard and mobile touch. The Spec 13 README and the installed Frontend Director skill supersede it. Only compatible rules were kept: ghost beneath the living player, identity never by colour alone, centralised tokens, semantic asset keys, full 16:9 arena visible.
- Spec directory: canonical `.kiro/specs/spec-13-ghostlap-complete-browser/{requirements,design,tasks}.md` exist (tracked, modified in the working tree). No nested copy exists now. The 13.0 caveat is out of date; no discrepancy was worked around.
## Typography refinement after first human review (HUMAN REVIEW PENDING)
Design preference: **Ghost Lap favors generous, effortless readability over tiny minimalist labels**, while preserving hierarchy (title > menu label > subtitle > secondary text). Not zoom-keyed and not a global scale-up.
- New tokens in `ghost-lap-tokens.css`: `--gl-text-label: clamp(17.5px, 1.31cqw, 21px)` (menu and button labels; reuse for primary actionable labels), `--gl-text-subtitle: clamp(16.5px, 1.2cqw, 19.5px)`, `--gl-subtitle-ink: #3f586a`.
- Spacing only as needed: `.gl-button` min-height `clamp(46px, 3.45cqw, 58px)` (quiet `clamp(44px, 3.1cqw, 52px)`), `.gl-menu` width `clamp(272px, 22.5cqw, 368px)`.
- Unchanged: title, eyebrow, panels, shape marks, hourglass, timeline, dock, arena, notice and all other text sizes. Only secondary text changed: the tagline (size, weight 400 to 500, line-height 1.5 to 1.55, darker ink).
| Computed (px) | 1600x900 before / after | 1280x720 before / after | 1100x760 before / after |
|---|---|---|---|
| Menu label | 17.49 / 20.46 (+17%) | 15 / 17.5 (+17%) | 15 / 17.5 (+17%) |
| Subtitle | 17.49 / 18.74 | 15 / 16.5 | 15 / 16.5 |
| Title (h1) | 81.20 / 81.20 | 64.97 / 64.97 | 56.25 / 56.25 |
