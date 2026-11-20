# Spec 08 requirements — Revenant intelligence, model-aware finance, advisory liquidity and verified replay integration

**Basket:** 3  
**Implementation baseline:** completed R1–R8 delivery checkpoints, current through 14 November 2026  
**Dependencies:** Specs 05 and 07 finance, ledger, human-review and Marrow Timeline contracts  
**Primary owner:** Revenant Intelligence, its versioned model evidence and the model-aware financial adapter  
**Downstream owner:** Spec 09 owns the five-screen Revenant experience and full alternate-policy replay application. The completed R8 integration is recorded here as a dependency/interface checkpoint, not as a second implementation of the shared engine.

## Outcome and delivered scope

Revenant combines genuine trained ML.NET evidence with financially authoritative, causally replayable synthetic banking. A pinned FastTree classifier participates in ModelRequired Screening and can create a real hold/case. A separately trained ML.NET SSA–Poisson detector analyzes outgoing-account velocity and publishes advisory evidence. Eight five-minute bank liquidity projections are supplied by the explicitly versioned **statistical persistence baseline**, not by a learned SSA forecaster. Recorded evidence, explanations and financial facts feed the V4 Marrow history and the existing Spec 09 consumer.

**Completed operational objective:** A six-bank synthetic payment run, audited model-required holds, human review/settlement, source/child financial lineage, supported advisory evidence, and a real counterfactual product with the four economic KPI families and five connected screens. The financial and user-experience scope is unchanged by the liquidity-method substitution.

**Explicit model-scope amendment:** The earlier three-*learned*-workload expectation was resolved as **two genuine trained workloads plus an eight-horizon statistical liquidity projection**. `ForecastBySsa` research remains preserved but is not a supported active producer; learned interval bands and predictive shortfall warnings are not emitted. This document specifies the completed, versioned product baseline rather than changing historical research artifacts.

**Excluded:** new Marrow engine, new ledger, new generic Timeline UI, unpinned live scoring, online training, inference from hidden evaluation truth, unrestricted Replay ledger writes, unsupported learned forecast claims, unlicensed PaySim metrics, real-world fraud-accuracy claims, new Ghost Lap ML dependencies, and reclassification of legacy evidence.

## EARS-style requirements and verified acceptance contracts

## EARS-style requirements and verified acceptance contracts

### R08.01 — Reproducible provenance and lawful data

**WHEN** a model or observation is trained, loaded or produced, **THE SYSTEM SHALL** bind the model/series to a versioned canonical input, producer, model/policy/manifest identity, runtime family and checksum. Supported ML.NET `5.0.1` inference and its native TimeSeries dependencies execute on the proven Linux `amd64` runtime; macOS ARM64 is not represented as an independently qualified SSA runtime. Project-generated labeled synthetic observations and evaluation sidecars are not presented as PaySim or live-bank data.

**Evidence:** R1 native train–save/load–predict path; R4 canonical longitudinal finance histories; R5 and R7 pinned artifact ZIPs, manifests, deterministic reloading and Linux CI.

### R08.02 — Model-driven authoritative Screening

**WHEN** a payment enters the V4 ModelRequired Screening path, **THE SYSTEM SHALL** build `risk-features-v2` from the authoritative pre-screening account state, use the pinned trained R5 FastTree model, and record `v4-model-screening-evidence/1` in the ordered financial fact. The `v4-model-review-capacity/1` policy uses the inclusive `852469` ppm learned-risk threshold while preserving its compatible `policy-v7`/`policy-v8` hard controls. A learned hold creates a real reserved balance and case; it never directly settles, rejects or confiscates principal.

**WHEN** required model inference or artifact validation fails, **THE SYSTEM SHALL** fail closed without silently choosing an unpinned or rules-only replacement. Committed screening evidence is reused in predictor-free historical replay.

### R08.03 — Authentic outgoing-velocity anomaly signal

