# R7 core ML on executed G4

The research baseline was frozen from `6d8755876f28d95809ee4747e8947c1b223270e1` before changing the forecast adapter. Its one-bucket-change fit used BANK-B on root 0, 2,016 points, window 24, series length 96 and 288 warmup points. On the original measurement root it recorded 1.706 overall MAE ratio, −39.8% active skill and zero of two developing crossings warned. The retained source, settings and result hashes are in `TestResults/r7-recovery/core-ml/baseline`. The old ZIP was unavailable. Gate D refit that control on each rotation with the existing training-bank rule.

All three gates used source `f5d36244cd6dcc2a887e5e06f965a043b75b1bbe`. Executed G4 manifest SHA-256 was `19b136929aec3e30032995bb7004dbb102693e810ea772007e04073d004e3ce6`. Roots 0, 1 and 2 begin with `eafc18ee2bb318b6`, `b628ba72f37514717` and `2a519d1b80ffa2cb`. The original PostgreSQL job produced 6,048 completed seals and 48,866 committed finance facts. These are previously inspected development roots.

## Gate M and Gate S

Gate M passed seven deterministic arithmetic tests. The exact inverse adds twelve scaled forecast-rate units to the known lagged balance at each horizon. Integrated extrapolation instead accumulates forecast rates from the origin as if they were future one-bucket changes. On the isolated debit fixture, oracle rates reconstruct the flat post-debit path with the inverse. Integration projects another 960 minor units of depletion. Both decoders reproduce oracle constant and sustained-drift fixtures. Tests also cover recovery, seal boundaries, ties-to-even rounding, finite inputs, checked overflow and the weighted-median shrinkage.

Gate S checked 246,600 authoritative target pairs, including the partial masks at the end of each stream. The zero-innovation forecast equals persistence algebraically. The maximum float round-trip error was one minor unit for both the oracle inverse and the zero-innovation reference. Forecast movement skill is measured against actual future balance change, with a separate rate MAE comparison to the zero-innovation reference. Eight managed tests passed in the combined M/S run.

## Gate D procedure

Each rotation trained on one bank from a seven-day training root. The predefined selection used greatest normalized change variance among banks with a developing-pressure label in that training root. The fit used 2,005 complete rates from 2,016 seals, including the first complete rate anchored to the known opening balance. No rate was fabricated for an incomplete twelve-interval window. The first forecast follows 288 prior complete subject rates at bucket 299, using a fresh engine per bank. Both decoders receive the same eight raw outputs from a single engine. Cold-prefix equality and an altered leading observation check are exercised on every calibration and measurement bank.

