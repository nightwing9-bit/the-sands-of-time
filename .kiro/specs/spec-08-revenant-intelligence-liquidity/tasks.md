# Spec 08 implementation tasks — Revenant V2: ML.NET risk, anomaly, liquidity forecast and explanations

**Execute tasks in listed order.** Dependencies: spec-05, spec-07. Only check a box after actual changed files, tests and observed proof exist. Tasks are bounded work packages, **not AI chat prompts**. The user-supplied context is a requirements baseline, not permission to invent completed code. For actual code inspection, open the relevant named paths plus narrow dependent contract/migration/tests; follow a genuine dependency when discovered, but do not load the entire repo automatically.

## Ordered implementation tasks

- [ ] **8.1 ETL and feature-version baseline**
  - **Precondition/input & inspect:** scenario inputs, available PaySim source/license, model manifests. Completed prerequisite spec exit gate must already have evidence.
  - **Produce/modify:** deterministic preprocessing/split and leakage tests.
  - **Forbidden changes:** ground-truth leakage into predictors or downloading unstated data. Do not change frozen shared public contracts without ADR.
  - **Verification:** group/temporal split assertions and dataset manifest hash.
  - **Done when:** training lineage reproducible.

- [ ] **8.2 Fraud classifier and actual risk integration**
  - **Precondition/input & inspect:** ML.NET APIs via targeted Context7, policy v7/v8 adapters. Completed prerequisite spec exit gate must already have evidence.
  - **Produce/modify:** trained versioned artifact, score evidence and held-out report.
  - **Forbidden changes:** claimed quality without test metrics or real-time training. Do not change frozen shared public contracts without ADR.
  - **Verification:** real evaluation script, score consistency across restart.
  - **Done when:** risk influences actual hold flow.

- [ ] **8.3 Anomaly plus honest explanation**
  - **Precondition/input & inspect:** velocity stream feature windows and classifier type. Completed prerequisite spec exit gate must already have evidence.
  - **Produce/modify:** anomaly pipeline, versioned threshold, grounded reason API.
  - **Forbidden changes:** global feature importance mislabelled as local cause. Do not change frozen shared public contracts without ADR.
  - **Verification:** known velocity spike vs baseline fixtures and explanation audit.
  - **Done when:** visible case reasons trace to real features.

- [ ] **8.4 SSA forecast and causality**
  - **Precondition/input & inspect:** balances from 07 and branch source from 05. Completed prerequisite spec exit gate must already have evidence.
  - **Produce/modify:** forecast horizon/bands/error, shortfall threshold from predicted values.
  - **Forbidden changes:** fixed illustrated 40m warning detached from payments. Do not change frozen shared public contracts without ADR.
  - **Verification:** backtest with held/settled scenarios; interval semantics tests.
  - **Done when:** forecast reacts to causal settlement state.

- [ ] **8.5 Pin inference in Timeline**
  - **Precondition/input & inspect:** spec-02 manifest/version and 05 domain adapter. Completed prerequisite spec exit gate must already have evidence.
  - **Produce/modify:** model/feature evidence recording, incompatible version handling, branch deterministic mode.
  - **Forbidden changes:** mutable online predictor without version identity. Do not change frozen shared public contracts without ADR.
  - **Verification:** original hash reproducibility; v7/v8 semantic branch test.
  - **Done when:** counterfactual engine can consume pinned model outputs.

## Spec exit verification (blocking)

All four ML.NET paths execute with authentic manifest/evaluation artifacts and grounded explanations; causal forecast and pinned branch inference tests work; no invented accuracy numbers.

Minimum proof includes successful targeted automated tests and relevant integration observation **actually performed**. Record real result paths, commands, commit IDs, encountered defects and measured Kiro credit use (if observable) in `docs/kiro-log.md`; if any check cannot run, mark blocker and leave task/spec incomplete. Do **not** infer a passing test from this document.
