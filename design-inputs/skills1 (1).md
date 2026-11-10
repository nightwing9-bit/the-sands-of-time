---
name: revenant-frontend-design
description: Authoritative UI/UX design system and implementation guidance for Revenant.
---

# REVENANT — Frontend Design Skill

> All money, banks, accounts and people in Revenant are **synthetic**. Every example number in this document is an *illustrative placeholder* and must never be hardcoded as a result. Values shown on screen come from application state.

---

## 1. Design North Star & Signature Demo

**Creative thesis — "The Ledger Remembers."** Revenant is a financial instrument that looks like a printed broadsheet and behaves like a time machine for decisions. It never reverses money; it *re-reads history under a different rule* and shows the difference with typographic and graphical precision.

**Emotional impression:** quiet authority with a pulse. Paper-warm, ink-sharp, and one living thing on the page: money in motion.

**First three seconds (Control Tower, 1440×900):**
1. The eye lands on a large *Constellation* of six banks on warm paper, directed arcs breathing with flow.
2. One amber ring of three arcs (a suspected circular route) is the only saturated warning on screen.
3. A serif headline states the system's state in one sentence ("38 payments held · 1 suspected ring · liquidity shortfall forecast at 10:22 — forecast, not certainty").

**Essential mental model (identical to Ghostlap):** *Choose original history → scrub backward → select exact point → branch with a different decision → watch divergence → compare outcomes.* The original is immutable. A branch is an isolated simulation, never a reversal.

**Defining demo moment:** the analyst fixes the fork at **09:00**, switches fraud policy **v7 → v8**, presses **Run Scenario**, and a second curve peels away from the original ghost-blue line while four tradeoff cards resolve beside it — some green, some red — and an integrity seal confirms *"Original ledger unchanged."*

**Visual principles**
1. Numbers outrank charts, charts outrank labels, labels outrank chrome.
2. One hero visual per screen; everything else is supporting evidence.
3. Colour is information only. Warm paper is the neutral; ghost-blue = original history; violet = alternate; amber/red/green = state.
4. Rules and typography do the work that shadows and gradients do elsewhere.
5. Trust is built through exactness: units, timestamps, sources, and caveats always visible.

---

## 2. Art Direction & Visual Identity

**Internal name: "Broadsheet Terminal"** — Financial Times editorial typography fused with a trading-desk's density, built on a ruled grid.

| Aspect | Decision |
|---|---|
| Geometry | Rectilinear. 12-column ruled grid, 1px hairlines between regions instead of boxed cards wherever possible. Radii 2–4px only. No pills, no circles except bank nodes and the playhead flag. |
| Composition | Asymmetric: hero visual takes 8 of 12 columns, evidence stacks in 4. A serif "lede" sentence sits above every hero as the screen's headline. |
| Texture | Warm paper `#F7F7F3` with an optional 3% fine-grain noise (inline SVG, `opacity:.03`, `mix-blend-mode:multiply`). Surfaces are flat matte. |
| Imagery | None. Only data graphics: constellation, curves, bands, sparklines, hash glyphs. |
| Emotional hierarchy | Consequence (money, shortfall) → decision (actions) → explanation (reasons) → provenance (hash, source, version). |
| Signature elements | (a) Constellation with arcs; (b) the **Fork Rule**: a 1px vertical ghost-blue rule with a diamond head, appearing wherever a branch point exists; (c) the **Integrity Seal** (small ruled stamp: "ORIGINAL LEDGER UNCHANGED"); (d) serif ledes. |

**Deliberate anti-patterns:** dark neon sidebars, purple-blue SaaS gradients, giant rounded white cards, empty-state illustrations, stock donut charts, rainbow bank colouring, "undo/rollback money" language, glassmorphism, Halloween or game motifs, any glow stronger than 8px blur.

**The single permitted gradient:** arc strokes shift opacity along their length (source 90% → destination 40%) to show direction. No other gradients.

---

## 3. Complete Design Tokens

