# Marrow × Revenant R8 implementation handoff

**R8 release candidate — local verification and stacked review**

| Gate | Status | Decisive proof |
|---|---|---|
| 0 — Contracts and compatibility | PASS | Exact R7 base confirmed. Both policy configurations are already accepted by the V4 model evidence and context contracts. |
| 1 — Policy and lawful terminal outcomes | PASS | Five intents exercised the published R5 scorer and real V4 reducer. The fixture proved terminal rejection with a fresh MFA analyst, legitimate delay, unresolved malicious exposure and a separate funding failure. |
| 2 — Pure counterfactual execution | PASS | Exact inherited state, identical source bytes, repeatable suffix, causal feature recomputation, conservation and rejection of a corrupt manifest or invalid fork. |
| 3 — Financial economics | PASS | Per-intent reconciliation, equal principals with distinct IDs, pending versus terminal, recovery, shared holds, wrongful rejection, unknown truth, zero and negative net, and checked overflow. |
| 4 — Isolated Worker and PostgreSQL | PASS | PostgreSQL 18.6 applied Replay job migration 0016. A restricted credential created and verified a child with divergent finance, preserved the source hashes, denied direct live and source writes, blocked a result without the matching child, and enforced tenant scope. |
| 5 — Authenticated API | PASS | Actual staff login and MFA, denied unauthorized and other-tenant access, duplicate and conflicting operations, stale review, distinct same-seed runs, replay readback, signed economics and cancellation. |
| 6 — Five connected screens | PASS | All five views passed the retained real Chromium phone journey. New focused browser tests cover the Liquidity Fan marker, a shared opening reference for the comparison, and the corrected decimal-string adapter. |
| 7 — Complete bounded journey | PASS locally | The real phone fixture passed again with the decimal-string position contract, automatic live financial refresh, and the separate Replay process. |

## Compatibility and authority

- Both branches use Marrow Timeline API v1 and the same registered Revenant finance family. The pinned components are reducer `revenant-finance-reducer/4`, input `revenant-finance-fact/4`, state `revenant-finance-state/4` and context `revenant-finance-context/4`. `ObservedFinanceReducer` applies V4 Screening and delegates review, settlement and return to the existing ledger reducer. `V4ObservationProjector` supplies branch-causal features and sealed curves. `V4BranchExecution` supplies live-root binding and closure. Sandbox persistence calls the shared Marrow fork, append, restore, complete and replay operations directly.
- The original base policy is `policy-v7`, configuration digest `sha256:687078f26fefcf081ee0f39e5ff185385c11130b52198e74c3861f7347b5c023`. It applies the velocity rule at 8 intents and USD 1,000.00. The alternative is `policy-v8`, digest `sha256:b702047a23f2e5e02efc44ef1354aec91e913159f35b69f65b112612f1cbafbd`. It applies the velocity rule at 5 intents with zero amount floor and adds a USD 1,500.00 new-beneficiary rule. Both retain the sanctions rule, review policy and `v4-model-review-capacity/1`. The versioned base rules operate inside V4 Screening. The V1 reducer, adapter and metrics remain separate compatibility surfaces.
- R5 uses `revenant-behavioral-risk-v2/d1801a3f72f9d4525cd39b8b7873e2fc43288dea0f7c1f5ee8646612833a28e4`, ZIP SHA-256 `56bca32c36dd1911d163a6f92369da43382e1937d875228339f15f8608519c7c`, inclusive 852469 ppm, `risk-features-v2` and `v4-model-screening-evidence/1`. R7 retains the advisory-only `revenant-ssa-anomaly/672a05a26995653585e5ba9505807df7a2f707914886634c39b133c7dbc72980` pin and `R7-AnomalyGlobalPolicy/1`, schema `anomaly-evidence/2`. Its ZIP SHA-256 was rechecked as `03e895da4fa4348b69b1251c6ef9c1c9e6c2deee893e83bf2a983c698281b35e`. `LiquidityPersistenceBaseline/1` uses `liquidity-projection-evidence/1` and eight constant points at +5 through +40 simulated minutes. The UI identifies it as an uncalibrated statistical baseline.
- `analyst-continuation-v1` uses the committed action and its journal receipt. Replay preserves the original effective order, checks identity and the expected case or payment version, rechecks MFA and staff authority, and uses `CaseMachine` for open status and distinct-supervisor approval. Inapplicable actions have explicit receipts. Unreconstructible authority or action provenance fails closed. Explicit sandbox actions are attributed separately and enter the branch after recorded actions at the same instant. Policy-only runs leave newly held cases pending unless an eligible recorded action applies.
- LiveOps keeps the ledger writer and existing Timeline pool. `Hosts.Worker --replay [job-uuid]` uses `sands_replay` with `marrow_timeline_revenant_replay`, `revenant_replay_reader` and `revenant_counterfactual_worker`. Database policies bind the login to one tenant, admit sandbox child writes and deny changes to source history and live finance. Job publication checks the verified parent and child hashes, fork and horizon. Operation keys deduplicate submissions. A process can resume a Running job and reuse the Marrow fork and append keys. The focused fixture exercised duplicate requests and terminal retry. It did not kill the process during an append. Cancelled jobs do not publish results. The result, including points and actions, is stored atomically in `revenant.counterfactual_run` JSONB.