ML.NET 5.0.1 used nonadaptive `ForecastBySsa`, exact rank selection with `rank=null`, default maximum rank 23, stabilization, discount factor 1, fixed horizon eight, window 24 and series buffer 96. The scale was opening divided by 1,000 with a minimum of one minor unit. [ML.NET 5.0.1 estimator contract](https://github.com/dotnet/machinelearning/blob/v5.0.1/src/Microsoft.ML.TimeSeries/ExtensionsCatalog.cs#L420-L460)

Volatility tercile edges, each decoder's shrinkage and naive drift shrinkage were fixed on Calibration. Weights minimize absolute level error via the clipped lower weighted median. Decoder selection prioritized satisfying the overall constraint on Calibration and then minimizing drain-phase MAE. Gate D measures ≥10% drain-phase skill against persistence and separately requires improvement over calibrated drift. High-volatility contribution requires a nonzero change to measured drain predictions and lower high-volatility drain MAE than persistence. The overall comparator is the strongest of persistence, the trailing median, prior-day same-clock net movement and calibrated trailing-rate drift. The old G4 SSA remains a measured control. The drain, recovery and shock slices use the retained sidecars for evaluation. The isolated-debit slice is a balance-change proxy, since net seal changes can combine several transactions.

## Gate D result

**R7 SCIENTIFIC PARTIAL — EXACT GATE FAILED.** Zero of three rotations passed. Calibration selected integrated extrapolation in all three. The exact inverse remains the mathematical reference. Neither decoder qualified as a learned forecast.

| Train / Calibrate / Measure | Training bank | Integrated overall ratio | Integrated drain skill vs persistence | Integrated drain MAE | Calibrated drift drain MAE | Exact inverse drain skill |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| 0 / 1 / 2 | BANK-A | 1.0000002 | 0.0000% | 2,262,412 | 2,256,599 | 0.0000% |
| 1 / 2 / 0 | BANK-B | 1.0000875 | +0.0008% | 2,635,225 | 2,633,722 | +0.0002% |
| 2 / 0 / 1 | BANK-F | 0.9998564 | +0.3893% | 1,539,428 | 1,541,562 | +0.0304% |

Each measurement root had 10,302 eligible origins and 10,296 down to 10,254 valid targets per horizon. Developing drain slices contained 24–38, 26–40 and 38–52 horizon targets respectively, from two actual episodes per root. The sampled origins overlap. They do not increase the number of independent roots or episodes.

| Train / Calibrate / Measure | Raw SSA rate MAE | Zero-innovation rate MAE | Raw exact-inverse movement MAE | Zero-innovation movement MAE | Largest high-volatility integrated weight |
| --- | ---: | ---: | ---: | ---: | ---: |
| 0 / 1 / 2 | 1.482 | 0.579 | 189,321 | 73,612 | 0 |
| 1 / 2 / 0 | 8.915 | 0.557 | 1,024,639 | 70,262 | 0.0000435 |
| 2 / 0 / 1 | 1.712 | 0.568 | 223,066 | 69,065 | 0.02061 |

Balance and movement MAE are in USD minor units, averaged over eight horizons. Raw rate and movement MAE here are averaged over bank/horizon cells. The exact inverse exposes poor raw innovation skill in all three rotations. Shrinkage eliminates or nearly eliminates the learned contribution in the first two rotations. The third rotation narrowly beats calibrated drift on drain MAE but misses the 10% skill floor by a wide margin. The G4 one-bucket-change control had overall MAE of 125,591, 79,369 and 70,940 in the three rotations, compared with persistence at 73,612, 70,262 and 69,065. The result falsifies the proposed transfer and shrinkage combination at the declared threshold. It does not establish a general limit on forecasting from available balances.

Full per-bank and all eight horizon metrics, signed bias, RMSE, movement, volatility, drain, recovery, sudden-shock and isolated-debit proxy slices are in `gate-d-rotations.json` under `TestResults/r7-recovery/core-ml`. Its SHA-256 is `964755b9dbdd3722a607e4b2d17a2b10e0e4060782c52b56003a44743afd57b0`. The native run used ML.NET 5.0.1 with .NET 10.0.15 on Linux x64 in the standalone emulated guest and recorded 2/2 executed tests, including the existing research/Worker-source parity test. The six research ZIPs are preserved locally and their digests were verified against the results. The three trailing-rate ZIPs, in training-root order, have SHA-256 values `6b959e29c9c4f85767210846c35fce90b4efa967f04a4676ec91794740385510`, `0041360b517c083c72e53046e5a194a086479f4a8315cf9f23c006a9954e3151` and `44b0add9801241d5e40e1f7ca3fb7c1a9c60d08595d6acd1175e98f83196b4af`.

Gate D withheld authorization for G5. G5 finance execution, path-risk and warning calibration, and the 22-root independent qualification were not started. The existing anomaly development result remains 14/23 detections with seven false alerts across 71.26 normal account-days and five alerts among 30 legitimate hard negatives. Anomaly promotion still requires independent results. There are no new model pins for R8, no new advisory contract or migration, and no published Worker proof for the experimental representation. Existing /2 scoring and causal branch contracts remain in place. This checkpoint was kept local to avoid triggering broad regression CI.
