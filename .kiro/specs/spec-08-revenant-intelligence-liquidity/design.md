# Spec 08 design — Revenant Intelligence, V4 financial evidence and R1–R8 integration

## 1. Architecture, authority and boundaries

**State:** implementation-aligned design for the completed R1–R8 baseline (14 November 2026). This document describes the supported architecture consumed by Spec 09. All historical version keys and artifact hashes retain their original meaning.

### 1.1 Product architecture

The system has **one shared Marrow Timeline engine** and two independent domains. Marrow preserves canonical history, exact replay, verified forks and lineage. Ghost Lap owns its game reducer and UI adapter. Revenant owns synthetic finance, real PostgreSQL ledger authority, model evidence, human review, branch-specific financial continuation and its five-screen product adapter. The same compiled `@sands/timeline-ui` scrubber serves both domains.

Revenant does not derive balances or fraud losses from a chart or an ML output. Actual committed finance creates financial facts; pinned trained ML creates permitted evidence; the domain policy decides whether a payment is held; authorized review and settlement change the live or isolated branch state; the comparison service derives economic impact from executed outcomes.

```text
Synthetic finance + account observations
    │
    ├── Offline ETL/ML.NET FastTree training → pinned R5 risk ZIP
    │                                      │
    │                                      ▼
    └── Live V4 Screening → causal risk-features-v2 → model score / ppm
                                         │
                                         ▼
              hard v7/v8 rules + v4-model-review-capacity/1
                                         │
                  PostgreSQL ordered facts + holds + cases + outbox
                                         │
             Marrow V4 source history / completed five-minute seals
                         │                                  │
                R7 advisory worker                R8 authorized replay
              ┌──────────┴──────────┐              │
      learned SSA anomaly    statistical bank       verified child
      + Poisson surprise     persistence points     │
              └──────────┬──────────┘               │
                         └───────────┬───────────────┘
                                     ▼
                     /revenant/r8 authenticated API
                                     ▼
           Control Tower · Held Queue · Liquidity Fan
                    Rewind Mode · Audit
             (shared Marrow Timeline UI, local adapter)
```

### 1.2 Ownership and change boundaries

| Owner | Implemented capability | Boundary |
|---|---|---|
| Marrow Timeline | Verified source/child histories; contexts, canonical state, hashes, immutable prefix, deterministic append | No ML.NET, finance rules, R8-specific scrubber or new history engine |
| Revenant ledger / finance V4 | Financial truth, holds, reserve, return, settlement, review state, ordered facts | Payment authority never comes from advisory output or browser state |
| Revenant Intelligence | R5 trained FastTree scoring, R7 learned anomaly, statistical liquidity projection, evidence manifests, typed failures | No separate ledger, no inference inside pure reducer |
| Revenant Replay | V4-compatible alternate-policy continuation, durable jobs, child financial outcomes, comparison, restricted DB credentials | Cannot mutate source or live finance |
| Revenant UI / Spec 09 | Five screens, exact Timeline adapter, authorized review, graphs, economic cards, audit | Does not implement another Timeline engine or manufacture savings |
| Ghost Lap | Game state, domain-specific replay and presentation | No dependency on Revenant ML or Replay services |

**Important version boundary:** V1 policy v7/v8 and historical V2/V3 finance remain preserved, but they are not implicitly model-aware V4 histories. The completed alternative comparison uses policy choices within the same V4 reducer/input/state/context family.

### 1.3 Implementation checkpoints and architecture contribution

| Checkpoint | Delivered architecture |
|---|---|
| R1 | Executable ML.NET 5.0.1 baseline and Linux AMD64/native dependency proof; compatible historical model/runtime boundary |
| R2 | Deterministic five-minute V4 bank observation clock with completed causal seals |
| R3 | Durable PostgreSQL V4 financial ledger, Marrow binding, source/child lineage, postings/reservations/reviews/returns |
| R4 | Versioned, finance-valid, labeled longitudinal synthetic six-bank observations and causal evaluation sidecar |
| R5 | Trained, saved and measured FastTree risk classifier with immutable manifest, feature identity and threshold |
| R6 | Financially authoritative ModelRequired Screening, real ML-driven holds and lawful human review/settlement |
| R7 | Saved SSA–Poisson velocity anomaly; eight-horizon statistical liquidity baseline; typed, versioned advisory runtime |
| R8 | Compatible V4 alternate-policy replay, restricted Worker, measured counterfactual economics, authenticated API and five connected UI screens |