## Executed economic results

The browser used **policy plus sandbox intervention**. All values are synthetic and derived from executed finance. USD is represented internally as checked integer cents, and HTTP money values are decimal strings.

| Headline | Source | Alternative | Signed delta |
|---|---:|---:|---:|
| Fraud prevented | $0.00 | $1,500.00 | +$1,500.00 |
| Legitimate payments delayed | 0 | 1 | +1 |
| False-positive intervention cost | $2.50 | $5.00 | +$2.50 |
| Net financial position | −$1,502.50 | −$6.50 | +$1,496.00 |

One additional $1,500.00 malicious payment remained deferred. Gross malicious principal ever settled fell from $3,000.00 to $0.00. Recovered principal was $0.00 in both branches. The extra legitimate intervention was a hold, with 15 whole minutes of incremental delay and $1.50 in delay cost. Terminal paired exposure avoided was $1,500.00.

Interception requires an authorized terminal fraud-control rejection before settlement. Monetized exposure compares surviving settled malicious principal with the matching terminal interception on the other branch. Pending holds, incidental non-settlement and compensated principal are tracked separately. Unknown labels remain unknown. Evaluation labels come from a separate retrospective join. `counterfactual-cost-v1` charges 250 cents per additional legitimate intervention and 10 cents per additional whole minute, capped at 120 minutes per intent. Net delta is avoided exposure minus incremental intervention and delay costs. The separate pure fixture measured 150,000 cents avoided, two delayed intents, six incremental minutes, 250 cents intervention, 60 cents delay and 149,690 cents net. Its digest is `d6e0e9cec7cfde0fde8032e1362188dd379661aa227815877fd0bca9e4fec88e`.

The final browser fixture forked at event 288, 08:55 UTC, with a 09:10 UTC comparison horizon. Source record hash before replay and in Audit was `0f6ceefd5747521322f2ae85b5a5a6d69bdb550c7860d60b97c790fd4ae39dda`. Verified child `01a1d670-41a6-79a8-a4e8-107310cd7825` had record hash `d01bf4132a1bf7ee005d136f780bdb1f94d09f891188b7169e74260cf8186e41` and economic digest `573a92897135aa7dae4f581de761bf9ddbd59e412171b708cc1f1cf66e7d867f`. The 09:00 seal also existed. The earlier point was required to include the compared payment instructions.

## API and browser

The authenticated prefix is `/revenant/r8`. OpenAPI generates the TypeScript response and request types.

