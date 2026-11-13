# R7 anomaly and forecast evaluation

The first native gate is `Frozen_SSA_models_transfer_across_independent_committed_histories` in the existing integration workflow. It trains the two starter models on committed six-bank V4 history and attempts transfer to two independent roots with distinct balances, activity rhythms, known spike examples and a causal change in normal activity. It records alert burden and spike detection against a trailing 48-bucket empirical p95 rule, eight-horizon MAE against persistence, observed coverage of nominal bands, normalization error, and a digest of exact rolling SSA output bits. Forecast-only reads at each origin are checked against the corresponding observation update and against a repeat read. Fresh loads are compared across ordering and cold reconstruction.

A verified Marrow fork uses the source prefix through its completed seal. A legal v8 hold of 175,000 USD minor units in the child tests availability and forecast divergence at the next seal. Expected evidence is the named passing TRX, the two experimental ZIPs with SHA-256, and `transfer.json`. The initial PR opening triggers the gate. An edited PR with `R7-ssa-transfer: <head SHA>` can repeat it for a focused correction.

## Status at first-push checkpoint

No native R7 test has executed. The small experimental fixture and the historical census support feasibility work. Scientific promotion requires the separate seven-day pilot, the independent cohort and frozen FinalTest and shifted-challenge evaluation. No anomaly performance, forecast skill, interval calibration or warning lead time has been established. No deployment artifact has been promoted.

## First native result

**NATIVE TRANSFER MECHANICS VERIFIED — SCIENTIFIC QUALIFICATION PENDING**

The initial run tested source `ae86288d480d2d21d947ea9666166aafbf273975` at PR merge `29725280a8d8d000aaa5fd66e135968b19ee6a5e`. The R7 TRX records one executed and passed case with zero skips. The preceding Revenant PostgreSQL regression TRX records 93 executed and passed cases with zero skips. [Workflow](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43362355358) · [Native transfer report and ZIPs](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43362355358/artifacts/14008538433)

| Metric | Unseen root A | Unseen root B |
| --- | ---: | ---: |
| SSA alerts / planted spikes detected | 3 / 2 | 2 / 2 |
| p95 baseline alerts / spikes detected | 2 / 2 | 2 / 2 |
| Fully covered forecast origins | 72 | 72 |
| SSA MAE, USD minor units | 13,532 | 13,435 |
| Persistence MAE, USD minor units | 47,597 | 45,547 |
| Nominal 95% interval coverage | 80.90% | 81.08% |
| Maximum observed normalization error, minor units | 0 | 0 |

Anomaly recall tied the comparator in these fixtures. Forecast error improved on the short constructed rhythms, while the nominal intervals undercovered. The complete native test took 124 seconds. Cold reconstruction matched at the inherited seal. At the next seal the child held 175,000 minor units, leaving available balances of 3,979,995 in the child and 4,161,348 in the source. The first-horizon forecasts were 3,934,060 and 4,165,289 respectively.

The experimental anomaly ZIP SHA-256 was `38b7252db1ac9b75357988dcf2d1bb1883ed2506d61c29782f45b8140fb84e1a`. The forecast ZIP SHA-256 was `6c631772883fcf520712d387f11c5553d9bf263c1cf4badba834504484e565fd`. These artifacts are native mechanics evidence for the initial fixture. Promotion requires the longitudinal evaluation.

## Seven-day pilot evaluation

The 8- and 12-bucket starter candidates were fitted on the first root and scored on the second. The first scoring attempt failed on a nonfinite ML.NET p-value. A diagnostic run retained missing values as missing and measured them explicitly. [Starter run](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43365500420) · [Executed TRX and metrics](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43365500420/artifacts/14009769436)

- The 8-bucket anomaly detector returned 2,400 unavailable scores across eight qualified accounts. At p ≤ 0.001 it detected three of three eligible events and produced 1.50 false alerts per qualified normal account-day. The 12-bucket detector returned 5,572 unavailable scores and reached two of three events at 1.00 false alert per normal account-day. The operational budget is 0.10.
- Both forecasters lost to persistence at all eight active horizons. The 8-bucket model's active MAE was 196,485 versus 137,401 minor units at +5 minutes and 648,201 versus 336,287 at +40 minutes. Native interval coverage at those horizons was 86.96% and 94.22% overall, versus 5.26% and 6.03% during stress.
- Both candidates warned before one of two developing crossings, 40 minutes ahead. Each generated five warnings with four false warnings. The two sudden-shock scenarios did not cross the floor.

A bounded correction tests a one-observation p-value history to eliminate repeated subtraction from the ML.NET kernel distance accumulator, and a stabilized rank-two forecast with a reference at the current completed seal and a fresh 32-bucket replay. Both use the same frozen pilot streams. A correction that makes a native score finite still has to satisfy the false-alert and comparator requirements.
