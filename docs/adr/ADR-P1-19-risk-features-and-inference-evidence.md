# ADR-P1-19 — `risk-features-v1`, canonical feature snapshot and `InferenceEvidenceV1`

- **Status:** Accepted for implementation, Spec 08 task 8.1 (2026-11-11). `InferenceEvidenceV1` has not been persisted or consumed by any reducer yet; Task 8.3 may amend it before its first persistent use and must do so through a new `FormatVersion`.
- **Scope:** `src/Revenant/Intelligence/*` (pure types and ETL), `tests/Revenant/{Intelligence,Platform}`, `scripts/checks/ml-platform-probe.sh`, CI job `ml-platform-x64`. No change to `Marrow.*`, `Revenant.Contracts`, finance V1/V2 reducers, codecs, migrations or the OpenAPI document.
- **Related:** ADR-P1-17 (finance versions; learned evidence may need `/3`), ADR-P1-07 (version-pinned ML evidence), Spec 08 `design.md` sections 1A, 2 and 3.

## Decisions

1. **One pure feature builder.** `RiskFeaturesV1.Build(currency, amountMinor, sourcePostedBeforeMinor)` is the only path to a vector. Offline ETL and the live Worker both call it. The input is three integers; no overload accepts a label, a post-state balance, a timestamp, a row position or an identifier.
2. **Frozen vector, in this order:** `logAmount = ln(1 + amountMinor/100)`, `logSourcePostedBefore = ln(1 + postedBeforeMinor/100)`, `transferFraction = min(10, amountMinor / max(postedBeforeMinor, 100))`, `wouldDrainPosted = 1` iff `postedBeforeMinor > 0 && amountMinor >= postedBeforeMinor`. Applicable to **USD only**; any other currency, a non-positive amount, a negative posted balance, or a value above 2^53 minor units is a typed `RiskFeatureError`, never clamped.
3. **Canonical raw snapshot.** `CanonicalWriter(schema id "risk-features-v1", currency, amountMinor, sourcePostedBeforeMinor)`; the feature hash is SHA-256 of those bytes, lower-case hex. Floats never enter a hash or a decision. `Math.Log` may differ in the last double bit between CPU architectures, which is why the integers and the quantised PPM score are the only replay inputs.
4. **Synthetic amount convention.** One PaySim nominal unit is one simulated USD major unit, i.e. 100 minor units. Decimal parse (invariant culture, no exponents or separators), round half away from zero, overflow and malformed text rejected. This is a modelling choice for shared magnitude features, not a currency conversion, and only PaySim `TRANSFER` rows are used.
5. **As-of identity.** The live adapter supplies the committed **posted** balance (reserves not subtracted) of the source account *before this screening decision* as a `SourceBalanceObservation(PostedMinor, Moment, AsOfFactSeq)` with a `ScreeningBoundary(DecisionBoundaryFactSeq)`. `Moment = AfterPayment` is `BalanceNotPreScreening`; `AsOfFactSeq > boundary` is `BalanceAfterDecisionBoundary`. Run id and intent id live in the evidence, not in the vector, so a PaySim row and a live row with the same pre-screening facts have identical vectors and hashes.
6. **Allow-list.** The model input columns are exactly the four names above, in order (`ValidateModelInputColumns`). `isFraud`, `isFlaggedFraud`, `newbalanceOrig/Dest`, `oldbalanceDest`, names, `step`, `type`, run/intent/account ids, truth and the v7 rule flags are listed as forbidden and would also fail the exact-list check.
7. **ETL.** `RiskDatasetEtl` streams the source three times (hash, scan, emit), so memory is O(distinct steps + distinct source accounts/scenarios). Row problems are counted, each row exactly once. Splits are chronological over the observed distinct steps (60/20/20, rounded half away from zero) with a purge rule: a source account (and `scenarioId` when present) belongs to the cohort of its earliest step and its later-cohort rows are dropped and counted. A split without both classes makes `Run` throw (default) and sets `MetricsEligible=false`. The emitted examples file holds only split, ordinal, step, label, currency, amount and posted balance. The manifest has no timestamp, so equal input gives an equal `ManifestSha256`.
8. **Two honest cohorts.** Full PaySim is optional and offline (not acquired here, see the log). `RiskFixture` is the mandatory seeded, PaySim-shaped, labeled fallback. Its limitations travel in the manifest; it is independent of the Friday Ring seeds and must not be reported as a Friday Ring evaluation.

