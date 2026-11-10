# Spec 05 — Revenant V1: seeded six-bank causal vertical slice

**Basket:** 2 | **Dependencies:** spec-02, spec-03, spec-04 | **Owner paths:** `src/Revenant`, `src/Revenant.Contracts`, `web/apps/revenant`, `tests/Revenant`

## User value and outcome

First real Friday Ring upstream-input stream, pure deterministic payment/hold/settlement projection and policy v7/v8 branches using Marrow, with a visible bounded UI path.

## In scope / excluded

**In scope:** Implement the named outcome including its authoritative state and integration tests as detailed below. **Explicitly excluded:** No bank ledger DB posting yet (07 owns), no trained ML.NET inference (08), no five polished screens (09), no real bank transactions.

**Existing versus new:** This is a planning package, not evidence of existing implementation. A greenfield repo has none of these files. When working in an existing repo, inspect named owned paths and dependent public contracts first; adapt *private* implementation to existing real code without rewriting frozen interfaces. Public interface owner is the spec named by `docs/architecture/03-contracts.md`.

## EARS-style functional requirements and acceptance criteria

**R05.01 — Scenario reproducibility.** WHEN a six-bank Friday Ring seed and input version are chosen, THE SYSTEM SHALL produce same ordered, labelled, synthetic payment intents including the mule ring and ordinary legitimate traffic on every run.
**R05.02 — Causal evolution.** WHEN a payment intent is screened against pinned policy, THE SYSTEM SHALL produce legal initiated/screened/held-or-cleared/settled projections with balance and liquidity effects, not predetermined chart shapes.
**R05.03 — Policy distinction.** WHEN the same upstream intents replay after fork under v8 rather than v7, THE SYSTEM SHALL record at least one different decision and its computed downstream affected balance/hold trajectory.
**R05.04 — Correct alternative.** WHEN the user selects any valid financial event position, THE SYSTEM SHALL call Marrow restore/fork, run actual pure domain reducer and return aligned original and alternate states/metrics.
**R05.05 — Source protection.** WHEN a v8 branch is created or cancelled, THE SYSTEM SHALL leave source inputs, v7 decision evidence and state/journal hashes unchanged.
**R05.06 — Visible thin UI.** WHEN a user visits Revenant V1, THE SYSTEM SHALL see six banks, payment event list, policy selector, scrub and branch comparison driven only by returned state.
**R05.07 — No false posting.** WHEN a simulated payment is displayed, THE SYSTEM SHALL label projection-only operations until durable DB ledger is supplied by 07; no claim of verified ledger mutation.
**R05.08 — Truth labels.** WHEN a test scenario measures fraud/legitimate decisions, THE SYSTEM SHALL distinguish synthetic known labels from real-world proven fraud and compute metrics from actual input classifications.
**R05.09 — Version pins.** WHEN a branch starts, THE SYSTEM SHALL pin scenario, policy, reducer and input feature versions and fail mismatch rather than use live-current defaults.
**R05.10 — Stable run namespace.** WHEN Friday Ring is started with a new `ScenarioRunId`, THE SYSTEM SHALL produce equivalent synthetic facts for equal seed/version under distinct **run-scoped bank/account starting balances, holds, projections and business/idempotency IDs** without changing a prior run's source or state.
**R05.11 — Prevention versus deferral.** WHEN a fraud-labelled intent is only held at an unfinished horizon, THE SYSTEM SHALL label the state as deferred/pending, not terminal fraud prevented or principal avoided.

## Non-functional and adversarial requirements

- Relevant source/contract semantics and ownership in `docs/architecture/03-contracts.md` must hold, and no cross-domain dependency may be added. Avoid unnecessary background jobs/abstractions.
- API/auth/realtime commands must validate untrusted input and return typed failures; do not acknowledge persistence or successful replay before actual verification/commit.
- Deterministic or transactional properties require real unit/property/integration evidence; only measured performance can be reported as achieved.
- Work must be done with the actual source repository and tested in the prescribed context, not with invented previous artifacts.

## Exit gate

A genuine six-bank v7/v8 branch produces distinct, computed hold/settlement/liquidity states and synthetic metrics through Marrow; source unchanged; V1 browser path works; no claim of DB posting before 07.