**WHEN** an account has eligible completed history, **THE SYSTEM SHALL** apply the real loaded ML.NET `DetectSpikeBySsa` model for learned preceding-bucket expected counts, calculate the six-bucket Poisson upper-tail surprise, and persist versioned `anomaly-evidence/2` under `R7-AnomalyGlobalPolicy/1`. The pinned inclusive score threshold is `3.4120270786372`. An account needs at least `1,152` warmup buckets and eight nonzero warmup observations; otherwise the result is typed `InsufficientHistory` without a fabricated alert.

**The anomaly is advisory:** it does not independently change money, Screening, case decisions or reviewer authority. An actual alert and its evaluation quality must not be inferred from a short demo run lacking warmup.

### R08.04 — Causal bank liquidity projection

**WHEN** a completed bank seal is projected, **THE SYSTEM SHALL** read the authoritative USD minor-unit bank available balance `Posted − Reserved` and emit eight points at +5, +10, +15, +20, +25, +30, +35 and +40 simulated minutes, each equal to that completed-origin balance. The producer is `LiquidityPersistenceBaseline/1`; evidence is `liquidity-projection-evidence/1`; uncertainty is typed `Uncalibrated`.

**THE SYSTEM SHALL NOT** publish this as a learned SSA forecast, a model interval, a guaranteed shortfall warning or financial posting. Historical `liquidity-forecast-evidence/1` and `/2` retain their old producer meanings and cannot select the active runtime.

### R08.05 — Grounded explanations and authorized reasons

**WHEN** a staff-authorized explanation is requested, **THE SYSTEM SHALL** present the recorded R5 score and threshold, actual causal feature observations, applicable v7/v8 rules, final financial decision, evidence/policy references and any eligible R7 advisory context. **THE SYSTEM SHALL NOT** show retrospective synthetic truth as a live decision input, present unsupported per-payment feature contributions or leak private review text.

### R08.06 — Compatible causal branches and verified source

**WHEN** a compatible alternative is selected, **THE SYSTEM SHALL** vary versioned `policy-v7` versus `policy-v8` base rules within the same V4 family while preserving R5 evidence, sanctions and human-review authority. The alternative does not gain the ability to rewrite source facts. Source decisions are not copied as alternative outcomes; the child recomputes lawful post-fork consequences from common upstream instructions and permitted recorded actions.

**WHEN** a source history is forked, **THE SYSTEM SHALL** keep the registered V4 reducer/input/state/context family and immutable prefix; `ObservedFinanceReducer`/`V4ObservationProjector` reconstruct branch-causal state, and historical V1/V2/V3 meanings remain frozen. Inapplicable source-review actions are not silently copied as valid child actions.

### R08.07 — Measured scientific evaluation and limitations

**WHEN** a model or product claim is published, **THE SYSTEM SHALL** distinguish held-out synthetic fraud metrics, anomaly development measurements, actual financial outcomes and production-independent performance. The R5 held-out synthetic classifier achieved ROC-AUC approximately `0.838` and average precision approximately `0.635`. R7 G4 account-anomaly development measured `14/23` events, `7` false alerts over `71.26` normal account-days, and a `10`-minute median delay; independent quality qualification remains a separate status. The unsupported bank SSA research cannot be represented as a currently serving model.

### R08.08 — Explicit missing-artifact and replay behavior

**WHEN** a pinned model or required series is missing, corrupt, ineligible or incompatible, **THE SYSTEM SHALL** return the applicable typed failure without successful evidence bytes. R5 ModelRequired failure prevents a new Screening decision; R7 advisory unavailability does not turn into a Screening authority or invented bank forecast. **WHEN** existing recorded financial evidence is replayed cold, **THE SYSTEM SHALL** reproduce its legal decisions without calling the scorer.

### R08.09 — Cross-domain exclusion and shared Marrow integrity

