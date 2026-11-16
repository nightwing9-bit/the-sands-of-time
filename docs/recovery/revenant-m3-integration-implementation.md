# Revenant M3 integration implementation handoff

This records the original implementation handoff. The subsequent Codex verification report records the hosted execution and the final test setup.

## 1. Milestone verdict
**PARTIAL — EXACT BLOCKER IDENTIFIED.** The error-mapping fix, the real approval-dialog journey, ledger reconciliation, audit-chain verdict and Replay/marker contract checks are implemented. None of the new browser tests has run against the real backend; they need the hosted CI run. Two-actor four-eyes proof (Work Package C) cannot be built on the existing fixture (section 9).

## 2. Starting baseline
Branch `codex/r8-counterfactual-revenant-20261114`, HEAD `e08ac9ba86dc8c11813d3a0a29edfb3d061bd3fa` (unchanged). Previous green run: https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43851411472 (job 133244619860). The preflight report `revenant-m3-ci-preflight.md` was present (untracked) and was read. No commit, push, stash or reset.

## 3. Changes implemented
| File | Change |
|---|---|
| `web/apps/revenant/src/r8/decisionErrors.ts` (new) | Pure `explainDecisionFailure`. Matches structured codes most specific first: `sandbox_live_write_denied`, `mfa_required`, `StaleVersion`, `DistinctSupervisorRequired`, generic permission/forbidden/denied, unknown. |
| `web/apps/revenant/src/r8/Held.tsx` | Removed the inline `explain`; the dialog uses the new function. The raw `message · code` stays in the `<small>` diagnostic line. No layout or CSS change. |
| `tests/E2E/revenant-r8-errors.spec.ts` (new) | Pure formatter test (presentation coverage, not backend proof). Picked up by `playwright.config.ts` (`*.spec.ts`), not by the R8 real config. |
| `tests/E2E/revenant-r8.spec.ts` | Existing journey kept. Added: stores the TOTP secret in memory only; horizon-seal seek proves fork stays disabled; marker kinds and fork-seal kind checks; `verified.balances` contract checks; Audit ledger verdict asserted from `GET /audit`. Added a second test, the real approval dialog journey. |

The sandbox bug: `sandbox_live_write_denied` is a 403 whose message contains "denied", so it matched the generic role branch. It now returns "Live payment decisions cannot be made on a sandbox branch. Return to the original authorized run." Only that exact code matches, so ordinary sandbox wording is not misclassified. Other messages: distinct supervisor → "A different authorized supervisor must complete the required approval."; generic → "The current staff authority does not permit this action."

No other frontend file was changed. `Rewind.tsx`, `Audit.tsx`, `Liquidity.tsx` and `RevenantR8.tsx` needed no correction (sections 6 and 7).

## 4. Real-journey coverage matrix
Legend: **CI** proven in run 43851411472; **Authored** written here, not yet executed; **Local** executed locally; **Pending** awaits hosted run; **Open** unresolved.

