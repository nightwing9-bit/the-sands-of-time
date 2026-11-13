# R7 final scientific decision (R7/3)

Basis: `r7-g4-development-gate.md` at `4d2ec87`, plus `r7-scientific-recovery.md`. No new experiments were run for this decision. Every claim below is either a measured result cited from those documents, a derivation, or a hypothesis that is marked as such.

## A. Root cause

### Demonstrated

1. **The change-series SSA adds error that persistence does not.**
   - Its +5-minute MAE was 36,477 against 23,904 for persistence. At +40 minutes it was 203,682 against 116,861.
   - Signed bias grows almost exactly linearly: +317 per 5-minute horizon step, reaching +2,546 at +40.
   - The 288-point fit, which is closer to "no change", scored +6.6% active skill and a 1.032 overall ratio.
   - The full 2,016-point fit scored −39.8% and 1.706.
   - A pooled least-squares AR also lost to persistence. Because AR is not SSA, the cross-bank linear predictor itself does not transfer. This is not specific to how SSA was fitted.

2. **Generator 4 gives each bank pair its own randomly drawn cycle period, redrawn per root.**
   - The code shows this: `AssignCycles` picks the major period from {6, 8, 12} and the minor from {4, 5, 10} for each pair.
   - One fixed linear recurrence (from SSA or AR) can only extrapolate the frequencies present in its training bank. On a bank with different periods it predicts payments at the wrong times. With a 0/1 spike structure, a misplaced spike costs about twice as much as predicting no change.
   - This is a property of transferring any fixed linear recurrence, independent of SSA's fitting or state handling. Prefix parity and state memory have already been verified.
   - The daily account rhythm transfers because its frequencies (the daily harmonics) are the same for every account; only phase and amplitude differ, and the engine state supplies those.

3. **Point forecasting and crossing warnings pull in opposite directions.**
   - MAE is minimized by the conditional mean. Developing pressure is rare and starts unpredictably, so the mean shrinks toward "no change".
   - The short-training model learned from calm day 0, without the trend component a drain needs. It therefore stayed near persistence: better MAE, no warnings.
   - A warning concerns the event A_{t+h} < floor, which is a lower-tail quantity.

### Derived, not yet measured

4. **Drains are forecastable once they start.**
   - Each drain is a run of equal debits at a fixed gap of 1–3 buckets, 8–16 installments long.
   - It drives the bank to 4–8% of opening, so the 10% floor is crossed at roughly 93–97% of the drain. By then at least 7 installments have been observed.
   - The leading information exists in the past of A_t. A predictor that continues an observed run can warn up to 8 horizons ahead. Persistence never can.

5. **Bilateral cycle payments are forecastable from a bank's own history if the period is known.**
   - Amounts vary ±15%, about 8% of instances are skipped, and timing jitters ±3 buckets per day.
   - **Hypothesis:** a model whose frequency set matches the bank recovers most of the cycle error. Test T1 decides this.

### Not identifiable from current causal inputs

- When a drain or shock starts.
- The size of a sudden shock.
- The timing of a regime switch before it happens.
- Individual retail customer instructions.

The ledger has no pipeline lead. Intents settle in the same seal, and a reservation changes A only when the hold is placed and when it is released. Settlement and reservation history therefore adds no causal information beyond A_t itself, so I do **not** propose a multivariate model.

## B. Anomaly decision: keep the hybrid, change the tail distribution, fix the gate

**Within budget, but not yet established statistically.** 7 false alerts over 71.26 normal account-days is 0.0982 per account-day, inside the 0.1 budget. The 95% Poisson interval is about 0.039–0.20, so one root cannot establish compliance; the cohort must.

**The comparator gate was ill-posed, and I corrected it** in `R7QualificationEvaluationTests.Judge`.
- The old condition required SSA burden ≤ comparator burden. A comparator that never alerts has zero burden, so the condition rewards silence.
- The new condition requires SSA recall to exceed each comparator's recall (trailing rank and seasonal excess) at **equal realized burden**. Each comparator gets its best threshold on the same evaluation cases with false alerts no higher than the SSA realized. Choosing that threshold with test labels favors the comparator, so the test is conservative.
- In development, both comparators detected 0 and 2 of 23 at zero burden. They should also be reported at matched burden.