R8 is listed as the completed downstream integration checkpoint; the distinct Spec 09 design remains the canonical owner of the user-facing counterfactual experience.

---

## 2. Executable platform and model lifecycle

### 2.1 Native platform

The target runtime is .NET 10 with `Microsoft.ML`, `Microsoft.ML.FastTree` and `Microsoft.ML.TimeSeries` in the pinned compatible **ML.NET 5.0.1** family. Native-dependent SSA fitting/inference is executed and tested on **Linux AMD64 with glibc**; successful Docker or SDK startup alone is not a sufficient SSA probe. Model artifacts and their dependencies are checked in a published Worker/runtime rather than merely in an offline training process. Native macOS ARM64 can perform compatible data/finance work but is not the authority for SSA native acceptance.

Inference is not retrained per payment, seal, branch or dashboard request. The Worker uses an immutable model ZIP and checked manifest; account/branch-local inputs are reconstructed causally. No global mutable predictor state is shared between unrelated executions or accounts.

### 2.2 Provenance and data separation

R4 supplies real executed synthetic finance histories with complete five-minute seals, account counts, bank balances and retrospective labels. This is an internal synthetic cohort, not a PaySim download or production-bank data. A ground-truth sidecar is available for offline evaluation and economic accounting; it is excluded from operational risk features and review decisions. Cohort and result digests are retained with generator, scenario and model identities. Training, calibration and measurement sources are explicitly distinguished.

The completed financial clock is based on committed ordering and closed seals. A zero outgoing count is an observed interval only when that interval exists. Future settlements must not be folded backward into an earlier cutoff. `Posted − Reserved` is the available balance at an actual completed boundary. A forked child inherits the source's canonical prefix but uses its own causal suffix thereafter.

---

## 3. R5 — Model-aware risk and financial authority

### 3.1 Pinned supervised model

The published R5 model is a genuine trained ML.NET **FastTree binary classifier**, evaluated on a held-out project synthetic cohort. Its observed held-out ROC-AUC was approximately **0.838** and average precision **0.635**. The artifact is immutable and not silently retrained to match an individual demonstration.

```text
Producer:
revenant-behavioral-risk-v2/d1801a3f72f9d4525cd39b8b7873e2fc43288dea0f7c1f5ee8646612833a28e4
ZIP SHA-256:
56bca32c36dd1911d163a6f92369da43382e1937d875228339f15f8608519c7c
Feature schema: risk-features-v2
Policy:         v4-model-review-capacity/1
Evidence:       v4-model-screening-evidence/1
Inclusive hold threshold: 852469 ppm
```

The published score uses the pinned monotonic sigmoid applied to the trained FastTree raw score (slope `0.1647929876432001`, intercept `0.057602363131310064`), quantized to integer ppm using the recorded conversion rule. The quantitative score is a versioned synthetic model-risk signal; the reducer consumes the recorded integer decision evidence, not a newly computed float.

**Inference-time features** come from the actual pre-screening account/intent snapshot. The model never receives retrospective fraud truth, future balances, case outcomes, uncommitted settlements or browser-provided score values. Feature validity, transaction order and as-of version are checked before the committed decision. `risk-features-v1` material may remain in historical assets, but R8 reads the active V4 `risk-features-v2` evidence schema.

### 3.2 V4 screened-intent transition

The V4 runtime uses existing authorizing ingestion and financial transaction boundaries:

1. Resolve the authenticated scenario, committed account state, active V4 compatible policy and pinned trained artifact.
2. Create the causal pre-screen feature vector and compute loaded-model risk once for the applicable state.
3. Apply the exact registered base-rule policy and inclusive learned-risk threshold, retaining hard-rule precedence.
4. Atomically commit the ordered Screening fact, raw/pinned inference evidence, hold/reservation or clear decision, case/outbox and idempotency identity.
5. Project through the existing Marrow-compatible finance adapter; later review, settlement, return or release uses the existing finance authority.

An ML score can cause **a genuine Held case**. It does not itself produce terminal prevented fraud, payment rejection, money creation or release. A missing/corrupt required risk artifact is typed **ModelRequired failure** and does not silently revert to rules-only. Cold historical replay reads recorded evidence; the pure finance reducer does not load ML.NET or rescore historical source facts.

