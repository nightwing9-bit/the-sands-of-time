# R7 anomaly and forecast evaluation

The first native gate is `Frozen_SSA_models_transfer_across_independent_committed_histories` in the existing integration workflow. It trains the two starter models on committed six-bank V4 history and attempts transfer to two independent roots with distinct balances, activity rhythms, known spike examples and a causal change in normal activity. It records alert burden and spike detection against a trailing 48-bucket empirical p95 rule, eight-horizon MAE against persistence, observed coverage of nominal bands, normalization error, and a digest of exact rolling SSA output bits. Forecast-only reads at each origin are checked against the corresponding observation update and against a repeat read. Fresh loads are compared across ordering and cold reconstruction.

A verified Marrow fork uses the source prefix through its completed seal. A legal v8 hold of 175,000 USD minor units in the child tests availability and forecast divergence at the next seal. Expected evidence is the named passing TRX, the two experimental ZIPs with SHA-256, and `transfer.json`. The initial PR opening triggers the gate. An edited PR with `R7-ssa-transfer: <head SHA>` can repeat it for a focused correction.

## Status at first-push checkpoint

No native R7 test has executed. The small experimental fixture and the historical census support feasibility work. Scientific promotion requires the separate seven-day pilot, the independent cohort and frozen FinalTest and shifted-challenge evaluation. No anomaly performance, forecast skill, interval calibration or warning lead time has been established. No deployment artifact has been promoted.
