# R5 fraud model evidence

This is the checkpoint for the first native qualification. The implementation base is R4 documentation tip `6ec0607210392e1bd2b9db4182a22ef6e11a2290`, descended from verified implementation `73e1fa4d4f172ca32bb81a325353a487ac1aa006`. The R4 canonical manifest still hashes to `a40cfb31cab319541a339d5c07a7068b6fda4717718a8012af5a999a83e793df`, and the local regression reproduces its published logical root keys, opening hashes and behavior hashes. The V3 model ZIP still hashes to `c13491fef4252e462165c84d1df5c861596638ca60eeb9e86fd020496aacd3de`.

Generator `revenant-episode-generator/2` is pinned through the V4 scenario extension and `dataset-manifest/2`. Master seed `2697831047` defines 12 Train, 4 Validation, 4 Calibration and 4 FinalTest roots of 72 hours. Each uses an independent root namespace and a seeded calendar origin. Each development root has attacks and legitimate lookalikes on both scored days in the pure planner check. Malicious and comparable legitimate schedules sample the same six-hour strata. The first 24 hours are legitimate financial history and excluded for both classes from model and budget evaluation. All payment payloads retain neutral historical truth. The shifted ring challenge is reserved under seed `4814623101`: four independent roots, with 180–490 second ring spacing instead of 16–150 seconds. It has not been executed.

The existing CI workflow runs the R5 PostgreSQL smoke in the regular native Revenant integration suite. A draft against R4 with `R5-native-cohort: qualification` in its body enables the 24-root job on opening or edit, after all required integration tests pass. Native qualification will retain the manifest and development distributions, and upload the three research partitions separately. Read only the development artifact until selecting the model; read Calibration only after selection, and FinalTest only after the calibrator and threshold are frozen. Use the binary manifest SHA-256 as the reader pin. The research reader opens only a requested split. It verifies its required stream hashes and validates canonical record framing, ordering, split identity, joins and campaign membership. The local reducer fixture exercises this path with real reducer outcomes; it is not PostgreSQL qualification.

The fixed review budget is `floor(eligible screenings × 5 / 100)`, with 2% and 10% sensitivity. All eligible Screening opportunities count in the denominator; recorded Screening rejections never enter the review queue. Intrinsic ranking uses the entire non-rejected universe. The combined policy keeps existing v7 holds and uses only the remaining slots; if v7 holds exceed capacity, the report records the overload and adds no reviews. Ties sort by effective time, root key, cutoff sequence and intent ID. The fixed heuristic adds principal relative to $2,000 (capped at 1,000,000), amount divided by available funds (capped at 1,000,000; zero balance gets the cap), and 500,000 for reported beneficiary novelty. v7-only, heuristic-only and v7-plus-heuristic comparisons are wired for native development evidence. A threshold policy will be reported separately from retrospective top-K ranking. A flagged attempt is a research outcome; it does not establish any intervention or financial effect.

The initial macOS ARM64 Release build passed with zero warnings and errors; 371 regular unit tests passed across five assemblies with zero skips. The [native smoke run](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43221126952) tested head `62e5d6ec7a2efc9d51345dab84f2da2f4fe1ebf4` at synthetic merge `53fd8d44a809c5eadc1e89b2f07cf6d18c4bd604`. Its [integration TRX](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43221126952/artifacts/13946182206) records 89 executed and passed Revenant cases, including `V2_six_bank_smoke_verifies_real_opening_risk_export_and_reader`. The 48-hour /2 smoke produced 70 eligible rows and manifest `78900f76fab3968206a30bc04c040e52dbc239c24af7dabaea397995893a0b70`. The native /1 smoke reproduced the exact prior fixture manifest `48d51fc2b7cebc879de49bfc2bfaf09aea6d0047a34934744580391ac06532e0`.

All seven jobs in the qualification run passed. The [cohort TRX and binary manifest](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43221126952/artifacts/13945274825) record one executed and passed `Generate_native_R5_cohort` case (6m 37s), all 24 roots, 20,736 seals, 124,416 bank rows, and 3,406 pre-Screening rows. The canonical manifest SHA-256 independently verified as `c1c2b2178ea80946f195d821d848e60059c3607fb10fac506b70d7eddecdf5e9`. Actual v7 outcomes across all hours were 35 holds, 8 Screening rejections, 3,284 settlements, 79 unposted settlement outcomes and 69 compensating returns. The test verified a live-compatible prefix against replay in each root. No FinalTest row-level truth was used in development.