### 3.3 Compatible policy choices

The active R8 source and alternative are both V4 model-aware Screening configurations:

| Versioned choice | Implemented base rule | Immutable config digest |
|---|---|---|
| `policy-v7` | Velocity rule at 8 intents and USD 1,000 amount floor | `sha256:687078f26fefcf081ee0f39e5ff185385c11130b52198e74c3861f7347b5c023` |
| `policy-v8` | Velocity rule at 5 intents, zero amount floor, plus USD 1,500 new-beneficiary rule | `sha256:b702047a23f2e5e02efc44ef1354aec91e913159f35b69f65b112612f1cbafbd` |

Both preserve sanctions, the published R5 model, `v4-model-review-capacity/1` and authorized staff review. The base policy difference can change holds, reservations, fundability and later settlements; the implementation does not swap a V1 reducer into V4 to obtain that difference.

---

## 4. R7 — Supported behavioral anomaly producer

### 4.1 Causal account-count observations

Observation schema: `account-outgoing-velocity-series/1`. Account series are per tenant, execution, account, source/child and fully completed five-minute boundary; they count real committed accepted outgoing intents. The canonical `V4BranchExecution.ReadProjectionHistoryAsync` → `V4ObservationProjector.Project` path builds the observation from verified finance. Identical inherited prefixes produce matching semantic series; physical evidence hashes may differ because execution identity is part of persistence.

### 4.2 Pinned learned model and scoring

```text
Producer:
revenant-ssa-anomaly/672a05a26995653585e5ba9505807df7a2f707914886634c39b133c7dbc72980
ZIP SHA-256:
03e895da4fa4348b69b1251c6ef9c1c9e6c2deee893e83bf2a983c698281b35e
Model family:  ML.NET 5.0.1 DetectSpikeBySsa
Policy:        R7-AnomalyGlobalPolicy/1
Evidence:      anomaly-evidence/2
Alert cutoff:  3.4120270786372 (inclusive)
Authority:     Advisory only
```

The loaded model uses a 576-bucket SSA seasonality, native history 32, positive-spike side, confidence 95 and fixed seed 7. The account independently warms up at least **1,152** completed buckets and eight nonzero counts. The expected count for a decision is taken from the **preceding-bucket** SSA state, not from a predictor updated with the current observed count.

At each eligible bucket, the runtime compares six observed account-count buckets with six learned expected-count values. Each expectation is floored at `0.02`, and the surprise score is `−log10 P(Poisson(sum expected) ≥ sum observed)`. Alerts require positive current count, the pinned inclusive threshold and valid history. Offline event analysis coalesces alert buckets and matches them to labeled windows; that retrospective matching never becomes a live financial hold.

### 4.3 Qualification and runtime behavior

The saved-and-reloaded artifact reproduced the G4 development behavior: **14/23** labeled events detected on the root-2 measurement fold, **7 false alerts/71.26 normal account-days**, **66.7%** development precision, **10-minute** median detection delay. Root-1 exploratory reverse measurement detected **16/31**. This is concrete simulation evidence, not a guarantee for unseen accounts. A research-only seasonal Poisson comparator produced higher event recall in both folds; it is not an active published producer.

`Hosts.Worker --v4-advisory <execution-uuid> <completed-boundary-ms> [account-id]` produces a versioned account result. An ineligible short Friday Ring account may return `InsufficientHistory` with no score/alert. This is an ordinary typed runtime outcome; it does not stop the six-bank UI or the financially authoritative R5 Screening workflow.

---

## 5. R7 — Supported bank liquidity producer

### 5.1 Authoritative observations and eight horizons

Input schema: `bank-available-balance-series/1`. For a completed bank seal `T`, authoritative available balance is:

```text
A(T) = Posted(T) − Reserved(T)    [signed integer USD minor units]
```

The supported `LiquidityPersistenceBaseline/1` producer emits:

```text
Horizon minutes: 5, 10, 15, 20, 25, 30, 35, 40
Projected point: A(T) at every horizon
Evidence:        liquidity-projection-evidence/1
Uncertainty:     Uncalibrated
Model ZIP:       none
Advance warning: none
```

