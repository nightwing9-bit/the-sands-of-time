# R7 final G4 diagnostics

**R7 SCIENTIFIC PARTIAL — EXACT GATE FAILED**

Both forecasting diagnostics missed their predeclared thresholds. The retained G4 histories contain measurable periodic structure, but own-bank training at the specified budget did not make that structure useful enough for this forecast. The matched transfer experiment does not support a common clearing schedule as the next intervention. G5 and independent qualification remain unopened.

The starting R7 head was `4d2ec879b19d85812949d469b638f0187ec8381f`. Commit `47a94b315de6c231f19e8e2a66c9126d62ea768f` preserves the final Opus decision and local comparator edit. The [earlier gate report](r7-g4-development-gate.md) identifies all three retained PostgreSQL roots and reconciles their 48,866 facts, 6,048 seals and 14,211 accepted intents. All diagnostics reused the same manifest, SHA-256 `19b136929aec3e30032995bb7004dbb102693e810ea772007e04073d004e3ce6`. No financial generation, historical migration edits or model promotion occurred.

## T1 and T2

Each own-bank model fit the first 576 completed changes on root 2, then continued from the fitted state at bucket 576 without replaying the training observations. The matched transfer fit the first 576 changes of root 0 BANK-B and consumed the measurement bank's causal prefix under the existing transfer contract. Both used change-series SSA window 24, series length 96, exact rank, horizon eight, opening balance scale and the same origins and target masks. The 2,016-point historical configuration was reproduced separately. All fits were research-only. The separate managed AR(24) control reused the same split.

The median own-bank SSA ratio was **1.109** against a maximum of **0.95**. Pooled active skill was **+1.1%** against a minimum of **+10%**. The pooled own-bank ratio was 1.134. The AR control had a median ratio of 1.470 and pooled active skill of −12.4%. BANK-E met both thresholds individually. Each bank supplied 1,440 origins and 1,439 to 1,432 valid targets per horizon.

| Bank | Dominant period in buckets | Own SSA MAE | Persistence MAE | Trailing median MAE | Matched transfer MAE | Own ratio | Own active skill | Own improvement vs transfer |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| BANK-A | 21.33 | 13,754 | 12,585 | 12,346 | 7,449 | 1.114 | −27.5% | −84.6% |
| BANK-B | 5.94 | 141,093 | 124,717 | 195,214 | 140,413 | 1.131 | −1.7% | −0.5% |
| BANK-C | 3.97 | 153,123 | 110,908 | 182,941 | 133,348 | 1.381 | −5.3% | −14.8% |
| BANK-D | 5.94 | 71,082 | 68,447 | 107,325 | 66,515 | 1.038 | +1.6% | −6.9% |
| BANK-E | 5.94 | 76,385 | 84,630 | 129,983 | 76,383 | 0.903 | +14.7% | −0.003% |
| BANK-F | 21.33 | 20,993 | 19,011 | 19,491 | 14,752 | 1.104 | −21.0% | −42.3% |

MAE is averaged across the eight horizons and shown in USD minor units. Demeaned Hann-window periodograms use the first 576 actual balance changes and report dominant peaks within 3–24 buckets. Root 0 BANK-B's dominant peak was 5.94 buckets. Three measurement banks had a different dominant peak, yet own-bank SSA had **8.6% higher pooled MAE** than matched transfer against a required **25% improvement**. Own fits also lost around the estimated historical cycle phase on every bank. This phase slice uses observed balance activity as a proxy for clearing times. The test does not identify the separate contributions of training length, noise and SSA model capacity.

## T3 warning diagnostic

The original model ZIP was unavailable. I recreated the full-length root 0 BANK-B fit with the original configuration and reused that one fit for central and lower-tail paths on roots 1 and 2. Volatility edges were fixed on root 1 at 1.000 and 1.276 in units of local scale. Normalized signed level residuals were sampled every eight buckets per bank. The 40-sample fallback was unused, with 747–753 low-group, 113 medium-group and 430 high-group calibration observations per horizon. These are separated observations from one calibration root.

| Warning path | Calibration precision | Developing crossings warned on root 2 | False warnings on root 2 | Root 2 precision | Actual developing lead |
| --- | ---: | ---: | ---: | ---: | --- |
| Central | 0% | 0/2 | 3 | 0% | — |
| β = 0.50 | 0% | 0/2 | 3 | 0% | — |
| β = 0.25 | 0% | 1/2 | 3 | 25% | 5 min |
| β = 0.10 | 25% | 2/2 | 3 | 40% | 15 and 20 min |

All paths missed the sudden crossing. Each had 0.0833 false warnings per scored bank-day. Calibration selected no β at the 50% precision threshold. Empirical level coverage across eight horizons was 96.7–97.7% overall and 96.6–97.9% in the high-volatility group, which had 5,104 targets per horizon. Stress-slice coverage was 46.9–78.9%. Mean calibrated widths ranged from 261,695 to 977,904 minor units. The native change-band diagnostic covered 97.7–98.4% overall. Brier scoring is inapplicable because the model supplies no calibrated joint crossing probability.

## Anomaly and qualification

The focused pure gate test covers a silent comparator, an equal-hit comparator and an over-budget detector. The executed G4 remeasurement reproduced **14/23 hits, seven false alerts in 71.26 normal account-days, 66.7% precision and five alerts on 30 legitimate hard negatives**. All deployable operating points used root 1 thresholds. On root 2, fixed rate and trailing rank scored 0/23 with zero false alerts, while seasonal excess scored 2/23 with zero false alerts. The explicitly retrospective comparison gave rank 0 hits and zero false alerts, and seasonal excess 8 hits and six false alerts, each allowed up to the SSA model's seven false alerts. The development anomaly criterion passed. The model remains unqualified on independent roots. The optional negative-binomial candidate was deferred at the scientific stop.

The [24-gate ledger](r7-g4-development-gate.md#gate-reconciliation) remains the qualification record. R7-11 now includes frozen and retrospective comparator evidence. R7-12 still awaits independent qualification. R7-15 and R7-16 now include the matched-budget and own-bank diagnostics, with R7-16 still failed. R7-17 adds group-conditional development coverage. R7-18 remains failed after the warning diagnostic. The other dispositions stand. There are no promoted models or published Worker results from this checkpoint, and R8 has no new model pins.

## Reproduction

Run `Measure_G4_own_bank_and_transfer_AR_control` on the managed test host. On Linux AMD64 run `Measure_G4_SSA_transfer_and_warning_diagnostics`, `Reconcile_and_measure_anomaly_on_generator_4_PostgreSQL_streams` and `Anomaly_gate_compares_event_hits_at_the_allowed_burden`. Set `REVENANT_R7_PILOT_INPUT` to the retained G4 pilot and `REVENANT_R7_DIAGNOSTICS_OUTPUT` to a separate results directory. The native diagnostic used .NET 10.0.15 and ML.NET 5.0.1 in a standalone emulated Linux AMD64 guest. No Docker or PostgreSQL generation was used. Detailed JSON under `TestResults/r7-recovery/final-diagnostics` includes per-bank and per-horizon signed bias, active/stress errors, coverage, training-prefix digests and development ZIP hashes.
