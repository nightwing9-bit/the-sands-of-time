# Spec 08 implementation tasks — Revenant Intelligence and verified R1–R8 integration

**Task format:** eight completed, evidence-linked delivery checkpoints (`R1`–`R8`). This is the current implementation-status task record; it maps the delivered code to the Spec 08 operating contract and its completed Spec 09 integration consumer. The underlying R1–R6 foundational work retains its original finance/architecture ownership. R8 is the completed downstream replay/UI integration checkpoint and does not transfer ownership of the five-screen application away from Spec 09.

**Product invariant:** One immutable Marrow Timeline engine for Revenant and Ghost Lap. A real model-required fraud decision creates authorized money-state consequences; separately learned velocity evidence and statistical liquidity evidence remain advisory. Verified alternate finance supplies the economic KPI values. No duplicate ledger, replay engine, Timeline scrubber or live inference from hidden truth.

## Completed delivery checkpoints

- [x] **R1 — ML.NET execution baseline, native runtime and version preservation**
  - **Objective:** Establish real .NET 10/ML.NET execution and a supported native path for the coming learned workloads without modifying existing finance history formats.
  - **Delivered:** Actual trained-model save/load/predict capability; validated Linux AMD64 path for native ML.NET TimeSeries dependencies; reusable model identity and artifact checksum conventions. Historical V3 reference compatibility remained intact as V4 was developed.
  - **Verified by:** Executable ML.NET/runtime probes and subsequent R5/R7 saved-artifact and native release/CI load tests.
  - **Boundary:** Native SSA qualification belongs to supported Linux x64; neither a macOS ARM64 score nor a successful container `uname` alone is claimed as native SSA evidence.

- [x] **R2 — Deterministic V4 five-minute finance clock**
  - **Objective:** Supply sufficient, causally ordered observation histories for genuine account-velocity and bank-liquidity intelligence.
  - **Delivered:** Completed five-minute seals, stable committed fact ordering and time-to-observation mapping. Observed zero-flow buckets are distinguished from missing periods; source/branch histories retain correct as-of cutoffs. Seven-day longitudinal histories can be executed independently of the short interactive Friday Ring.
  - **Verified by:** Deterministic seal reconstruction, source/branch projection and compatible history tests.
  - **Boundary:** Browser animation, host wall time and Redis delivery order are not financial fact or ML cutoff authority.

- [x] **R3 — Durable V4 PostgreSQL ledger and Marrow-backed financial lineage**
  - **Objective:** Make the future counterfactual physically meaningful: payments and human decisions must alter real committed synthetic finances and leave verifiable history.
  - **Delivered:** Durable ordered financial facts, posted/reserved accounting, holds, case state, settlement/returns, source/child fork and restoration through the existing Marrow engine, run closure, ledger/Timeline reconciliation and tenant-aware storage.
  - **Verified by:** Actual PostgreSQL financial/branch tests, immutable source checks, causal continuation and balanced posting/reservation invariants.
  - **Boundary:** No new Timeline engine or source-history rewriting; financial state is not derived from advisory forecasts.

- [x] **R4 — Canonical finance-valid longitudinal synthetic dataset and evaluation provenance**
  - **Objective:** Produce actual observed finance sequences and labels adequate to train/evaluate authentic risk and behavioral models, while keeping retrospective truth outside live decisions.
  - **Delivered:** Deterministic six-bank account/activity/balance histories, labeled fraud and legitimate episodes, commercial hard negatives, completed seal streams, account outgoing counts, available-balance observations, training/calibration/measurement identities and source digests. Research cohorts remain distinct from the interactive demonstration's live decisions.
  - **Verified by:** Canonical stream hashes, finance-valid PostgreSQL facts/seals, isolated retrospective labels and reproducible series reconstruction.
  - **Boundary:** Internal synthetic data are not described as PaySim or real-world bank outcomes; no labels or future settlements enter risk features.

- [x] **R5 — Train, evaluate, pin and load the authentic FastTree risk classifier**
  - **Objective:** Supply one genuine immutable learned model that can later influence actual V4 Screening rather than simply decorating the UI with probabilities.
  - **Delivered:** ML.NET FastTree binary classification; `risk-features-v2` causal builder; synthetic held-out evaluation with ROC-AUC approximately `0.838` and average precision approximately `0.635`; pinned trained ZIP/manifest; deterministic loaded scoring and integer PPM policy identity.
  - **Published artifact:** `revenant-behavioral-risk-v2/d1801a3f72f9d4525cd39b8b7873e2fc43288dea0f7c1f5ee8646612833a28e4`, ZIP SHA-256 `56bca32c36dd1911d163a6f92369da43382e1937d875228339f15f8608519c7c`.
  - **Verified by:** Train–save–load–score, held-out synthetic metrics, feature/evidence provenance, checksum and pinned inference tests.
  - **Boundary:** Existing artifacts and thresholds are never overwritten by a new trainer run; synthetic evaluation is not real-world accuracy certification.

