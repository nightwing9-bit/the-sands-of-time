# Revenant Milestone 2 — Light-teal React implementation handoff

16 Nov 2026. Frontend only. No commit or push was made.

## 1. Git

- Branch `codex/r8-counterfactual-revenant-20261114`, HEAD `a1c7c2473ac05f15005c1f0cba477c90a2c96656` (unchanged; work is uncommitted).
- Pre-existing dirty files (spec-08 edits, deleted `design-inputs/skills1 (1).md`, untracked design inputs and recovery docs) were left as found. `design-inputs/skills1.html` and the nine PNGs were not modified.

## 2. Files

`web/apps/revenant/src/r8/`

| File | Role |
|---|---|
| `RevenantR8.tsx` | Shell: auth/session, run list, snapshot and job polling, hash routes, `run`/`job` URL params (plus new optional `fork`), masthead, status line, run/seed controls. Re-exports `money`. |
| `shared.tsx` | `unwrap`, `useLoad`, `useFinanceRefresh`, `useOperation` (moved unchanged; `run` now returns success), `useSize`, `Table`, `Facts`, `Drawer`, icons, hourglass, `ScreenBoundary`. |
| `format.ts` | Minor-unit money, compact labels, UTC clocks, the BANK-A…F display-alias table. |
| `charts.tsx` | One hand-written SVG time chart drawn at 1:1 pixels (no `preserveAspectRatio="none"`): area, fork line, now line, marker lane, divergence wedge, endpoint callouts, illustrative buffer, `BufferNote`, `placementOf`. |
| `SignIn.tsx`, `Tower.tsx`, `Held.tsx` (queue + decision dialog), `Liquidity.tsx`, `Rewind.tsx` (original, fork, running, comparison, dock), `Audit.tsx` | The nine states. |
| `r8.css` | Full rewrite: tokens from the HTML, viewport-bound grid shell, all screen styles, dock styling of the shared scrubber, compact-laptop rules, reduced motion. |
| `fonts/` | Self-hosted Latin subsets of Plus Jakarta Sans and JetBrains Mono (woff2, from Google Fonts) with their SIL OFL texts. |
| `bufferReference.ts` | Unchanged. |

Tests: `tests/E2E/revenant-r8-m2.spec.ts` (new), `revenant-r8-liquidity.spec.ts` and `playwright.r8.config.ts` / `revenant-r8.spec.ts` (viewport re-baseline only).

Not touched: `src/**`, `web/packages/**` (including `timeline-ui` and `contracts`), `web/apps/ghost-lap/**`, `main.tsx`, `index.html`, Ghost Lap tests, `.kiro/specs/**`.

## 3. Nine states

All nine exist in React and were observed at 1440 × 900 with stubbed responses:

1. Control Tower: four tiles (settled principal, reserved funds, open review cases, accepted payments), six-basin reservoir ported from the active HTML SVG, held-payment callout, Priority Alerts (open cases plus advisory anomalies or the `InsufficientHistory` state), current balances, selected-bank available trend with buffer and gauge, recent activity.
2. Held Queue: search, status/bank filters, sort, selectable rows, dark hero, bank-route path card, recorded facts, decision history, risk card (ppm against pinned threshold on a 0–1,000,000 scale), evidence digests, action bar.
3. Approval dialog: `role=dialog`, focus trap, Escape, blurred background, close, Approve and Reject. The consequence text is derived from `approvalCount`, `requiredApprovals` and status: an approval below the required count is described as an additional authorization with the payment still held; the final approval is described as release with a settlement attempt; rejection is described as terminal. No note field. Errors for MFA, stale version, distinct supervisor, role and sandbox are explained, and retry reuses the idempotency key.
4. Liquidity Fan: chart with now marker, eight hollow persistence points from `projection[].atMs`, illustrative buffer line, actual-available callout, limitation card, six-bank position table, three Posted/Reserved/Available tiles, data-table drawer.
5. Rewind original: scenario bar, per-bank source chart built from `/liquidity` `actual` with a real marker lane, dark dock wrapping the unchanged `TimelineScrubber`, hourglass, source verification seal.
6. Rewind fork: the heading, fork line and diamond are drawn only for a real `ObservationSeal` before the horizon. Shows "FORK FIXED · AWAITING REPLAY", the `policy-v7 → policy-v8` chip, and the mode and intervention panel. A Pending/Running/Verified stepper and Cancel replay appear once a job exists.
7. Rewind comparison: a bank-specific source (teal) and verified child (coral) chart. It defaults to the first bank with a real difference. The divergence wedge starts at the fork, or at the first observed difference when the fork is unknown. Endpoint callouts come from the data. Four `r8-kpi` tiles from `verified.headlines`, with Net shown first, `data-delta` preserved, and exact `money()` values. Breakdown, aligned data and payment evidence sit in drawers.
8. Audit: search, event-type and actor filters, a CSV export of the loaded rows labelled as such, a sequence-ordered table with ORIGINAL chips and no timestamps, a dark chain-integrity card from the real fields where "Verify again" refetches, and source-versus-child provenance that reads "No verified sandbox in view" when no verified job is loaded.
9. Sign-In: split card with Username, Password, MFA code and Sign in. The chrome is inert and has no links before auth.

## 4. Layout checks

