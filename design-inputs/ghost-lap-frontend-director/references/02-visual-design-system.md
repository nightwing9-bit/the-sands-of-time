# 02 — Visual design system: precise premium baseline

**Design intent, DIRECTOR BASELINE:** original Ghost Lap interface inspired by the **restraint** of Alto's Adventure: soft atmospheric transparency, serif-led editorial headings, quiet functional sans-serif, orderly spacing and very few chrome elements. Not a clone; **do not replicate Alto's artwork or exact screen geometry**. The snow and giant monster shadow belong to Ghost Lap. No blocky fantasy/pixel type, noisy ornaments, neon dashboard or ubiquitous giant card shadows.

## 1. Free, exact font choice for the BASELINE (not a claim about Alto's proprietary font)

- **Display/title:** `Source Serif 4` (regular 400, medium 500 or semibold 600 as actual files support). Readable, bookish and elegant; use for main title, panel headings, results headline, historical section titles, selected-moment presentation. **No cursive**.
- **Controls/body/numerics:** `Source Sans 3` (400, 500, 600, 700); use for every button, control label, score, timer, help text, lobby list, table and micro-label.
- **Fallback:** display `Georgia, serif`; body `system-ui, Arial, sans-serif`.
- Both families: freely available through Google Fonts/Fontsource with open licences; download/self-host **only allowed WOFF2/WOFF** and preserve OFL notices. Never package font files in the exported skill. Link: https://fonts.google.com/specimen/Source+Serif+4 and https://fonts.google.com/specimen/Source+Sans+3 .
- `font-variant-numeric: tabular-nums` for timeline times, scoreboard and countdown. Avoid serif digits in fast gameplay HUD. No all-caps on paragraph text.
- Two families only. No ornamental third font, no faux cursive for the engine title, no gamey pixel font.

## 2. Token-level theme (these numbers are art-direction defaults, not backend facts)

Create ONE CSS token file for runtime appearance (`src/styles/ghost-lap-tokens.css` in the discovered frontend root, adapting if project style differs). **Never scatter colour/blur numbers across components.** Defaults:

```css
:root {
  --gl-font-display: 'Source Serif 4', Georgia, serif;
  --gl-font-ui: 'Source Sans 3', system-ui, sans-serif;
  --gl-snow-white: #edf5fa;
  --gl-snow-blue: #d8e8f4;
  --gl-ink: #263c4b;
  --gl-muted-ink: #5b7382;
  --gl-white-ink: #f5fbff;
  --gl-stone: #717c8d;
  --gl-stone-dark: #444f61;
  --gl-night-ink: #162c40;
  --gl-night-surface: rgba(27, 51, 70, .69);
  --gl-frost-surface: rgba(239, 249, 252, .66);
  --gl-frost-border: rgba(255,255,255,.53);
  --gl-divider: rgba(73, 110, 134, .19);
  --gl-hourglass-gold: #dfb87d;
  --gl-ghost-cyan: #83e4eb;
  --gl-action-dark: #254a5b;
  --gl-action-light: #f2faff;
  --gl-radius-sm: 8px;
  --gl-radius-md: 14px;
  --gl-radius-lg: 20px;
  --gl-space: 8px;
  --gl-panel-blur: 12px;
  --gl-panel-shadow: 0 10px 30px rgba(24, 49, 66, .10);
}
```

Colour IDs convey the design and can be revised as a complete palette after actual screen captures. 100% opacity for vital text is more important than literal token fidelity.

## 3. Type scale and information density

| Role | Baseline desktop spec | Typeface |
|---|---|---|
| Main Ghost Lap title | `clamp(48px, 5vw, 78px)`, weight 500, 1.08 line | Source Serif 4 |
| Screen heading | `clamp(30px, 3vw, 44px)`, weight 400/500, 1.12 line | Source Serif 4 |
| Major panel heading | 24–28px, 1.2 | Source Serif 4 |
| Character name / result subheading | 20–22px | Source Serif 4 |
| Primary CTA | 16–18px, 600, sentence case | Source Sans 3 |
| List/menu row | 17–19px, 500 | Source Sans 3 |
| Body/explanatory copy | 15–17px, 1.45–1.55 line | Source Sans 3 |
| HUD timer/score | 23–29px, 600, tabular | Source Sans 3 |
| Dense timeline annotation | 13–15px, 500–600 | Source Sans 3 |
| Small metadata | 13–14px minimum outside deliberately ignorable decoration | Source Sans 3 |

Avoid tiny decorative labels on a 16:9 desktop where the whole arena must stay visible; primary live-game HUD labels should never be below ~14px at intended viewing scale. Use semantic text/ARIA in DOM, not bitmap text for navigation.

