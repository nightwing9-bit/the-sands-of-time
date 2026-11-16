# Revenant Milestone 1 — Frontend Baseline and Milestone 2 Handoff

Read-only discovery, 16 Nov 2026. No source, style, API, test or Git history was changed. This file is the only artifact.

Labels used below: **[Inspected]** read in source or image, **[Executed]** run in this assignment, **[Prior]** reported by earlier work and not rerun here, **[Unverified]** not established.

---

## Section 1 — Executive baseline

| Item | Finding |
|---|---|
| Branch | `codex/r8-counterfactual-revenant-20261114`, tracking `origin/codex/r8-counterfactual-revenant-20261114` with no ahead/behind. |
| HEAD | `a1c7c2473ac05f15005c1f0cba477c90a2c96656`, "Scope legacy payment guard checks to the fixture run". |
| R8 commit | Exists locally. HEAD *is* that commit, so it is trivially an ancestor. Contained by the local branch and `origin/codex/r8-counterfactual-revenant-20261114` only. |
| Relation to `main` | `main` (`2fde4a5`) is an ancestor of HEAD; HEAD is 66 commits ahead. **The R8 implementation is not on `main`.** It lives only on this unmerged draft-PR branch (R7 `fe83c52` sits underneath at `codex/r7-ssa-anomaly-liquidity-20261113`). |
| Implementation present in working tree | Yes. `web/apps/revenant/src/r8/RevenantR8.tsx`, `r8.css`, `bufferReference.ts`, `src/Revenant/Api/RevenantR8Api.cs` and `tests/E2E/revenant-r8*.spec.ts` are tracked and present. |
| Working tree | Dirty before this task: modified `.kiro/specs/spec-08-*/{design,requirements,tasks}.md`; deleted `design-inputs/skills1 (1).md`; untracked `design-inputs/skills1.html`, `design-inputs/mockups1/`, `SPEC_07-13_ARCHITECTURE_AUDIT.md`, `docs/codex-ml-recovery-log.md`, `docs/recovery/{r1-resumption-checkpoint,r3-kiro-implementation-handoff,spec08-architecture-inspection}.md`. This task adds only this report. |
| Design inputs | `design-inputs/skills1.html` (344,014 bytes, 744 lines) exists. All nine PNGs exist (about 2660×1664 each) and were all viewed. `skills2 (1).md` ignored as instructed. |
| Integration step needed before frontend work? | **No**, provided Milestone 2 is done on this branch (or a branch cut from it). Anyone starting from `main` would be editing an obsolete frontend. |
| Design inputs are uncommitted | `skills1.html` and `mockups1/` are untracked. Commit or otherwise protect them before Milestone 2 so they survive branch changes. |

**What was inspected:** Git state, `main.tsx`, all of `RevenantR8.tsx`, `r8.css`, `bufferReference.ts`, `RevenantR8Api.cs` (routes, DTOs, service methods), the shared `TimelineScrubber` surface, the Vite config, the R8 and liquidity Playwright specs and configs, the CI job names, `skills1.html` (CSS tokens, JS state/handlers, both embedded base64 documents decoded to `/tmp`), and all nine screenshots.

**What was executed:** see Section 8. In short, Revenant typecheck (pass), one stubbed Playwright spec file (4/4 pass), and a throwaway script that opened `skills1.html` at 1440×900 and 1366×768. The authenticated live backend was **not** run (Docker daemon is down, no PostgreSQL).

---

## Section 2 — Current implementation inventory

| Feature / subsystem | Source path | Existing behavior | Evidence | Reuse decision |
|---|---|---|---|---|
| Entry and routing | `web/apps/revenant/src/main.tsx` | R8 app mounts only when URL has `?view=r8` (lazy import). Default is the legacy Friday guest slice (`slice/FridaySlice.tsx`); `?timeline=<id>` mounts a generic inspector. | Inspected | Preserve the lazy mount. Decide in M2 whether `/` should open R8 (see §9). Leave slice and inspector untouched. |
| Screen routing | `RevenantR8.tsx` `routes`, `currentRoute()` | Hash routes `#tower #held #liquidity #rewind #audit`; query `run=` and `job=` kept via `history.replaceState`. | Inspected; e2e uses `#rewind` links | Preserve hash keys and query keys. |
| Auth and session | `RevenantR8.tsx` `RevenantR8`, `doLogin` | `POST /auth/staff/login {userName,password,totpCode}`; in-memory access token; cookie refresh via `POST /auth/staff/refresh` when the `STAFF_CSRF_COOKIE` exists; `POST /auth/staff/logout`. No MFA enrollment UI. No use of `/auth/staff/me`. | Inspected; contract lists `/auth/staff/me`, `/mfa/enroll`, `/mfa/confirm`, `/mfa/verify` | Preserve auth flow. Restyle the form only. |
| API client | `@sands/contracts` `createApiClient`, `components["schemas"]` (`R8*` types) | Typed `api.get/post` with path params, idempotency key, CSRF getter, 45 s timeout. Generated types in `web/packages/contracts/src/generated/hosts-api.ts`. | Inspected | Preserve. Do not hand-roll `fetch`. |
| Backend routes | `src/Revenant/Api/RevenantR8Api.cs` `MapRevenantR8`; registered at `src/Hosts.Api/Program.cs:38` with `IdentityPolicies.Staff` | 11 routes under `/revenant/r8` (see §5). Staff only, tenant required, role `operator` or `supervisor`, per-run membership check. Replay worker registered in `Hosts.Worker/Program.cs` (`AddRevenantV4Worker`). `Hosts.Realtime` is not used by R8. | Inspected | Preserve. |
| Live finance refresh | `useFinanceRefresh`, `useLoad` | Polls `GET /runs/{id}` every 2.5 s while the tab is visible, on tower/held/liquidity, until the run status is `Verified`. Replay job polls every 1.2 s until `Verified/Failed/Cancelled`. **No SignalR in R8.** | Inspected | Preserve. The HTML "Feed · Live" must be worded as polling. |
| Idempotent writes | `useOperation` | One UUID key per logical operation, reused on retry, cleared on success; busy lock. | Inspected | Preserve. Reuse for the modal confirm. |
| Control Tower | `RevenantR8.tsx` `Tower` | 3 metric tiles (settled count, held count, reserved sum), SVG six-node constellation with hard-coded anchor coordinates, balances table, opening balances, advisory anomaly table, decisions table. | Inspected | Restyle and restructure. Keep data derivation. |
| Held Queue | `Held`, `Facts` | Case table (intent, status, approvals n/m), evidence panel (amount, score ppm, threshold ppm, reason, policy, observations, model and evidence digests), Approve/Reject buttons with `expectedVersion`. | Inspected | Restyle and restructure. Keep the operation call. |
| Liquidity | `Liquidity`, `Chart`, `BufferNote`, `bufferReference.ts` | `GET /liquidity`; per-bank select; Posted, Available, Reserved series plus "Statistical baseline" 8-point projection; labelled illustrative buffer line (10 % of opening available, policy `R8-SyntheticBufferReference/1`); data tables. | Inspected; `revenant-r8-liquidity.spec.ts` | Preserve data and disclosure logic. Replace the chart drawing. |
| Rewind and replay | `Rewind`, `Comparison` | Shared scrubber over source timeline; exact BigInt-safe seek via `GET /timeline/{position}`; "Close and verify source"; policy and mode selectors; `POST /counterfactual`; status, cancel; 4 KPI tiles from `verified.headlines`; per-bank source/alternative chart; breakdown and payment evidence tables. | Inspected; e2e specs | Preserve all logic. Restyle. Add the missing source chart (see §4). |
| Audit | `Audit` | `GET /audit`: timeline status, state/record/manifest hashes, policy, model, ledger verification and audit head, last 30 events; sandbox verification panel shows "Original history unchanged" when audit hashes equal the job's source hashes. | Inspected | Preserve. Restyle. |
| Metrics (KPIs) | `RevenantR8Api.cs` `Describe` | Four headlines from backend (`Fraud prevented`, `Legitimate payments delayed`, `False-positive cost`, `Net financial effect`) plus six breakdown rows. | Inspected; e2e expects deltas `150000,1,250,149600` **[Prior]** | Preserve. Never recompute in browser. |
| Shared Timeline | `web/packages/timeline-ui/src/TimelineScrubber.tsx` (370 lines), `adapter.ts` | Single scrubber (`role=slider` "Timeline position", marker buttons `Marker: <label>`, `data-testid=timeline-scrubber`, `timeline-label`, `fork-reason`, `read-only-note`, `timeline-error`). **Inline-styled**, not class-themed. Also imported by `web/apps/ghost-lap` (4 files). | Inspected | Preserve the component API and behaviour. Theme it from outside only (see §6). |
| Read projections | `R8Snapshot`, `R8Liquidity`, `R8Audit`, `R8Preview` records | See §5. | Inspected | Preserve. |
| Styling | `web/apps/revenant/src/r8/r8.css` (5.4 KB) | One plain CSS file scoped under `.r8`, CSS variables (`--r8-*`), dark palette, Georgia/Inter, document scroll, `max-width:1480`, media queries at 860 px and 570 px. No CSS modules, Tailwind or CSS-in-JS. No web fonts loaded. | Inspected | Replace the palette and layout. Keep the one-file-per-app pattern or add sibling CSS files in `r8/`. |
| Charts | Hand-written SVG in `Tower`, `Chart` | No chart library in `web/apps/revenant/package.json` (deps are `@sands/*`, `react`, `react-dom`). | Inspected | Keep hand-written SVG; do not add a chart dependency. |
| Tests | `tests/E2E/revenant-r8.spec.ts`, `revenant-r8-liquidity.spec.ts`, configs `playwright.r8.config.ts`, `playwright.config.ts` | See selector list in §9. | Inspected | Update deliberately; see conflict with laptop-only target in §6. |
| CI | `.github/workflows/ci.yml` (~line 868 "Playwright R8 with the real API and separate Replay Worker") | **[Prior]** Reported green on Linux. Not rerun. | Inspected job name only | Treat as prior evidence. |