### 3.1 Typography (two web families + system mono)
| Family | Role | Why |
|---|---|---|
| **Instrument Serif** (400, 400 italic) | Wordmark, ledes, screen titles, scenario headline | Condensed editorial authority; distinctive on camera |
| **Inter** (400, 500, 600) with `font-feature-settings:"tnum" 1,"cv11" 1,"ss01" 1` | All UI, tables, chart text, buttons, financial amounts, KPI numerals, operational figures | Neutral, superb tabular figures |
| `ui-monospace, "SF Mono", Menlo, Consolas` (system) | Hashes, IDs only | Zero extra font weight |

| Role | Font | Size/Line | Weight | Tracking |
|---|---|---|---|---|
| Display XL (rewind lede) | Instrument Serif | 56/56 | 400 | −0.02em |
| Screen title | Instrument Serif | 32/36 | 400 | −0.01em |
| Hero KPI | Inter tnum | 48/48 | 600 | −0.02em |
| Eyebrow / label | Inter | 11/14 | 600 | +0.08em, UPPERCASE |
| Card title | Inter | 14/20 | 600 | 0 |
| Body | Inter | 14/22 | 400 | 0 |
| Body S | Inter | 13/20 | 400 | 0 |
| Caption | Inter | 12/16 | 400 | +0.01em |
| Case amount / prominent operational figure | Inter tnum | 40/40 | 600 | −0.02em |
| Table numeric | Inter tnum | 13/20 | 500 | right-aligned |
| Chart axis | Inter tnum | 11/14 | 500 | +0.02em |
| Button | Inter | 13/16 | 600 | +0.01em |
| Hash / ID | system mono | 12/16 | 400 | 0 |

**Typography boundary:** Instrument Serif stays on the wordmark and editorial ledes/headings. All *standalone* monetary amounts, KPI figures, balances, case amounts, score/risk values, tradeoff deltas and time readouts use Inter with tabular numerals (`font-variant-numeric: tabular-nums`); currency and unit labels use Inter. Inline figures inside a serif headline remain part of that headline’s editorial typography. Do not introduce a third display/number font.

### 3.2 Colour — light (primary)
| Token | Hex | Use |
|---|---|---|
| `--canvas` | `#F7F7F3` | Page background (paper) |
| `--surface` | `#FEFEFC` | Panels |
| `--elevated` | `#FFFFFF` | Drawers, modals, tooltips |
| `--archive` | `#ECEFF1` | Canvas while in Rewind (cooler, "archival") |
| `--border` | `#D9DCD6` | Region borders |
| `--hairline` | `#E6E9E5` | Row dividers, grid |
| `--ink` | `#11161F` | Primary text (16.9:1 on canvas) |
| `--ink-2` | `#3B4350` | Secondary text |
| `--ink-3` | `#5F6672` | Captions (5.4:1 on canvas) |
| `--accent` | `#2748D9` | Interactive primary, links, focus base |
| `--accent-ink` | `#0F1B3D` | Primary button fill |
| `--positive` | `#0E8A5F` | Benefit, authorized, pass |
| `--warning` | `#B7791F` | Held, pending, forecast caution |
| `--negative` | `#C0392B` | Loss, fraud, fail |
| `--disabled` | `#A8A89F` on `#E9E6DD` | Disabled |
| `--original` | `#0A8DB0` (stroke) / `#07708C` (text) | **Original history (ghost-blue)** |
| `--alternate` | `#5B3FD1` | **Alternate scenario** |
| `--focus` | `#2748D9`, 2px ring + 2px canvas offset | Keyboard focus |
| Tints | semantic colour at 8% fill / 24% border | Chips, bands |

**Neutral treatment:** The lighter paper canvas and near-white panels improve separation while retaining the ruled, matte visual language. Keep all existing semantic colours and their meanings; do not add lime/sap green as a competing brand accent or repurpose `--positive`.