- [x] **R6 — Atomically bind learned Screening to legal money movements and human review**
  - **Objective:** Ensure learned risk materially changes an authorized payment workflow without bypassing the ledger, v7/v8 hard rules or human controls.
  - **Delivered:** ModelRequired V4 Screening under `v4-model-review-capacity/1` with **inclusive `852469` ppm** cutoff; recorded `v4-model-screening-evidence/1`; real ML-triggered hold/reservation and case; approved/released and eventually settled financial continuation; fail-closed unavailable-model handling; idempotency and ordered Timeline projection.
  - **Verified by:** Real PostgreSQL Screening, hold/case, reviewer authorization, settlement and release, ledger parity and source history checks; the R6 native release reported **94/94** Revenant PostgreSQL integration tests.
  - **Boundary:** An ML hold is an actual delay, not automatically terminal fraud prevention; pure reducer and historical replay do not call the predictor.

- [x] **R7 — Publish genuine SSA anomaly evidence and eight-horizon bank liquidity baseline**
  - **Objective:** Make account behavior and bank liquidity available as correctly scoped, model-identified evidence that survives publication and supports both source and child histories.
  - **Delivered, anomaly:** Saved ML.NET 5.0.1 `DetectSpikeBySsa` model with account-local causal warmup, preceding-bucket expected-count state, six-bucket Poisson surprise and pinned global policy. Producer `revenant-ssa-anomaly/672a05a26995653585e5ba9505807df7a2f707914886634c39b133c7dbc72980`, ZIP SHA-256 `03e895da4fa4348b69b1251c6ef9c1c9e6c2deee893e83bf2a983c698281b35e`, policy `R7-AnomalyGlobalPolicy/1`, inclusive `3.4120270786372`, evidence `anomaly-evidence/2`. Warmup requires at least **1,152** completed buckets/eight nonzero warmup observations.
  - **Delivered, liquidity:** Causal `bank-available-balance-series/1` from completed posted minus reserved amounts; `LiquidityPersistenceBaseline/1` supplies exactly eight points at +5 to +40 minutes using the completed-origin available balance. Evidence `liquidity-projection-evidence/1`; uncertainty `Uncalibrated`; no model ZIP or predictive warning. The earlier `ForecastBySsa` research is preserved but not active runtime selection.
  - **Delivered, integration:** `Hosts.Worker --v4-advisory`; tenant-scoped immutable advisory evidence; migration `0015`; typed insufficient-history/unavailable failures; historical `/1`/`/2` evidence not reinterpreted.
  - **Verified by:** Saved anomaly ZIP cold load and causal replay, root-2 *development* detection `14/23` and 7 false alerts/71.26 normal account-days; actual published Linux AMD64 Worker eligible inference and eight-point projections; branch reservation divergence; idempotent PostgreSQL evidence; successful R7 CI.
  - **Boundary:** R7 advisory never becomes Screening authority or a source of dollar savings. Short histories legitimately show `InsufficientHistory`. The original third trained-forecaster expectation is replaced by an explicitly versioned statistical projection, without altering the remaining product goals.

