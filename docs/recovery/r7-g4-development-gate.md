# R7 G4 executed development gate

**R7 SCIENTIFIC PARTIAL — SPECIFIC MODEL GATE FAILED**

The executed G4 forecast failed the predeclared active-skill, overall-error and developing-pressure warning gates. The anomaly model showed development utility and stayed inside the primary false-alert budget, but exceeded the trailing-rank comparator's false-alert burden at that budget. Both promotion decisions are withheld. The 20-root PostgreSQL qualification, freeze, FinalTest and shifted challenge remain unopened. A change to the forecasting mathematics requires a separate decision.

## Recovery and provenance

The starting branch was `codex/r7-ssa-anomaly-liquidity-20261113` at `e305a795e0aee8bc7969a9a1253ebc00ac2e7e86`, on stacked base `320681cdc5f06b8f2f02e74adf9b49e23c904fa7` with R6 implementation ancestor `4ad02158b5b626738bc7ae88d7ffa4dee099b5d5`. Commit `949a022db1b8807f28608432b72aa3cc51978ad7` preserves the 28-file handoff, including G4, shared inference, evidence /2, migration 0014, tests and the original scientific notes. The private log is untracked. Historical migrations through 0013, R5 model assets, R6 financial code and the retained G3 pilot were preserved.

The original PostgreSQL test completed in 50m32s and removed its disposable database. Its terminal log records one pass. A generation TRX was unavailable. Projection and export took 1.56s, 0.92s and 0.50s per root. The run used .NET 10.0.15 on osx-arm64 for finance generation. The manifest has SHA-256 `19b136929aec3e30032995bb7004dbb102693e810ea772007e04073d004e3ce6` and records the revision as `local-r7-recovery`. The preserved G4 source file has SHA-256 `065bcaf06e586109a483ff17a98680958f691745dba84050d6e4e9c68349e800`. All three current plan behavior hashes, opening hashes, labels and account rhythms matched the export. The retained streams are development evidence because the original build lacks an exact revision pin.

| Pilot root key prefix | Seals | Accepted / attempted | Facts through final seal | Settlement facts | Posted settlements | Holds / releases | Return facts |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| eafc18ee2bb318b6 | 2,016 | 4,423 / 4,423 | 15,354 | 4,423 | 4,423 | 7 / 7 | 62 |
| b628ba72f37514717 | 2,016 | 4,761 / 4,761 | 16,365 | 4,761 | 4,761 | 7 / 7 | 59 |
| 2a519d1b80ffa2cb | 2,016 | 5,027 / 5,027 | 17,147 | 5,016 | 5,013 | 18 / 7 | 54 |

The exporter reconciled projected counts against committed intent facts. The independent reader checked all 111 file digests, compared all 145,152 account-bucket counts against freshly planned counts, and checked the six bank streams and eight horizon masks on each root. Target decoding verified posted minus reserved and forecast evaluation used those decoded targets. The original database teardown prevents a new query of raw committed intent identities and reservation journals. The retained summaries include reservation and release amounts. Peak memory in the original manifest was zero and is unavailable as a measurement.

## Anomaly development