### 3.3 Dark alternative (secondary)
`canvas #0E1218 · surface #141A22 · elevated #1B232D · border #2A3441 · ink #E8ECF1 · ink-2 #A9B3C0 · ink-3 #7C8795 · accent #7C95FF · positive #3CCB92 · warning #E0A84A · negative #F0705F · original #4CC9EA · alternate #9E86FF`. Structure, type and layout identical; paper grain removed; hairlines at 12% white.

### 3.4 Chart palette (banks — always direct-labelled, never colour-only)
Meridian `#2748D9` · Halden `#0E8A5F` · Corvane `#B7791F` · Ostrava `#A04668` · Tessel `#3D7A8C` · Quillon `#6B6F3A`. Each bank also owns a **dash pattern** (solid, long, short, dot-dash, dot, double) and a 3-letter code (MRD HLD CRV OST TSL QLN).
Forecast: central path dashed `--ink-2`; 80% band `--accent` @10%; 50% band @18%; shortfall zone hatched `--negative` @14%.

### 3.5 Spacing, radii, elevation, icons
- Spacing (4px base): 4, 8, 12, 16, 24, 32, 48, 64. Page margin 32. Gutter 24. Card padding 16/20.
- Radii: 2 (buttons, inputs, chips), 4 (panels), 6 (modals). Bank nodes circular.
- Borders: 1px `--border`; selected = 2px `--accent` inset.
- Shadow: `e1 0 1px 0 rgba(17,22,31,.04)` · `e2 0 8px 24px -12px rgba(17,22,31,.18)` (drawer/tooltip) · `e3 0 24px 64px -24px rgba(17,22,31,.28)` (modal).
- Icons: 16px, 1.5px stroke, square caps, `currentColor`; 20px only in nav.
- Layers: canvas 0 · panels 1 · sticky headers 10 · timeline dock 40 · drawer 50 · modal 60 · toast 70.
- Density: table rows 36px (compact 32); never below 12px text; touch targets ≥44px on touch.
- Contrast: body ≥ 7:1, secondary ≥ 4.5:1, chart strokes ≥ 3:1 against surface plus a non-colour cue.

---

## 4. Layout Architecture & Responsive Geometry

**Persistent navigation:** 56px **Masthead** — wordmark "Revenant" (Instrument Serif italic 24) left; tabs *Control Tower · Held Queue (count badge) · Liquidity · Rewind · Audit* (Inter 600 13, 24px gaps; active = 2px ink underline, Rewind active = 2px ghost-blue underline); right: `SYNTHETIC DATA` chip, UTC clock, role chip, bell, avatar. Beneath it a **28px Status Strip**: `Synthetic data — no real funds · Feed ● Live · Active policy v7 · Last tick 09:41:07 UTC`.

**Grid:** 12 columns, max content width 1680, margin 32, gutter 24. At 1440×900: content 1376 wide, 816 tall below chrome.

**Control Tower region weights (1440×900):**
| Region | Size | Weight |
|---|---|---|
| Title row (lede + as-of) | 1376×56 | — |
| **Constellation hero** | 8 cols ≈ 896×480 | 55% of attention |
| KPI 2×2 | 4 cols, each ≈ 212×104 | 15% |
| Alerts feed | 4 cols ≈ 448×~340 | 12% |
| Bottom row: Balances · Liquidity sparkline · Live activity | 3×4 cols, 220 tall | 18% |

**Scroll:** page-level vertical scroll only; no nested scroll except lists/queue panes. **Rewind** locks viewport: chart region flexes, Timeline Dock fixed at bottom (88px).

**Breakpoints**
| Width | Behaviour |
|---|---|
| ≥1280 | Full layout |
| 1024–1279 | Hero 12 cols; KPIs become a 4×1 strip; bottom row 2+1 |
| 768–1023 | Tabs collapse into horizontally scrollable strip; Held Queue list+drawer becomes list with full-height slide-over |
| <768 | Bottom tab bar (56px, 5 icons+labels); Constellation becomes 360×360 square, arcs only ≥ threshold, tap-to-inspect; tables become cards; Rewind shows a 3-way segmented switch *Original / Alternate / Compare*, dock 72px |