---

## Section 3 — Visual design inventory (`design-inputs/skills1.html`)

### 3.1 Global structure

- Single page, `#shell > #frame[role=application] > #app`, plus `iframe#signInGate` overlay. [Inspected]
- **The design is a fixed 1440×900 artboard.** `resizeFrame()` applies `transform: scale(min(1, innerWidth/1440, innerHeight/900))`. At 1366×768 the scale is 0.8533 [Executed]; there is no responsive reflow. Text shrinks with it (an 11 px eyebrow renders at about 9.4 px).
- Screen 1 (Control Tower) is **a separate 72,574-byte HTML document** (`RESERVOIR_B64`) decoded into `iframe#reservoirFrame` (`.screen` is 1440×900, `overflow:hidden`). It posts `{type:'revenant:navigate',screen:n}` to the parent for nav. It has its own CSS and 2 scripts and no data fetching.
- Sign-in is a second embedded document (`SIGN_IN_B64`, 19,175 bytes) in `iframe#signInGate`; it posts `{type:'revenant:enter-demo'}`.
- Screens 2–8 are JS template strings rendered into `#app`: `casePage()`, modal, `screen4` (liquidity), `screen5to7()`, audit. They reuse chrome (`masthead` 64 px high, then a status sub-bar).
- Dead code: `plate()` embeds a base64 WebP terrain image and `screen1()` renders a different tower. Neither is used by `render()` for screen 1. Do not port them.
- Navigation model: numbers 1–8 (`navigate(n)`); 3 = modal over screen 2; 5/6/7 = rewind original / fork / comparison. `activeNav()` maps these onto five tabs. The "SCREEN PREVIEW 02 / 08" pager in the sub-bar is a prototype affordance, not product UI.

### 3.2 Design tokens (final `reservoir-design-system` `:root`, lines 42–60)

| Group | Values |
|---|---|
| Primary teal | `--navy #0F4C5C`, `--navy-900 #0A3946`, `--navy-700 #186575`, `--mist #C3DFDE` |
| Surfaces | `--canvas #E8F1F0`, `--surface #F5FAF9`, `--surface-tonal #D8EBE9`, `--surface-sunken #EDF6F4`, page bg `#D5E6E4` |
| Lines | `--border #BDD8D6`, `--hairline #D1E3E1` |
| Ink | `--ink #0B2530`, `--ink-2 #30535B`, `--ink-3 #548087` |
| Semantic | positive `#0F6270` (on dark `#BDE7E7`); warning `#C7543D` (fill `#FF6B4A`, on dark `#FFB19C`); negative `#D6533A` |
| Series | original `#3F8F9A` (text `#276872`); alternate `#FF6B4A` (text `#CC5037`); focus `#3F8F9A` |
| Elevation | `--e1 0 1px 3px rgba(9,59,66,.028)`, `--e2 0 8px 25px -18px rgba(9,59,66,.20)`, `--e3 0 30px 100px -35px rgba(7,38,46,.36)`, `--e-up 0 -13px 35px -22px rgba(8,56,65,.38)` |
| Dark dock | gradient `#1A2742 → #14203A` (inherited from the first, overridden palette) |

An earlier `:root` (lines 12–33, navy `#1F2E4A`, Inter, Source Serif 4) is fully overridden by the later block. Do not use it.

### 3.3 Typography and conventions

- `Plus Jakarta Sans` (weights 400–800) for everything (forced with `!important`); `JetBrains Mono` for times, codes and hashes. Large display headings are heavy and tightly tracked (screenshots show roughly −0.04 em). Eyebrows: 11 px, 600, uppercase, wide tracking.
- Card classes: `.s1` white card, 10 px radius, `--e1`; `.s2` dark gradient card, 11 px radius; `.s3` tonal. Pills/chips, `.t-eyebrow`, `.t-figure` + `.unit`, `.quad-tile` KPI tiles (dark/light variants).
- Fixed heights in the artboard: `.tower-top 452px`, `.fan-layout 615px`, `.rewind-screen 808px`, `.instrument-dock 128px`, `.scenario-bar 56px`, `.audit-toolbar 54px`, queue sidebar `382px`, modal `520px` wide, modal scrim `inset:92px 0 0`.
- Motion: `red-peel` (alternate path stroke-dashoffset, 0.9 s), `wedge-appear`, `scrim-in` (0.16 s), hourglass sand level on the dock; `prefers-reduced-motion` rules exist (2 blocks).

### 3.4 Graphics

- Tower: six reservoir "basins" (circles with water level) joined by dashed channel paths with direction marks, one red highlighted ring route, a callout card, plaques. Built as inline SVG inside the iframe document. No raster.
- Screens 4–7: SVG chart with `viewBox 0 0 1000 422`, `preserveAspectRatio="none"` (this distorts strokes and glyphs when the panel aspect ratio changes; avoid in React), marker lane (▲ ● ◆ ✕), tooltip on pointermove, dashed projection with 8 hollow points, hatched divergence wedge between curves.
- Dock: hourglass SVG, seek groove with drag (`pointerdown/move` on `#dockSeekZone`), play/step buttons, speed 1×/4×/16×, "Set Fork Here", read-only archive badge.

### 3.5 Fonts and external assets