Train has 1,317 scored opportunities (265 malicious, 1,052 legitimate); Validation has 441 (91 malicious, 350 legitimate). Malicious prevalence on the two scored days was 20.5% and 19.7% in Train and 20.8% and 20.5% in Validation. Every eligible row had all four windows fully covered. Root prevalence ranged from 19.1% to 21.4% in Train and 19.8% to 21.7% in Validation. Every weekday and UTC hour had opportunities in each development split. By weekday the prevalence ranged from 9.8% to 26.7% in Train and from 7.9% to 27.3% in Validation. The highest observed hourly prevalence was 40.0% in Train and 53.6% in Validation; some hours had no malicious attempts. The controls measure how much discrimination these differences permit.

| Development split | Policy | 2% malicious/reviews | 5% malicious/reviews | 10% malicious/reviews |
| --- | --- | ---: | ---: | ---: |
| Train | Actual v7 | 0/10 | 0/10 | 0/10 |
| Train | Heuristic ranking | 2/26 | 7/65 | 20/131 |
| Train | v7 + heuristic | 2/26 | 7/65 | 16/131 |
| Validation | Actual v7 | 0/4 | 0/4 | 0/4 |
| Validation | Heuristic ranking | 0/8 | 3/22 | 6/44 |
| Validation | v7 + heuristic | 0/8 | 2/22 | 6/44 |

The selection protocol is fixed before model fitting. The metadata controls fit one L-BFGS logistic regression each using only time/coverage or amount/flags; either reaching Validation ROC-AUC 0.85 or average precision 0.75 stops production fitting. The four production candidates all use the 70 columns without episode calendar inputs. Logistic L2 values are 0.1 and 2 (L1 0, 120 iterations, Train-fitted min/max with zero fixed); FastTree configurations are 4 leaves × 35 trees with 25 minimum examples per leaf, and 8 × 60 with 18 minimum examples per leaf (learning rate 0.08). Seed is 17081129; both trainer families use one thread. Fixed logistic ablations cover amount/balance, current features, source-account history, novelty, and adding time. A permutation control shuffles labels inside each Train root. The selected model must beat the time diagnostic on combined 5% detections and by more than 0.05 average precision; the permutation control must stay below ROC-AUC 0.65 and AP below Validation prevalence plus 0.15.

The selection run requires an explicit `R5-offline-experiment: select <cohort-run-id> <manifest-sha256>` PR marker. It downloads only Train and Validation initially. After the selected architecture and saved ZIP hash are recorded, a separate process fits a monotonic sigmoid to raw Calibration scores, quantizes to PPM with midpoint rounding away from zero, and chooses the smallest inclusive threshold that fits the Calibration budget without breaking ties. The independent stage requires `R5-offline-experiment: evaluate <cohort-run-id> <manifest-sha256> <selection-run-id> <freeze-sha256>`. It validates the freeze and ZIP before running the independently seeded PostgreSQL ring cohort or opening FinalTest. Its grouped uncertainty calculation enumerates all 256 four-root bootstrap draws. The primary promotion comparison is strict incremental malicious detections over v7 plus the same-budget heuristic; it also requires ring detection on both evaluations (incremental in the challenge), detections across multiple families, no increase in FinalTest hard-negative reviews, and a feasible retrospective 5% queue. The fixed threshold's observed demand is reported separately.

## Observed selection and calibration

[Native selection and calibration](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43222808084/artifacts/13946163976) ran on Linux x64 at head `15f3a0b8181669f0d33607051982c849b66dc368`, synthetic merge `f25fc6f94dc569bf4b04ae4ba057738930c53903`. Both named experiment cases executed and passed, in separate processes. Validation prevalence was 20.6%; time/coverage AP was 0.199 (ROC-AUC 0.443) and amount/flags AP was 0.284 (ROC-AUC 0.620). The root-stratified label permutation produced AP 0.196 and ROC-AUC 0.406. The guards cleared.

