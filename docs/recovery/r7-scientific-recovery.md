# R7 scientific recovery (R7/2) — handoff

Branch `codex/r7-ssa-anomaly-liquidity-20261113`, based on `e305a79`. The changes are local and uncommitted. Nothing has been pushed.

**Verdict: R7 SCIENTIFIC REPAIR IMPLEMENTED — NATIVE QUALIFICATION REQUIRED.** No R7/2 model is qualified or promoted. FinalTest and Challenge have not been opened, and their labels have never been printed.

All numbers below were measured in this session on Linux AMD64 (Docker, `scripts/checks/r7-native-lab.sh`, ML.NET 5.0.1) or with managed code. Reports are under `TestResults/r7-recovery/diagnostics/` (gitignored).

## 1. Confirmed causes

### Data and label defects (generator 3, retained pilot run 43363691834)

- **Anomaly labels could not be identified from the observations.** I compared 12 anomaly windows with 12 legitimate-burst windows that had prior history. Every observable feature separated them at chance level (AUC): event maximum 0.448, total 0.542, duration 0.59, seasonal excess 0.566, trailing rank 0.479, prior mean 0.549. Both classes typically sit at the top of the account's trailing day (rank 0.003) where nothing is expected seasonally. Identical counts carried opposite labels.
- **Ordinary rhythms did not persist from day to day.** The median correlation between consecutive days' smoothed account profiles was 0.050.
- **Ordinary bank changes had no learnable structure.** Changes to available balance were autocorrelated only during stress windows. Banks without stress had autocorrelation of about 0 at lags 1–8.
- **Ceiling from the best linear predictor.** A least-squares AR(24) on changes, trained on root 0 and tested on root 1, scored as follows against persistence:
  - Active MAE at +5 minutes: 94,652 versus 137,401.
  - Active MAE at +40 minutes: 287,308 versus 336,287.
  - Overall MAE at +40 minutes: 56,377 versus 50,318, which is worse than persistence.
  - An oracle fitted in-sample on root 1 was no better than the transferred model, so the limit was the information in the data, not the fitting.

### Algorithm and implementation defects

- **The native p-value has a numerical defect in ML.NET.** `ComputeKernelPValue` keeps a running sum of squared pairwise distances. It adds terms using the `double` raw score but subtracts them using the `float` value stored in the buffer. When every score in the window is equal (runs of zero counts), the true sum is 0. The leftover rounding residue is then either negative, so `Math.Sqrt` returns NaN and `Math.Max(NaN, 1e-6)` stays NaN, or a tiny positive number, which shrinks the bandwidth so far that any nonzero score gets p≈0.
  - Nonfinite p-values across 13,824 scored buckets, by p-value history length: 4,391 at 8, 3,533 at 16, 2,630 at 32 and 1,988 at 64.
  - The share of those that occurred in zero-dispersion windows was 98.9%, 97.8%, 96.8% and 93.4% respectively.
  - In the traced cases, NaN appears exactly when the last nonzero residual leaves the window.
  - A history of 1 removes the NaNs only because the kernel then compares each score with the previous one, which is not a usable p-value.
  - Conclusion: the native p-value cannot be used for zero-inflated counts. It is kept only as a nullable diagnostic.
- **Research and Worker scored differently (Finding G).** The pilot re-centred each origin and replayed a fresh engine. The Worker centred on the opening balance and ran through the full history. This is fixed in §3.
- **Engine state at load is not empty.** A freshly loaded engine starts from the tail of the training series. This was measured: after 2L−2 of the subject's own observations, residuals no longer differ by more than 1e-6 for L = 8, 24 and 289. That bound is now enforced as `SsaAnomalySpec.StateMemory` and checked in `IsValid` for both kinds of model.
- **ML.NET requires training size > 2L (strictly).** It throws otherwise. This is now encoded in the specifications.

### One-factor forecast ablations

All variants were scored at identical origins on the generator-3 unseen root (6 banks) and compared with persistence:

| Variant (one change from L0) | Active skill +5 / +20 / +40 min | Overall nominal coverage +5 / +40 min |
|---|---|---|
| L0: level, w8, train 288, exact rank | −0.430 / −0.479 / −0.928 | 0.870 / 0.942 |
| L1: rank 2 only | −0.399 / +0.001 / −0.089 | 0.719 / 0.672 |
| L2: train on 2,016 points only | −3.045 / −0.732 / −0.356 | 0.982 / 0.976 |
| D0: change representation only | −0.508 / −0.015 / −0.251 | 0.876 / 0.970 |
| D1: change + train 2,016 | +0.138 / −0.003 / −0.145 | 0.981 / 0.975 |
| D3: change, w24, rank 4, train 2,016 | +0.165 / +0.003 / −0.043 | 0.142 / 0.109 |