---

## 5. Complete Screen-by-Screen Blueprints

### 5.1 Control Tower — the signature overview
- **Intent:** know the state of money in motion in 5 seconds; enter any case or the past.
- **Lede (Instrument Serif 32):** one sentence from live state. Right of it: `As of 09:41:07 UTC · Synthetic`.
- **Hero — the Constellation.** Six bank nodes (44px circles, 1.5px ink ring, 3-letter code inside, balance beneath in Inter tnum 12/600) on an ellipse. Directed arcs are cubic Béziers bowed toward the centre; stroke width 1–8px = payment volume (√ scale); opacity gradient source→destination; 3px × 6px "packets" travel each arc proportional to count (max 12 per arc). Incoming = arc ends with a small chevron; outgoing = arc starts at a filled dot. Held amounts appear as an **amber arc segment** that stops mid-path with a small bracket "⌐ held" and amount. Suspected ring: the three arcs get a dashed amber stroke and a ruled callout "Suspected circular route · 3 accounts · 11 min". Centre label: `Settlement window 09:00–10:00`.
- Hover a node: other arcs fade to 15%, tooltip with in/out volume, balance, held, status. Hover arc: tooltip with count, volume, held, top reason. Click arc/alert/node → opens the related case in the Held Queue (or bank filter).
- Causal link: releasing/rejecting a held payment visibly changes the arc, the bank's balance count-up, and the Liquidity sparkline in the same 600ms transaction.
- KPIs: Volume Today, Held Amount, Open Alerts, Release Rate. Each: eyebrow, hero numeral (Inter 600 48 tnum), delta vs. prior hour with ▲▼ plus text.
- Alerts: 3px left border by severity, title (600), reason line, bank codes, timestamp, chevron.
- States: *loading* = skeleton nodes + hairline arcs shimmer 1.2s; *empty* = "No payments in this window" with last-seen time; *feed lost* = amber strip banner "Feed interrupted 09:41:07 — showing last known state" with Retry; *permission-limited* = restricted banks drawn hollow with lock glyph.
- Entry to history: tab **Rewind**, plus a ruled link under the hero "Review earlier decisions →".

### 5.2 Held Queue
- **Layout:** left list 420px; right Case Panel fills (≥1280); below, list + slide-over.
- **List header:** search (⌘K), filters (Status, Bank, Amount, Risk, Age), sort (Risk ↓ default), result count. Rows 56px: risk bar (4px, 0–100), amount (600), currency code, `MRD → OST`, age, status chip, score tnum.
- **Case Panel:** header with case ID (mono), amount (Inter 600 40 tnum), status chip; **Payment Path** (sender → bank → bank → beneficiary, ruled schematic); **Risk Indicators** (scored bars, each a plain-English reason, "contributes +18 to 82" with a source tag naming the real signal); **Payment History** (mini table); **Evidence** (collapsible); **Decision History** (timeline of who/when/what, including system holds); sticky **Action Bar**: `Approve` (primary), `Reject` (outlined negative), `Inspect` (ghost), `Close case`.
- **Confirmation modal:** summary rows, consequence line ("Releases $X to OST; Corvane forecast changes from … to …" computed), required note for Reject, `Cancel` / `Confirm Approve`.
- **Second approval:** over a supervisor threshold, Approve becomes `Request Second Approval`; case shows a two-step stepper *Analyst requested → Supervisor review* with chips: `Awaiting Second Approval` (amber), `Authorized` (green), `Unauthorized` (red outline, shows reason: approver lacks authority/declined), `Released` (green solid, completed).
- **States:** loading skeleton rows; *No cases* ("Queue clear — 0 held · last cleared 09:12"); *Insufficient permissions* (actions replaced by a ruled notice with role needed); *Conflict* ("Another analyst already acted on this case" + view their decision); *Pending action* (button spinner, panel locked, aria-busy); *Failure* (inline negative banner + Retry; state never optimistic-lied); *Success* (toast + chip transition).