| Validation model or control | Average precision | ROC-AUC | Malicious reviews / 22 | Ring reviews | Families | Hard-negative reviews |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Time/coverage | 0.199 | 0.443 | 3 | — | — | — |
| Amount/flags | 0.284 | 0.620 | 5 | — | — | — |
| Logistic L2=0.1 | 0.652 | 0.837 | 16 | 9 | 3 | 5 |
| Logistic L2=2 | 0.639 | 0.835 | 15 | 9 | 3 | 6 |
| FastTree 4 × 35 | 0.696 | 0.859 | 18 | 18 | 1 | 4 |
| FastTree 8 × 60 | 0.734 | 0.883 | 18 | 18 | 1 | 4 |
| Logistic: money only | 0.269 | 0.533 | 6 | 0 | 1 | 4 |
| Logistic: current features | 0.303 | 0.601 | 7 | 1 | 2 | 9 |
| Logistic: current + account history | 0.533 | 0.762 | 13 | 5 | 3 | 9 |
| Logistic: history without current novelty signals | 0.526 | 0.753 | 12 | 5 | 3 | 10 |
| Logistic: full + time | 0.498 | 0.719 | 12 | 4 | 2 | 9 |
| Root-stratified permutation | 0.196 | 0.406 | 7 | 1 | 3 | 12 |

FastTree 8 × 60 won on Validation: it tied the smaller tree on constrained reviews and had higher AP. Both trees concentrated their primary-budget detections in Ring; this is a risk to the multi-family promotion gate. The selected artifact's raw scores were unchanged by save/load. Its ZIP SHA-256 is `56bca32c36dd1911d163a6f92369da43382e1937d875228339f15f8608519c7c`, and the independent evaluation freeze SHA-256 is `0855896671643c0e4492850a4e81565f6b2abd0d677ce12067771d5a441b400a`.

The monotonic Calibration mapping is `sigmoid(0.1647929876432001 × rawScore + 0.057602363131310064)`. The fixed threshold is 852,469 PPM. On the 426 eligible Calibration opportunities (85 malicious), it requested 21 reviews, including two existing v7 holds, with 18 malicious and three legitimate reviews. Review rate was 4.93%, Brier 0.1044 and log loss 0.3410. Peak five-minute demand was five reviews, all additional. The score is calibrated to this synthetic population only.

## Frozen independent evaluation

[Independent evaluation](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43223488498) tested the same selection head and merge. All seven jobs passed. Its [evidence](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43223488498/artifacts/13946722635) contains one executed and passed financial challenge case (1m 39s) and one executed and passed frozen evaluation case. Report SHA-256: `542d2d551e183a60e677fe6b9dd2200ffddb777b2128424c2cdfb9aec2b9b432`. The [challenge manifest](r5-ring-challenge-manifest.json) has canonical SHA-256 `3f4daa236769240a69891aa6cbe1ed1de089b0ae12710baa0c188a16a5b1da5d`. The [qualified cohort manifest](r5-qualified-dataset-manifest.json) records the four original split hashes, stream counts and finance outcomes.

| Split | Eligible | Malicious | Legitimate | v7 holds | Screening rejections |
| --- | ---: | ---: | ---: | ---: | ---: |
| Train | 1,317 | 265 | 1,052 | 10 | 2 |
| Validation | 441 | 91 | 350 | 4 | 1 |
| Calibration | 426 | 85 | 341 | 2 | 0 |
| FinalTest | 435 | 86 | 349 | 2 | 3 |
| Independent ring challenge | 425 | 91 | 334 | 4 | 0 |

| Evaluation | Policy | 2% malicious/reviews | 5% malicious/reviews | 10% malicious/reviews |
| --- | --- | ---: | ---: | ---: |
| FinalTest | Actual v7 | 0/2 | 0/2 | 0/2 |
| FinalTest | Heuristic ranking | 0/8 | 0/21 | 7/43 |
| FinalTest | v7 + heuristic | 0/8 | 0/21 | 7/43 |
| FinalTest | v7 + FastTree | 5/8 | 18/21 | 31/43 |
| Ring challenge | Actual v7 | 0/4 | 0/4 | 0/4 |
| Ring challenge | Heuristic ranking | 2/8 | 3/21 | 6/42 |
| Ring challenge | v7 + heuristic | 1/8 | 2/21 | 5/42 |
| Ring challenge | v7 + FastTree | 4/8 | 15/21 | 26/42 |