The first code candidate is `twoday-count-poisson-w6`, a six-bucket, 30-minute statistic. The `w3` candidate uses three buckets and is a separate ablation. Fitted `DetectSpikeBySsa` uses the first 1,160 raw counts of the strongest daily-rhythm training account, seasonal window 576, native diagnostic history 32 and nonadaptive positive spikes. A perturbation of the current count changed its inferred expectation by 0.00861 in the inherited prediction-engine adapter. ML.NET updates the state before the getter on this path, so the shared adapter now recovers the next expectation as input minus the signed raw score and carries it forward one bucket. The anomaly manifest binds this expectation rule. Native parity verifies the current-count perturbation leaves its own expectation unchanged. The six rolling expectations can incorporate observations from earlier buckets within the window. Each expectation is floored at 0.02 and the score is minus log10 of the Poisson upper tail for the six-bucket count. A nonzero current count is required to alert. Eligibility requires eight nonzero counts in 1,152 warmup buckets. Thresholds are calibrated at 75% of the requested event budget with the existing six-bucket coalescing and three-bucket matching extension. [ML.NET 5.0.1 anomaly score](https://github.com/dotnet/machinelearning/blob/v5.0.1/src/Microsoft.ML.TimeSeries/SsaAnomalyDetectionBase.cs#L303-L315), [prediction-state update](https://github.com/dotnet/machinelearning/blob/v5.0.1/src/Microsoft.ML.TimeSeries/SequentialTransformerBase.cs#L128-L167)

Root 0 trained, root 1 calibrated, root 2 measured. At budget 0.1 the calibrated score threshold was 3.4120270786. Root 1 detected 23/31 events with five false alerts over 70.71 normal account-days. Root 2 detected 14/23 events with seven false alerts over 71.26 normal account-days, 60.9% recall, 66.7% precision and 0.0982 false alerts/account-day. Median delay was 10 minutes with range 0–20. Five of 30 legitimate hard negatives alerted. Mechanism hits were off-schedule 5/9, amplitude surge 2/2, sustained elevation 5/8, dormant burst 2/3 and TreasuryDrain 0/1. Fixed historical rate with the same Poisson score and trailing rank each detected 0/23 with zero false alerts. Seasonal excess detected 2/23 with zero false alerts. The 0.05 sensitivity budget yielded 14/23 with four false alerts, and the 0.2 budget yielded 14/23 with seven. The earlier 94 positives and 212.9 normal days came from plan-only roots 3–5. The retained pilot executed roots 0–2, so those denominators cannot be substituted.

Commercial accounts produced five false alerts in 14.92 normal days. Normal-bucket Pearson dispersion ranged from 0.50 to 1.16 by activity cohort and the observed-to-expected count ratio from 0.37 to 0.74. Commercial lag-one residual correlation was 0.59. Normal-window incidence of score ≥ 2 varied from 0% to 1.07%. These are empirical diagnostics for a Poisson-tail surprise score. A separate regime-change estimate and independent-root uncertainty require additional executed measurement roots.

## Forecast development

Root 0 trained, root 1 selected and calibrated, root 2 measured. The primary `ForecastBySsa` fit used BANK-B's contiguous 2,016 five-minute changes, window 24, series length 96, exact rank and 288 warmup buckets. Inference accumulates predicted normalized changes from the available balance at the origin. The scale is opening balance divided by 1,000. Conformal intervals use horizon-wise level residuals divided by the mean absolute change over the last 12 intervals, floored at the model scale. The report retains interval widths, eight calibration multipliers and the hashes for each ephemeral model ZIP. Repeated fitting runs had different ZIP hashes and reproduced the reported metrics. No ZIP or promotion manifest was published.

| Minutes | SSA MAE | Persistence MAE | Signed bias | Active MAE | Developing MAE | Sudden MAE | Coverage | Stress coverage |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 5 | 36,477 | 23,904 | +317 | 115,244 | 635,900 | 1,813,452 | 95.9% | 73.7% |
| 10 | 69,941 | 45,687 | +635 | 143,179 | 1,226,085 | 3,440,559 | 97.1% | 63.3% |
| 15 | 99,828 | 64,478 | +953 | 178,097 | 1,777,039 | 4,903,923 | 97.3% | 59.4% |
| 20 | 117,478 | 70,797 | +1,272 | 221,655 | 2,297,118 | 6,250,733 | 97.5% | 53.8% |
| 25 | 135,826 | 77,569 | +1,590 | 266,082 | 2,802,430 | 7,790,680 | 97.8% | 49.5% |
| 30 | 154,618 | 85,205 | +1,908 | 304,979 | 3,286,662 | 9,219,156 | 97.8% | 42.7% |
| 35 | 180,466 | 100,637 | +2,227 | 364,229 | 3,737,795 | 10,519,313 | 97.3% | 40.7% |
| 40 | 203,682 | 116,861 | +2,546 | 396,569 | 4,178,194 | 11,193,514 | 96.9% | 37.9% |

Amounts are USD minor units. There were 10,368 eligible origins across six banks, with 10,362 valid +5-minute targets and 10,320 valid +40-minute targets. These overlapping origins are from one measurement root. The primary candidate's active skill was −39.8% and its overall ratio to the stronger baseline was 1.706. The required values were at least +5% and at most 1.05. It missed both developing crossings and the sudden crossing. All three issued warnings were false, so there was no lead-time measurement. The 288-observation ablation scored +6.6% active skill and a 1.032 overall ratio on root 2, but its only warning was false and it missed both developing crossings. Both retained linear references also lost to persistence. The calm-bank control picked the same BANK-B as the max-variance rule, so it adds no independent evidence. The G4 level w24 control scored −232.2% active skill and a 5.929 overall ratio. Earlier G3 results remain archived separately.

## Gate reconciliation

| Gate | Status | Evidence |
| --- | --- | --- |
| R7-01 | VERIFIED PASS | R6 ancestry and historical paths checked. |
| R7-02 | VERIFIED PASS | Retained R4/R5/R6 quantitative census in r7-historical-series-census.json. |
| R7-03 | VERIFIED PASS | Executed account and bank series reconciled on three roots. |
| R7-04 | VERIFIED PASS | Canonical seal identities, aligned authoritative targets and explicit end masks verified. |
| R7-05 | PARTIAL | Native fixture artifact transfer to independent pilot subjects. Promotion-level transfer remains open. |
| R7-06 | PARTIAL | Shared native prefix parity and future perturbation checks. Actual branch Worker verification remains open. |
| R7-07 | VERIFIED PASS | G4 versioned and executed on three PostgreSQL roots. |
| R7-08 | PARTIAL | Disjoint root matrix tested. Independent PostgreSQL qualification cohorts remain sealed. |
| R7-09 | VERIFIED PASS | Real native fit and Poisson-tail scoring on executed streams. |
| R7-10 | PARTIAL | Causal eligibility, unavailable path and independence from a perturbed leading prefix verified at the configured warmup for the primary candidates on pilot fixtures. Wider model-state characterization remains open. |
| R7-11 | VERIFIED PASS | Executed event metrics, hard negatives and three comparators reported for development. |
| R7-12 | BLOCKED | One development measurement root, comparator false-alert condition unmet, independent evaluation unopened. |
| R7-13 | VERIFIED PASS | Real native horizon-eight fit on committed available balances. |
| R7-14 | PARTIAL | Shared transform arithmetic passed. Original pilot exported zero measured round-trip error for its legacy level normalization. |
| R7-15 | VERIFIED PASS | Identical-origin persistence and trailing-median comparisons. |
| R7-16 | FAILED | Actual rolling-origin error and active/stress skill missed the development gate. |
| R7-17 | PARTIAL | Development level coverage measured at all horizons. Independent calibration and stress coverage remain open. |
| R7-18 | FAILED | Frozen 10% buffer policy measured. Zero of two developing crossings warned. |
| R7-19 | NOT STARTED | Shifted challenge unopened. |
| R7-20 | PARTIAL | Development artifact identities recorded. Immutable promotion artifacts withheld. |
| R7-21 | BLOCKED | Published Worker gate requires promoted artifacts. |
| R7-22 | PARTIAL | PostgreSQL storage and idempotency and in-process corrupt artifact path exercised. Published cold-process evidence remains open. |
| R7-23 | PARTIAL | Native shared-inference inherited-prefix equality and divergent synthetic reservation path. Real independent fork evidence remains open. |
| R7-24 | PARTIAL | Targeted local native and PostgreSQL TRX evidence. Full qualification CI, R1–R6 closure and R8 promotion handoff remain open. |

R8 can inspect the shared causal inference and evidence contracts. There are no promoted R7 anomaly or forecast artifacts for it to consume.

## Reproduction

Local outputs are under `TestResults/r7-recovery/continuation`, including the handoff log, architecture TRX (11/11), native development TRX (21/21), and reconciled forecast/anomaly and PostgreSQL TRX (17/17). The native lab used Linux AMD64, .NET 10.0.15 and ML.NET 5.0.1. The native parity test calls the Worker's shared source directly and reloads pinned fixture ZIPs in process. It does not substitute for the separately gated published Worker.

```sh
bash scripts/checks/r7-native-lab.sh \
  'FullyQualifiedName~Reconcile_and_measure_anomaly_on_generator_4_PostgreSQL_streams|FullyQualifiedName~Develop_and_measure_forecast_candidates_on_generator_4_PostgreSQL_streams|Category=R7Parity' \
  REVENANT_R7_PILOT_INPUT=/repo/TestResults/r7-recovery/g4-pilot \
  REVENANT_R7_DIAGNOSTICS_OUTPUT=/repo/TestResults/r7-recovery/continuation
```