| Method and resource | Contract |
|---|---|
| GET, POST `/runs` | `R8Run[]`, `R8Create` → `R8Run` |
| GET `/runs/{id}`, POST `/runs/{id}/close` | `R8Snapshot` with balances, activity, cases, markers and anomaly status |
| GET, POST `/runs/{id}/cases/{intent}` | `R8Case`, `R8Review` → `R8Case` |
| GET `/runs/{id}/timeline/{position}` | `R8Preview` |
| GET `/runs/{id}/liquidity` | `R8Liquidity` |
| POST `/runs/{id}/counterfactual` | `R8Replay` → `R8Job` |
| GET `/runs/{id}/counterfactual/{job}`, POST `/runs/{id}/counterfactual/{job}/cancel` | `R8Job` with `R8Verified` when verified |
| GET `/runs/{id}/audit` | `R8Audit` |

Open Revenant with `?view=r8` for staff sign-in and the five connected views. Control Tower renders the six-bank flow and recorded evidence. Held Queue uses live review authority. Liquidity Fan shows sealed balances and the persistence baseline. Rewind Mode mounts the same `@sands/timeline-ui` scrubber used by Ghost Lap and adds a sandbox submission form. Audit checks the source and displays verified branch hashes, action evidence and provenance. The existing app entry remains available for the V1 slice. Job progress uses polling and authoritative refetch. The checked browser viewport was 390 × 844. The short scenario reached the real R5 scoring cutoff and had insufficient R7 anomaly history. Learned anomaly alert readback for a long history remains unverified. Liquidity Fan now includes a selected-bank operating buffer reference derived from the verified opening balance. The supported fixture uses the six-bank behavioral generator with four authored payment instructions and seed-dependent opening state.

For local setup, configure Hosts.Api with `ConnectionStrings__RevenantLedger` and `ConnectionStrings__RevenantTimeline` plus its normal identity settings. Provision `sands_replay` using `REPLAY_DB_PASSWORD` and `REPLAY_TENANT_ID` in the migration executor. Supply the resulting `ConnectionStrings__RevenantReplay` and matching `Revenant__Ledger__TenantId` to the separate Replay process. The qualified R5 runtime used here was .NET 10 on Linux AMD64.

## Verification and workspace

- Five pure Revenant tests and one short PostgreSQL integration test passed in the Linux AMD64 runtime. The PostgreSQL case verified restricted child write, protected live and source tables, tenant boundaries, deduplication and durable readback.
- One focused authenticated ASP.NET/PostgreSQL integration test passed, including cancellation.
- One Chromium Playwright test passed against real services with separate Replay worker. The browser test took 17.8 seconds. Changed .NET project builds, the Revenant Vite build, OpenAPI generation and `git diff --check` succeeded. Broad CI, long historical generation, release validation, and a full browser matrix remain outside this local gate.
- Branch `codex/r8-counterfactual-revenant-20261114` remains at `fe83c52ca7629d8ce7cb100b59cd8143c2d1ba64`. The working tree has 29 R8 file entries and six pre-existing local entries, all unstaged or untracked. There were zero staged files, commits, pushes or PRs.
- Changed areas: migration executor and additive Marrow 0002/Revenant 0016; API and Worker composition; shared Timeline DI pool selection; Revenant R8 API, replay, economics and worker; generated OpenAPI/TypeScript and the shared client's idempotency header; Revenant R8 shell and styles; focused Revenant, integration and E2E fixtures. Existing local design edits and private recovery files were preserved.


## Earlier Gate 6 recovery (retained checkpoint)

| Gate | Status | Evidence |
|---|---|---|
| A — Shared contract ownership | PASS | Compared tracked and untracked paths against the exact R7 base. The engine, canonical codecs, shared scrubber, Ghost Lap, R5/R7 artifacts and historical specifications match the base. The existing R8 diff includes Marrow infrastructure DI and an additive Marrow replay-role migration. Neither was edited during recovery. R8 HTTP positions now follow the existing shared wire format. |
| B — Buffer provenance | PASS | The R8 snapshot exposes posted and reserved USD cents restored from Marrow position 0. The active scenario declares no operating-buffer policy. The authorized R8 rule uses floor(opening available cents / 10). |
| C — Selected-bank display | PASS | Focused Chromium tests covered two distinct reference values, in-range and out-of-range rendering, bank reselection, invalid or absent openings, the accessible table, and the same reference for the source and alternative. |
| D — Replay and shared Timeline review | BLOCKED | The audit-head policy and original history write denial satisfy their scoped checks. The existing Replay role also has SELECT on the global Marrow context artifact table without a tenant filter. Closing that gap requires a Marrow persistence permission change, which this recovery explicitly excludes. |
| E — Focused validation and evidence report | PASS | Three scoped Chromium tests, Revenant typecheck, generated API contracts, integration project compilation and diff checks passed. Previously recorded database and full browser results are distinguished below. |