What these isolate:

- Fixed rank 2 cut active +40-minute MAE from 648,202 to 366,196. It also cut overall coverage from 0.942 to 0.672.
- Changes alone did not help when training on 288 points.
- Changes plus representative training produced the only positive short-horizon skill.
- On generator 3, no variant beat persistence on overall MAE. This agrees with the linear ceiling above.

ML.NET's default stabilization also pulls a level recurrence toward the training reference, because the recurrence has no intercept. A change representation turns that pull into decay toward "no change", which is persistence.

## 2. Generator 4 (`revenant-episode-generator/4`, truth schema 2)

Generator 3 and its pilot are unchanged. The new planner is `src/Revenant/Synthetic/V4LongitudinalEpisodeG4.cs`.

**Population and ordinary behaviour**

- 24 accounts (4 per bank): 6 treasury, 5 commercial, 6 regular, 4 sporadic, 3 quiet.
- Each account keeps the same rhythm every day: session times, length, step and volume. Daily variation comes from bounded jitter, skipped slots and count noise.
- Treasuries settle with persistent bilateral cycles. Major periods are 6/8/12 buckets and minor periods 4/5/10. Amounts vary stochastically, some instances are skipped, there is daily jitter, and there is one explicit period change per root.

**Stress and funding**

- Each root has two developing-pressure drains with random timing and size, and one sudden shock.
- A planned balance walk keeps every payment fundable, with a 2% reserve. `V4LongitudinalPlannerG4Tests` checks this invariant for all 26 roots without printing any labels.

**Injected episodes and truth**

- Injected episodes start on day 4, eight per day.
- Mechanisms: off-schedule, amplitude surge, sustained elevation and dormant burst. Treasury drains and recoveries are labelled separately as `TreasuryDrain`.
- Statistical truth comes from a Monte Carlo oracle that simulates the account's own ordinary process 4,000 times on an independent random stream. A window is Anomalous if P ≤ 0.001, Ambiguous if P ≤ 0.05, otherwise Normal. Ambiguous windows, and injected windows judged Normal, are excluded: an alert there counts as neither a hit nor a false alert.
- Business intent (Legitimate or FraudAssociated) is recorded separately from statistical truth.
- Commercial sessions are labelled as hard negatives.

**Census on the pilot plans** (`generator-4-census.json`, from the earlier census run, which still used day-2 injections)

- Median consecutive-day profile correlation: commercial 0.83–0.87, regular 0.68–0.73, treasury 0.69–0.83. Sporadic and quiet accounts have no rhythm by design.
- Positives versus hard negatives: event maximum AUC 0.192, total AUC 0.103. Positives have smaller counts than the hard negatives, so a count threshold does not work.
- Trailing-rank AUC 0.465, seasonal-excess AUC 0.666, start-of-day AUC 0.643. Time of day carries some signal but is not a deterministic shortcut.
- Some episodes come out Ambiguous or Normal, and those are reported.

## 3. One shared inference implementation

| Concern | Location |
|---|---|
| Pure contract: transforms, eligibility, decoding, conformal arithmetic, alert state, manifest | `src/Revenant/Intelligence/SsaAdvisoryContract.cs` (`SsaAdvisory`, `SsaAnomalySpec`, `SsaAlertPolicy`, `SsaAlertState`, `SsaForecastSpec`, `SsaIntervalPolicy`, `SsaAdvisoryManifest`) |
| ML.NET fit, score and forecast (single path) | `src/Hosts.Worker/RevenantSsaInference.cs`: `FitAnomaly`, `FitForecast`, `ScoreAccount`, `ForecastBank` |
| Published Worker | `RevenantAdvisoryWorker.Score`: validates the manifest and ZIP, calls `ScoreAccount`/`ForecastBank`, and returns the step at the requested seal |
| Research, qualification and development | `R7QualificationEvaluationTests.DevelopAnomaly`/`DevelopForecast`/`Judge`, `R7Generator4DevelopmentTests`. These call the same methods; `R7Evaluation` only measures. |

**How each call works**