- External: Google Fonts (`Plus Jakarta Sans`, `JetBrains Mono`) in the host page; the Tower document additionally requests `Archivo`, `Cormorant Garamond`, `Inter`, `Newsreader`. Sign-in requests Plus Jakarta Sans and JetBrains Mono. No images except the dead terrain WebP. **Fonts are needed for fidelity.** Per instructions nothing was downloaded. M2 must decide self-hosting (an offline demo should self-host; the repo currently loads no web fonts).
- The Playwright observation blocked Google Fonts and the layouts still rendered with fallback fonts; fidelity of type metrics is therefore font-dependent.

### 3.6 State and interaction logic (all client-side, illustrative)

`S` object: `screen, selectedCase, caseQuery, decision, decisionNote, selectedBank[], liquidityBank:'CRV', timeframe:'DAY', policy:'v8', forkPos, playhead, hasFork, ran, running, runProgress, playing, speed, modalOpen, auditQuery, auditSource, auditRow, verified, branchVerified, toast, viewTable`.

- `eventAction('confirmDecision')` mutates `DEMO.cases[...].status` locally; no network.
- `runScenario()` fakes progress with `setInterval` and random increments (`8 + floor(rand*10)`) and then navigates to screen 7. Fake.
- `policySelect` accepts `v8` and `v9`; `v9` multiplies the alternate curve by 1.3 and swaps in a second hard-coded metric set (fraud 2.71, false 0.23, analyst 0.42, net 2.06, delayed 19).
- Playback (`beginPlay`) advances `playhead += .0017 * speed` every 35 ms. The seek is a continuous 0–1 fraction mapped to a clock 06:00–12:00, not to event sequence numbers.
- Audit search and source filter are client-side; `exportAudit()` builds a CSV blob client-side; "Run demo check" flips local flags.
- Modal: focus trap on Tab, reject requires a non-empty note.

### 3.7 Hard-coded mock data (`DEMO`)

balances for 6 banks (MRD, HLD, CRV, OST, TSL, QLN), 7 cases (`RV-HLD-2030…2041`, $44k–$1.84M, risk 44–94/100), `metrics` (volume 218.6, held 3.47, alerts 7, release 96.4, fraudPrevented 2.46, delayed 14, falsePositive .18, netEffect 1.92), `original`/`alternate` arrays of 19 points ($15.4M–$31.2M), two fake 16-hex hashes, 10 fake audit rows. The Tower document hard-codes `$18.4M`, `$3.47M`, `92.4%`, `173 routed transfers`. Internal inconsistencies: "Volume today $18.4M" vs `metrics.volume 218.6` and release rate 92.4 vs `metrics.release 96.4`; "Oster/Ostera" and "Tessel/Tessell" spellings differ between screens.

---

## Section 4 — Nine-state implementation matrix

Status vocabulary: **EB** existing and backend-connected, **EV** existing but visually different, **HO** HTML-only/illustrative, **M** missing, **U** blocked or not verified.

Global notes that apply to every screen:

- All nine screenshots use the light teal "paper" theme. The existing React is dark obsidian. This is a theme decision (see §9, "Unresolved question 1").
- Header chips in the mockups ("Synthetic data", "Feed · Demo/Live", "Active policy v7", "Last tick 09:41:07 UTC", clock "09:41:07 UTC", role "Analyst", bell, avatar) have these real equivalents: the synthetic tag exists; policy v7 is real (`audit.policy`, base policy pinned); time must be the **scenario boundary clock** (`snapshot.boundaryMs`, simulated origin `1_789_203_300_000`), not wall time; role maps to `operator`/`supervisor`; bell has no backend; avatar initials have no backend source (could derive from user name, **U**).
- 1440×900 primary, 1366×768 compact (see §6 for how).

### State 1 — Control Tower

- **Reference:** `mockups1/control-tower.png`.
- **HTML source:** `RESERVOIR_B64` document (decoded; `<h1>` "Money is moving across six banks; one ring is asking for attention."); parent `render()` for `screen===1`.
- **Existing React:** `Tower` in `RevenantR8.tsx`. **EV**.
- **Visual treatment to preserve:** four KPI tiles (two dark-teal, two pale) across the top; large map card (about 2.04 fr) with six reservoir circles holding balance labels, dashed channels with direction ticks, one orange-highlighted route and floating callout; right "Priority Alerts" card (3 rows with red left rule, time at right, footer "three require human review · 07 / open"); bottom strip of three panels (posted bank positions list, single-bank posted chart with buffer line plus level gauge, live activity list); heading in the 50-px-plus heavy display style.
- **Existing data:** `GET /revenant/r8/runs/{id}` → `R8Snapshot { balances, payments, cases, markers, anomalyStatus, anomalies }`. Settled/held counts, summed reserved, payments with route and status are **EB**.
- **Missing or incompatible:**
  - "Volume today $18.4M · 173 routed transfers" and "Release rate 92.4 % vs 89.1 % yesterday": no such fields. Seeded run has 4 payments of $1,500.00 each. Volume, count and a release rate can only be derived from `payments[]` and must be defined explicitly (for example, "settled principal / total accepted principal in this run"). "Yesterday" has no source: drop.
  - "Open alerts 7 · 3 high priority": backend has *cases* (review) and *advisory velocity anomalies* (only returned when at least 1,152 five-minute buckets of history exist, else `InsufficientHistory`). No "priority" or severity field. Show real counts separately and label advisory.
  - "Suspected circular route MRD→OST→CRV, 3 accounts, 11 min", "Circular route evidence", "Corvane reserve utilization": no circular-route detector or evidence exists in the DTOs. **HO**. Do not render as findings.
  - Corvane "Declining" label and gauge: could be derived from the liquidity series; not a backend statement.
  - Live activity feed: derivable from `payments[]` (intent, route, amount, status), but `R8Payment` has `acceptedAtMs` only; no event stream. Actions: "released/held/routed" must map to real statuses (`Settled`, `Held`, `Returned`, etc.).
  - Bank names: backend IDs are `BANK-A…BANK-F`. MRD/HLD/QLN/OST/TSL/CRV and names have no source. A display alias table is a presentation choice (see §6).
- **Reusable:** balance derivation, held/settled counts, payments → edge drawing (including dashed held edges), `useFinanceRefresh`, `openCase` jump to Held.
- **Remaining work:** port the Tower SVG (basins, channels) into React as a data-driven component; compute basin fill from real `availableMinor/postedMinor` per bank (state the formula in a tooltip); build tiles from real aggregates; Priority Alerts from real cases plus advisory anomalies, honestly labelled; bottom strip from `Liquidity.actual` plus `bufferReference`; remove HTML numbers.

### State 2 — Held Queue

- **Reference:** `held-queue.png`. **HTML:** `casePage()`; `DEMO.cases`; queue sidebar 382 px.
- **Existing React:** `Held`. **EV**.
- **Preserve visually:** left 382-px queue card (search, filter chips, risk sort, selectable rows with big amount, score `94/100` and bar); hero dark card (case ID, timestamp, amount $1,840,000 USD, route); "Payment path" and "Payment history", "Decision history" cards; "Risk indicators" card with four bars; "Evidence & provenance"; bottom action bar with Inspect, Reject, Approve.
- **Existing data:** `R8Snapshot.cases[]` (`caseId, intentId, version, status, requiredApprovals, approvalCount, amountMinor, risk`) and `payments[]` (route, status, reason, risk). **EB** for list, selection, amounts, route, approvals n/m, reason, policy, model ID, evidence digest. `GET /runs/{id}/cases/{intent}` exists but is unused by React.
- **Missing or incompatible:**
  - Score "94/100" and "Model score": real value is `scorePpm` (integer, parts per million) and `thresholdPpm`; first-N cases have `null` (Warmup). Converting ppm to /100 is a new unit; show ppm and threshold as stored, with the existing caution that this is a recorded model score, not a probability of fraud (the mockup caption already says so).
  - "Observed transaction velocity 11/15m" and "Route evidence 3 banks · circular trace": not in `R8Risk.observations`, which contains only *Base rule*, *Review slots available*, *Prior causal fact* and *Feature vector digest*. **HO**. Do not invent features. Real velocity data exists only as advisory `anomalies[]` (observed, expected, score, threshold) per subject.
  - "Payment path: Northstar → MRD → OST → Account": `R8Payment` exposes source and destination **bank** only, no sender/beneficiary names or account IDs. Show bank → bank (two nodes). Adding accounts needs an API change: out of scope.
  - "Payment history" rows with timestamps: no per-case history in the DTO. `audit.recentEvents` (last 30, filtered by `subject == intentId`) may supply event names but has **no timestamp** field; **U** whether intent IDs appear as `subject`.
  - Status vocabulary: `HELD`, "REJECTED", "AWAITING SECOND APPROVAL" in HTML vs real `CaseStatus` strings (`Open`, `AwaitingSecondApproval`, `Released`, etc.; exact enum list **U**; React already treats `Open` and `AwaitingSecondApproval` as actionable).
  - Age ("12m ago"): derivable from scenario clock only.
  - Filters (Status, Bank, Amount, Risk, Age) and search: client-side over the list; no backend.
  - Footer "Actions append to synthetic audit history": true (`ledger.ReadAuditAsync`), cite it only after the audit refresh shows the new row.
  - "Analyst authority · supervisor sign-off required": must come from the case (`requiredApprovals > approvalCount`) and the actor role, not a constant.