A child reservation, release, settlement or return can change the actual child balance and its subsequent persisted projection, while the source remains intact. The alternative **ghost line** in Spec 09 is a verified sequence of completed child finances, not this eight-point baseline.

### 5.2 Separation from historical SSA forecast research

The previously investigated ML.NET `ForecastBySsa` family is retained as non-active research: no supported runtime selection, no trained confidence bands in current evidence, and no learned 40-minute shortfall warning. The eight-horizon time contract, causal bank series, financial branch comparison and all five UI screens are intact. Historical `liquidity-forecast-evidence/1` and `/2` remain version-specific historical formats; they are not reinterpreted as persistence projections.

### 5.3 Display reference distinct from a forecast

Liquidity Fan has a separate Revenant-owned marker: `R8-SyntheticBufferReference/1`. For a bank with valid, positive, verified **opening** available balance, the illustrative reference is `floor(opening available minor units / 10)`. It is rendered as “Illustrative operating buffer — 10% of opening available balance.” It is not regulatory, a live hold threshold, a trained model output or a counterfactual cost term. Missing/inconsistent opening state produces `ReferenceUnavailable`.

---

## 6. Worker evidence, persistence and typed failures

### 6.1 Supported R7 request/receipt

The supported advisory Worker command is:

```text
Hosts.Worker --v4-advisory <execution-uuid> <completed-boundary-ms> [account-id]
```

One account ID requests the account anomaly receipt; bank-level usage returns the available bank projection receipts. Each receipt uses typed `Kind`, `Schema`, `ProducerId`, `PolicyId`, `Execution`, root `Lineage`, `Subject`, `Unit`, requested `BoundaryMs`, source `CausalOrigin` and `SeriesSha256` where applicable. Successful receipts have `EvidenceSha256`; learned anomaly receipts also identify the exact model artifact. Non-successful requests have typed failure and **no** successful evidence digest.

The immutably persisted tenant-scoped evidence uses `revenant.v4_advisory_evidence` with same-key/same-bytes idempotency and conflicting-bytes rejection. Migration `0015_r7_liquidity_projection_evidence.sql` extends the accepted projection schema. Historical advisory formats are read according to their own producer/version; old ZIP pins cannot select the new supported method.

### 6.2 Failure behavior

| Situation | Required observable behavior |
|---|---|
| Unknown subject/tenant, missing seal | Typed failure; no fabricated observation |
| Account too short or inactive | `InsufficientHistory`, no anomaly score/alert |
| Missing/corrupt R7 anomaly ZIP | Typed unavailable/incompatible; no advisory success |
| Unsupported native SSA runtime | Typed unavailable; no fake score |
| Missing R5 required risk artifact during new Screening | Fail closed; no completed Screening decision |
| Database read/write failure | Command failure; no successful evidence receipt |
| Matching repeated advisory result | Idempotent return |
| Conflicting same-key persisted bytes | Reject conflict |

All R7 advisory status is non-authoritative for live money movement. Separate ModelRequired failure retains its stricter financial effect.

---

## 7. R8 interface: lawful alternative finance and measured economics

**R8 already provides the Spec 09 consumer.** Its interface is included here because subsequent phases must bind to the implemented domain and API rather than assume a new greenfield product.

### 7.1 Marrow and V4 continuation

Active registered V4 components:

```text
revenant-finance-reducer/4
revenant-finance-fact/4
revenant-finance-state/4
revenant-finance-context/4
```

`ObservedFinanceReducer` applies V4 Screening and delegates review/settlement/return to the existing financial reducer. `V4BranchExecution` handles source binding and closure; `V4ObservationProjector` supplies branch-causal features and bank/account series. The existing Marrow API handles fork, append, replay, restore, complete and verification. The source/child share the exact verified prefix; a child appends its own financial suffix.

Common upstream payment instructions are replayed; post-fork financial *outcomes* are recomputed. Review actions are reapplied only when their committed provenance, staff identity, MFA, case state and expected-version preconditions remain valid. `analyst-continuation-v1` records inapplicable actions and does not invent an approval. Explicit sandbox actions are distinct authorized interventions and receive separate attribution.

### 7.2 Economic comparison semantics

`counterfactual-cost-v1` compares individual corresponding intents at a common horizon. Fraud **interception** requires an authorized terminal rejection before settlement; pending malicious holds are deferred, not prevented. Settled-and-returned principal and incidental insufficient-funds non-settlement are separate categories. The retrospective fraud truth is used only for measurement, not live R5 scoring or reviewer automation.