`revenant-r8-m2.spec.ts` asserts `scrollWidth ≤ innerWidth` and `scrollHeight ≤ innerHeight` on Sign-In, Tower, Held, the approval dialog, Liquidity, Audit and the verified comparison at **1440 × 900 and 1366 × 768**. It also checks that the dialog box and its primary button are inside the viewport, and that chart heights are above a minimum. The stubbed liquidity spec checks every screen at 1366 × 768. Lists and tables scroll internally. Screenshots of each state were reviewed manually. They were written to `/tmp/m2/shots`, outside the repo.

## 5. Commands and results

| Command (cwd) | Result |
|---|---|
| `npm run typecheck -w @sands/revenant` (`web/`, Node 24.12.0) | pass |
| `npm run build -w @sands/revenant` (`web/`) | pass; R8 chunk 88 kB JS / 48.6 kB CSS, fonts emitted as assets |
| `npx playwright test revenant-r8-m2.spec.ts revenant-r8-liquidity.spec.ts` (`tests/E2E`, stubbed routes) | **11 passed** |

Liquidity-spec changes: the fixed `y1="137"` assertion became `data-scale-bp="3750"`. That is the same 37.5 % position of the buffer in the data-only domain; the chart now draws at container pixel size, so a fixed y coordinate no longer exists. The tablet block became a 1366 × 768 laptop block and now also checks vertical overflow. All data assertions were kept.

Not run: `revenant-r8.spec.ts` (needs the E2E backend and PostgreSQL); its viewport was changed from a 390 × 844 phone to 1440 × 900 with touch. Ghost Lap, Marrow, ML and database suites were not run because no shared code changed.

## 6. Reused APIs and preserved behaviour

These calls are unchanged and use the same typed client: `/auth/staff/login|refresh|logout`, `GET/POST /revenant/r8/runs`, `GET /runs/{id}` (2.5 s visible-tab polling), `/liquidity`, `/timeline/{position}`, `/close`, `/cases/{intent}` (`{action, expectedVersion}` + idempotency key), `/counterfactual`, `/counterfactual/{job}` (1.2 s polling), `/counterfactual/{job}/cancel` and `/audit`.

One read is new: `GET /auth/staff/me`, used only to show the role chip. It already exists in the contract.

Also preserved:

- KPIs and child hashes render only when the job status is `Verified`.
- "Original history unchanged" appears only when the audit record and state hashes equal the job's source hashes.
- Positions stay decimal strings, and seeking goes through the shared scrubber.
- The buffer disclosure and `data-reference-status` are unchanged.
- All selectors in M1 §9 survive. Nav links still resolve to "Rewind Mode" through visually hidden text.
- The scenario clock shows `boundaryMs`, never wall time.
- `?view=r8` and the legacy default route are unchanged.

## 7. Visual differences from the references, and why

- Magnitudes are real: about $1,500 payments and $23–40K balances, not millions. Axis and basin labels use compact $K.
- The Tower callout describes a genuinely held payment, not a "circular route", because there is no route evidence. The coral path is that payment's real bank route. Water level = available ÷ largest posted, clamped to 6–94 %; this rule is stated in the SVG `<title>`.
- The Held Queue shows scores in ppm rather than /100. The path card has two bank nodes because account and beneficiary data are not exposed. The fact rows show "—" where no timestamp exists. Approver names are not returned by the API.
- The approval dialog's title and primary label change with the case state. "Request Second Approval" appears in no state, because there is no such operation.
- Liquidity drops the 1H/4H/DAY control and the +8h axis. The time axis spans the real observations plus 40 minutes.
- Rewind shows one bank, not a network "net position". Playback is the shared scrubber's real Play button, which steps through verified seeks; there are no 1×/4×/16× controls. "Fork from here" in the shared component stays visually hidden because the R8 adapter is read-only, and the fork is chosen from seal markers. Replay is submitted with "Run sandbox replay". The scenario-bar meta text truncates when the child chip is shown at 1366 px.
- Audit has no timestamp column, no per-row integrity cells and no child rows, because the API has none of these.
- Fonts: Latin subsets only. Characters outside Latin fall back to system fonts.
- Theme: the workspace `finance-integrity` steering rule still says dark/obsidian is the locked-demo default. This milestone follows the brief and makes light teal the default. No dark theme is shipped; the old obsidian tokens were removed. Updating the steering rule is an owner decision.

## 8. Not yet checked against the real backend

1. Run `playwright.r8.config.ts` (real API, PostgreSQL, replay worker) at 1440 × 900. It is the only proof of approve → release → settle, replay Verified/Failed and the audit match.
2. Confirm the `/auth/staff/me` payload for staff tokens. If it fails, the role chip is simply hidden.
3. Confirm whether `verified.balances` include pre-fork points. The chart draws only what is returned.
4. Confirm the real `R8Marker.kind` values beyond `ObservationSeal` and `Screening` (other kinds draw as dots).
5. Confirm that the error codes from the real server (`mfa_required`, stale version, distinct supervisor, `sandbox_live_write_denied`) produce the explained dialog messages. These were checked here only with a stubbed `mfa_required`.

## 9. Readiness

The work is ready for a full authenticated end-to-end integration pass. That pass has not been run, so the real-backend journey is unverified.