- One engine is loaded from the immutable artifact bytes, and the subject's verified series is consumed in order with one `Predict(observation)` per completed seal.
- There is no re-centring, no replay, no refitting and no future observation.
- Outputs before the warm-up are discarded.
- The test project compiles the Worker's source files directly (linked in `Revenant.Tests.csproj`).

**Parity test (`R7InferenceParityTests`, category `R7Parity`), passed natively**

- Worker scoring on a causal prefix equals the research output at that bucket, bit for bit, for both the anomaly and forecast models.
- A cold restart reproduces identical evidence bytes.
- A prefix one bucket short of the warm-up reports `InsufficientHistory`.
- A child with a 175,000-minor-unit hold from bucket 1,200 matches its source at every inherited origin and differs at the fork.
- A tampered ZIP is reported as `ArtifactInvalid`.

**Evidence and storage**

- New schemas `anomaly-evidence/2` and `liquidity-forecast-evidence/2`, policy `revenant-ssa-advisory/2`. Schema-1 encoding is unchanged.
- Additive migration `0014_r7_advisory_evidence_v2.sql`. The pre-V4 and pre-R3 fixture removals and the upgrade assertion were updated to match.
- The legacy R7/1 procedures were renamed `R7LegacyV3Procedures`. They exist only to reproduce archived evidence.

## 4. Anomaly model (R7/2)

**Design.** `DetectSpikeBySsa` on raw five-minute counts:

- Seasonal window L = 576 (two days), so the fitted recurrence averages the account's last two days.
- Training window of 1,160 counts from one training account. The account is chosen by the strongest lag-288 autocorrelation in its first window.
- Warm-up of 1,152 buckets, which is at least 2L−2.
- SSA expected count μ = input − native raw score.
- Alert statistic: −log10 P(Poisson(Σμ) ≥ Σc) over the last 6 buckets (30 minutes), with μ floored at 0.02. An alert also requires the current count to be above zero.
- The threshold is calibrated on calibration roots to 75% of the budget, for budgets 0.05, 0.1 and 0.2.

The residual still comes from the fitted SSA. The Poisson tail fixes a measured failure of residuals in transformed units: two intents where μ = 0.05 and five where μ = 1.8 scored about the same (see the contract test).

**Development measurement.** Generator-4 plan-derived counts on Pilot roots: root 0 trains, roots 1–2 select and calibrate, roots 3–5 measure (94 positives, 212.9 normal account-days).

| Detector | Budget 0.05: recall / false alerts per day | Budget 0.1 | Budget 0.2 |
|---|---|---|---|
| SSA two-day, Poisson window 6 | 0.362 / 0.014 | **0.564 / 0.038** (precision 0.869, median delay 15 min) | 0.734 / 0.047 |
| SSA two-day, Poisson window 3 | 0.213 / 0.019 | 0.330 / 0.033 | 0.606 / 0.066 |
| SSA one-day, Poisson window 3 | 0.106 / 0.038 | 0.160 / 0.052 | 0.351 / 0.127 |
| SSA two-day, Anscombe residual | 0.160 / 0.005 | 0.202 / 0.042 | 0.234 / 0.047 |
| Trailing-rank comparator | 0 / 0 | 0 / 0 | 0 / 0 at the 0.2 threshold, but 0.467 recall at 0.182 false alerts per day elsewhere |
| Seasonal-mean heuristic (diagnostic only) | 0.191 / 0.009 | 0.191 / 0.014 | 0.319 / 0.094 |

Window-6 detections at budget 0.1, by mechanism:

| Mechanism | Detected / events |
|---|---|
| Off-schedule | 12/26 |
| Dormant burst | 4/11 |
| Amplitude surge | 7/15 |
| Sustained elevation | 17/19 |
| Treasury drain | 13/23 |

Commercial hard negatives produced 7 of the 8 false alerts.

These counts are plan-derived. The PostgreSQL export asserts that planned intents equal committed intakes, so the counts should match once the roots are executed, but that equality still has to be confirmed on executed streams.

The window-6 detector was found during development; it is not a frozen result. Qualification selects again among the six predeclared candidates using Validation.

## 5. Forecast model (R7/2)

**Design.** `ForecastBySsa` on completed-interval changes:

