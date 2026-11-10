# Spec 08 — Revenant V2: ML.NET risk, anomaly, liquidity forecast and explanations

**Basket:** 3 | **Dependencies:** spec-05, spec-07 | **Owner paths:** `src/Revenant/Intelligence`, feature extraction/config/artifacts, Worker, `tests/Revenant` model evaluation

## User value and outcome

Four honest versioned ML.NET capabilities with reproducible held-out evidence and causally fed anomaly/fraud/forecast signals for the financial decision pipeline.

## In scope / excluded

**In scope:** Implement the named outcome including its authoritative state and integration tests as detailed below. **Explicitly excluded:** No Python service, online retraining during demos, claim of bank-grade accuracy, explaining local decisions with unqualified global importance.

**Existing versus new:** This is a planning package, not evidence of existing implementation. A greenfield repo has none of these files. When working in an existing repo, inspect named owned paths and dependent public contracts first; adapt *private* implementation to existing real code without rewriting frozen interfaces. Public interface owner is the spec named by `docs/architecture/03-contracts.md`.

## EARS-style functional requirements and acceptance criteria

**R08.01 — Model provenance.** WHEN a model is trained/loaded, THE SYSTEM SHALL record dataset source, preprocessing/features, split, metric, artifact checksum/runtime and policy/feature version.
**R08.02 — Fraud classification.** WHEN a synthetic payment enters screening, THE SYSTEM SHALL score through version-pinned trained ML.NET classifier and record score+feature/model evidence that influences policy decision.
**R08.03 — Anomaly signal.** WHEN velocity behavior changes, THE SYSTEM SHALL compute ML.NET anomaly score on actual defined sequential feature data and present grounded reason/threshold rather than arbitrary warning.
**R08.04 — Liquidity forecast.** WHEN holds/settlements update bank balance trajectory, THE SYSTEM SHALL consume actual causal time series in SSA forecast with central path and uncertainty bands and shortfall only when computed threshold crossed.
**R08.05 — Explainability.** WHEN a payment is explained, THE SYSTEM SHALL provide verified method-specific per-record contributions or explicitly qualified model/rule feature evidence, no false local attributions.
**R08.06 — Counterfactual model consistency.** WHEN v7/v8 branch re-evaluates inputs, THE SYSTEM SHALL use pinned policy/model/feature artifacts, replay-safe inference evidence, and fail incompatible versions instead of re-training.
**R08.07 — Evaluation transparency.** WHEN training/evaluation job completes, THE SYSTEM SHALL report held-out precision/recall, confusion, class balance, forecast backtest error and known synthetic limitations.
**R08.08 — Outage fallback.** WHEN model artifact unavailable or invalid, THE SYSTEM SHALL fail closed or choose a clearly versioned explicit rules-only fallback with honest UI state; never silently show fabricated risk score.
**R08.09 — No game dependency.** WHEN GhostLap solution imports are checked, THE SYSTEM SHALL have zero ML.NET or Revenant model dependency.

## Non-functional and adversarial requirements

- Relevant source/contract semantics and ownership in `docs/architecture/03-contracts.md` must hold, and no cross-domain dependency may be added. Avoid unnecessary background jobs/abstractions.
- API/auth/realtime commands must validate untrusted input and return typed failures; do not acknowledge persistence or successful replay before actual verification/commit.
- Deterministic or transactional properties require real unit/property/integration evidence; only measured performance can be reported as achieved.
- Work must be done with the actual source repository and tested in the prescribed context, not with invented previous artifacts.

## Exit gate

All four ML.NET paths execute with authentic manifest/evaluation artifacts and grounded explanations; causal forecast and pinned branch inference tests work; no invented accuracy numbers.