- **Reusable:** `op.run` with `expectedVersion` and idempotency key, `reload()` in `finally`, selection by intent ID, `Facts` list.
- **Remaining work:** rebuild layout; split list/detail components; compute copy from case state; add search/sort/filters; show a risk bar from ppm against threshold; keep evidence digests in a collapsed "Evidence & provenance" block.

### State 3 — Payment Approval modal

- **Reference:** `payment-approval.png`. **HTML:** `.confirm-modal` (520 px) with `eventAction('openApprove'|'openReject'|'confirmDecision')`.
- **Existing React:** none; Approve/Reject buttons call the API directly with no confirmation. **M** (modal), **EB** (action).
- **Preserve:** scrim with blur over the held screen, "CONFIRM DECISION · <id>" eyebrow, large question, summary rows (originating bank, receiving bank, reserved amount, authority/status), warning callout "Immediate consequence", Cancel and primary buttons, focus trap, close button; Reject variant with destructive primary.
- **Real action:** `POST /revenant/r8/runs/{id}/cases/{intent}` body `{ action: "Approve"|"Reject", expectedVersion }`, header `Idempotency-Key`. Requires a **fresh MFA claim** (`403 mfa_required` otherwise), run membership, not a sandbox (`403 sandbox_live_write_denied`). Response is the updated `R8Case`. If the new status is `Released`, the server also dispatches settlement.
- **Conflicts:**
  - Button/text "Request Second Approval" and "This action does not release or post any funds" is **only true when `approvalCount + 1 < requiredApprovals`**. The API has no separate "request approval" operation: "Approve" records this actor's approval; when it satisfies `requiredApprovals` the case is released and settlement is attempted. The modal must branch: first of two → "Record first approval; funds stay reserved"; completing → "Release held funds and attempt settlement"; single-approval case → release.
  - "Authority · Analyst · 1 of 2 approvals": show the real `approvalCount/requiredApprovals` after-the-fact; names of earlier approvers are not in the DTO. Distinct-actor four-eyes is a project invariant; its enforcement was **not re-audited here** (U) and the UI should simply surface the server error if the same actor tries twice.
  - Reject requires a "REJECTION REASON" in HTML. `R8Review` has only `Action` and `ExpectedVersion`: there is nowhere to store a reason. Omit the field (or make it explicitly "not recorded"). Do not collect text that is silently discarded.
  - Version conflict (stale `expectedVersion`), MFA missing and `finance_pending` (409) must have visible failure states. The existing code surfaces `error` text only.
- **Reusable:** `useOperation`, `unwrap`, `reload`.
- **Remaining work:** modal component (portal inside `.r8`), focus trap and Escape, copy computed from case + role + MFA state, disable while `op.busy`, error panel with `code` from `ProblemDetails`.

### State 4 — Liquidity Fan

- **Reference:** `liquidity-fan.png`. **HTML:** screen 4 template (`fan-layout 615px`, chart `viewBox 1000×422`).
- **Existing React:** `Liquidity` + `Chart` + `BankSelect`. **EV**.
- **Preserve:** heading "Know what is posted, reserved and available." chart card with chips "Observed ledger", "8-point persistence" and 1H/4H/DAY segmented control; vertical "now" line with time tag; dashed projection with hollow points; dashed warning-red illustrative buffer line with label; right column: orange-edged "STATISTICAL MODEL · LIMITED / A persistence reference, not a crisis forecast" card, and bank positions table with checkboxes; three tiles posted/reserved/available for the selected bank.
- **Existing data:** `GET /liquidity` → `R8Liquidity { method, uncertainty ("Uncalibrated"), anomalyStatus, actual: {atMs, balance}[], projection: {bankId, atMs, availableMinor}[] }`. **EB.** `bufferReference()` supplies the labelled illustrative buffer with the disclosure text.
- **Conflicts:**
  - The HTML copy ("Eight equal-value points extend the last observed available balance… illustrative only") is **consistent** with the backend method (`AdvisoryLiquidityProjection.Method`, persistence baseline). Keep that framing. There is no SSA confidence band and no early-crisis forecast; do not draw a band or a "fan".
  - HTML axis `+120m / +240m / +8h` is wrong for the real data: projection points are on 5-minute bucket boundaries (fixtures use `+300 000 ms`), so eight points span 40 minutes. Axis labels must come from `projection[].atMs`.
  - Buffer "$1.00M" is HTML-only; real buffer is 10 % of the bank's opening available balance, only when that is positive and `posted − reserved = available`; otherwise "Reference unavailable".
  - Values: HTML shows millions; real seeded values are thousands of dollars.
  - 1H/4H/DAY chips: no backend counterpart; a client-side window over `actual` is possible but the scenario lasts a short span; consider dropping.
  - Multi-bank checkboxes: React supports one bank (`BankSelect`). Multi-select is a front-end filter only.
  - Triangle markers on the HTML chart: no corresponding backend events. Omit or map only to real markers.
  - "Live" claim: polling every 2.5 s while the run is not `Verified`; once closed the page is static.
  - "Review related held instructions": link to `#held` is fine.
- **Reusable:** `useLoad`, `useFinanceRefresh`, `bufferReference`, `BufferNote`, "Liquidity data table" disclosure (existing test targets this).
- **Remaining work:** replace `Chart` drawing (keep data prep and `bigint` scaling), add the right-hand cards, three tiles, and responsive chart sizing without `preserveAspectRatio="none"`.

### State 5 — Rewind, original history