**WHEN** dependency and compatibility tests execute, **THE SYSTEM SHALL** preserve the canonical Marrow engine, hash/codec/history behavior and the compiled shared Timeline UI. Ghost Lap SHALL remain free of Revenant Intelligence/ML.NET dependencies. A Replay-specific database permission migration may restrict the dedicated Replay credential without changing ordinary Marrow/Ghost Lap semantics.

### R08.10 — Identical causal training/serving features and observation boundaries

**WHEN** a completed five-minute boundary exists, **THE SYSTEM SHALL** construct account outgoing-count and bank available-balance observations from committed, causally visible finance facts. A no-event bucket is not silently equated to an unobserved interval. The observation identity records tenant, physical execution, root lineage, source/child branch, completed boundary, fact sequence/prefix digest and series hash as applicable. A child inherits the verified prefix and uses only its own suffix after the fork.

**WHEN** the trained FastTree model scores a payment, **THE SYSTEM SHALL** use the pinned `risk-features-v2` pre-screening builder and the exact versioned feature normalization and as-of fact inputs used for its published artifact. Unknown/future truth, later settlement, untrusted customer IDs or post-state balance SHALL NOT enter inference; the financial transaction SHALL validate that its inputs have not changed before committing evidence.

### R08.11 — Atomic screening/evidence, ordering and retry safety

**WHEN** Screening commits, **THE SYSTEM SHALL** bind the intent, causal pre-state, score, base/learned decision, hold/case, ordered finance fact, outbox and idempotency within the existing authorized PostgreSQL transaction. **WHEN** a V4 history is replayed or forked, **THE SYSTEM SHALL** use the compatible finance reducer/input/state/context family, not reinterpret old V1/V2/V3 records as V4.

**Registered active V4 family:** `revenant-finance-reducer/4`, `revenant-finance-fact/4`, `revenant-finance-state/4`, `revenant-finance-context/4`.

**WHEN** a held case is reviewed, **THE SYSTEM SHALL** retain the existing authorized staff/MFA, expected-version, case state and four-eyes protections. **WHEN** a ledger commit or Replay append is retried, **THE SYSTEM SHALL** enforce same-operation idempotency and refuse conflicting evidence.

### R08.12 — Bounded training, short-history and truthful unavailability

**WHEN** external research corpora are absent, **THE SYSTEM SHALL** use the already versioned project-generated synthetic corpus and independently recorded synthetic metrics; it SHALL NOT invent PaySim results, retrain full models on each request, or require network access to run normal CI. **WHEN** the source/account history is too short, **THE SYSTEM SHALL** produce typed `InsufficientHistory` for advisory inference rather than supplying synthetic score or warning. A short Friday Ring display remains functional with real balances and R5 decisions.

### R08.13 — Executable ML.NET Linux AMD64 platform compatibility

**WHEN** native ML.NET SSA anomaly code is built, loaded, scored or validated, **THE SYSTEM SHALL** use the proven native Linux `amd64`/glibc runtime and executed `DetectSpikeBySsa` fit/save/load/predict checks. The existing Worker publish and CI actually load the pinned artifact; tests are not recorded as passing merely because an unsupported environment skipped them. Native macOS ARM64 or a successful container startup is not a substitute for the Linux SSA proof. Historical `ForecastBySsa` probe/research can remain archived; current runtime qualification does not require promoting that failed forecaster.

## Completed downstream Spec 09 integration contract (R8 checkpoint)

**Authenticated app and API.** The existing `/revenant/r8` endpoints provide scenario runs, cases/review, Timeline preview, liquidity, counterfactual job submission/status/cancel and audit with exact decimal-string money and Timeline coordinates. The five connected screens are Control Tower, Held Queue, Liquidity Fan, Rewind Mode and Audit; Rewind uses the same compiled `@sands/timeline-ui` package used by Ghost Lap.

**Replay and human continuation.** Compatible `policy-v7`/`policy-v8` choices are both within V4 and retain the published model and hard controls. Authorized staff review preserves role/MFA/four-eyes/version semantics. `analyst-continuation-v1` reapplies valid recorded actions, distinguishes stale/inapplicable actions, and attributes new authorized sandbox interventions separately. The original source remains immutable while the child has its own lawful financial suffix.