### 5.3 Liquidity Fan
- **Chart (uPlot):** x = intraday 06:00–18:00 UTC (timeframe 1H / 4H / Day), y = liquidity in $M. Historical = solid bank-coloured line; forecast = dashed central path; **fan** = 50% and 80% bands widening with horizon; **now** marker = ink hairline with label. Shortfall threshold = red hairline labelled `Minimum liquidity $X M`; area below hatched.
- Legend rows double as bank toggles. Crosshair tooltip: time, bank, value, band range, and the *caption "Forecast — not a guarantee"*.
- **Cause→effect:** held payments appear as amber ticks on the axis; hovering one shows ghosted counterfactual forecast (released vs. held) as two thin dashed lines.
- **Warning card (appears only when scenario crosses threshold):** "Corvane projected below minimum at ~10:22 (≈40 min) · 80% band · Review 3 related held payments →". Illustrative scripted scenario; copy says "scenario", never "will".
- States: loading chart skeleton; forecast unavailable (history only, banner); no bank selected.

### 5.4 Rewind Mode — the hero (see §8–9)
Layout: masthead + status strip; **Scenario Bar** (60px) below; **Compare Stage** (flex); **Timeline Dock** (88px). Left of Scenario Bar: `SOURCE · Live ledger · policy v7`. Right: policy selector `Alternate policy ▾ v8`, `Run Scenario` (primary), `Exit Rewind`.
Stage in idle state: one wide chart "Net position, original history" with event ticks. After run: two lanes — **Outcome Curves** (8 cols) and **Tradeoff Cards** (4 cols), with the lede above.

### 5.5 Audit
- Toolbar: search, event type, source (Original / Scenario ID), date range, `Export ▾` (CSV, JSON, signed PDF when supported).
- Table (virtualised): Seq #, Timestamp (UTC ms), Event, Actor, Source chip (`ORIGINAL` ghost-blue / `SCENARIO s-0007` violet), Payload hash (mono, 8 chars), Integrity glyph.
- Row expand: full metadata, previous/next hash, linked case.
- **Integrity panel (right 4 cols):** status chip (`Verified`, `Verifying…` with progress, `Failed` with first bad Seq, `Unavailable`), last verified timestamp, chain length, head hash, `Verify now`.
- **Original vs. Scenario proof card:** two head hashes side by side with identical-state tick "Original chain head unchanged since 09:00 fork".
- States: empty search; verification pending skeleton; fail = red banner + isolate row; unavailable = grey with reason.

### 5.6 Supporting surfaces
Sign-in (centered 420px ruled panel on paper, SSO + role clarification), role indicator chip (Analyst / Supervisor / Auditor), Settings drawer (theme light/dark, density, reduced motion, units), Notifications popover (grouped by severity), Confirmation modals (as above), Tooltips (ink background, 12/16, 6px arrow, never the only source of information).

---

## 6. Components, Visualization & Asset Language
| Component | Spec |
|---|---|
| Button | 36px tall, radius 2. Primary `--accent-ink` fill/white text; Secondary 1px ink border; Ghost text-only underline on hover; Destructive outlined `--negative`. |
| Tabs | Text only, 2px underline, 24px gap; counts in 11px tnum badge. |
| Filter chip | 28px, 1px border, radius 2; active = ink fill. |
| Table | No vertical rules; 1px hairline rows; header Inter 600 11 uppercase `--ink-3`; hover row `#F2F5F1`; selected 2px accent left edge. |
| Metric | Eyebrow → numeral → delta line. |
| Status chip | 22px, radius 2, 6px dot + label, 8% tint fill/24% border. |
| Risk meter | 4px bar on 100 scale, segmented at 40/70; value tnum. |
| Tooltip | `#11161F` bg, white text, e2. |
| Drawer | 560px right slide-over, e2, hairline left border. |
| Timeline Dock | §8. |
| Integrity Seal | 1px ink double rule rectangle, uppercase 11/600 text, small check glyph; *not* a badge pill. |
| Comparison Card | eyebrow, delta numeral (Inter 600 28 tnum), original→alternate pair line, ▲▼ + word. |
| Bank node/arcs | SVG; nodes `<g>` with ring; arcs `<path>` with animated `stroke-dashoffset` packets via CSS (no JS per frame). |