- **Reference:** `rewind-original.png`. **HTML:** `screen5to7()` with `mode==='original'`, `scenarioBar()`, `instrumentDock()`.
- **Existing React:** `Rewind` (scrubber + preview line + action row). **EV**; **no source chart exists**.
- **Preserve:** scenario bar (badge, source ledger text, policy select, "Execute Replay", "Exit Rewind"), heading "Explore the completed original history, without changing a single entry.", large "net position · original history" chart with marker lane and callout "ORIGINAL · v7 24.1M", bottom dark instrument dock (hourglass, seek groove with draggable thumb and time bubble, transport buttons, "Set Fork Here", read-only badge "ORIGINAL ARCHIVE UNCHANGED, chain head …").
- **Existing data:** `R8Snapshot.markers[]` (positions are decimal strings), `GET /timeline/{position}` → `R8Preview {position, boundaryMs, stateHash, status, balances}`; `POST /close`; shared scrubber. The source balance history is available from `GET /liquidity` `actual` (per bank, per seal). That is the data to draw the original curve **[Inspected; the existing UI does not use it for this]**.
- **Conflicts:**
  - "Archived session · 15 Nov 2026, 06:00–12:00": real runs are freshly seeded scenarios on a simulated clock; they become "completed" only after **Close and verify source** (`R8Snapshot.status === "Verified"`). Until then, replay is not offered.
  - Seek is by event **sequence number** (BigInt-safe decimal strings), not a continuous time fraction; the HTML thumb snaps to 0–1. Time labels come from markers only.
  - Valid fork points: only an `ObservationSeal` marker whose `atMs < boundaryMs`, and `position ≠ "0"` (server rejects 0). Existing UI shows markers for the last four seals and the seal at the first payment's acceptance time. "Set Fork Here" must stay disabled otherwise and explain why.
  - Marker glyphs (▲ alert, ● payment, ◆ fork, ✕ fraud) are HTML-only categories. Real `kind` values include `ObservationSeal` and `Screening`; the full enum was not inspected (U).
  - Playback (play, 1×/4×/16×, step buttons) has no backend counterpart; the shared scrubber has step controls but no autoplay. Stepping is the real, verified behaviour; autoplay is an optional frontend-only extra that would issue many `GET /timeline/{position}` calls.
  - Policy select: only `policy-v8` is accepted (`req.Policy != PolicyCatalog.V8Id → 400 invalid_replay_request`). `v9` and the label "Enhanced detection" are HTML. The existing label is "V8 rules + pinned R5".
  - "Chain head 9f3c…a71e": real values are `audit.recordHash`, `ledgerHeadHash`, `preview.stateHash` (full hex strings).
- **Reusable:** adapter in `Rewind` (`seek`, `getMarkers`, `getLabel`), `eligible` logic, `op.run` for close and replay, `isTimelinePosition`.
- **Remaining work:** the dock layout around the *unchanged* `TimelineScrubber`; a source chart from `Liquidity.actual` (decide on per-bank or a clearly labelled sum, see §6); read-only badge from `snapshot.status` and audit hashes; fork marker; reasons for disabled controls.

### State 6 — Rewind, fork selected / awaiting replay

- **Reference:** `rewind-fork.png`. **HTML:** same function with `S.hasFork`, `forkPos`.
- **Existing React:** the same `Rewind` panel when a seal position is chosen: `eligible` enables "Run sandbox replay". **EV**.
- **Preserve:** heading "A decision point at 09:00:00. A different rule is ready.", "v7 → v8" pill, "FORK FIXED · AWAITING REPLAY", vertical fork line with label, active "Execute Replay" button, "Fork 09:00:00" button on the dock.
- **Real behaviour:** nothing is created when a fork is chosen; the preview is a read-only `GET`. Replay is created by `POST /counterfactual` with `{forkPosition, horizonMs: snapshot.boundaryMs, policy:"policy-v8", interventions}`; response `R8Job {jobId,status,failure,verified}`; statuses seen in code: `Pending`, `Running`, `Verified`, `Failed`, `Cancelled`.
- **Conflicts:** HTML offers one hidden mode; real offers *Policy only* vs *Policy + sandbox intervention* (payment, `Approve|Reject`, `expectedVersion`; needs fresh MFA when interventions are present; at most 20; `atMs` before the horizon). The existing selects must stay visible or be moved into an "Intervention" drawer. The HTML run simulation (random progress bar) has no backend equivalent: real progress is state only, so show a status stepper (Pending → Running → Verified) with a real **Cancel** (`POST …/cancel`, allowed only for the submitting actor).
- **Reusable:** all existing handlers; `job` polling; `chooseJob` URL persistence.
- **Remaining work:** fork-line overlay on the chart at the selected marker; "awaiting replay" banner derived from `eligible && !job`; pending/failed UI; no KPI numbers until `Verified`.

### State 7 — Rewind, completed source/child comparison

- **Reference:** `rewind-comparison.png`. **HTML:** `mode==='result'`; `quad('result')`; `historyChart('compare')`.
- **Existing React:** `Comparison`. **EV**.
- **Preserve:** "Child s-0007 · v8" chip, two-curve chart (original teal, alternate orange) with hatched divergence region and end-value callouts, four stacked KPI tiles on the right (hero dark, three tonal), dock now showing the child with orange progress on the groove after the fork, "✓ Replay fixture complete" badge.
- **Existing data:** `R8Job.verified: R8Verified` — `headlines[4]`, `breakdown[6]`, `payments[]`, `balances[]` (per bank, per boundary, source vs alternative posted/reserved/available), `actions[]`, `childTimelineId`, source/child record and state hashes, `evidenceDigest`, `costPolicy`, `continuationPolicy`, `mode`. **EB.**
- **KPI mapping (labels from `Describe`):** `Fraud prevented` ← "Terminal fraud intercepted"; `Legitimate payments delayed` ← "Legitimate payments delayed"; `False-positive cost` ← "False-positive cost"; `Net financial effect` ← "Net financial effect". Order is backend order; HTML places Net first, which is a presentation reordering only.
- **Conflicts:**
  - The alternate curve is an **executed child history**, not a prediction. Keep the text "Archived source and executed child … curve endpoint difference ≠ net economic effect"; that sentence is accurate and should be kept.
  - HTML KPI equation "$2.46M − $0.18M − $0.36M" does not match real data. Prior e2e evidence ([Prior]) shows deltas `150000, 1, 250, 149600`: net `149600 ≠ 150000 − 250`, so a three-term subtraction cannot be asserted. Show the six real `breakdown` rows instead.
  - Magnitudes: real deltas are dollars to thousands (`+$1,496.00`), not "+1.92 M USD". Tiles must format with `money()` (exported from `RevenantR8.tsx`).
  - "Fraud prevented" is computed against the seeded synthetic truth labels (P-001 and P-003 are labelled malicious in `Create`). It is valid inside the verified comparison; do not show a "prevented fraud" figure for any unresolved hold (workspace finance rule).
  - "Analyst reviews included · review handling impact $0.36M": no such figure; the intervention mode lists `actions[]` (reviewer, time, action, disposition). Show those.
  - Network-wide "net position $24.1M vs $30.3M": **no such series.** Only per-bank balances exist. Options in §6.
  - The `balances[]` boundaries may not start at the fork; **U** whether pre-fork points are included. Verify against a live response before drawing the pre-fork shared segment.
  - Dock "12:00:00" scrubber after the run: the shared scrubber seeks the **source** timeline; the child timeline has its own ID but this UI has no child seek endpoint (`/timeline/{position}` is bound to the source run binding). Do not imply seeking inside the child.
- **Reusable:** `Comparison` data prep and `metric()` formatting; `data-testid="r8-kpi"` with `data-delta`; "Aligned balance data" disclosure; payment evidence table.
- **Remaining work:** KPI column, divergence drawing, child chip and badge from real IDs and hashes, error and failure states, keep only-on-`Verified` rule.

### State 8 — Audit