| Economic quantity | Derivation |
|---|---|
| Fraud prevented/intercepted | Terminal fraud-control rejection of labeled malicious principal before settlement |
| Paired avoided exposure | Qualifying malicious settlement exposure in source minus surviving qualifying exposure under alternative, with terminal rules applied |
| Legitimate delayed | Distinct labeled legitimate intents completing later or unresolved at the shared horizon |
| False-positive intervention cost | Policy-defined incremental legitimate hold/rejection charge |
| Legitimate delay cost | Whole incremental minutes × 10 cents, max 120 minutes per intent |
| Net financial effect | Incremental avoided malicious exposure minus incremental intervention and delay costs |

The published sample verified policy-plus-sandbox-intervention run produced **$1,500 terminal interception**, **$1,500 additional deferred**, **one additional legitimate delayed intent**, **$2.50 additional intervention cost**, **$1.50 delay cost**, and **+$1,496 net**. This is derived from synthetic executed financial evidence and is not an unconditional ML-saves-$1,496 claim. Cases with negative net effects remain representable.

### 7.3 Process isolation and durable jobs

Normal LiveOps retains its existing financial writer. Replay executes in the separate existing Worker mode:

```text
Hosts.Worker --replay <job-uuid>
```

The dedicated `sands_replay` PostgreSQL login holds only the Replay-specific roles, e.g. `marrow_timeline_revenant_replay`, `revenant_replay_reader`, `revenant_counterfactual_worker`. It cannot issue or redirect a trusted job, mutate source/live money, read unrelated contexts, or publish an unverified child. The job includes tenant/actor/source/child, compatible policies, operation key, fork/horizon, model/evidence/cost identities, verified hashes, result and typed status. The Worker reuses deterministic fork/append operation keys after interruption.

**Marrow storage correction (without engine changes):** `marrow_timeline.context_artifacts` is globally content-addressed. R8's trusted-job Replay needs the source/ancestor context and its authorized new child context, not unrelated contexts. The additive security implementation is documented in `docs/adr/ADR-P1-21-replay-context-isolation.md`:

- `scripts/db/migrations/revenant/0016_counterfactual.sql`: Replay job/result persistence and scoped base authorization.
- `scripts/db/migrations/revenant/0017_replay_context_isolation.sql`: job/tenant-bound RLS and constrained artifact read/child insertion.
- `scripts/db/migrations/revenant/0018_replay_scheduler_context_isolation.sql`: matching isolation for V4 scheduler canonical context copy.
- `scripts/db/migrations/revenant/0019_*`: retain the original Marrow owner-side immutability guard while enforcing Replay restrictions.

The database policy uses provisioned login identity and trusted durable job/history bindings rather than arbitrary caller-supplied session tenant settings. Original canonical context bytes, digests, Marrow reducer and engine APIs, source history contracts, Ghost Lap and the shared scrubber remain unchanged. A tested upgrade from existing histories retained ordinary Marrow/Ghost Lap forks and cold restores.

### 7.4 Actual authenticated Revenant API

Base: **`/revenant/r8`**. Generated OpenAPI/TypeScript contracts live in `web/packages/contracts/openapi/hosts-api.json` and `web/packages/contracts/src/generated/hosts-api.ts`.

| Request | Existing resource and response |
|---|---|
| `GET/POST /runs` | `R8Run[]`; `R8Create` → `R8Run` |
| `GET /runs/{id}`, `POST /runs/{id}/close` | `R8Snapshot` |
| `GET/POST /runs/{id}/cases/{intent}` | `R8Case`; `R8Review` → `R8Case` |
| `GET /runs/{id}/timeline/{position}` | `R8Preview` |
| `GET /runs/{id}/liquidity` | `R8Liquidity` |
| `POST /runs/{id}/counterfactual` | `R8Replay` → `R8Job` |
| `GET /runs/{id}/counterfactual/{job}` | `R8Job` with `R8Verified` only after verification |
| `POST /runs/{id}/counterfactual/{job}/cancel` | `R8Job` |
| `GET /runs/{id}/audit` | `R8Audit` |