**Chart grammar:** gridlines `--hairline` horizontal only; axis labels 11px; direct end-labels instead of legends where possible; units in axis title ("USD millions"); fork rule is always ghost-blue and 1px with a 9px diamond; delta fill between curves hatched 45° 4px, tinted by the sign of the difference.

---

## 7. Exact Interaction & State Language
| Interaction | Motion | Notes |
|---|---|---|
| Tab change | content cross-fade 160ms, ease-out | Underline slides 200ms |
| Hover arc/node | others fade to 15% over 120ms | State change, informational |
| Drawer/Case panel | 240ms `cubic-bezier(.2,.8,.2,1)` | Focus moves into panel |
| Modal | scrim 0→40% 160ms; modal translateY 8px→0, 200ms | Traps focus; Esc cancels |
| Approve success | chip morphs 200ms; row removes after 400ms; balance count-up 600ms | Real state change only |
| Toast | 240ms in, 5s hold, 160ms out | Errors persist until dismissed |
| Skeleton | 1.2s linear shimmer, `prefers-reduced-motion` = static | |
| Rewind enter | §8 | |
| Curve draw | 900ms `cubic-bezier(.3,.7,.1,1)` | Meaningful: encodes simulation result |

**Button states:** hover = +4% ink; active = translateY(1px); focus = 2px accent ring offset 2px; disabled = flat fill, `aria-disabled` plus tooltip stating *why*. **Cancel** always sits left, **Confirm** right with explicit verb. **Save success vs failure** are visually unmistakable: success = green check toast; failure = red banner *in context*, retry preserved.

---

## 8. Timeline / Restore / Branch / Compare UX

**Anchored Timeline Dock** — fixed bottom, full width, 88px, `--surface`, 1px top rule, e2 upward.
Left → right: `⏮ ▶/⏸ ⏭` transport (36px squares) · speed `1× 4× 16×` · **source chip** `SOURCE · Original ledger · v7` · **Track (flex)** · time readout `09:00:00` (Inter 600 16 tnum) · `Set Fork Here` / `Fork 09:00:00` · `Exit`.

**Track:** 1px ink axis; hour ticks 12px with labels, 15-min ticks 6px; 24px tall band under axis: `--original` @18% (original history span). **Event markers** (8px glyphs above axis): ▲ hold placed, ● release, ◆ policy change, ✕ fraud confirmed. **Playhead:** 2px ink line full-height with a 56×20 flag showing HH:MM:SS. **Fork point:** 1px `--original` rule extended through the stage with a diamond head and label `FORK 09:00:00`; after a run, a violet band `--alternate` @18% under the axis from fork to end labelled `SCENARIO s-0007 · v8`.

**Seeking:** drag playhead or click track (ease 180ms); ←/→ = ±1 event step, Shift+←/→ = ±1 min, `[`/`]` previous/next marker, Home/End, Space play/pause, `F` set fork, Enter run (focus on Run), Esc exit. Scrub direction: left = earlier. Stage updates ≤ 80ms via throttled data window.

**Branch eligibility:** fork permitted only at timestamps with a complete ledger snapshot. Ineligible spans render hatched on the track; selecting them shows inline text "No complete snapshot at this time" and disables Run. Last tick of the window cannot be forked ("Nothing left to compare").

**Progress:** `Running scenario… 38% · replaying 09:00→10:00` determinate bar in the Scenario Bar; accelerated replay is stated as "accelerated replay", never a measured speed.

**Comparison:** two lanes sharing x-axis; synced hover crosshair; signed deltas; labels `ORIGINAL · v7` (ghost-blue) vs `ALTERNATE · v8` (violet), always both text and colour/dash (original solid, alternate long-dash).