- **Reference:** `audit.png`. **HTML:** `auditRows()`, toolbar, "Chain integrity" dark card, "Source vs child · provenance" card.
- **Existing React:** `Audit`. **EV**.
- **Preserve:** toolbar (search, event type select, source select, time window, Export CSV), wide event table with ORIGINAL/CHILD chips and orange row tint for child rows, right dark integrity card, provenance card with two coloured rules (original vs child).
- **Existing data:** `GET /audit` → `R8Audit { timelineId, status, stateHash, recordHash, manifestHash, policy, model, lastPosition, ledgerVerification, ledgerHeadHash, recentEvents[≤30] }`; each event `{sequence, event, actor, subject, subjectVersion, evidenceSha256}`. Each load re-verifies the source timeline and the SQL audit chain. **EB.**
- **Conflicts:**
  - There is **no timestamp** on audit events; the HTML "TIMESTAMP · UTC MS" column cannot be filled truthfully. Omit or show "—". The time-window filter has no data to act on.
  - Integrity is **per chain**, not per row. Show the chain status (`status`, `ledgerVerification` = `Verified|Unverified`) once. Per-row "PENDING/✓" cells are HTML.
  - Rows are source-only. Child events are not returned; the child appears only as the sandbox verification summary (hashes, evidence digest, `actions[]`) when a `Verified` job is in context (`job` URL param or state). Without it the "child" half of the provenance card must show "No verified sandbox in view" rather than a placeholder hash.
  - Event names in HTML ("Fraud confirmed", "Simulated hold", "Rule evaluation", "Risk engine") are invented; show the real `event` strings.
  - "Verification not yet run / Run demo check": the real verification runs on load. A "Verify again" button can only reload the endpoint.
  - Export CSV and search: possible client-side over loaded rows only.
  - Hashes: show real full values with copy/trim; the HTML `9f3c28b14c7aa71e` is fictional.