Authentication binds actor and tenant on the server. Mutation operations use idempotency keys; case decisions validate expected version and staff authority. HTTP money amounts and Timeline positions remain **decimal strings** for exact cross-runtime representation; UI arithmetic does not round large integer positions into JavaScript `number`.

### 7.5 Existing five-screen UI and shared control

The Revenant app R8 entry is available at `?view=r8` and uses the actual API. **Control Tower** shows six-bank committed flows, balances, cases and evidence. **Held Queue** runs authenticated role/MFA/case review. **Liquidity Fan** displays observed posted/reserved/available bank curves, the uncalibrated R7 eight-point baseline and illustrative selected-bank buffer. **Rewind Mode** uses `@sands/timeline-ui` to seek a verified position, run compatible alternative continuation and show actual child ghost curves and the four computed KPI families. **Audit** displays original/child hashes, provenance, actor actions and verified result identity.

The UI uses normal event/snapshot refresh rather than deriving financial state from animation; counterfactual progress is polled and authoritative snapshots refetched. Pending/failed/cancelled comparisons do not expose Verified KPI cards. The shared package and Ghost Lap reducer remain independent.

---

## 8. Verification and downstream continuity

### 8.1 Verified release boundaries

- R6 real PostgreSQL finance and model-required validation: **94/94** relevant integration checks in its release evidence.
- R7 real published Linux AMD64 advisory Worker: eligible SSA anomaly load/score/replay; eight `Uncalibrated` bank projections; source/child isolation; pinned artifacts.
- R8 final release commit: `a1c7c2473ac05f15005c1f0cba477c90a2c96656` (stacked on R7 commit `fe83c52ca7629d8ce7cb100b59cd8143c2d1ba64`).
- R8 Linux CI: eight passing jobs; 109 API/service tests, 61 Marrow PostgreSQL tests, 96 Revenant PostgreSQL tests, plus one separate R7→R8 upgrade test. Phone Chromium counterfactual journey and tablet coverage passed.
- Focused authorized operations covered MFA/four-eyes, source hash stability, cross-tenant and unrelated-context denial, forged-job denial, retry after interruption, missing-model behavior and exact Timeline position `9007199254740993`.

These are synthetic/runtime compatibility results; they do not constitute a deployed production bank or independently qualified real-world fraud quality. Production migration rollout and provisioned Replay credentials are separate operational deployment matters.

### 8.2 Essential source map

| Concern | Existing reference |
|---|---|
| R7 supported scope | `docs/recovery/r7-option-a-supported-scope.md` |
| R7 Worker evidence | `docs/recovery/r7-r8-advisory-handoff.md` |
| R8 complete integration | `docs/recovery/r8-implementation-handoff.md` |
| R8 Marrow DB access | `docs/adr/ADR-P1-21-replay-context-isolation.md` |
| V4 financial observation | `V4BranchExecution`, `V4ObservationProjector`, `ObservedFinanceReducer` |
| R8 domain and economics | `src/Revenant/Timeline/` and existing Revenant counterfactual/metric modules |
| R8 API | `src/Revenant/Api/RevenantR8Api.cs` |
| R7 advisory Worker | `RevenantAdvisoryWorker` in the existing Worker composition |
| Model artifacts | `src/Revenant/Intelligence/Artifacts/` |
| R8 React application | `web/apps/revenant/src/r8/RevenantR8.tsx` |
| UI reference logic | `web/apps/revenant/src/r8/bufferReference.ts` |
| Shared Timeline UI | `web/packages/timeline-ui` / `@sands/timeline-ui` |
| Generated HTTP contract | `web/packages/contracts/` |
| Versioned SQL | `scripts/db/migrations/revenant/0015_*` through `0019_*` |

### 8.3 Explicit Spec 09 compatibility state

The five original Spec 09 product surfaces are already implemented and tested as an R8 integration. Spec 09 remains the named owner of the experience. The only substantive change from the older projected UI contract is the **liquidity method**: it consumes eight `LiquidityPersistenceBaseline/1` points with `Uncalibrated` uncertainty instead of learned `ForecastBySsa` central paths/bands/advance warnings. Short scenarios may legitimately have no eligible SSA velocity alert, while long histories support the real trained advisory.

The core ambition is unchanged: **real financial ML influence, six-bank operations, lawful review, immutable source, separate verified future, measurable economic consequences, five connected screens and the one Marrow Timeline scrubber shared with Ghost Lap.**