| Item | Status |
|---|---|
| Staff login, MFA enrol/confirm, session | CI. The new test also signs in a second time through the UI form with a real TOTP code (Authored, Pending). |
| UI approval modal (amount, banks, status/count/version, consequence wording, cancel, confirm, single POST) | Authored, Pending |
| Cancel does not mutate | Authored, Pending (no POST seen; case and balances equal before/after) |
| MFA on the UI decision | Authored, Pending. The session MFA state is server-side, so the same bearer used by the UI is valid after confirm (CI's direct API approval already relied on this). |
| Distinct actors / same-actor denial | **Open: blocked.** Section 9. |
| Final release and settlement | API path CI; UI path Authored, Pending |
| Ledger reconciliation (integers) | Authored, Pending |
| Liquidity tiles vs endpoint, 8 points, no confidence band | Authored, Pending |
| Source verification, Replay, four KPIs, invalid Replay | CI (kept intact) |
| `verified.balances` shape | Authored, Pending |
| Audit ledger-chain verdict | Authored, Pending |
| Role chip vs `/auth/staff/me` | Authored, Pending. Failed-role-read fallback is by code reading only (`me.value?.roles ?? []` gives no chip); no stub was used. |
| Real `sandbox_live_write_denied`, stale version, distinct-supervisor, missing-MFA responses | Open for real-backend proof. Formatter coverage only (Local). |

## 5. Financial assertions (BigInt minor units)
For the Open case the server returns, with `final = approvalCount + 1 >= requiredApprovals`:
- Before: for every bank `posted − reserved = available`.
- Cancel: case record and all balances deep-equal the before snapshot.
- Not final: case `AwaitingSecondApproval`, approvalCount +1, payment status and all balances unchanged.
- Final: case `Released`, payment `Settled`; source posted −principal; destination posted +principal; destination reserved unchanged, destination available +principal; source reserved change in `[−principal, 0]`; source available change = `−principal − Δreserved` (zero net when the full principal had been reserved while held). Banks not on the route are unchanged. The test deliberately does not require source available to fall by the principal again.
- After: `posted − reserved = available` for every bank.
- Liquidity tiles equal the endpoint's latest observation for the source bank (not the live snapshot, because observations land at completed seals).

## 6. Source/child semantics
Source reading confirms (and the test now asserts) that `verified.balances` rows are `{atMs, bankId, source, alternative}` with inherited rows up to the fork seal and diverging rows afterwards. The test checks: both sides carry the row's bank; `posted − reserved = available` per side; rows at or before the 08:55 seal (position 288) are identical; at least one later row differs. `Rewind.tsx` plots `source` as "Source history" and `alternative` as "Verified sandbox" directly, does not synthesise a prefix, and labels the curve "executed child"; no chart change was needed. Fork eligibility is `kind === "ObservationSeal" && atMs < boundaryMs`; the test seeks to the final event and asserts the replay button is disabled with the "not a completed observation seal" reason. Marker kinds must be in the six-kind enum. `@sands/timeline-ui` is untouched.

## 7. Error mapping
Real responses seen from source: 403 `mfa_required`, 403 `sandbox_live_write_denied` (R8 review route when the run has a fork binding), 409 `inapplicable-StaleVersion`, 409 `inapplicable-DistinctSupervisorRequired`. A genuine sandbox denial needs a forked run bound to a live review, which this fixture does not offer without a loosened rule; it is not forced. Formatter coverage covers all five outcomes plus the unknown-failure case (no claim of success).

## 8. Verification executed
| Command | Result |
|---|---|
| `npm run typecheck -w @sands/revenant` (web/) | passed |
| `npm run build -w @sands/revenant` (web/) | passed |
| `playwright test` for `revenant-r8-errors.spec.ts` with a throwaway no-server config (deleted) | 1 passed |
| `playwright test --config playwright.r8.config.ts --list` | discovers both R8 tests |
| `playwright test --config playwright.config.ts --list` | discovers the errors test |

The real-backend tests were not run. No Docker, PostgreSQL or .NET build was started. Test files were not type-checked beyond Playwright's transpile (tests/E2E has no `@types/node`).

## 9. Blocker: two-actor four-eyes (Work Package C)
- `R8Create` accepts only `Seed`. `Create` hard-codes all four intents at 150,000 minor units ($1,500). `ReviewPolicy.RequiredApprovals` requires two approvals for USD at or above 1,000,000 minor units ($10,000) and for non-USD payments. Every real R8 case therefore needs exactly one approval.
- Order-2 approval requires a supervisor distinct from the first approver (`ReviewRules.cs:189`). No such case can be produced.
- The E2E harness provisions only an operator (`r8.browser.operator`) and the R8 API has no run-sharing route that would give a second staff user legitimate run access.
The existing browser fixture cannot demonstrate the two-actor path. The real PostgreSQL case-workflow test provides lower-level proof.

## 10. Codex cloud verification handoff
- Files to commit: `web/apps/revenant/src/r8/decisionErrors.ts`, `web/apps/revenant/src/r8/Held.tsx`, `tests/E2E/revenant-r8.spec.ts`, `tests/E2E/revenant-r8-errors.spec.ts`, this report.
- Job: `e2e` "browser e2e (stubbed + real backend)", step "Playwright R8 with the real API and separate Replay Worker" (`playwright.r8.config.ts`, `testMatch: revenant-r8.spec.ts`, so both real tests are picked up). The pure errors test runs in the stubbed step.
- Success: both R8 tests pass; the stubbed suite passes including the errors test.
- Risks to check first: (1) the second UI sign-in waits for an unused TOTP window and could add up to 30 s, still inside the 120 s test timeout; (2) the audit `ledgerHeadHash` is asserted to change after a decision, an assumption from the preflight; (3) the audit row-count text and `.r8-fan-tile` selectors rely on current markup; (4) the source-available-change formula assumes the hold reserves part or all of the principal.
- Preserve the unrelated modified/untracked spec and recovery files.

## 11. Remaining for Milestone 4
Two-approval four-eyes proof (needs a fixture or contract decision, section 9); real-backend denial responses for stale version and missing MFA; Replay cancellation race; later-horizon (09:00) proof.