`R8-SyntheticBufferReference/1` derives opening available from posted minus reserved in the initial state. The UI validates canonical, nonnegative signed-64-bit USD cents and requires a positive, internally consistent opening balance. Exact BigInt division by ten rounds down without overflow. The source and alternative share the source opening reference. Chart axes use the actual and projection points. A reference beyond those axes gets a text indicator. Missing, duplicate, zero, inconsistent or out-of-range input yields `ReferenceUnavailable`.

The fixture's BANK-A opening of 100,009 cents produces a reference of 10,000 cents ($100.00). BANK-B opens at 300,001 cents and produces 30,000 cents ($300.00). BANK-C has no opening row, BANK-D has zero available, BANK-E has inconsistent amounts and BANK-F exceeds signed 64-bit range. The label reads “Illustrative operating buffer — 10% of opening available balance.” The disclosure reads “Scenario reference only; not a bank or regulatory minimum, and not an ML warning.”

The shared engine uses one TimelineId type for both histories. Position 0 is the initial state. The worker compares source and child at T and uses the existing append path at T+1. The recorded context pins historical versions. Revenant owns the marker mapping, reads and Replay submission. Both apps import `@sands/timeline-ui`. The R8-only position correction covers bounds, markers, previews, audit positions, replay action fact positions, and fork submission. The worker's persisted numeric position and replay schema are unchanged.

The audit-head grant is read-only. Its restrictive policy follows the run directory, whose restrictive policy pins the Replay session login to its provisioned tenant even when its tenant setting or current role changes. Existing readers retain their prior table privileges. The job publication trigger checks the matching verified child, parent, fork, state and record hashes. The retained PostgreSQL fixture directly proved live writes and source writes were denied, an alternate tenant's run was invisible after changing role and setting, and a job without a matching child could not publish. Audit-head row filtering itself was reviewed in SQL. No security migration changed in this recovery.

The remaining isolation gap is in existing Marrow migration `0002_revenant_replay_access.sql`. Its Replay role has table-level SELECT on `marrow_timeline.context_artifacts` without a row policy. The table holds canonical contexts, which can expose other histories' configuration, such as finance manifest digests and game seeds. The history, step and checkpoint restrictions do not scope that table. The minimum follow-up is a Marrow-owned permission change that scopes context access while retaining fork creation and content-addressed deduplication, plus a targeted denial and authorized-fork check.