**Economic impact.** The four visible KPI families are fraud intercepted/prevented, legitimate payments delayed, false-positive cost and signed net financial effect, with actual paired-intent lineage. Separate deferred exposure, return/recovery and indirect insufficient-funds outcomes remain accessible. `counterfactual-cost-v1` charges 250 cents per additional legitimate intervention plus 10 cents per incremental whole minute of delay capped at 120 minutes per intent. Source/alternative evidence and an immutable result digest must reconcile before successful display.

**Replay storage security.** The Replay login cannot mutate live ledger or source Timeline facts and cannot read unrelated `marrow_timeline.context_artifacts` or the V4 scheduler's copied contexts by creating/redirecting jobs or changing tenant/role settings. The job-bound PostgreSQL grants/RLS and forward migrations `0016`–`0019` retain ordinary Marrow/Ghost Lap behavior. See `docs/adr/ADR-P1-21-replay-context-isolation.md`.

**Liquidity display.** The eight active persistence horizons are shown as an uncalibrated statistical baseline; the separate `R8-SyntheticBufferReference/1` marker is illustrative (10% of valid verified opening bank available balance), not a financial policy or trained warning.

**Release validation.** The final R8 branch CI reported eight passing jobs, 109 API/service tests, 61 Marrow PostgreSQL tests, 96 Revenant PostgreSQL tests and a separate R7→R8 upgrade test; browser phone/tablet and exact shared Timeline integration were included in targeted acceptance.

## Required fixed identity references

| Capability | Versioned identity | Role |
|---|---|---|
| R5 trained fraud | `revenant-behavioral-risk-v2/d1801a3f72f9d4525cd39b8b7873e2fc43288dea0f7c1f5ee8646612833a28e4`; ZIP `56bca32c36dd1911d163a6f92369da43382e1937d875228339f15f8608519c7c` | Financially authoritative ModelRequired Screening |
| R5 policy/evidence | `v4-model-review-capacity/1`; `v4-model-screening-evidence/1`; inclusive `852469` ppm | Recorded canonical screening fact |
| R7 trained anomaly | `revenant-ssa-anomaly/672a05a26995653585e5ba9505807df7a2f707914886634c39b133c7dbc72980`; ZIP `03e895da4fa4348b69b1251c6ef9c1c9e6c2deee893e83bf2a983c698281b35e` | Advisory-only native SSA–Poisson |
| R7 anomaly policy/evidence | `R7-AnomalyGlobalPolicy/1`; `anomaly-evidence/2` | Causal account velocity |
| Liquidity | `LiquidityPersistenceBaseline/1`; `liquidity-projection-evidence/1` | Statistical, eight horizons, `Uncalibrated` |
| R8 replay/economics | `analyst-continuation-v1`; `counterfactual-cost-v1` | Lawful branch action continuation and comparison |
| R8 presentation marker | `R8-SyntheticBufferReference/1` | Illustrative opening-balance reference only |

## Exit status and downstream boundary

The completed R1–R8 implementation satisfies the above **amended** operating scope, including model-required fraud, trained anomaly, eight-step statistical liquidity, verified branch economics and connected Spec 09 consumption. It **does not** assert the original third learned SSA bank forecaster or validated interval/40-minute warning. The full Spec 09 implementation remains the owner of its UI, replay job semantics and human-action experience; this specification records the consumed contracts and their completed integration.

**Release references:** `docs/recovery/r7-option-a-supported-scope.md`; `docs/recovery/r7-r8-advisory-handoff.md`; `docs/recovery/r8-implementation-handoff.md`; `docs/adr/ADR-P1-21-replay-context-isolation.md`; R7 commit `fe83c52ca7629d8ce7cb100b59cd8143c2d1ba64`; R8 commit `a1c7c2473ac05f15005c1f0cba477c90a2c96656`.
