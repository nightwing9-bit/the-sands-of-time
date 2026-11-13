# R7 drain continuation diagnostics

## Retained foundations

The starting head was `130f8de17c78651ed28ded3d17476a3e4a687f86`. Gate M and Gate S remain passed at `f5d36244cd6dcc2a887e5e06f965a043b75b1bbe`. [Core ML evidence](r7-core-ml-gates.md) records the arithmetic checks, the maximum one-minor-unit round-trip error on 246,600 targets, and the prior failed rotations. The retained G4 manifest SHA-256 is `19b136929aec3e30032995bb7004dbb102693e810ea772007e04073d004e3ce6`. The Gate S evidence SHA-256 is `360808b17905d7a7699d543677e3dea8b56ac51f0b2578263a0f80703c653863`. The previous Gate D result SHA-256 is `964755b9dbdd3722a607e4b2d17a2b10e0e4060782c52b56003a44743afd57b0`. Neither gate was rerun.

## D0 — Measurable causal continuation signal

The gate requires at least two negative changes of material size in the last four completed buckets and negative net movement over those buckets. The nearest-rank 95th percentile of ordinary debit magnitude divided by opening balance is fitted on Train. Ordinary debit classification uses the retrospective bank episode windows on Train. Live gate evaluation uses balances and opening balance. The deterministic empty-data threshold disables the gate. Drift extrapolates the trailing twelve-bucket rate. The conditional median is the training-root median of normalized future movements at each horizon in each of the two gate states. All methods share bucket 299 onward and authoritative target masks.

| Train / Calibrate / Measure | Threshold as % opening | Gate activations / 10,302 | Active inside the drain phase | Active outside developing/shock event windows | Gated drift drain skill | Conditional median drain skill |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 0 / 1 / 2 | 0.821% | 43 | 19 | 19 | +40.1% | +63.9% |
| 1 / 2 / 0 | 1.197% | 56 | 22 | 28 | +36.4% | +52.5% |
| 2 / 0 / 1 | 0.852% | 65 | 33 | 27 | +49.6% | +54.7% |

| Measurement root | +5 min | +10 | +15 | +20 | +25 | +30 | +35 | +40 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 2 | 52.0% | 33.5% | 27.4% | 24.4% | 22.2% | 20.5% | 19.0% | 17.7% |
| 0 | 60.0% | 42.4% | 33.6% | 28.4% | 25.0% | 22.6% | 20.8% | 19.2% |
| 1 | 56.9% | 38.9% | 31.4% | 26.9% | 24.2% | 22.1% | 20.5% | 19.1% |

These are exact zero future-movement rates. The +5-minute unconditional median is zero on each root. Conditional medians in the activated state are negative at all eight horizons. At +5 minutes, ordinary settlement-only windows accounted for 4,784 of 4,943 nonzero movements on root 2, 3,974 of 4,123 on root 0, and 4,251 of 4,438 on root 1. These counts combine retail and treasury effects because the retained bank-flow export has no transaction-source attribution. Developing, return/recovery and other counts are separate in the JSON. The test reports signed movement, sign accuracy, persistence error and the eight horizon masks per bank.

Recognition occurred one or two seals after first debit on each of the six drain phases. Five crossed the floor during the drain itself. Initial recognition preceded those crossings by 35–60 minutes. There was an active eligible origin 35 minutes before one crossing and 40 minutes before the other four. The sixth drain phase bottomed at 10.86% of opening. The full event later crossed during its post-drain phase, so it does not supply an independent near-miss episode. For each root, 14 activated drain origins had their episode's last debit before the +40-minute target. These counts are repeated observations of six episodes, not independent trials.

## D1 and D2 — G4 model evidence

D1 hash-verified and reused the three original fitted twelve-bucket ZIPs. The candidate applies one calibration-only weight per horizon inside the gate and persistence outside it. Drift is calibrated identically on the same eligible samples. The report also applies the literal SSA weights to drift as a control. D2 uses exactly one new representation, `z[t] = (A[t] - A[t-3]) / (3s)`. The inverse uses the observed anchor for horizons 1–3 and the earlier predicted anchor for horizons 4–8. A focused fixture verifies this recursion. Both experiments retain all eight levels and the original active and stress diagnostics.

| Train / Calibrate / Measure | D1 overall SSA MAE | D1 drain skill | D2 overall SSA MAE | D2 drain skill | Conditional median overall MAE |
| --- | ---: | ---: | ---: | ---: | ---: |
| 0 / 1 / 2 | 73,612 | 0.0% | 73,614 | +0.06% | 71,391 |
| 1 / 2 / 0 | 69,872 | +5.19% | 64,672 | +57.86% | 65,943 |
| 2 / 0 / 1 | 65,850 | +42.87% | 69,065 | 0.0% | 65,649 |

Amounts are USD minor units, averaged across eight horizons. No D1 rotation beat the conditional median overall. D2 beat both calibrated drift and the conditional median in one rotation. Its first rotation retained two nonzero weights and worsened overall error by about 1.6 minor units. The third collapsed to persistence. The three training banks each contained one developing drain and one recovery. Nonzero completed changes were 774, 840 and 862 of 2,015 adjacent seal pairs. None of the selected training drains supplied a near miss. The short representation showed incremental skill once, so these data do not establish an SSA model-family limitation. The confirmed results are intermittent flows, regime-dependent predictive information, and inconsistent SSA transfer from sparsely covered fitted histories. A sample-design deficit warranted D3.

D0 source `055cae6`, D1 `8089af2`, D2 `2e2676d`. Focused D0, D1, D2 arithmetic and D2 native tests executed successfully. Full outputs and exact ZIP hashes are under `TestResults/r7-recovery/drain`.