| Spec 09 item | Status | Evidence and limits |
|---|---|---|
| R09.01 Five screens | PROVEN | Retained authenticated browser navigation through all five views. New tests cover the missing liquidity display. |
| R09.02 Live financial display | PARTIAL | Durable API balances, flows, cases and liquidity were displayed. Automatic refresh after independent live events across every screen was not exercised. |
| R09.03 Authorized review and four-eyes | PARTIAL | Authenticated review and stale version denial passed. The R8 journey did not exercise a two-reviewer approval. |
| R09.04 Replay isolation | PARTIAL | Restricted child write and protected source/live write denial passed. Shared context artifact reads remain unscoped. |
| R09.05 Recomputed counterfactual | PROVEN | Pure and real fixtures executed divergent finance and reconciled the requested metrics. |
| R09.06 Aligned Timeline | PROVEN | Source and alternative curves use the same completed timestamps. The shared scrubber and exact R8 wire positions passed targeted checks. |
| R09.07 Source hash audit | PROVEN | Retained PostgreSQL and browser proofs compared source hashes after branching. |
| R09.08 Cancellation, crash and failure | PARTIAL | Cancellation, invalid-fork failure and write denial passed. Process crash recovery during append and missing model handling were not exercised in the real R8 journey. |
| R09.09 Synthetic and performance honesty | PROVEN | UI labels synthetic finance and an uncalibrated baseline. Reported performance is fixture timing only. |
| R09.10 Deferred versus terminal | PROVEN | Retained pure and real results separated deferred principal from terminal interception. |
| R09.11 Analyst continuation | PARTIAL | Explicit intervention, stale inapplicability and deterministic continuation are covered. The full range of recorded post-fork actions and unresolved authority failures was not exercised end to end. |
| R09.12 Independent seeded runs | PROVEN | Retained authenticated API and browser tests created two same-seed runs with distinct IDs. |
| R09.13 Responsive workflow | PARTIAL | Real Chromium phone coverage included review, chart/table and touch seeking. Tablet and broader accessibility verification remain pending. |

New validation ran `scripts/contracts/generate.sh`, `npm --prefix web run typecheck -w @sands/revenant`, `dotnet build tests/Integration/Integration.Tests.csproj --no-restore --nologo -v quiet`, and three tests in `revenant-r8-liquidity.spec.ts` using the stubbed Playwright configuration. The targeted adapter test round-trips position `9007199254740993`. The retained reports cover five pure tests, one PostgreSQL role test, one authenticated API test and one real Chromium phone journey. Full browser and database journeys were not rerun during recovery.

Recovery files:

- `src/Revenant/Api/RevenantR8Api.cs`
- `web/apps/revenant/src/r8/RevenantR8.tsx`
- `web/apps/revenant/src/r8/bufferReference.ts`
- `web/packages/contracts/openapi/hosts-api.json`
- `web/packages/contracts/src/generated/hosts-api.ts`
- `tests/E2E/revenant-r8-liquidity.spec.ts`
- `tests/E2E/revenant-r8.spec.ts`
- `tests/Integration/RevenantLedger/RevenantR8ApiTests.cs`
- `docs/recovery/r8-implementation-handoff.md`


## Final security and Spec 09 recovery

| Gate | Status | Evidence |
|---|---|---|
| S0 — Permission diagnosis | PASS | Disposable PostgreSQL through migration 0016: SELECT and INSERT were effective through `marrow_timeline_revenant_replay`. The global context table had RLS disabled. |
| S1 — Security design | PASS | [ADR-P1-21](../adr/ADR-P1-21-replay-context-isolation.md) records Replay-specific RLS, its fixed-path predicate, child binding and rollback. |
| S2 — PostgreSQL proof | PASS | The candidate migration passed a disposable database fixture under the restricted Replay login before promotion. |
| S3 — Applied integration | PASS | The normal runner upgraded a database already containing Revenant and Ghost Lap history. Effective denials, legitimate child creation, cold restore, new ordinary Marrow context creation and Ghost Lap fork/restore passed. |
| S4 — Spec 09 acceptance | PASS | Focused authenticated API, worker, missing-artifact and tablet checks close the targeted gaps below. |
| S5 — Handoff | PASS | Changes, hashes, evidence and remaining release checks recorded here. |

Before the fix, the context table was owned by `postgres`. The provisioned `sands_replay` login could assume `marrow_timeline_revenant_replay`, but could not assume `marrow_timeline_runtime`. Its other inherited roles were `revenant_replay_reader` and `revenant_counterfactual_worker`. The login and its inherited roles had neither superuser nor BYPASSRLS. The Timeline replay grant in Marrow 0002 conferred context SELECT and INSERT directly; permissions on the history, step and checkpoint tables did not scope those reads. Existing job and history authorization uses the tenant setting for routing and combines it with the login’s durable tenant binding.