- [x] **R8 — Close compatible V4 financial replay, impact economics and connected Spec 09 consumption**
  - **Objective:** Complete the visible Marrow/Revenant product: choose a compatible policy at an earlier verified point, lawfully execute a child financial future, measure the difference and inspect it across the five existing Revenant surfaces.
  - **Delivered, finance:** Same-family V4 `revenant-finance-reducer/4`/`revenant-finance-fact/4`/`revenant-finance-state/4`/`revenant-finance-context/4`; source `policy-v7` and alternative `policy-v8` with real distinct base rules and the same pinned R5 and human-review capacity; actual upstream-intent continuation; `analyst-continuation-v1` with authority, MFA, expected version and distinct-reviewer checks; explicit attributed sandbox analyst intervention; exact source prefix and verified child suffix.
  - **Delivered, economics:** `counterfactual-cost-v1` and executed payment-level source/alternative classification. All four outcome families: **fraud intercepted, legitimate delayed, false-positive cost, signed net financial effect**, with separate deferred malicious principal/recovery/funding failures. The real synthetic browser fixture measured $0 → $1,500 interception, an additional $1,500 deferred, one legitimate delayed payment, $2.50 extra intervention cost, $1.50 delay cost and **+$1,496.00 net**, attributed to policy-v8 **plus** a recorded analyst intervention.
  - **Delivered, process/security:** Durable counterfactual jobs, `Hosts.Worker --replay <job-uuid>` under restricted `sands_replay`, source/child hash verification, operation-key idempotency, retry/cancel, job-scoped content-addressed context RLS, cross-tenant denial; migrations `0016`–`0019` and `docs/adr/ADR-P1-21-replay-context-isolation.md`. Marrow canonical engine/codec/history semantics and Ghost Lap are unchanged.
  - **Delivered, API/UI:** Authenticated `/revenant/r8` runs, cases, Timeline, liquidity, counterfactual and audit operations; generated OpenAPI/TS contracts; Control Tower, Held Queue, Liquidity Fan, Rewind Mode and Audit; actual shared `@sands/timeline-ui` component; phone/tablet input, exact decimal-string positions, four KPI readbacks and typed failure display. Liquidity Fan includes the non-authoritative `R8-SyntheticBufferReference/1` opening-balance marker.
  - **Verified by:** Real published Worker and PostgreSQL replay roles, forged-job/context denial, intact source record/state hash, real MFA/four-eyes and authenticated API, crash/retry and artifact-unavailable fixtures, browser phone+tablet flow and signed KPI reconciliation. Final Linux CI reported **8 jobs passed**, **109** API/service tests, **61** Marrow PostgreSQL, **96** Revenant PostgreSQL and one separate R7→R8 upgrade test.
  - **Release:** R8 commit `a1c7c2473ac05f15005c1f0cba477c90a2c96656`, stacked on R7 commit `fe83c52ca7629d8ce7cb100b59cd8143c2d1ba64`; draft PRs remain separate and unmerged.
  - **Boundary:** R8 is the downstream Spec 09 integration receipt, not permission for Spec 08 to change the generic Marrow engine or implement a competing Replay/UI. A statistical liquidity reference must not affect finance or economics.

## Completed exit verification

| Acceptance surface | Recorded result |
|---|---|
| Native trained ML models | R5 FastTree and R7 SSA–Poisson genuinely fitted, saved, loaded and run under supported runtime |
| Financial authority | Pinned R5 score actually changes a lawful V4 hold; original hard rules, reservation/review, settlement and fail-closed behavior intact |
| Supported liquidity | Eight completed-origin persistence points, typed `Uncalibrated`, real bank balances, no fabricated learned bands |
| Model/evidence lifecycle | Immutable ZIP hashes, causal evidence versions, typed unavailable and exact historical interpretations |
| Replay and child causality | Original source immutability; compatible V4 policy choice; rebuilt branch consequences; lawful human continuation |
| Economic impact | Four reconciled KPI families; terminal versus pending and direct versus incidental effects; checked minor-unit cost arithmetic |
| Shared engine/security | Same Marrow APIs and shared Timeline UI; scoped global-context reads; no Ghost Lap ML dependency |
| API and visual product | Actual `/revenant/r8` endpoint family and five functioning screens; authenticated review; accessible phone/tablet views |
| Integration release | Final R8 Linux CI: 8 passed jobs; model checks, PostgreSQL, API, migration upgrades and browser journey |

## Existing source references for subsequent spec consumers

- **Intelligence scope:** `docs/recovery/r7-option-a-supported-scope.md`
- **R7 produced evidence/Worker contract:** `docs/recovery/r7-r8-advisory-handoff.md`
- **R8 integrated finance/API/UI contract:** `docs/recovery/r8-implementation-handoff.md`
- **Replay/Marrow database permission ADR:** `docs/adr/ADR-P1-21-replay-context-isolation.md`
- **Risk and anomaly artifacts:** `src/Revenant/Intelligence/Artifacts/`
- **Worker inference and projection:** `RevenantAdvisoryWorker`, `V4ObservationProjector`, `V4BranchExecution`
- **R8 API:** `src/Revenant/Api/RevenantR8Api.cs`
- **R8 UI:** `web/apps/revenant/src/r8/RevenantR8.tsx`
- **Shared UI:** `web/packages/timeline-ui/`
- **API models:** `web/packages/contracts/`
- **PostgreSQL migration chain:** `scripts/db/migrations/revenant/0015_*` through `0019_*`

**Status:** All eight R1–R8 checkpoints are implemented and checked under the current supported product scope. The original learned liquidity `ForecastBySsa` research is deliberately not an active workload; its old evidence stays historical. Production deployment and external financial accuracy are not represented by the synthetic release tests.
