# Spec 08 design — Revenant V2: ML.NET risk, anomaly, liquidity forecast and explanations

## Preconditions, owned implementation and boundaries

**Prerequisites (completed observable exit gates):** spec-05, spec-07. **This spec owns:** `src/Revenant/Intelligence`, feature extraction/config/artifacts, Worker, `tests/Revenant` model evaluation. **Do not implement:** No Python service, online retraining during demos, claim of bank-grade accuracy, explaining local decisions with unqualified global importance.

**Consumes:** Public contracts from spec-05, spec-07; inspect only their exported interfaces, named migrations and relevant tests. **Exposes:** The implemented behavior and stable public contract of this spec for downstream dependents. No spec may rewrite `Marrow.Timeline` public or `web/packages/timeline-ui` contracts without an approved cross-app ADR and dual-client test.

**Architecture authority:** `docs/architecture/01-master.md` defines dependency direction and state authority, `03-contracts.md` defines exact conceptual interfaces. This design defines this spec's *unique implementation ownership*; prefer whichever private class names fit the repo once validated, but preserve semantics. A superior nonbreaking local algorithm is allowed; shared ABI/persistence schema/version/security authority is not a local choice.

## Implementation strategy, contracts and data flow

### Data lineage and four independently testable capabilities

1. **Fraud classification:** public synthetic PaySim dataset only after verified local license/provenance, supplemented by project-produced Friday Ring seed. Build a reproducible ETL with explicit label fields **excluded from predictors**, chronological/group splits protecting leakage by account/identity or scenario; train ML.NET FastTree/LightGBM with pinned model artifact, feature schema manifest, held-out precision, recall, PR-AUC or ROC-AUC and confusion with class prevalence. Do not assume a numeric quality benchmark before actual data. The canonical manifest contains data hash, split algorithm, runtime/package version, trained artifact checksum and model ID. No live demo-time training.
2. **Velocity anomaly:** derive per-account/window count/amount/timestamp features from causal event stream; select supported ML.NET anomaly detector after inspecting current API (SSA or PCA only when semantically applicable to data). Define anomaly score/threshold/false-positive evaluation; avoid inventing a “12× velocity” without computing it. Emit actual score, observed window and baseline into case reasons.
3. **Liquidity SSA:** derive per-bank available/posted balance timeline from holds and settlements, regularize to forecast interval with explicit no-data/missing-data handling; train/backtest SSA on simulated flow; output point plus intervals based on supported implementation method and calibrated residuals if ML.NET forecast intervals are insufficient. Label interval interpretation precisely; scenario may show **~40 min** synthetic warning *only if actual predicted values support it*. Forecast must respond to changed holds in a seeded source vs branch test.
4. **Explanations:** use feature contribution API only if supported for the chosen model and actual per-row value semantics; otherwise return transparent rules/feature evidence (velocity window, beneficiary age, threshold and model score) and clearly label model feature ranking as global if permutation importance. Define `RiskExplanation {riskScore, modelVersion, observedFeatures[], localContributions? [,method], globalImportance?}` and UI must not promote global importance to local cause.

### Replay model pinning

Spec 08 defines canonical normalized `InferenceEvidence` before 8.2 integration, using Spec 07's additive version boundary: pin canonical feature hash, features/time-window basis, model checksum and relevant normalization/runtime identity, with an exact replay-significant score/evidence representation. Execute predictors outside the pure deterministic reducer and durably record evidence before the corresponding history input depends on it; verification requires that evidence. Cold replay consumes the recorded canonical values without rerunning inference or predictor I/O and must reproduce exact Timeline state bytes/hashes. A new branch computes evidence from its **own causal features** using pinned artifacts, then freezes it for replay; never reuse source evidence solely by payment ID. Missing required evidence, tampered/mismatched features or incompatible identities fail explicitly (`IncompatibleVersion` for unavailable required versions/artifacts), never silently default. Numerical tolerances are permitted for ML evaluation only, never Timeline replay integrity. Preserve frozen V1 policies/reducer/hashes; incompatible finalized evidence requires explicit compatible version evolution under 07's plan, not mutation of historical bytes or a new replay engine/service. Fraud metrics calculated from **synthetic truth** and domain settlement consequences defined in 05/09; ML metrics and economic scenario results are distinct.

### Verification and evidence

Train/evaluate offline as a **real code path** and include small documented deterministic test fixture if full PaySim dataset redistribution is legally unavailable; never fabricate computed metric files. Model manifests are portable but if artifacts licensing/size block repo, include reproducible fetching/build instructions and a tiny working fallback fixture that meets functional use. Evaluate on held-out synthetic truth, backtest forecast, test feature leakage, branch source/alternate version reproducibility, stale artifact rejection. Ownership is Revenant only.

## Permissions, concurrency and failure rules

- Every publicly callable operation derives actor/session from verified server auth and uses the domain's ownership/membership/role policy; never trust a submitted principal ID.
- Retry behavior must distinguish idempotent retry from conflicting duplicate payload. A cancellation or unreachable dependency must leave prior durable authority intact and return visible error state.
- Persist state only in the owning module's schema and through frozen contracted ports. No direct cross-domain table mutation, no use of browser-rendered state as authority, no silent change to original source.
- Contract/rules/codec changes are versioned; emit readable incompatibility rather than secretly reinterpret historical data.

## Testing strategy (owned by this spec)

- Dataset label leakage test: truth is never predictor
- Held-out confusion/precision/recall and model checksum report actually generated
- Anomaly window/threshold and per-payment explanation correspond to actual inputs
- SSA output/bands are model computed and change when holds alter balance time series
- Missing model artifact or version mismatch fails closed
- NetArchTest checks GhostLap excludes ML.NET dependencies

## Specific implementation traps

- A syntactically present endpoint or view without real state transitions does not pass.
- Generic framework logic cannot embed money/game rules or clone the shared Timeline engine.
- Public interface changes require updating its producer, typed clients/consumers and corresponding architecture tests in the *same* compatible change, with ADR if consequential.

**Kiro model routing:** Strong for ML semantics/leakage/model pinning; economical for evaluation report formatting.