Revenant migration `0017_replay_context_isolation.sql` applies restrictive row policies to the existing context table. The authorization predicate uses `session_user`, with no reliance on the caller's mutable tenant setting. A durable job authorizes a verified V4 source and the ancestor contexts required to read its inherited prefix. Only a Running job can insert the exact policy-v8 child context; the existing V4 source pin supplies all other canonical bytes. The child insert guard checks its parent, tenant, fork, operation key and context. A matching verified child remains readable after result publication. A shared content reference is readable when the same bytes are independently authorized by a job.

The upgraded fixture used the Replay login to enumerate only its authorized source, then attempted reads by known foreign, same-tenant unrelated and Ghost Lap references. The denials survived changing the tenant GUC and the assumed role. The fixture also denied writing a different valid V4 context, protected finance, case, audit and original Timeline writes, and rejected a Verified update without a matching completed child. It created a child with a different context through the unchanged engine, interrupted after its first committed append, and retried with a fresh worker and data source. The job remained Running without a result during interruption. Retry reused the already committed append, published the verified result and left source state and record hashes identical. A third host restored the child and its inherited fork prefix. The ordinary role could create and read a separate Revenant root after upgrade, and fork historical Ghost Lap context into a different pinned context and restore both.

| Spec 09 item | Status | Executed evidence |
|---|---|---|
| R09.01 Five screens | PROVEN | Retained real Chromium phone journey in `revenant-r8.spec.ts`; focused tablet navigation through all five views. |
| R09.02 Live financial display | PROVEN | `R8_second_approval_refreshes_only_after_distinct_supervisor_commits`: normal snapshot refresh reflected the committed case release, settled payment and released bank reserve. Scope is one actual financial event. |
| R09.03 Four-eyes review | PROVEN | Same authenticated API test: first approval succeeds; same actor, wrong role and stale version rejected; distinct supervisor releases, with PostgreSQL case and settlement readback. |
| R09.04 Replay isolation | PROVEN | `Replay_login_forks_only_its_tenant_and_publishes_verified_readback`: effective-role denials, original write protection, authorized child and ordinary Marrow/Ghost Lap compatibility. |
| R09.05 Recomputed counterfactual | PROVEN | `V4CausalPolicyTests`, authenticated verified job readback and retained real Chromium journey reconcile executed economic values. |
| R09.06 Aligned Timeline | PROVEN | Retained comparison uses the same completed seal times; upgraded database matches the exact inherited fork state; tablet preserves position `9007199254740993` through seek and submission. |
| R09.07 Source immutability | PROVEN | Upgraded PostgreSQL compares source record and state hashes before/after. Retained browser Audit also verifies the source. |
| R09.08 Crash and artifacts | PROVEN | Injected interruption after committed append, fresh worker retry and no duplicate; retained cancellation and invalid fork checks. `Missing_required_model_blocks_screening_and_missing_advisory_has_no_prediction` proves blocked R5 Screening with no fact or settlement and typed R7 `ArtifactMissing` without a prediction. |
| R09.09 Synthetic and uncertainty labels | PROVEN | Retained browser synthetic/uncalibrated labels and focused opening buffer data. Runtime timings are fixture measurements. |
| R09.10 Deferred versus terminal | PROVEN | Pure economics and retained real browser proof distinguish $1,500 deferred from $1,500 terminally intercepted. |
| R09.11 Analyst continuation | PROVEN | Authenticated case fixture replays two applicable recorded supervisors in policy-only mode and reports an authenticated but no-longer-applicable intervention separately. Pure fixture covers applied sandbox actions, stale versions, invalid identities, absent MFA and invalid version provenance. |
| R09.12 Independent seeded runs | PROVEN | `Fresh_scenarios_reviews_and_verified_replay_use_real_authenticated_tenant` and retained Chromium journey: equal seed and manifest, distinct namespaces. |
| R09.13 Responsive and accessible UI | PROVEN | Retained real phone fixture and focused stubbed Chromium at 820 × 1180: keyboard and touch Timeline, five views, labeled chart and aligned table, four verified cards and no values in Pending/Failed. |

