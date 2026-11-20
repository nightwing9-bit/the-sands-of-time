# Revenant M3 hosted verification

## 1. Verdict

**GREEN — SUPPORTED R8 INTEGRATION VERIFIED.** Both authenticated R8 browser journeys passed on GitHub-hosted Linux against the real API, PostgreSQL, and a separate Replay Worker. The browser exercised one live authorization and settlement. PostgreSQL integration tests exercised four-eyes enforcement. The two-actor R8 browser demonstration remains blocked on fixture reachability.

## 2. Git state

- Branch `codex/r8-counterfactual-revenant-20261114`, [draft PR #8](https://github.com/nightwing9-bit/the-sands-of-time/pull/8).
- Starting local and remote SHA `e08ac9ba86dc8c11813d3a0a29edfb3d061bd3fa`.
- Implementation commit `ed6469da2e65f746133ab11bfb5f8add4725b0b2`, R8 authenticated approval and accounting.
- Final local, remote, and PR SHA `dc5fd6a5f974d063a113370ab807755f2a8255a5`, open Timeline versus ledger audit assertion.
- Both ordinary pushes were fast-forward. The existing Kiro specification edits, design-input deletion, untracked architecture and recovery notes, and untracked M3 preflight remained outside both commits. This verification report is local and uncommitted.

## 3. GitHub Actions

| PR synchronize workflow | Tested head | Browser result | Workflow result |
| --- | --- | --- | --- |
| [43858356803](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43858356803) · [browser job](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43858356803/job/133268366835) | `ed6469da2e65f746133ab11bfb5f8add4725b0b2` | 27 stubbed passed, 17 real-backend passed, 4 Revenant slice passed, R8 1 passed and 1 failed | The browser job failed. The corrective push cancelled the remaining integration job through PR concurrency. |
| [43859156963](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43859156963) · [browser job](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43859156963/job/133271192806) | `dc5fd6a5f974d063a113370ab807755f2a8255a5` | 27 stubbed passed, 17 real-backend passed, 4 Revenant slice passed, R8 **2 passed** | Success. All eight normal jobs passed. Four conditional R7 jobs were skipped. |

Two workflows ran, each on its first attempt. Both were triggered by pushes to the existing draft PR. The first failure was in “Playwright R8 with the real API and separate Replay Worker,” in “staff decides a held payment through the real approval dialog and the ledger reconciles.” The first causal error was an assertion on HTTP 200 from `GET /revenant/r8/runs/{id}/audit` before opening the dialog. It expected `status: Verified` and got `Recording`. The Timeline was still open. The [browser evidence artifact](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43858356803/artifacts/14223005264) contains the Playwright error context. The corrective commit checks `Recording` on the open run and separately requires `ledgerVerification: Verified`. The closed Replay journey still requires both verdicts to be `Verified`.

## 4. Code and test review

| File | Scoped change |
| --- | --- |
| `web/apps/revenant/src/r8/Held.tsx` and `decisionErrors.ts` | Preserve the dialog markup and surface a specific explanation for sandbox denial, fresh MFA, stale case version, distinct approver, and generic authority errors. The real `run_forbidden` code is recognized. The server diagnostic remains visible. |
| `tests/E2E/revenant-r8-errors.spec.ts` | Pure formatter coverage executed by the normal stubbed CI step. |
| `tests/E2E/revenant-r8.spec.ts` | Retain the original Replay journey and verify the real approval dialog, cancellation, request, refreshed state, isolated settlement, Liquidity and Audit. Check marker eligibility, aligned balances, source hash equality and child provenance. The second commit adjusts the open Timeline status assertion and checks both displayed verdicts independently. |
| `tests/E2E/backend/E2eBackendEntry.cs` | Each journey has a dedicated operator in the existing hosted fixture tenant and launches its own run. |
| `tests/E2E/playwright.r8.config.ts` | Extend the test-only host bound from 180 to 360 seconds. Keep one worker, 120 seconds per test and the existing eight-minute CI step limit. |
| `docs/recovery/revenant-m3-integration-implementation.md` | Preserve the implementation handoff and identify it as the pre-verification record. |

Local Node v24.12.0 checks passed for Revenant typecheck, production build, and the formatter test under the actual stubbed configuration. Real R8 discovery listed both tests. CI's 1440×900 and 1366×768 workspace-fit tests passed. The backend finance engine, production authorization policy, and approved frontend styling were outside the change.

## 5. Financial proof

The browser selected `P-004`, displayed its amount, route, approval count, case version and settlement consequence, then cancelled with zero review POSTs, unchanged case state and zero audit review facts. Confirming sent exactly one review POST with `action: Approve`, `expectedVersion: 1` and a nonempty `Idempotency-Key`. The case refreshed to `Released`, version 2, with 1 of 1 approvals and the approval control disabled. The payment became `Settled`. The observed transition added one review fact and one settlement fact, with the other payment records unchanged.

All amounts are exact USD minor units. The test asserted `posted − reserved = available` for all six banks on both sides of settlement and at the matched liquidity boundary.

| Bank and amount | Before | After | Delta |
| --- | ---: | ---: | ---: |
| BANK-C source posted | 3171688 | 3021688 | −150000 |
| BANK-C source reserved | 150000 | 0 | −150000 |
| BANK-C source available | 3021688 | 3021688 | 0 |
| BANK-E destination posted | 5121395 | 5271395 | +150000 |
| BANK-E destination reserved | 0 | 0 | 0 |
| BANK-E destination available | 5121395 | 5271395 | +150000 |

Liquidity rendered BANK-C’s posted/reserved/available values `3021688 / 0 / 3021688` from the completed observation at `1789289700000` (08:55 UTC in the scenario). The selected bank, timestamp, eight five-minute persistence points, `LiquidityPersistenceBaseline/1`, uncalibrated status and illustrative 10% opening-available buffer disclosure were checked. The browser audit returned a verified ledger head of `8d639add925171e983bf8d2d09ac5e56290a4e049ab8b15f4ef4682e807132b9`. The test checked the head change against the actual appended review and settlement events. [Final browser job](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43859156963/job/133271192806)

## 6. Authentication and MFA

The Replay operator uses password login, confirms enrollment, and steps up the calling session. The approval operator enrolls and confirms in a separate browser context, then signs in on a fresh session with a distinct, unused TOTP code. The backend persists enrollment and rejects reuse of the last accepted code during its replay window. The harness waits only for the next available code, with a bound of two 30-second windows plus one second to account for a possible repeated numeric code. The approval test verified the server's fresh `mfa` claim and matched the browser's operator chip and subject to `/auth/staff/me`. Each journey can start independently on a fresh hosted fixture.

## 7. Replay and audit

The completed 08:55 UTC `ObservationSeal` at event 288 drove the real policy-v8 Replay Worker. Seeking to the horizon disabled the fork action, and a request at invalid position 289 produced a failed job with no verified result or KPIs. The chart rendered the actual aligned source and child balances. The test checked bank mapping, exact accounting on both sides, equality through the fork seal and divergence after it. Backend KPI deltas were `150000`, `1`, `250`, and `149600` in the API's units. The net financial effect was +$1,496.

| Evidence | Final run |
| --- | --- |
| Source Timeline | `01a1e427-b47a-79ae-a533-9a0f38c8e985` |
| Source record hash | `481b478b195956935a40727ba63bf6bf70ec4b86ac7d170caf3964178b2ab435` |
| Source state hash | `9148dbb5f9e19414a99e259bb7a9ac8cd8c67f7a1d93d87567483a825619b4c5` |
| Source ledger audit-chain head | `ec2d2dce492c85da33c0528cf9aac5e5604d7006751a74108803b1fa01e127d8` |
| Child Timeline | `01a1e427-db5d-786b-b486-cd9c81eb029b` |
| Child record hash | `8fe92cc3aed52cb469fc8873a484a74a34c4d72de091ab54692f256703abba82` |
| Child state hash | `94cb9b5a0069e9785c263eead7eee0a71c9734645e14e3f1537fb4cc5852ba28` |
| Replay evidence digest | `be4fdb1f36b0552bc660aab60b4c6e42464d70aae2f7d55997cb0b2622f6838d` |

The browser verified both audit verdicts and displayed the child's separate identities. Audit record and state hashes matched the pre-Replay archive and the worker result's source hashes. The source ledger head also matched before and after Replay. [Final browser job](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43859156963/job/133271192806)

## 8. Four-eyes and coverage matrix

`ReviewPolicy.RequiredApprovals` requires two approvals for USD amounts at or above 1,000,000 minor units ($10,000), and for non-USD payments. Approval two requires a fresh-MFA supervisor distinct from the earlier approver. `R8Create` accepts a seed and the four R8 intents are all 150,000 USD minor units ($1,500). CI confirmed that the fixture's R8 review cases each require one approval. The two browser fixture identities are operators dedicated to separate runs. The public R8 API has no supported run-membership grant or high-value seed input.

The PostgreSQL case workflow passed with a 1,500,000-minor-unit hold. It verifies that the first authorization keeps the payment held, the original operator and a different operator are denied approval two, a stale version and missing MFA are rejected, and a distinct fresh-MFA supervisor releases the case before settlement. The executed V4 financial-fork test covers a case at exactly 1,000,000 minor units, a same-actor denial, and successful settlement after authorization by a distinct supervisor. Both tests also passed in the prior baseline. [Final integration job](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43859156963/job/133271192842) · [baseline integration job](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43851411472/job/133244619835)

| Area | Evidence item | Status |
| --- | --- | --- |
| Authentication | Genuine browser login, fresh MFA, role chip from staff identity, and authorized review | PROVEN IN THIS CI |
| Held Queue | Real case, dialog details, Cancel with no request or review fact, actual POST, case version and idempotency header, refreshed state | PROVEN IN THIS CI |
| Ledger | All-bank integer invariant, full reservation release, settlement, both posted deltas, available balances | PROVEN IN THIS CI |
| Ledger | A single committed settlement in the observed approval journey | PROVEN IN THIS CI |
| Liquidity | Selected bank, completed observation timestamp, posted/reserved/available, eight uncalibrated persistence points and illustrative buffer disclosure | PROVEN IN THIS CI |
| Rewind | Source closure, completed-seal fork, horizon disabled, invalid fork failed, genuine policy-v8 Worker, verified histories, chart and four backend KPIs | PROVEN IN THIS CI |
| Rewind | Failed Replay publishes no verified result or KPIs | PROVEN IN THIS CI |
| Audit | Timeline and ledger verdicts, source record/state, real child provenance, original hashes preserved | PROVEN IN THIS CI |
| Errors | Sandbox, fresh MFA, stale version and distinct approver explanations in the formatter | PROVEN IN THIS CI |
| Errors | Fresh-MFA refusal and stale version conflict enforced in real PostgreSQL service tests | PROVEN IN THIS CI |
| Errors | Sandbox live-write denial and other live rendered denial responses in the R8 browser | NOT COVERED |
| Four-eyes | Non-final approval preserves hold, distinct supervisor enforcement and final settlement in PostgreSQL | PROVEN IN THIS CI |
| Four-eyes | Two live browser approvals on the same high-value R8 case | BLOCKED — the supported R8 browser fixture cannot reach it |
| Replay isolation | PostgreSQL Replay credential isolation and interrupted append recovery on worker restart | PROVEN IN THIS CI |
| Replay cancellation | Explicit browser cancellation and deterministic cancellation of concurrent Replay jobs | NOT COVERED |

## 9. Remaining B3 obligations

- Demonstrate two distinct authorized staff approvals on one supported high-value case through the browser.
- Demonstrate equal-seed financial independence across R8 runs. The browser checked distinct run IDs.
- Execute the explicit 09:00 historical fork in the R8 browser. The V4 PostgreSQL financial-fork integration executed a 09:00 seal and the R8 browser executed the 08:55 seal.
- Exercise concurrent Replay jobs with deterministic cancellation.
- Collect end-to-end Replay interruption/recovery and performance evidence for the R8 workflow. PostgreSQL integration already proved recovery after an interrupted append and rejected a forged Verified result.
- Consolidate six-bank final acceptance evidence.

## 10. Handoff

B3 can begin without further M3 implementation. Use the final tested SHA and keep the browser-level four-eyes obligation open for B3 acceptance.