- Change δ_t = (A_t − A_{t−1}) / s_b, with A_{−1} equal to the opening balance and s_b = opening / 1,000. The model's point forecasts do not depend on the scale.
- Window 24, series length 96, exact rank. The model is trained on the full 2,016-point history of the training-root bank with the largest change variance, which includes developing pressure.
- Warm-up of 288.
- Levels are decoded as Â_{t+h} = A_t + s_b·Σδ̂. The product target is unchanged: eight future available-balance levels.
- The native per-change bounds are summed only as a labelled diagnostic.
- The level band is a horizon-wise split-conformal band on |error| / σ_t. σ_t is the mean absolute change over the last 12 intervals, floored at s_b. The multipliers are frozen on calibration roots at 95%.
- Candidates: change w24 and w12, and level w24 as a control. Selection requires an overall ratio ≤ 1.05, then the best active skill.

**Not yet measured.** Forecast quality on generator-4 PostgreSQL data has no result yet. The executed pilot is in its export phase (see §6).

**Expected behaviour.** Under developing pressure the model should help once the first installments are visible. Sudden shocks are not forecastable before they happen. The generator-3 evidence shows that SSA cannot beat persistence overall when ordinary flows carry no structure.

## 6. Codex continuation

**1. Collect the running generator-4 pilot.**

- If the local run finishes, its output is `TestResults/r7-recovery/g4-pilot` (log `/tmp/pg-native.log`).
- Otherwise, regenerate it on Linux x64: `Generate_the_generator_4_PostgreSQL_development_pilot` with `REVENANT_R7_GENERATION_TIMEOUT_MINUTES=120`.
- Writing facts for the 3 roots took about 10 minutes on native ARM64. Under Rosetta it was more than 3 times slower, and the default 35-minute timeout is too short.

**2. Run the forecast development experiment.**

- Test: `Develop_and_measure_forecast_candidates_on_generator_4_PostgreSQL_streams` (category `R7DiagnosticsNative`) with `REVENANT_R7_PILOT_INPUT` pointing at that pilot.
- It reports, all on the same origins: level versus change, windows 12/24/48, fixed rank 6, a 288-point training history, a calm training bank, the AR ceiling, conformal coverage by slice, and warnings.
- If the change models beat persistence on the overall ratio, keep the forecast candidates.
- If not, the next one-factor change to try is a lower fixed rank. Do not change the generator to rescue the model.

**3. Re-run anomaly development on executed streams.**

- Re-run `Develop_and_measure_anomaly_candidates_on_plan_derived_generator_4_counts` and check that executed counts equal planned counts.

**4. Generate the cohort and qualify.**

- Generate the 20 generator-4 cohort roots. The existing matrix and split sizes are unchanged.
- Run `Select_on_validation_and_freeze_calibration_before_final_data`, then `Evaluate_the_frozen_SSA_models_on_final_and_shifted_histories`.
- Promotion gates are unchanged from R7/1:
  - Anomaly: false alerts ≤ 0.1 per normal account-day, more detections than the comparator, and false alerts no higher than the comparator.
  - Forecast: active skill ≥ 0.05, overall ratio ≤ 1.05, calibrated coverage ≥ 0.90 at every horizon, at least half of developing crossings warned, and warning precision ≥ 0.5.

**5. Prove the published Worker.**

- Publish the frozen artifacts under `Intelligence/Artifacts/revenant-ssa/{kind}/{manifest-sha}/`.
- Run `Published_worker_writes_idempotent_pinned_SSA_evidence`. It now uses generator 4 with a seal at bucket 1,160.
- Add `R7Parity` against the executed pilot to CI.

**6. Run the regression suites.**

- Run the full Release and PostgreSQL suites. The upgrade test now expects 5 migrations.
- Locally passed: the Architecture tests (11), the SSA contract tests (12) and the generator-4 invariant tests (7).

**Must not be weakened**

- The gates in step 4.
- Root separation between partitions.
- Freezing everything before FinalTest is read.
- Excluding Ambiguous windows by rule, not by inspection.
- No output based on native p-values.
- No intervals widened beyond the conformal rule.
- R5, R6 and generator 3 artifacts stay unchanged.

**Open risks**

- The two-day window leaves only three scored days per 7-day root.
- 14-day roots would allow a three-day window but would double PostgreSQL cost.
- Forecast learnability on generator 4 is an unverified hypothesis until step 2 has run.

## 7. R8 compatibility

The design fits R8 structurally:

- Models are trained offline, and each manifest binds its preprocessing.
- Each call starts from a fresh engine and consumes only the subject's verified prefix, so the same artifact can be shared by source and child without sharing state.
- Outputs are advisory evidence only.

R7 is not scientifically ready until the qualification and the native Worker proof in §6 have passed.