**One mathematically justified change: a dispersion-adjusted tail.**
- The diagnostic is commercial lag-one residual correlation of 0.59.
- For a 6-bucket window sum with AR(1) correlation ρ, Var(ΣC) ≈ M·(1 + 2Σ_{k=1}^{5}(1 − k/6)ρ^k). At ρ = 0.59 that is about **2.76× the Poisson variance**. Session jitter moves a block of mass across adjacent buckets, which makes the counts correlated.
- So the Poisson tail is overconfident on exactly the accounts that produced 5 of the 7 false alerts. Quiet accounts have ρ ≈ 0 and are unaffected.
- Replace the Poisson tail with a negative binomial of mean M = Σμ and variance φ_{a,t}·M:
  - size r = M / (φ − 1), and r → ∞ gives the Poisson case.
  - φ_{a,t} = clamp(median{(C_w − M_w)² / M_w} / 0.4549, 1, 10).
  - The median is taken over the account's previous 96 non-overlapping 6-bucket windows with M_w ≥ 0.5, excluding the current window.
  - 0.4549 is the median of χ²₁. The median keeps φ robust against earlier anomalies.
  - This is causal and specific to each account, and it uses no labels.
- Keep the fitted SSA expectation μ (from the preceding bucket, as corrected in `4d2ec87`), the 0.02 floor, and threshold calibration at 75% of budget.
- In selection, the negative-binomial candidate goes alongside the current Poisson w6 candidate. Adopt it only if Validation recall at matched burden is not lower.

## C. Forecast decision: ML.NET change-series SSA on a shared settlement schedule (G5)

**Model**
- Keep `ForecastBySsa` on δ_t = (A_t − A_{t−1}) / s_b, with A_{−1} = A_opening and s_b = ⌊A_opening / 1000⌋.
- Decode levels as Â_{t+h} = A_t + s_b·Σ_{j≤h} δ̂_{t+j}.
- Window L = 24 (4 cycles of 6 buckets, 2 of 12), series length 96, exact rank, non-adaptive, stabilized.
- The level representation is rejected. It measured −232% active skill: with no intercept and stabilization, the recurrence pulls toward the opening reference.

**Why this can beat persistence once the schedule is shared**
- The recurrence then extrapolates frequencies that every bank has.
- Each bank's own state, after a warm-up of at least 2L − 2 = 46 (kept at 288), supplies its phase and amplitude. This is the same mechanism that already works for the anomaly model.
- A run of equal debits is the zero-frequency (or, for a gap of 2, the Nyquist) component of δ. A stabilized recurrence with that component continues the run while it lasts.

**Training**
- Fit on one Train-root bank's full, contiguous 7-day δ history (2,016 points). The bank must contain at least one DevelopingPressure label; selecting a training subject with training labels is permitted.
- The 576-point variant is the only predeclared one-factor alternative.
- Do not concatenate roots.

**Where it will still fail**
- Drain onset, sudden shocks and the first bucket after a schedule switch. These are reported separately and are not gated.
- If T1 fails, the cycle component is not learnable from univariate history, and G5 must not be adopted (see F).

## D. Warning decision: a separate objective with calibrated lower quantiles

**Interval**
- Mondrian (group-conditional) split conformal on the level error e_{t,h} = A_{t+h} − Â_{t+h}.
- σ_t is the mean |ΔA| over the last 12 intervals, floored at s_b.
- Groups are terciles of σ_t / s_b with edges fixed on Calibration. In each group and horizon, the multipliers are empirical quantiles of e / σ.
- This targets coverage *within* volatile states. The current marginal band over-covers calm periods (97%) and under-covers stress (38–74%).
- This is not arbitrary widening. Each group's band is the empirical error quantile for that group, so calm-period bands get narrower.

**Two-sided band (reported):** quantiles 0.025 and 0.975.

**Warning rule**
- Issue a warning at origin t if A_t ≥ F (F = A_opening / 10) and min_h L^{(β)}_{t,h} < F.
- L^{(β)}_{t,h} = Â_{t+h} − q^{(β)}_{g,h}·σ_t is the one-sided lower β-quantile bound.
- Choose β ∈ {0.5, 0.25, 0.1} on Calibration as the value with the highest developing-crossing recall subject to precision ≥ 0.5. Freeze it.
- β = 0.5 is the central-path rule.

**Evaluation**
- Count events: warnings are coalesced over 8 buckets. A warning is true if a downward floor crossing occurs in (t, t+8].
- Report precision, developing-crossing recall, lead time (first true warning to crossing, in minutes), and false warnings per bank-day.
- Sudden-shock crossings are reported but excluded from recall.
- Developing crossings number only about 8 in FinalTest, so also report the Brier score of the implied P(cross within 40 minutes) as a continuous check.