## D3 — Frozen G5 operational-flow pilot

G5 is `revenant-episode-generator/5`. It retains the G4 bank-specific treasury cycles and adds daily bank-specific operational clearing runs with variable amounts, duration, start jitter, skips and later funding runs. It schedules one developing drain per bank with independently drawn planned ending fractions from 4.0% through 13.0%, plus one separate sudden shock. All activity is emitted as ordinary V4 payment commands and goes through the existing funding walk and financial authority. No shared clearing schedule was introduced. The planned feasibility run also checked exact retained G4 opening hashes, behavior hashes and labels.

| G5 root | Root key | Planned behavior SHA-256 | Planned payments |
| --- | --- | --- | ---: |
| 0 | `4579209772648982799d0b866526b66992727dd60860b4e379ff8419d43f1394` | `44bd88cf7637c31907dc9cebf8d4411a91c32647907bcc08c074067dbfc5be68` | 5,997 |
| 1 | `3f11ab2155ac052faa30be1c2dda03e532107e0069fe9f5db4f3fb435e77a714` | `fba134b7b3b5d5cfd06e77c7a7fa7611171d92d1364e5333bbb9e3a05f5474c3` | 5,151 |
| 2 | `4386325fd8c79207a7ffac6d005e336a5ce2450558bbcabfff2d4a5b0c05701c` | `29ab23ecdc2909300c00e77d3a7a5821d6ff30bf7fbec8624082cbb4bb9a4104` | 6,276 |

The planned walk verifies fundability and conservation. It finds eight drain-phase crossings and six near misses between 10% and 13% of opening. Those are planning estimates. The executed audit determines realized outcomes from completed posted-minus-reserved balances. Planned gated drift and conditional median both improved on persistence on all three rotations without access to episode labels at the origin. This was the feasibility check for executing the single frozen pilot at `d5722dfd7ce315e1647a817f7afd28fb60314f40`.

### Executed G5 decision

**GATE D FAILED — SSA CONTRIBUTION NOT DEMONSTRATED**

The isolated PostgreSQL 18.6 pilot passed in 1h 1m. All three histories closed with Marrow status Verified. PostgreSQL ran natively on macOS ARM64 and ML.NET 5.0.1 scored the canonical export on .NET 10.0.15 in a standalone Linux AMD64 guest. No Docker, PlacementOps or GitHub CI was used. The manifest SHA-256 is `e6286648544f3c624db7f0cafd9e508113da208a225b3a7efc071505d9661e05`. All 111 stream hashes matched. Scoring checked accepted-count reconciliation, the eight-horizon masks and aggregate posted-principal conservation from the first seal with retained flow details onward.

| Root | Finance facts | Seals | Accepted intents | Holds / reviews | Return facts | Developing episodes crossing | Near misses at 10–13% |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 0 | 20,083 | 2,016 | 5,997 | 9 / 7 | 71 | 2 | 3 |
| 1 | 17,529 | 2,016 | 5,151 | 10 / 7 | 56 | 2 | 2 |
| 2 | 20,907 | 2,016 | 6,276 | 12 / 7 | 61 | 4 | 1 |

The preregistered development rule required at least two passing rotations with overall MAE at most the strongest baseline, 10% drain-continuation skill against persistence, and lower drain error than matched calibrated drift and the conditional median. The three-bucket SSA model and the causal policy used the same fit and state code as D2. Training and shrinkage used separate roots from the measured root. The matched baseline received separate calibration under identical rules; an additional control used the literal SSA weights.

| Train / Calibrate / Measure | Gate activations / drain activations | SSA overall MAE | Strongest baseline MAE | SSA drain MAE | Gated drift drain MAE | Conditional median drain MAE | SSA drain skill |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 0 / 1 / 2 | 215 / 71 | 215,556 | 204,924 | 2,575,143 | 1,598,460 | 1,332,339 | 0.000% |
| 1 / 2 / 0 | 193 / 91 | 178,289 | 166,344 | 1,893,289 | 1,142,142 | 859,562 | +0.076% |
| 2 / 0 / 1 | 317 / 77 | 237,298 | 229,956 | 2,256,812 | 1,422,859 | 1,015,855 | +0.002% |

The strongest overall baseline was the train-root conditional median in all rotations. G5 increased the count of eligible drain-continuation origins and supplied independent near misses. The frozen SSA collapsed to persistence in rotation one and adjusted only one horizon in each of the other rotations. It lost to both causal drain controls on every measurement root. This falsifies the claim that the frozen three-bucket exact-inverse SSA plus an observed-continuation gate adds consistent incremental skill under the tested one-bank training and transfer procedure. The data do show causal continuation information. The experiment does not isolate whether a different training-bank design or a different representation could learn it.

Scoring source `d97b33a71b2489b7e1231d773313ef92c5ae0d32`. The native executed scoring TRX was 1/1 in 41 seconds. All eight horizons and episode-level results, including near misses, are in `d3-executed-ssa.json`, SHA-256 `68e4d50b1f08b38d03b9838c1bc98dbc0aa34ee1251f0c6bc3c7e70a65dadfca`. Training-root ZIP hashes in order are `ccca177ef996b1dcf32181872c9df237369683db807eab779a353ea93d40e03e`, `e97c3c2542951e1020c4e9d1f3e67af39e37ec89d91fc3dbc478cb7e76bb39c5` and `2e80e180993ea9aa9c5f66ef38e2f79af7b2b9234a9b0a574eccf5ba0a5dbacd`. Finance SQL counts, Timeline receipts and the research artifacts are retained locally. No model promotion, warning qualification or independent cohort was run.