**Errors:** run failed (retry, no state changed), snapshot unavailable, scenario limit reached (clear oldest scenario action), integrity pending (comparison shown with amber "verification pending" seal).

**Integrity:** Integrity Seal is permanent in the Dock corner: `ORIGINAL LEDGER UNCHANGED · chain head 9f3c…a71e · verified 09:41:07`. It is *derived from verification state*, never decorative.

**Cross-app correspondence**
| Stays analogous | Visually transformed |
|---|---|
| Bottom-anchored dock | Ruled paper instrument (here) vs. carved stone slab with wax fuse (Ghostlap) |
| Left=earlier scrubber, playhead, markers | Ink line + HH:MM:SS flag vs. flame diamond + m:ss.t |
| Source chip, fork marker, branch ID | `SOURCE · v7`, `FORK 09:00:00`, `s-0007` vs. `ORIGINAL RUN`, tuning-fork notch, `Rewrite #2` |
| Play/pause, step, jump-to-marker, Enter to branch | Same keys; different iconography |
| Original ghost-blue vs. alternate | Violet curve (here) vs. warm living gold route (game) |
| Original never modified | "Ledger unchanged" seal vs. "Original run preserved" rune |
| Branch = isolated | Policy simulation (no money moved) vs. inherited game state |

---

## 9. Demo-First Hero Interaction Sequence