## `InferenceEvidenceV1` (specified now, first used by Task 8.3)

Fields, in canonical order (`FormatVersion = 1`, `CanonicalWriter`): format, run id, intent id, as-of fact seq, screening fact seq (must equal as-of + 1), feature schema id, currency, amount minor, posted-before minor, feature hash, model id, model artifact SHA-256, model manifest SHA-256, ML.NET package version, normalization id (`probability-ppm/1`), policy id (for example `v7-base+ml-risk-v1`), threshold PPM (1..1,000,000), score PPM (0..1,000,000), base decision, learned-hold flag, final decision. `Decode` is strict: unknown format, truncation, trailing bytes, a stored feature hash that does not match the stored integers, and a stored hold flag or final decision that disagrees with the rule all fail with a typed `EvidenceError`.

- **PPM normalization.** `RiskScoreNormalization.FromProbability(float)`: reject non-finite and values outside `[0,1]` (no clamping); compute `(double)p * 1_000_000` (exact, because a 24-bit mantissa times a 20-bit integer fits in 53 bits) and round half away from zero. `0.0078125f` (exactly 7812.5) gives 7813.
- **Threshold tie.** `scorePpm >= thresholdPpm` is a learned hold.
- **Decision rule.** Base `Rejected` stays `Rejected`; base `Held` stays `Held`; base `Clear` becomes `Held` iff the learned hold holds. A score never releases, approves or posts.
- **Combined screening input boundary.** The V3 input is one ordered pair: the unchanged `PaymentIntent` canonical bytes plus these evidence bytes. The reducer applies the screening transition only when both exist and `CheckBinds(intent)` holds (run, intent id, currency, amount). Evidence is produced before the combined fact is committed and outside the reducer; `Apply` runs no model, no I/O and no floating-point scoring. Reproducing a score on replay is not required; replay compares the stored integers.
- **Not implemented here (owner Task 8.3):** the V3 reducer, its version key, persistence, the Worker adapter, revalidation under the ledger locks, and the combined input codec.

## Platform contract (Spec 08 R08.13), as established in 8.1

- Package family: `Microsoft.ML`, `Microsoft.ML.FastTree`, `Microsoft.ML.TimeSeries` **5.0.1** (stable, MIT; TimeSeries pulls `Microsoft.ML.Mkl.Redist` 5.0.1). Currently referenced by `tests/Revenant` only.
- `Microsoft.ML.Mkl.Redist` ships `libMklImports.so` but **not** its dependency `libiomp5.so`. On Ubuntu 24.04 the smallest package that provides that file name is `libomp-dev`. Any Linux x64 image that runs SSA (the 8.4 Worker included) needs it; the probe installs it in its throwaway container and CI installs it on the runner.
- The first SSA detector fed fewer than `trainingWindowSize` observations after loading did not alert on an obvious spike; with 40 calm observations first it did. Task 8.4 must model this as warm-up (`InsufficientHistory`), not as a detector quirk.
- Command: `scripts/checks/ml-platform-probe.sh --native | --docker-amd64 | --ci`. A mode passes only when the expected number of probe tests executed and passed (TRX counters), so a skipped or filtered-out SSA test cannot be reported as success.

## Compatibility check (performed)

Added types and files only. `Revenant.Contracts`, `Marrow.*`, `FinanceReducer`, `FinanceLedger*`, all codecs, migrations and OpenAPI are untouched (`git diff` empty for them). The default fast test filters now exclude `Category=MlSsaX64`, which is run by the dedicated x64 job.

## Alternatives rejected

- *Hash the float vector:* last-bit differences between CPU architectures would make equal facts hash differently.
- *Put run and intent ids in the vector:* would make a PaySim row and a live row diverge and invite identifier leakage.
- *Clamp out-of-range probabilities or balances:* hides a model or ledger defect behind a plausible number.
- *Add ML.NET to `Revenant.Domain` now:* nothing in 8.1 needs it; 8.2 adds it with the trainer.
