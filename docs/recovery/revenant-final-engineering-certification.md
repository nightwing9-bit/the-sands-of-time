# Revenant final engineering certification

Milestone 4 · Stage C · 16 November 2026

## 1. Executive certification verdict

**REVENANT ENGINEERING COMPLETE — B3 CERTIFIED**

Milestones 1–4 are complete for the tested R8 branch. All nine B3 packages have executed GitHub Actions evidence. The review found no open material production security, Replay or financial-integrity defect in this scope. The final source has one production change area, Timeline marker presentation and hit targets, alongside its test coverage and the isolated four-eyes fixture.

## 2. Final source provenance

- Repository and branch are [nightwing9-bit/the-sands-of-time](https://github.com/nightwing9-bit/the-sands-of-time) and `codex/r8-counterfactual-revenant-20261114`. The tested implementation is `bb09c90ff8eddd3db492a227d0e0f4c6f7bb7cac`. At review, local HEAD, upstream, remote and [draft PR #8](https://github.com/nightwing9-bit/the-sands-of-time/pull/8) agreed.
- The final pull-request workflow succeeded on attempt 1, with eight successful jobs, zero failed or cancelled jobs and four opt-in jobs skipped. It covered .NET build and unit checks, Linux x64 ML.NET and published advisory proofs, PostgreSQL integration, web checks, contract freshness, browser E2E and script lint. The skipped jobs were R7 SSA evaluation, longitudinal pilot, model selection or independent evaluation, and qualification. [Final workflow 43885774349](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43885774349)
- The browser job passed 53 Chromium tests across 29 modeled, 17 real-backend, four Revenant slice and three real R8 tests. The PostgreSQL job passed 269 checks across 109 general integration, 61 Marrow, 98 Revenant and one upgraded-database R8 authorization check. The web job passed 17 shared Timeline unit tests. [Browser job](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43885774349/job/133363386113) · [PostgreSQL job](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43885774349/job/133363386694) · [Shared Timeline job](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43885774349/job/133363386628)
- CI used hosted Linux x64, .NET 10 and PostgreSQL 18.6 through Testcontainers. The Replay timing artifact records Ubuntu 24.04.5 LTS, X64, .NET 10.0.15 and a published Release one-shot Worker. The authenticated browser journey brings up the API test host and a separate Replay Worker. [PostgreSQL acceptance and timing artifact](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43885774349/artifacts/14234129077)

## 3. Project architecture

Marrow provides a deterministic event Timeline with completed observation seals, verified restore and branched histories. Revenant's V4 scheduler and Worker record intake, screening, reservations, review and settlement in the authoritative six-bank PostgreSQL ledger. Balances use integer minor units, with available equal to posted minus reserved. Staff authentication, run-scoped roles, fresh MFA and the four-eyes review rules govern financial actions.

Screening uses pinned ML.NET FastTree. Its ppm output is a risk score. Synthetic fraud labels describe fixture truth. SSA velocity detection supplies advisory signals. The eight-point liquidity persistence baseline repeats observed available liquidity at future points and provides a reference for the displayed series. The production UI identifies the score and liquidity baseline and shows the executed policy versions. The wording review found no material misrepresentation.

Rewind compares a verified policy-v7 source and a verified policy-v8 alternative. A separate Worker with restricted PostgreSQL authority builds the child and publishes backend-computed counterfactual KPIs. Audit shows history and ledger verification and links alternative evidence to its source. The authenticated React app exposes Control Tower, Held Queue, Liquidity Fan, Rewind and Audit.

## 4. B3 acceptance matrix

Every primary result below executed at `bb09c90ff8eddd3db492a227d0e0f4c6f7bb7cac` in workflow [43885774349](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43885774349). The matrix uses the exact test names below.

| Test | Exact name | Hosted evidence |
| --- | --- | --- |
| T1 | `staff reviews and verifies a replay through all five real Revenant screens on a laptop` | [Browser job 133363386113](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43885774349/job/133363386113) · [R8 JUnit and browser JSON](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43885774349/artifacts/14233593778) |
| T2 | `R8AcceptancePostgresTests.Equal_seed_R8_openings_match_and_one_review_only_changes_its_own_run` | [PostgreSQL job 133363386694](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43885774349/job/133363386694) · [Revenant TRX](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43885774349/artifacts/14234129071) |
| T3 | `R8AcceptancePostgresTests.Overlapping_R8_submissions_cancel_durably_and_recover_in_a_fresh_worker_process` | [PostgreSQL job 133363386694](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43885774349/job/133363386694) · [TRX](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43885774349/artifacts/14234129071) · [Lifecycle and timing JSON](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43885774349/artifacts/14234129077) |
| T4 | `V4ReplayPostgresTests.Replay_login_forks_only_its_tenant_and_publishes_verified_readback` | [PostgreSQL job 133363386694](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43885774349/job/133363386694) · [TRX](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43885774349/artifacts/14234129071) |
| T5 | `CaseWorkflowTests.A_high_value_hold_is_released_only_after_an_mfa_approval_and_then_a_distinct_mfa_supervisor_and_then_settles` | [PostgreSQL job 133363386694](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43885774349/job/133363386694) · [TRX](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43885774349/artifacts/14234129071) |
| T6 | `two authorized staff complete a high-value held payment through the real Revenant approval dialog` | [Browser job 133363386113](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43885774349/job/133363386113) · [R8 JUnit and four-eyes JSON](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43885774349/artifacts/14233593778) |

| Package and requirement | Test | Real PostgreSQL | Actual browser | Executed assertion and qualification | Result |
| --- | --- | --- | --- | --- | --- |
| B3-A · Complete six-bank financial journey | T1 | Yes | Chromium | Authenticated seed-42 creation, six banks, four instructions, held case, React review, settlement, integer reconciliation, Liquidity, closed verified source, verified child, four backend KPIs and unchanged source audit. Net change +$1,496 at 08:55. | **PASS** |
| B3-B · Equal-seed independence | T2 | Yes | Backend test | Two seed-42 runs have equivalent canonical economics across six bank positions, 12 account openings and four exogenous instructions. Physical account and payment IDs are disjoint. Scoped staff can act only on their authorized run. A settles while B retains its case, balances, ledger rows and audit. | **PASS** |
| B3-C · Exact 09:00 historical fork | T1 | Yes | Chromium, 1440 × 900 | A normal click selects the `2026-09-13T09:00:00.000Z` ObservationSeal at `302`. The later horizon at `304` disables fork. The policy-v8 child verifies with unchanged source hashes and four zero KPI deltas. | **PASS** |
| B3-D · Concurrent Replay | T3, T4 | Yes | Backend tests | Three overlapping submissions collapse to two permitted durable operation identities. Identical keys deduplicate, changed input under the same key is denied, and distinct verified children have unique contiguous committed suffixes. Source audit and live ledger rows remain identical. | **PASS** |
| B3-D2 · Cancellation | T3 | Yes | Backend test | Pending cancellation creates zero children. Running cancellation wins the gated publication race and remains Cancelled after Worker exit. The hosted Running case leaves one incomplete child artifact and publishes no Verified result. | **PASS** |
| B3-E1 · Interruption and recovery | T3, T4 | Yes | Backend tests | The test terminates its Worker before the first journal write, observes the durable Running job, and recovers it in a fresh published process. The separate committed-append lost-acknowledgment case recovers without a second append. Verified child and unchanged source are asserted. | **PASS** |
| B3-E2 · Replay performance | T3 | Yes | Backend test | Three jobs with 11 suffix events each reached Verified. Final-run observed Running-to-Verified times were 891–1,161 ms, median 1,148 ms. Stage A measured 782–973 ms, median 948 ms, on the same short test workload. See §8 for scope. | **PASS** |
| B3-F1 · Four-eyes backend enforcement | T5 | Yes | Backend test | The inclusive $10,000 USD threshold requires two approvals. The $15,000 exercise checks MFA denial, a first approval with the hold intact, same-actor and other-operator denial, fresh supervisor MFA, release, settlement and verified ledger evidence. | **PASS** |
| B3-F2 · Real four-eyes browser journey | T6 | Yes | Chromium, two contexts | The scheduler screens and holds $15,000. The run owner grants the supervisor access through the staff API. Both staff sign in with fresh MFA and use the React dialog. The case stays held at 1/2, the same actor receives HTTP 409, and the supervisor reaches 2/2. One settlement reconciles across six banks, with both actors attributed in Verified ledger audit. | **PASS** |

## 5. Financial demonstration evidence

The authenticated browser creates a seed-42 scenario with six banks and 12 account openings. Opening posted balances for BANK-A through BANK-F are 5,590,514, 5,977,575, 3,021,688, 4,092,434, 5,121,395 and 1,577,712 USD minor units, with zero opening reserves. Four $1,500 instructions route A→B, A→C, B→D and C→E. The first three settle. The browser reviews the held C→E payment P-004, verifies reservation release and settlement, reconciles all six banks and reads actual Liquidity observations and the eight persistence points.

The verified source and child fork at the 13 September 2026 08:55 UTC ObservationSeal, event `288`, before the payment instructions in event order. This comparison executes policy-v8 and a review intervention. The source record and state hashes match the original Audit readback after the alternative completes. [Browser acceptance JSON and JUnit](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43885774349/artifacts/14233593778)

| Backend KPI | Unit | Source | Alternative | Delta |
| --- | --- | ---: | ---: | ---: |
| Fraud prevented | USD minor units | 0 | 150,000 | +150,000 |
| Legitimate payments delayed | Payments | 0 | 1 | +1 |
| False-positive cost | USD minor units | 250 | 500 | +250 |
| Net financial effect | USD minor units | −150,250 | −650 | **+149,600 (+$1,496)** |

## 6. Exact 09:00 proof

In the same authenticated 1440 × 900 journey, a normal mouse click on the 09:10 horizon first returned HTTP 200 and Verified preview `304` while the Replay action stayed disabled. Clicking 09:00 returned HTTP 200 for the exact payment-day ObservationSeal `302` at `2026-09-13T09:00:00.000Z`. React displayed the server-selected preview. The request carried decimal-string position `302`, the 09:10 horizon and policy-v8 with no interventions. The child verified, the inherited prefix agreed and its subsequent balances reconciled. Full source Audit equality preserved the original record and state hashes. This later policy-only fork yielded four zero deltas. The earlier intervention at 08:55 yielded the +$1,496 result in §5. [Exact-09:00 JSON and JUnit](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43885774349/artifacts/14233593778)

## 7. Four-eyes proof

The test preparer submits a funded $15,000 BANK-A→BANK-B payment to the real V4 intake and screening scheduler. Policy-v7 returned `SanctionsHit`. The scheduler committed the hold, its exact 1,500,000-minor-unit reservation and a case with zero of two approvals. The authenticated operator granted the supervisor access to the same run through the normal run-staff route, which returned HTTP 204.

Separate Chromium contexts used real Identity accounts and fresh TOTP MFA. Cancel in the dialog sent zero review requests. The operator's first versioned approval returned HTTP 200 and left the payment held at 1/2 with unchanged balances. The repeated attempt returned HTTP 409 `inapplicable-DistinctSupervisorRequired` with no financial change. The supervisor's versioned approval returned HTTP 200, reached 2/2 and released the payment. The single settlement debited and credited exactly 1,500,000 minor units and cleared the reserve. All six banks reconciled. The ledger chain was Verified, with two review facts naming the actual distinct staff subjects and one settlement fact. The open run's Timeline remained Recording. [Four-eyes JSON and JUnit](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43885774349/artifacts/14233593778)

The preparer is a test-only hosted service. Its environment, database and publish boundaries are detailed in §10.

## 8. Replay safety and reliability

The hosted PostgreSQL lifecycle test overlaps submissions, verifies idempotency and distinct child identities, gates Running cancellation, kills and replaces the test-owned Worker, and compares source Audit and ledger rows after processing. The complementary V4 test interrupts after a committed append, then verifies cold recovery with a single committed suffix position. Tests verify that the restricted role cannot write the live ledger, cross tenant scope, alter source history or publish Verified without durable matching Timeline evidence. The authenticated browser also verifies that a Failed job publishes neither verified payload nor financial KPI tiles. [Lifecycle evidence](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43885774349/artifacts/14234129077) · [Revenant TRX](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43885774349/artifacts/14234129071) · [Browser evidence](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43885774349/artifacts/14233593778)

The three timed policy-v8 jobs in each run replayed 11 suffix events per job with a published one-shot Worker and polling of PostgreSQL-backed status. Values below are observed wall-clock milliseconds from Running to Verified, rounded to the nearest millisecond.

| Execution | Individual times | Range | Median |
| --- | --- | --- | --- |
| Stage A, `5900db2f72f3be633c02691f322ca1a023313c08` | 973, 948, 782 | 782–973 | 948 |
| Final implementation, `bb09c90ff8eddd3db492a227d0e0f4c6f7bb7cac` | 1,148, 1,161, 891 | 891–1,161 | 1,148 |

The measurements describe the hosted short-workload fixture and exclude queue, launch and browser time. No Replay performance SLO was documented. [Stage A timing artifact](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43876572065/artifacts/14230144002) · [Final timing artifact](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43885774349/artifacts/14234129077)

## 9. Frontend certification

The owner-approved Milestone 2 design has five functional sections and nine visual states. They are Sign-In, Control Tower, Held Queue, approval dialog, Liquidity Fan, Rewind original, Rewind fork, Rewind comparison and Audit. Milestone 3 connected those screens to real staff authentication and finance data. The final hosted journey verifies all five sections.

Rewind measures the dock track, separates crowded pointer controls and connects offset labels to the original track coordinates while retaining the dark dock. The shared component applies a CSS offset and sends the original decimal-string event position. The verified response drives the selected event and preview. CI passed `adjacent Rewind seals accept independent mouse clicks at 1440×900` and `adjacent Rewind seals accept independent mouse clicks at 1366×768` with modeled API responses, checking disjoint hit areas, both targets, eligibility changes and no document overflow. Real PostgreSQL-backed pointer proof ran at 1440 × 900. The shared unit test preserves exact positions above 2^53, and Ghost Lap keeps default placement and passed real-backend mount, seek, typed denial, fork and fork retry. [Milestone 2 handoff](revenant-m2-visual-implementation.md) · [Browser job](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43885774349/job/133363386113) · [Web job](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43885774349/job/133363386628)

## 10. Security and production isolation

- The 12-path diff from Stage A to the final implementation is confined to the React marker layout, shared offset and unit test, browser coverage and bounded test configuration, and the test-host four-eyes fixture. Production finance, authorization, authentication, RLS and Replay backend source are identical across those commits. [Source comparison](https://github.com/nightwing9-bit/the-sands-of-time/compare/5900db2f72f3be633c02691f322ca1a023313c08...bb09c90ff8eddd3db492a227d0e0f4c6f7bb7cac)
- `R8FixtureStartup` is registered only by the R8 E2E factory using test services and temporary Testcontainers PostgreSQL. It requires Testing in both the host and process environment. The preparer has no HTTP route. It provisions only test identities and uses real V4 intake, screening and synchronization with no prewritten review decisions. The normal API publish copies production source only and has no test project dependency. A targeted local Release publish confirmed that the test assemblies, dependency entries, fixture types and test route prefix were absent. The E2E executable returned exit code 2 under Production before database startup. [API Dockerfile](../../deploy/docker/Api.Dockerfile) · [Test startup](../../tests/E2E/backend/R8FixtureStartup.cs)
- The in-memory visual preview requires a separate loopback Vite development configuration, which refuses builds and preview-server mode. R8's preview token initialization is compile-time development-gated. The production R8 query selects the real UI and the URL can select resources, while the normal API still requires staff authentication and authorized run access. [Preview configuration](../../web/apps/revenant/vite.r8-preview.config.ts) · [R8 entry component](../../web/apps/revenant/src/r8/RevenantR8.tsx)
- Executed PostgreSQL evidence includes transaction-local RLS on a pooled connection, cross-run authorization, upgraded-database Replay authorization and denial of live finance writes with Replay credentials. Browser evidence exercises actual staff authentication, MFA, scoped role grants and approval decisions. [PostgreSQL job](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43885774349/job/133363386694) · [Browser job](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43885774349/job/133363386113)

## 11. Git and integration readiness

PR #8 was open, draft and reported `MERGEABLE` with a `CLEAN` merge state. Its base was the R7 branch `codex/r7-ssa-anomaly-liquidity-20261113` at `fe83c52ca7629d8ce7cb100b59cd8143c2d1ba64`, an ancestor of the implementation with 11 R8-side commits and zero base-only commits. The default branch `main` was also an ancestor at `2fde4a57f2f229743bc43d8a41e6bc989d433d09`, 72 implementation-side commits back, with zero main-only commits. Both remote refs matched the available local refs. GitHub reported the two target branches unprotected, with no applicable repository rulesets or R7 required status checks. Repository settings allowed merge commits, squash and rebase, with no single method enforced.

The tested branch is ready for owner review against R7. The owner should handle any later default-branch integration as an explicit stacked-branch decision. The draft PR and its base remain in place. Pre-existing local Kiro edits, a design-input deletion, recovery notes and generated evidence remain local and preserved. The certification report consolidates the durable handoff and links the hosted artifacts. [Draft PR #8](https://github.com/nightwing9-bit/the-sands-of-time/pull/8)

## 12. Known qualifications

- Modeled Chromium responses cover both laptop sizes. Real authenticated Timeline pointer and Replay proof covers 1440 × 900. The owner-approved baseline design predates the final marker spacing.
- Replay timings cover three short jobs per execution. The reported metric covers observed Running to Verified on the recorded CI runtime.
- A cancelled Running job can retain incomplete child history. Cancellation excludes publication of Verified financial results.
- GitHub Actions provides temporary verification infrastructure. A reliably launchable interactive backend is operational work. No open B3 defect was found.

## 13. Remaining operational work

**INTERACTIVE REVENANT BACKEND / LIVE HACKATHON DEMO READINESS**

Decide how to provide a persistent or reliably launchable backend, including API, PostgreSQL and Worker startup, judge/demo staff sign-in and MFA, deterministic data reset and rehearsal, and hosting resources and costs. Decide separately whether and how to integrate the draft branch. These items are outside B3 engineering acceptance.

## 14. Final sign-off

Revenant engineering is complete for Milestones 1–4. All nine B3 packages are certified on the tested implementation commit. **NONE REQUIRED FOR CURRENT B3 SCOPE** applies to additional Kiro implementation. The branch is ready for owner review. The next discussion is interactive backend and live hackathon demo readiness.