## 4. Panel and button construction

**Frost panel:** light context `rgba(239,249,252,.66)` + subtle border; `backdrop-filter: blur(12px) saturate(1.06)` only in short-lived/focused menus; dark context use blue/slate translucent panel, not fully black. Padding 24–32px; 14–20px border radius; subtle shadow. When `backdrop-filter` isn't supported or is expensive, substitute **opaque translucent-looking colour overlay with no blur**. Never blur Pixi world just to blur one UI card.

**Primary button:** 46–52px height, generous horizontal padding, quiet dark-slate fill or high-contrast frost, 8–12px radius, text 16–18px Source Sans 3 semibold, visible focus ring. Hover: short tint/opacity transition, not scale-bounce. Pressed: slight vertical displacement (<=1px). Disabled: visible but legible, explanatory tooltip/adjacent text when important. Destructive/leave action requires confirmation if session risk.

**Secondary:** lightly outlined/soft-white text button; back action simple arrow + `Back`, consistent location. No mobile floating radial menu. **Cards/list rows:** line/border separation and calm alignment; entire row clickable only if action is unambiguous; semantic buttons inside when multiple actions exist.

**Progress/charge indicators:** clean segmented rectangles or discreet four-tick bar; charge value is **0..4** and 0/1 share facial glow but **HUD can still show 0 vs 1 truthfully**. Use four small segments with current filled count; never mislabel Soulfire as health. Overlit gets an explicit distinct state, not only an imperceptible halo.

## 5. Composition / framing

- Design around a **16:9 arena remaining entirely visible**, including walls and players. It is *not* safe to crop the game view behind HUD. Use CSS `contain`/letterbox and anchor DOM overlays to the same arena rect if overlays must align.
- Example design canvas for drawing UI art boards: 1600×900 **only for layout mockups**. Never assume this is server world coordinate extent.
- Persistent HUD uses sparse perimeter regions; leave center unobstructed for gameplay and monster shadow. Modal/menu panels may overlay a **static or frozen presentation** where legal, but never imply the server paused.
- Typical maximum content width for a menu card: 520–620px; histories/comparison may use wider 1000–1200px panels. Restrict line lengths, preserve large negative space.
- Max three emphasis colours in one screen: neutral snow/slate, ghost cyan, selective sand gold. Character body colours are independent and remain vibrant.
- Avoid putting a massive global sidebar on game screens. Main menu shows direct access, histories use list/tabs inside one coherent screen.

## 6. Motion principles

**Animations should be noticeable only as guidance:** suggested menu fade/translate 180–240ms, interactive hover 100–160ms, list selection 130–190ms, modal opening 180–240ms, result number settling 350–650ms, timeline marker movement immediate during drag. These are **DIRECTOR BASELINE** and centralized in tokens. Game event durations follow server values/explicit user approvals, never assumed CSS timeouts.

Do not animate every letter, shimmer every panel, shake the camera or play unrequested bats. Main scene motion and sound take visual priority. For `prefers-reduced-motion`, keep state labels and exact timeline information while replacing decorative movement with opacity/instant change. No flashing/high-frequency CRT flicker for reduced-motion users.

## 7. Contrast, legibility, theme

- Day: use deep ink (`#263c4b`) over frost; snow is already almost white. Night: pale ink over slate or reliably darkened backdrop. Contrast aim WCAG 4.5:1 for normal copy, 3:1 for large title/graphics; tune using actual screenshots.
- `backdrop-filter` only on UI panels during menus/replay; no full-screen ongoing glass layer during active play. Protect performance with static opaque alternative.
- A warm candle should remain a bright focal point in night; charge glow should remain dimmer than candle. Ghost cyan must be recognizable even while overlapping living head.
- Do not add vignette. The monster shadow is part of artwork; do not paint another giant shadow overlay or visually obscure it with UI.

## 8. Shared primitives — expected reusable React components

`FrostPanel`, `GameButton`, `BackLink`, `Heading`, `InfoRow`, `SegmentedSoulfire`, `StatusPill`, `ProgressOverlay`, `ScoreReadout`, `ScoreDelta`, `MatchStandingRow`, `HistoryRow`, `ConfirmDialog`, `ToastArea`, `EmptyState`, `ErrorState`, `AudioSlider`, `KeyboardHint`, `HistoryTickLabel`, `HourglassMark`, `TimelineTrack`, `TimelineScrubber`. They may be implemented within existing design-library conventions, but each semantic function must be reusable. No bespoke one-off CSS for each screen.

**Visual acceptance:** menus look like one game, not a collage of borrowed fantasy skins; small copy readable at a distance; hierarchy obvious without pointer hover; UI visually separates itself from world without obliterating it.