- **Reusable:** `Facts`, the "Original history unchanged" comparison (`recordHash` and `stateHash` equal to the job's source hashes), `Table`.
- **Remaining work:** layout and chips, filters, truthful empty/loading/failed states, integrity card driven by real fields.

### State 9 — Sign-In

- **Reference:** `sign-in.png`. **HTML:** `SIGN_IN_B64` document: split card (left dark teal with tagline "Decisions, with a record of what might have been.", right form with "Work email" and "Continue with SSO").
- **Existing React:** the `!token` branch in `RevenantR8` (title "The ledger remembers.", form with Username, Password, MFA code). **EV**.
- **Preserve:** the split-card composition, tagline treatment, mini timeline motif, synthetic-data chip and footer, the faded app chrome behind the card.
- **Real auth:** username + password + optional TOTP (`/auth/staff/login`); staff sessions via cookie refresh. **There is no SSO and no work-email login.** The email field and "Continue with SSO" are **HO**; replace with Username, Password, MFA code and "Sign in", using the same field labels the e2e uses.
- **Remaining:** the "MFA code" is optional in the form but required for approvals and interventions. Consider a short helper line; no enrollment flow exists in the UI (e2e enrolls via API), so a user without MFA will meet `mfa_required` at approval time. Surface that error clearly. The header on the sign-in screenshot shows full navigation; in the real app show nav inert/disabled or hide it before auth.

---

## Section 5 — API and data mapping

All routes are under `/revenant/r8` on the API host; staff authorization, tenant claim, role `operator|supervisor`, and for run routes a per-run read/write membership check. Writes need an `Idempotency-Key` header (1–80 chars `[A-Za-z0-9_.-]`).

| Frontend action / panel | Route and client call | Semantics and caveats |
|---|---|---|
| Sign in | `POST /auth/staff/login` `{userName,password,totpCode}`; `POST /auth/staff/refresh`; `POST /auth/staff/logout` | Access token held in memory. TOTP optional at login; fresh MFA claim needed for review and interventions. `/auth/staff/me` exists in the contract but is unused (candidate source for role display, **U** payload). |
| List runs | `GET /runs` → `R8Run[] {runId, seed, manifestHash}` | Only runs the actor was granted; max 100, newest first. |
| Fresh run | `POST /runs {seed}` → `R8Run` | Seed must round-trip as a decimal `long`. Deterministic id from tenant + actor + idempotency key. Takes tens of seconds (e2e waits 40 s). Seeds 4 payments of 150,000 minor units (P-001…P-004; P-001 and P-003 are truth-labelled malicious in `counterfactual_truth`; P-004 is created with a sanctions flag) over 6 banks, 2 accounts each. |
| Bank state, payments, cases, markers, anomalies | `GET /runs/{id}` → `R8Snapshot` | Polled every 2.5 s while open. Amounts are decimal **strings of minor units**; use `BigInt`/`money()`. Bank IDs `BANK-A…F`. `opening` = genesis balances. `boundaryMs` = last sealed boundary. |
| Case detail | `GET /runs/{id}/cases/{intent}` → `R8Case` | Unused by React; snapshot already carries all cases. |
| Approve / Reject | `POST /runs/{id}/cases/{intent}` `{action,expectedVersion}` → `R8Case` | Fresh MFA required. Version mismatch and other domain errors → 409 with `code`. `Released` triggers settlement. No reason field. No approver identities returned. |
| MFA and roles | Claims on the access token; `RevenantStaffAuthorizer.HasStaffRole`, `RevenantLedgerService.IsSupervisor/HasFreshMfa` | UI cannot read claims directly today. 403 `mfa_required` / `run_forbidden` are the only signals. |
| Liquidity observations | `GET /runs/{id}/liquidity` → `R8Liquidity` | `actual`: per seal per bank posted/reserved/available. `projection`: 8 horizons per bank, persistence baseline, `uncertainty:"Uncalibrated"`. **No confidence interval; no crisis forecast.** |
| Illustrative buffer | Client: `bufferReference(opening, bank)` | 10 % of opening available; policy `R8-SyntheticBufferReference/1`. Not a backend value; labelled illustrative. |
| Timeline positions / preview | `GET /runs/{id}/timeline/{position}` → `R8Preview`; `@sands/timeline-ui` adapter in `Rewind` | Position is a decimal string; invalid → 400; failures → 409 with engine code. Preview balances are for that position, with `stateHash`. |
| Close source | `POST /runs/{id}/close` → `R8Snapshot` | Seals to the horizon and completes. Required before replay is offered. |
| Replay submit | `POST /runs/{id}/counterfactual` `{forkPosition,horizonMs,policy:"policy-v8",interventions}` → `R8Job` | Policy other than `policy-v8` rejected. `forkPosition` ≠ 0. Horizon must be a boundary. ≤20 interventions; MFA if any. A failed validation can still return a job id that later becomes `Failed` (e2e shows `forkPosition:"289"` case). |
| Replay status | `GET …/counterfactual/{job}` → `R8Job` | Poll 1.2 s. `verified` populated only when `Verified`. |
| Cancel | `POST …/counterfactual/{job}/cancel` | Submitting actor only (`403 cancel_forbidden` otherwise). |
| Source vs child comparison | `R8Job.verified: R8Verified` | `headlines`, `breakdown`, `payments`, `balances`, `actions`, child/source hashes, `costPolicy`, `continuationPolicy`, `mode`. |
| Financial KPIs | `verified.headlines[4]` | Units: `"USD minor units"` or a count; deltas are `alternative − source`. |
| Audit and provenance | `GET /runs/{id}/audit` → `R8Audit` | Re-verifies on every call. Last 30 events, no timestamps. |
| Live refresh | Client polling only | No SignalR for R8. `Hosts.Realtime` exists but is not wired to these screens. |

**Values the mockups need that are unavailable or different:** network volume and routed transfer count; yesterday comparison; priority/severity; circular-route evidence; per-case velocity count; sender/beneficiary names and accounts; per-event timestamps; per-row integrity; child event rows; whole-network "net position" series; a policy v9; bank names; user initials; rejection-reason storage.

---

## Section 6 — High-priority mismatches

| # | Mismatch | Intended resolution |
|---|---|---|
| 1 | **Theme: workspace rule says the locked demo uses dark/obsidian by default and the paper design may inform an optional light mode. The brief and all nine screenshots specify the light teal design.** | Needs the owner's one-line confirmation before editing (see §9). Recommended: build the teal light theme as the primary look per this brief, put every color in CSS custom properties, and keep the current obsidian values as a second token set, so either default is a one-line change. |
| 2 | Fictional policy numbers. HTML offers v8 and v9 with a ×1.3 multiplier and a second metric set. | Offer only `policy-v8` ("V8 rules + pinned R5"; source `policy-v7`). Delete the v9 option and its numbers. Do not invent a policy. |
| 3 | Illustrative money. Millions, `$1.84M`, `+1.92M`, `$24.1M → $30.3M`, `173 transfers`, `92.4 %`. | Replace with `money()` over real minor-unit strings. Real magnitudes are about $1,500 per payment. Tiles must handle `+$1,496.00` and `$600.00` widths. No HTML figure appears anywhere in the live UI. |
| 4 | Approval request vs completed authorization. | Single endpoint, action `Approve|Reject`. Modal text and the primary label are computed from `approvalCount/requiredApprovals` and role: "Record first approval", "Complete authorization and release". Never say "does not release funds" unless this approval leaves the case below the required count. |
| 5 | Reject reason textarea has no persistence field. | Remove it, or keep it disabled with "Reason is not recorded". |
| 6 | Learned score vs fraud truth. "94/100", "Fraud confirmed", "Risk engine", "Fraud prevented" for holds. | Show `scorePpm` and `thresholdPpm` verbatim as "Recorded model score". Never write "fraud confirmed" or "prevented" outside the verified comparison; inside it, label the metric as derived from synthetic truth labels. |
| 7 | Persistence projection vs counterfactual. | Liquidity: dashed 8-point line titled "Statistical baseline (persistence), uncalibrated" with axis from `projection[].atMs`. Rewind: orange line titled "Executed sandbox child". Different colours, titles and footers; never reuse "forecast". |
| 8 | Live ledger vs sandbox child. | Case actions only on the live run (`403 sandbox_live_write_denied` backs this). The child is read-only, produced by a Replay job, and never feeds the Tower or Held screens. The read-only dock badge must say source unchanged only when audit hashes match the job. |
| 9 | Source verification and Replay statuses. HTML shows PENDING per row and a fake progress bar. | Use real fields: `snapshot.status`, `audit.status`, `audit.ledgerVerification`, `job.status`, `job.failure`. KPIs and child hashes render only when `job.status === "Verified"`. |
| 10 | Compact viewport. HTML is a 1440×900 artboard scaled by 0.853 at 1366×768 (verified), not a responsive design. Fixed heights (452, 615, 808, 128 px) would overflow 768. | Build a real app shell: `100dvh` grid with fixed masthead (64 px) and sub-bar, panels `min-height:0` with internal `overflow:auto` for queue, tables and audit. Avoid `transform:scale`, which shrinks type and fights Playwright coordinates. Dock height may shrink (128→96 px) at ≤800 px tall. Chart SVGs must use `preserveAspectRatio` default and be sized by container. |
| 11 | Authentication. HTML: work email + SSO. | Real: username, password, MFA code, same input labels (`Username`, `Password`, `MFA code`) and the button text `Sign in`. Remove "Continue with SSO", "Work email" and "Analyst role" claims unless the role is read from a real source. |
| 12 | Shared scrubber is inline-styled and used by Ghost Lap. | Do not edit `TimelineScrubber.tsx` for Revenant looks. Theme it by wrapping in `.r8-dock` and overriding via attribute selectors (`[role=slider]`, `[data-testid=timeline-scrubber]`, marker buttons) in `r8.css`. If a genuinely missing prop is needed, add it optionally and run the Ghost Lap tests. |
| 13 | Bank and payment identities. UI names (MRD…, `RV-HLD-2041`) vs real `BANK-A…F`, `P-001…`. | If aliases are used, keep a single display alias table (BANK-A→MRD …), always show the real ID in tooltips and the evidence panel, and never send aliases to the API. Decide the mapping once (the HTML itself spells Ostera/Oster and Tessell/Tessel inconsistently). |
| 14 | "Net position" chart has no backend series. | Preferred: per-bank chart (existing, verified data) with an "All banks (sum)" option that adds the six committed integer balances client-side and labels itself "Sum of committed balances". If the owner objects to any client-side summation, ship per-bank only. Never compute economic outcomes in the browser. |
| 15 | HTML-only controls. 1H/4H/DAY, play/1×/4×/16×, bell, pager, notifications, "Run demo check". | Drop or visibly disable. If playback is retained, it must call the same verified seek, throttled; mark as optional. |
| 16 | Audit has no timestamps or child rows. | Omit the timestamp column; filter by event/actor/subject only; label child half as unavailable unless a verified job is in view. |
| 17 | Mobile tests conflict with a laptop-only target. `revenant-r8.spec.ts` uses a 390×844 touch viewport; the liquidity spec asserts no horizontal overflow at 820 px and uses `.tap()`. | Re-baseline these two specs to 1440×900 / 1366×768 deliberately in M2 (do not delete assertions about data). Keep every data selector (§9). Note it in the PR. |
| 18 | Default entry. R8 only appears at `/?view=r8`; `/` shows the legacy guest slice. | Product decision for M2; the least disruptive option is to keep `?view=r8` working and add the default switch in one place (`main.tsx`). |
| 19 | Seeded scenario has fewer cases than the mockup's seven. | Design the empty and low-count states: expect 0–a few cases. The 7-row scroll is only demonstrable by seeding extra data, which is out of scope. |

---

## Section 7 — Reuse and preservation boundaries

**Preserve without redesign**

- Backend financial authority: `RevenantR8Api.cs`, ledger, replay worker, policy and model pins, idempotency, MFA and role checks.
- Contracts and client: `@sands/contracts` (`createApiClient`, generated `R8*` types, `readCsrfToken`, `describeFailure`, `isTimelinePosition`).
- Shared `TimelineScrubber` and its adapter contract (used by Ghost Lap).
- Authenticated operations: login/refresh/logout, create run, close, review, replay submit/cancel, polling and `useOperation` idempotency.
- Evidence and verification semantics: `Verified`-only KPI rendering, source-unchanged check, buffer disclosure text, advisory-only anomaly wording.
- BigInt-safe money and position handling (`money()`, `unwrap`).
- Hash routes, `run`/`job` query parameters, and the e2e selectors in §9.
- Legacy `slice/*` and the `?timeline=` inspector (not part of this work).

**Visually adapt**

- All of `r8.css`: tokens, layout, typography, panels, tables, tiles, chips, nav tabs, status treatments, modal composition.
- Chart drawing (`Chart`, `Tower` SVG) and the sign-in composition.
- Page structure: from stacked document scroll to a viewport-bound dashboard with panel-level scroll.
- Header chrome (brand, five tabs with held-count badge, synthetic chip, scenario clock, role chip, sign-out).

**Replace only when appropriate**

- Mock `DEMO` data, HTML placeholder handlers (`runScenario`, local status mutation, fake integrity, v9 factor), and showcase-only copy: not ported.
- Presentation code that cannot be styled into the design (single 380-line component file). Splitting into per-screen files is advisable; keep the hook helpers and exported `money` signature.

**Not allowed:** a second backend, mock services that stand in for real responses in the shipped UI, browser-side financial arithmetic that produces outcomes, or any new policy or API.

---

## Section 8 — Verification evidence

**Executed in this assignment**

| Command | Result |
|---|---|
| `git status --short --branch`, `git rev-parse HEAD`, `git branch -a -vv`, `git cat-file -t a1c7c24…`, `git merge-base --is-ancestor a1c7c24… HEAD`, `git branch -a --contains a1c7c24…`, `git log --oneline -8`, `git merge-base --is-ancestor main HEAD`, `git rev-list --count main..HEAD` | HEAD = R8 commit; ancestor yes; local + origin R8 branch only; `main` is an ancestor, 66 commits behind. |
| `npm run typecheck -w @sands/revenant` (in `web/`, Node v24.12.0) | Exit 0, no diagnostics. |
| `npx playwright test revenant-r8-liquidity.spec.ts` (in `tests/E2E`, default config, stubbed routes with `page.route`, starts both Vite dev servers) | **4 passed** in 9.2 s: buffer scale, exact decimal-string seek, shared buffer, and the tablet all-screens flow (including `r8-kpi` count and Net financial position `+$1,496.00` from fixture data). Proves the five screens mount and navigate against *stubbed* responses; does not prove backend behaviour. |
| Throwaway script `/tmp/m1/obs.mjs` (Playwright Chromium, fonts blocked) opening `file://…/design-inputs/skills1.html` at 1440×900 and 1366×768 and calling `navigate(1…8)` | All eight screens render; modal present on screen 3; scale `1` and `0.853333`; document height equals viewport (no page scroll) at both sizes. Temporary screenshots in `/tmp/m1`, not in the repo. |
| Decoding the two base64 documents with Python to `/tmp/m1/*.html` | Used for structure only. |

**Prior evidence, not rerun**

- User-reported R8 verification: real PostgreSQL tests, Chromium journeys, authorization, financial replay, green Linux CI on `a1c7c24`. The CI job "Playwright R8 with the real API and separate Replay Worker" exists in `.github/workflows/ci.yml`. The e2e expected KPI deltas (`150000, 1, 250, 149600`) are from that spec and are **[Prior]**.
- `TestResults/**` folders were not opened.

**Not run**

- Authenticated live API smoke and `revenant-r8.spec.ts` (needs the E2E backend and PostgreSQL; the Docker daemon is not running: `docker ps` failed to connect; `pg_isready` is not installed).
- Full CI, database suites, Marrow, ML, Ghost Lap, builds (`npm run build`), bundle check.
- Pixel comparison of React against screenshots (React was not restyled; no capture of the current React UI was taken).
- No browser run of the real R8 UI against a live backend, so **runtime behavior of Held approval, replay and audit against live data is unverified in this assignment.**

**Not changed:** no tracked file edited (post-check in the final response).

---

## Section 9 — Milestone 2 readiness verdict

### **READY**

The correct implementation is in this checkout, source and visual references are mapped, and no missing asset or interface prevents starting. Two items need a decision before or at the start of M2 (below); neither blocks the first step.

### Ordered frontend transformation sequence

1. **Protect inputs.** Commit `design-inputs/skills1.html` and `mockups1/` (owner action) and work on this branch or one cut from it. Do not start from `main`.
2. **Tokens and shell.** Add the teal token set to `r8.css` (CSS custom properties; keep obsidian values as an alternate set), load fonts (self-hosted preferred), build the viewport-bound grid: masthead 64 px, status sub-bar, content area with `min-height:0`. Verify at 1440×900 and 1366×768.
3. **Chrome.** Brand + hourglass icon, five tabs with held-count badge, synthetic chip, scenario clock from `boundaryMs`, role chip, sign-out. Keep `<nav aria-label="Revenant screens">` and the tab texts.
4. **Sign-In** (real fields, split-card design).
5. **Control Tower** (port SVG basins and channels as data-driven component, real KPI tiles, alerts, bottom strip).
6. **Held Queue** and **Approval modal** (computed copy, MFA error handling, no reason field).
7. **Liquidity Fan** (new chart, right cards, tiles, honest axis).
8. **Rewind**: dock around the unchanged scrubber, source chart from `/liquidity`, fork overlay, job states, then comparison with the four KPI tiles and divergence region.
9. **Audit** (table, integrity card, provenance, client-side filter/export).
10. **Tests:** re-baseline viewports, run the stubbed specs, then the real-backend R8 spec where Docker/PostgreSQL is available, then a manual pass against both screenshot sets.

### Files and components likely to change

- `web/apps/revenant/src/r8/r8.css` (rewrite), new sibling CSS if split.
- `web/apps/revenant/src/r8/RevenantR8.tsx` (split into per-screen files; keep exported `money`, hooks and API calls).
- `web/apps/revenant/src/r8/bufferReference.ts` (unchanged).
- `web/apps/revenant/index.html` (font links) and optionally `main.tsx` (default view).
- `tests/E2E/revenant-r8.spec.ts`, `tests/E2E/revenant-r8-liquidity.spec.ts`, `tests/E2E/playwright.r8.config.ts` (viewport only).
- Not to change: `web/packages/timeline-ui/**`, `web/packages/contracts/**`, anything under `src/**`.

### Selectors and behaviours to preserve

`data-testid`: `revenant-r8`, `r8-preview`, `r8-job-status`, `r8-kpi` (+ `data-delta`), `r8-buffer-line`, `timeline-scrubber`, `timeline-label`, `fork-reason`, `read-only-note`, `timeline-error`; attribute `data-reference-status`.
Roles and names: `navigation "Revenant screens"` with links containing `Control Tower`, `Held Queue`, `Liquidity Fan`, `Rewind Mode`, `Audit` and `aria-current="page"`; `region` named for each screen; `img "Committed payment flows among the six banks"`; `img /source and completed alternative balances/`; slider `Timeline position`; marker buttons `Marker: <hh:mm> UTC completed seal`; labels `Username`, `Password`, `MFA code` (existing test fills the first two), `Scenario`, `Seed`, `Bank`, `Balance`, `Comparison mode`, `Payment`, `Analyst action`, `Expected case version`, `Alternative policy`; buttons `Sign in`, `Launch fresh scenario`, `Close and verify source`, `Run sandbox replay`, `Cancel replay`, `Approve`, `Reject`, `Sign out`; text `Current balances`, `Recorded score:`, `statistical baseline, uncalibrated`, `Liquidity data table`, `Aligned balance data`, `Original history unchanged`, `Shared operating buffer reference: $…`, `Replay Verified|Pending|Failed`; table cells `Settled`, `Released`; column header `Available`. If a visible label is redesigned, keep it as `aria-label` or visually hidden text so these still resolve.
Note: the stubbed liquidity spec's own fixture uses labels "False-positive intervention cost" and "Net financial position", whereas the backend emits "False-positive cost" and "Net financial effect"; that is a fixture mismatch, not a UI contract. Do not copy either set into the UI without checking `Describe`.

### Most significant visual-fidelity risks

1. Font metrics: the design is Plus Jakarta Sans with tight tracking; without the font the headings reflow.
2. Fixed-artboard assumptions (fixed pixel heights, `preserveAspectRatio="none"` charts) will not survive at 1366×768 without deliberate rework.
3. The Tower map is a bespoke illustration inside an iframe; reproducing basins, channels and the callout without the iframe's absolute coordinates needs care and a data-driven fill rule.
4. Real data is sparse and small (4 payments, thousands of dollars): tiles, charts and lists will look emptier than the mockups. Empty-state composition is part of the design work.
5. The shared scrubber is inline-styled; the dock look must come from outside styling only.
6. Contrast of orange (`#FF6B4A`) text on light surfaces is borderline; use the text variants (`#CC5037`, `#C7543D`) for copy.

### Unresolved questions to settle before editing

1. **Default theme** (rule vs brief, §6 item 1): proceed with light teal as primary and obsidian as alternate token set, unless the owner says otherwise.
2. **Bank and payment display aliases** (§6 item 13) and whether an "All banks (sum)" series is acceptable (§6 item 14).
3. **Does `balances[]` in a Verified job start at the fork or at the origin?** Inspect one live `R8Verified` response before drawing the shared pre-fork segment.
4. Full enum of `CaseStatus` values and `ObservedFactKind` marker kinds, to finalize status chips and marker glyphs.
5. Whether the case-level history can be filled from `audit.recentEvents` by subject; if not, drop the "Payment history" card or reduce it to what `payments[]` and `markers[]` provide.
6. Whether `/auth/staff/me` returns role and MFA state (if so, it can drive the role chip and an up-front MFA hint).
7. Where fonts should live (self-host vs CDN) for an offline demo.
