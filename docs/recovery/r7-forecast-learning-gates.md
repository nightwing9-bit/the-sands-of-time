# R7 forecast learning on the frozen G5 development roots

The R7 forecasting decision concerns incremental SSA skill after a causal conditional median. The previous Gate D result remains a failure; see [the D3 checkpoint](r7-drain-continuation-gates.md#executed-g5-decision). The frozen measurement manifest is `e6286648544f3c624db7f0cafd9e508113da208a225b3a7efc071505d9661e05`, generator source `d5722df`, D3 scoring source `d97b33a`. All three measurement roots are reused from their verified, canonical exports. M/S, independent final roots, anomaly, advisory publication and Worker integration are outside these runs.

## E0: Confirmed training-design deficiency

Each model received 2,014 contiguous, overlapping three-bucket rates from one bank's 2,016 completed five-minute seals. It had no access to the other five banks during fitting. The rank column is read from the verified ML.NET ZIP payload (the original specification requested automatic rank). Window and series length are 24 and 96.

| Train root / bank | Pressure episodes | Crossings / near misses | Gate active / during drain | Ordinary material debits | Drain / full episode percent | SSA rank | Prior zero weights |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 0 / BANK-A | 1 | 0 / 1 | 37 / 12 | 22 | 0.65% / 1.89% | 2 | 8/8 |
| 1 / BANK-B | 1 | 1 / 0 | 26 / 7 | 26 | 0.40% / 1.59% | 1 | 7/8 |
| 2 / BANK-F | 1 | 0 / 0 | 19 / 8 | 13 | 0.45% / 1.79% | 1 | 7/8 |

Each comparator fitted on six banks and 10,296 one-step outcomes, with six developing episodes represented. Of the 24 prior active-state weights, 22 were zero. The exceptions were 1 at 15 minutes in rotation 1 and 0.0059473 at 30 minutes in rotation 2. The per-horizon inside/outside MAEs, fit-series digests and ZIP hashes are in `e0-training-exposure.json`.

## E1: Existing SSA replay

The diagnostic loads the original ZIPs and reproduces the causal forecasts using the original artifact, decoder, and active-state weights. It learns a new convex mixing weight only on the calibration root for each horizon and state. It also reports raw ungated SSA as a sensitivity check. G5 root assignments are Train / Calibration / Measure = 0/1/2, 1/2/0 and 2/0/1.

For the prior gated candidate, overall MAE changes versus median alone were −0.34%, 0%, +0.83% (positive is better); drain-continuation changes were −23.84%, 0%, −0.38%. None of the inactive coefficients were positive; 14 of 24 active coefficients were positive. Recovery and isolated-debit MAE were identical to the median in these samples. This is not stable incremental skill. See `e1-frozen-ssa-blend.json` for all coefficients, signed bias, false extrapolation counts, near misses, recovery and all eight horizons.

The ungated sensitivity check changed overall MAE by −0.075%, −0.001%, and +0.257%; it also failed to improve drain MAE consistently. For the original gated blend, near-miss MAE changed by −4.65%, 0%, and +1.93%, and there were no false isolated-debit extrapolations. The raw blend produced one such extrapolation.

| Horizon | Active λ (rot. 0 / 1 / 2) | Gated blend overall MAE gain (rot. 0 / 1 / 2) |
| ---: | ---: | ---: |
| 5 min | 0.504 / 0.000 / 0.000 | -0.09% / +0.00% / +0.00% |
| 10 min | 0.221 / 0.000 / 0.203 | -0.75% / +0.00% / +2.56% |
| 15 min | 0.301 / 0.000 / 0.030 | -0.90% / +0.00% / +0.31% |
| 20 min | 0.258 / 0.000 / 0.192 | -0.53% / +0.00% / +2.05% |
| 25 min | 0.297 / 0.000 / 0.184 | -0.42% / +0.00% / +1.79% |
| 30 min | 0.349 / 0.000 / 0.082 | -0.23% / +0.00% / +0.64% |
| 35 min | 0.284 / 0.000 / 0.069 | -0.15% / +0.00% / +0.35% |
| 40 min | 0.334 / 0.000 / 0.000 | -0.17% / +0.00% / +0.00% |

## E2/E3: Residual architecture and feasibility

The baseline is the frozen two-state conditional movement median. The gate sees the latest four completed intervals, requiring at least two material debits and negative net change. The state threshold and eight cumulative median fractions are fit on one G5 Train root. SSA uses a different root entirely. It sees completed normalized innovations `(A[t] - A[t-1] - m1(H[t-1])) / s`, beginning at seal index 5; `s = max(1, openingMinor / 1000)` uses integer division. Each forecast adds `w[g,h] * s * cumulativePredictedResidual[h]` to the baseline level. SSA uses the frozen 24/96 window/series length and exact rank selection. Calibration is bounded in [0, 1] and minimizes absolute error via a clipped weighted median. All banks use the same eligible origin and target masks. Event annotations are used for retrospective scoring and Train materiality estimation only. Inference receives completed balances, opening balance and fixed parameters.

A cheap audit of the retained streams used leave-one-bank-out medians on Train and independent Train/Calibrate/Measure roots for scoring. Across the 18 bank histories on the measurement side, lag 1 correlations were all positive (0.06–0.81), and lag 2 reached 0.79. Lag 288 ranged from −0.02 to 0.09 overall. The independently calibrated same-residual continuation correction improved overall error 10.38%, 9.37%, and 9.86%, and active-state error 18.23%, 15.01%, and 13.33%. The corresponding developing-pressure changes were +4.37%, −5.72%, and −0.69%. A daily residual comparator improved overall error by at most 0.08%. Short-lag predictability justified the single training-only run. It also established the stronger simple comparator SSA must beat. The correlations and signed residuals by bank, ordinary settlement, continuation, isolation and recovery are in `e3-residual-feasibility.json`.

The ten-day variant `revenant-g5-residual-training/1` retains G5 background, settlement, return, and review behavior and adds eight varied, fundable pressure and recovery schedules to one continuous BANK-A history. A warm background day precedes the eight separate pressure days; a distinct sudden shock is scheduled on another bank on day nine. Exposure was checked using the dry financial walk before PostgreSQL. The retained G5 plan behavior hashes also matched exactly. This deliberately enriched sample (eight pressure episodes in one ten-day history instead of one in seven days) is small synthetic training evidence and provides no evidence about natural event prevalence.

- Training generator/execution source: `cac5a0ecafb6a649bb7fe5fd48aca54277637c42`.
- Root: `7dcfc2fddb24c8ae2461e82bee980dd19d04bdc6a938d453957aba182de7f24f`.
- Planned behavior hash: `facbaf7174bc9fc8838053fbd015898ddfcaae7c6eaf963c23d3bba8a2345781`; 8,744 payment intents and 2,880 seals planned.
- Residual scoring source: `1808df3e6ffa6c738c4d8caffea4d60c1b8a65a9`. Gate thresholds were committed before the model ran: no overall degradation, at least 10% pressure improvement over persistence, at least 1% overall and continuing-drain improvement over the strongest matched correction, and positive weights at two or more common active horizons in at least two passing rotations.

All numerical reports, model ZIPs and provenance pins for this study are retained under `TestResults/r7-recovery/forecast-learning`. No model is eligible for promotion on the basis of a planned outcome or a successful diagnostic test alone.

Evidence digests (SHA-256): E0 `4df3cf05b88ceeec5aaeb0e23c1fec416c5c168b35ced164f63aa7697ca941a4`; E1 `6c4880f65a51a58314dcc6ab60feddae694ad8d9f8733f458fa5b8f0499b9395`; E3 residual audit `d96515772a914d908aa972841509f5f9eca5b4aec49d7233fd4c97f85a6a6463`; E3 dry plan `8e3056558f34d434b5fffd3e29a0911c3146afe245a236e894d86f3d923809e5`.