## E. Generator decision: G5 (`revenant-episode-generator/5`), one causal change, only if T1 and T2 confirm

- Treasury settlement follows a **system-wide clearing schedule**: major bilateral cycles every 6 buckets and minor cycles every 12, for all banks in a root. This is how deferred net settlement works.
- Amounts, imbalance, skips, phase jitter for each pair, drains, shocks and holds stay exactly as in G4.
- The explicit regime change becomes one pair switching between the two system periods.
- The shifted Challenge uses a different system schedule (8/16) as a genuine out-of-distribution test.
- No other change.
- The justification is institutional realism, and the fact that G4's randomly drawn periods made transfer of any fixed model unidentifiable. It is not that the model favors it.
- G4 and its evidence stay as development history.

## F. Codex scientific gate (existing G4 pilot first)

**T1 — learnability from the bank's own history** (decisive, research only, no promotion)
- Setup: on root 2, fit change-SSA (L24) on each bank's own buckets 0–575 and score buckets 576–2015.
- Repeat with a least-squares AR(24) on the same split. This is managed code and needs no ML.NET.
- Pass: the median per-bank overall ratio is ≤ 0.95 against the stronger of persistence and the trailing median, *and* active skill is ≥ +10%, for SSA or for AR.
- Fail: the cycle structure is not learnable at this noise level. **Do not create G5.** Declare the cycle component an information limit. Recast forecast value as drain continuation, and gate only D on the existing G4 data.

**T2 — the mismatch is the cause** (managed)
- Compute periodograms of δ by bank.
- Confirmation: the dominant periods differ between the training bank and the measurement banks, *and* in T1 the bank's own fit beats the BANK-B transfer by at least 25% overall MAE.

**T3 — warning policy on G4 root 2, with Calibration on root 1**
- Use the current artifact and Mondrian bands, with no refit.
- This measures D independently of point accuracy.
- Report whether any β warns at least 1 of the 2 developing crossings with precision ≥ 0.5. Development only; not a gate.

**If T1 and T2 pass:** implement G5, generate 3 G5 PostgreSQL pilot roots, and rerun the forecast and anomaly development.

**G5 pilot pass criteria, all predeclared:**
- Overall ratio ≤ 1.0.
- Active skill ≥ +10%.
- Two-sided Mondrian coverage ≥ 0.90 at every horizon, overall and in the highest-σ group.
- At least half of developing crossings warned with median lead ≥ 10 minutes.
- Warning precision ≥ 0.5.
- Anomaly: false alerts ≤ 0.1 per normal account-day, and recall above both comparators at matched burden.

**Then:** the 20-root cohort, freeze, FinalTest and Challenge, with the R7/1 promotion gates unchanged except for the corrected anomaly comparator rule.

**The proposal is invalidated if any of these occurs:**
- T1 fails.
- G5 own-schedule transfer still has an overall ratio > 1.0.
- The warning recall at precision ≥ 0.5 comes only from β where the band is so wide that calm-period false warnings exceed 0.05 per bank-day.

## G. Handoff, in priority order

**Scientific work**
1. T1, T2 and T3 harness in `R7Generator4DevelopmentTests`, including an own-bank fit variant. Uses existing data only.
2. Mondrian conformal and the β warning rule:
   - `SsaIntervalPolicy`: group edges, multipliers per group, β.
   - `SsaAdvisory.Band`.
   - `RevenantSsaInference.ForecastBank`.
   - `R7Evaluation.ConformalMultipliers`.
   - `R7ForecastMetrics.Warnings`: band-based warnings and Brier score.
3. Negative-binomial tail: `SsaAlertStatistic.DispersionAdjustedSurprise`, `SsaAlertState` (rolling robust φ), and a regularized-incomplete-beta tail in `SsaAdvisory`.
4. G5 cycle schedule in `V4LongitudinalEpisodeG4.cs` (`AssignCycles` only), as a new version constant and roots. Only if T1 and T2 pass.

**Routine work**
- Manifest and evidence fields for β, group edges and φ: an additive `/3` schema and migration.
- Worker receipt fields.
- Native parity rerun.
- PostgreSQL generation, cohort and qualification jobs.
- Published-Worker proof and CI filters.

**Already done in this session:** the matched-burden comparator gate in `R7QualificationEvaluationTests.Judge`. It compiles; it has not been run.