**Shot R1 — Opening Control Tower (hero still #1).** Paper canvas, serif lede, Constellation dominates 8 columns; amber dashed ring visible among HLD→OST→CRV; KPIs top-right with Held Amount in amber; one open alert at top of feed; sparkline row shows Corvane dipping toward the red hairline. Judges understand: six banks, money moving, something suspicious.

**Shot R2 — Alert → case.** Click the ring alert → Held Queue; Case Panel slides 240ms; reasons list in plain English; analyst clicks Approve → modal shows consequence line → confirm → chip/balance changes.

**Shot R3 — Liquidity Fan.** Fan with 80% band crossing hatched shortfall at ≈ +40 min; warning card labelled *scenario*.

**Shot R4 — Enter Rewind.** Click Rewind: canvas shifts from `#F7F7F3` to `#ECEFF1` over 400ms; masthead underline turns ghost-blue; a 2px ghost-blue inset frame draws around the workspace; ruled label `ARCHIVE VIEW · READ-ONLY` appears; Dock rises 320ms; stage shows a single ghost-blue net-position curve.

**Shot R5 — Scrub to 09:00.** Playhead dragged; stage hairline follows; event ticks light as passed; click `Set Fork Here` → diamond + `FORK 09:00:00`; Scenario Bar shows `v7 → v8`.

**Shot R6 — Run Scenario.** Progress bar in Scenario Bar fills; the original curve stays; after fork, a violet curve *draws out of the diamond* over 900ms and the delta hatch fades in 300ms after.

**Shot R7 — THE SIGNATURE STILL.** Composition (1440×900):
- Lede (Instrument Serif 56): *"At 09:00, policy v8 would have blocked **$X** more fraud — and delayed **N** more legitimate payments."* (values from state)
- Left 8 cols: two curves diverging from a labelled fork; original solid ghost-blue, alternate long-dash violet; hatched delta; end-labels with signed net value.
- Right 4 cols: four Comparison Cards stacking: **Fraud prevented** (▲ green), **Legitimate payments delayed** (▼ red), **False-positive cost** (▼ amber/red), **Net financial effect** (signed, dominant). Mixed colours demonstrate honest tradeoffs.
- Bottom Dock: playhead at end, violet scenario band beneath ghost-blue original band.
- Integrity Seal lit in the Dock corner; caption beneath curve: "Simulated alternative on identical synthetic inputs. No ledger entries were changed."
Visual weight: lede 25%, curves 40%, cards 25%, dock/seal 10%.

**Shot R8 — Audit proof.** Audit tab: two head hashes identical; scenario events tagged violet.

Judges understand without narration: same history, different rule, quantified tradeoff, nothing altered.

---

## 10. Accessibility, Responsiveness & Failure States
- Contrast as §3; every semantic state has text + icon/shape, never colour alone (bank dashes, chip labels, ▲▼).
- Full keyboard model; visible focus ring everywhere; logical tab order; `Esc` closes layers; dock is a `role="slider"` with `aria-valuetext="09:00:00, 3 events"`.
- Charts expose an accessible data table toggle (`View as table`); live regions announce alert arrivals (`aria-live="polite"`) and run completion.
- Nothing hover-only: tooltips mirrored by focusable info buttons and row expansions.
- Reduced motion: packets static, no count-up, curves appear instantly with 100ms fade, skeleton static.
- Touch: ≥44px targets, scrubber gets a 44px grab zone, long-press shows event detail.
- Failure recipes: loading skeleton shapes match final content; empty = factual sentence plus next step; disconnection amber strip with automatic retry timer; permission = named role required; stale data dims numerals to `--ink-3` and shows "stale since".
- Privacy: `SYNTHETIC DATA` chip always visible; account numbers masked `•••• 4821`, reveal requires permission and is logged.

---

## 11. Implementation-Ready Frontend Design Rules
1. Load fonts via `<link rel="preconnect">` + Google Fonts `display=swap`: `Instrument+Serif:ital@0;1`, `Inter:wght@400;500;600`. Fallbacks `Georgia`, `system-ui`. Preload Inter 500.
2. Implement tokens as CSS variables in `:root` and `[data-theme="dark"]`; components use semantic tokens only.
3. Never import a component library's styles; wrap headless primitives and style from tokens.
4. uPlot wrapper: one `<RevenantChart>` component injecting tokens; layers via hooks: bands → hatch → lines → fork rule → hover crosshair (canvas for lines, SVG overlay for annotations).
5. Constellation: SVG; arcs computed from bank coordinates; packets animate through CSS `offset-path`; hard cap 12 packets/arc.
6. Timeline Dock: a single `<TimelineDock>` with props `{span, markers, source, forkAt, branches, onSeek, onFork}`. Same props interface is reused by Ghostlap; only the theme/skin differs.
7. Motion driven by real events: arc changes, balance count-ups, curve draws, seal state come only from application data. Decorative-only motion is forbidden.
8. Throttle scrub updates (rAF), virtualise Audit rows, debounce search 200ms.
9. Fallbacks: if chart fails, render a table; if animation disabled, static end-states.
10. Responsive via CSS grid + container queries; no horizontal page scroll.

---

## 12. Design Acceptance Checklist & Forbidden Patterns

**Checklist**
- [ ] Control Tower reads as a financial instrument in 3 seconds; Constellation is unmistakable hero.
- [ ] Only Instrument Serif + Inter (+ system mono for hashes).
- [ ] Paper canvas `#F7F7F3`; ghost-blue = original everywhere; violet = alternate everywhere.
- [ ] Rewind shows fork rule, source chip, scenario ID, both curves, mixed-tradeoff cards, Integrity Seal.
- [ ] Original never described as changed; copy says "simulated", "forecast", "scenario".
- [ ] Timeline Dock identical placement and keys to Ghostlap.
- [ ] Every screen has loading, empty, error, permission states.
- [ ] Tables use tabular numerals, right-aligned.
- [ ] Dark theme keeps structure; mobile usable; focus visible; reduced motion supported.
- [ ] Synthetic-data disclosure visible on every screen.

**Never**
- Pure `#FFFFFF` page background, dark sidebars, pill buttons, glassmorphism, neon.
- Gradients (except arc opacity ramps), decorative charts, rainbow bank colours.
- "Undo", "rollback", "restore money", or any wording implying history was altered.
- All-green outcomes; hiding losses; presenting forecasts as certainties.
- Halloween or game motifs; emoji; illustration empty states.
- Hover-only critical information; colour-only meaning.
- Hardcoding example figures as results.