FinalTest average precision (step-integrated PR-AUC) was 0.6353 and ROC-AUC 0.8382. At 5%, combined FastTree recall was 20.9% and precision 85.7%; 3 of 181 hard negatives were reviewed (heuristic: 21 of 181). The model identified Ring 17/24 and CoordinatedBurst 1/24; it did not detect Takeover (0/8), Dispersal (0/18), or BeneficiaryAbuse (0/12) at that capacity. At 10% it identified 19 Ring, eight CoordinatedBurst and four BeneficiaryAbuse attempts. Additional malicious detections by FinalTest root were 5, 5, 5 and 3 (the equal-capacity heuristic found none). The empirical 256-draw whole-root bootstrap had 2.5th/50th/97.5th percentile recall uplift of +17.8/+20.9/+23.8 percentage points. Four roots are insufficient for a population-level confidence claim.

The independent challenge identified 15/24 Ring attempts and six legitimate payments (all hard negatives) in 21 reviews. Ring detections by root were 4, 2, 4 and 5 out of six. The matched v7-plus-heuristic policy found two malicious attempts total and reviewed 19 legitimate attempts. Challenge AP was 0.6072 and ROC-AUC 0.8315; the root-bootstrap recall delta percentiles were +9.8/+14.3/+17.8 points. The challenge varies timing and participants but shares the simulated motifs and enriched synthetic prevalence; this does not establish sensitivity to other real-world rings.

Intrinsic FastTree ranking, without mandatory holds taking slots, identified 7/8, 20/21 and 32/43 malicious reviews at 2%, 5% and 10% on FinalTest. It identified 7/8, 18/21 and 27/42 in the challenge. The combined policy rows above are the comparisons that preserve the existing v7 workload.

The fixed threshold requested 25/435 (5.75%) reviews on FinalTest, four above the nominal budget, with 21 malicious, four legitimate, F1 0.3784, Brier 0.1134, and log loss 0.3672. It requested 25/425 (5.88%) in the challenge, also four above budget, with 18 malicious, seven legitimate, F1 0.3103, Brier 0.1272, and log loss 0.4010. Peak five-minute demand was five on FinalTest and two in the challenge. The evidence report contains bank and episode-day demand. R6 must enforce capacity and preserve mandatory v7 reviews; the threshold alone does not enforce the budget.

## Artifact and reference vectors

The packed ZIP is the unchanged selected ZIP; no retraining occurred during packaging. Its SHA-256 is `56bca32c36dd1911d163a6f92369da43382e1937d875228339f15f8608519c7c`. The [canonical manifest](../../src/Revenant/Intelligence/Artifacts/revenant-behavioral-risk-v2/manifest.json) SHA-256 is `37a00f88142256ead46d958696c8bd7d77d8d7115c3e52b78cd578536c2820ec` and contains all seven vectors and their hashes. Five reference vectors come from the verified Train streams; the two edge vectors use genuine reducer prefixes. Their fixed scores are:

| Reference | Raw score | Risk PPM | Meets 852,469 |
| --- | ---: | ---: | --- |
| Ordinary | -10.756849 | 152,510 | No |
| Large legitimate | -17.33811 | 57,346 | No |
| Legitimate new beneficiary | -13.532233 | 102,255 | No |
| Takeover deviation | -14.78212 | 84,836 | No |
| Rapid ring forwarding | 16.032776 | 937,008 | Yes |
| Missing history | -5.137154 | 312,391 | No |
| Zero available | 5.92906 | 737,820 | No |

The [native artifact run](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43225461324) tested implementation `88eb898d289233739706961da612c65d9934cfb3` at synthetic merge `9ff1b0c777913130f996eadb303cd782e658adfa`. All seven jobs passed; the [native integration TRX](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43225461324/artifacts/13947128600) records 89 executed and passed Revenant cases, zero skips. The [platform evidence](https://github.com/nightwing9-bit/the-sands-of-time/actions/runs/43225461324/artifacts/13947542793) contains one executed and passed R5 artifact case (zero skips, TRX SHA-256 `e7d30e8b65a61b15d2e5d522bf444136592f4f88e42fddbdd04c2a4d1afb05de`). It tested exact seven-vector parity, 56 concurrent predictions, missing/corrupt ZIP, wrong schema/order, malformed vector, altered calibration and wrong threshold. The published Linux AMD64 Worker ran without network access and reported PASS on .NET 10.0.14 with all seven references, the same model ID and ZIP hash, and threshold 852,469 (report SHA-256 `e15a5648362a2ccf755efbd91eadc7f6e7e78e633167e55ff82009ed9eca76e1`). No claim is made that retraining would reproduce ZIP bytes; the evaluated ZIP itself was retained and pinned.