All five screens and the four KPI calculations remain unchanged. The retained comparison is policy-v8 plus explicit sandbox analyst intervention: source intercepted $0.00; alternative intercepted $1,500.00; malicious still deferred $1,500.00; one additional legitimate delay; $2.50 incremental intervention cost; $1.50 incremental delay cost; **+$1,496.00 net**. This comes from the combined policy, intervention and executed financial outcomes. R5 remains mandatory after warmup; R7 is advisory only. Rechecked ZIP SHA-256: R5 `56bca32c36dd1911d163a6f92369da43382e1937d875228339f15f8608519c7c`; R7 `03e895da4fa4348b69b1251c6ef9c1c9e6c2deee893e83bf2a983c698281b35e`.

Validation: both changed test projects compiled, eight focused Revenant tests passed on Linux AMD64 (including the pinned R5 references, economics and upgraded PostgreSQL), two actual R8 API tests passed with PostgreSQL and the focused Chromium tablet fixture passed. The interruption is injected after a real committed append; no operating-system process kill was used. The optional full R7 pilot parity requires the retained `REVENANT_R7_PILOT_INPUT` and was not qualified in this recovery. Comprehensive CI, release environment validation, broader history and browser regression, and long-history learned R7 alert qualification remain release checks.

## Release verification

The R8 branch is stacked on `codex/r7-ssa-anomaly-liquidity-20261113` at `fe83c52ca7629d8ce7cb100b59cd8143c2d1ba64`. The intended release includes the R8 code, migrations, ADR, API contracts, UI, CI selection and test fixtures. Private local edits and research/recovery files are excluded. The shared engine, public Timeline API and canonical formats, shared Timeline UI, Ghost Lap physics, R5/R7 pins and historical specifications match the R7 base. The composition helper retains its original overload.

- The migration runner applied 0016, 0017 and 0018 in order to an R7 database containing historical Revenant and Ghost Lap state. The provisioned Replay login could not insert a job, change its tenant/source/request/key, change the login's tenant binding, write the source allowlist, or call guarded functions even after role switching. Borrowing an authorized job ID did not admit unrelated bytes. Migration 0018 scopes a second copy of the canonical context in the V4 scheduler to issued jobs. Both context read paths deny an unrelated same-tenant root and a foreign tenant. The fixture preserves the original hashes, creates and cold-restores the distinct child context, verifies ordinary Marrow and Ghost Lap, and blocks unverified publication.
- The foreground Control Tower, Held Queue and Liquidity Fan poll the durable API on active runs. The phone test independently committed a review via the authenticated API, then observed the settlement on the already open Control Tower without reloading the page. The browser also exercised the real six-bank launch, all five screens, the separate Replay process, exact fork wire format, verified branch, four reconciled KPIs and a second same-seed namespace. The measured test case took 21.7 seconds locally. This is fixture timing only.
- The existing R7 published-Worker proof runs pinned SSA and the liquidity baseline without enabling the retired forecast. The R8 receipt reader preserves unavailable advisory status without showing a score. The short financial demo has insufficient anomaly history by design; qualified anomaly evidence comes from its separate sparse sealed-history fixture.
- Release build with warnings as errors, generated-contract consistency, frontend typechecks/builds and bundle isolation passed locally, as did the architecture tests, the upgraded PostgreSQL checks, the published R7 Worker proof and four focused Chromium data/accessibility cases. The legacy R2 upgrade fixture also verifies that all three R8 migrations apply while preserving historical fingerprints. The existing GitHub Linux AMD64 jobs execute the R8 PostgreSQL fixture on fresh and upgraded schemas, both authenticated R8 tests, the published R7 Worker proof and the real phone Replay path. Use [GitHub Checks on the stacked R8 draft](https://github.com/nightwing9-bit/the-sands-of-time/pull/8/checks) for final-head test status and actual executed results.

Deployment still needs the restricted Replay service credentials, matching tenant binding, migration-owner rollout, and environment-specific checks. The fixtures establish deterministic synthetic financial behavior and artifact/runtime compatibility. They do not establish production throughput, statistical calibration, real fraud performance or operating risk thresholds.
