# Spec 09 — Revenant V2: isolated counterfactual, five functional screens and audit

**Basket:** 3 | **Dependencies:** spec-04, spec-05, spec-07, spec-08 | **Owner paths:** `src/Revenant` replay application/Worker isolation, `web/apps/revenant`, `tests/E2E`, read-only audit projections

## User value and outcome

Complete five-screen Revenant with actual held workflows, live six-bank flow, v7/v8 isolated causal counterfactual, time-aligned liquidity ghost line and verifiable audit.

## In scope / excluded

**In scope:** Implement the named outcome including its authoritative state and integration tests as detailed below. **Explicitly excluded:** No live-money promise, arbitrary policy comparison not tied to same upstream facts, separate Timeline control, custom unlicensed assets.

**Existing versus new:** This is a planning package, not evidence of existing implementation. A greenfield repo has none of these files. When working in an existing repo, inspect named owned paths and dependent public contracts first; adapt *private* implementation to existing real code without rewriting frozen interfaces. Public interface owner is the spec named by `docs/architecture/03-contracts.md`.

## EARS-style functional requirements and acceptance criteria

**R09.01 — Five real screens.** WHEN a staff user navigates the application, THE SYSTEM SHALL reach Control Tower, Held Queue, Liquidity Fan, Rewind Mode and Audit with real loading/error/empty states.
**R09.02 — Live causal display.** WHEN a Friday Ring payment settles or is held, THE SYSTEM SHALL update bank balances, flow, cases and liquidity views according to durable backend events/queries.
**R09.03 — Case action.** WHEN a staff operator approves/rejects a held case, THE SYSTEM SHALL apply authorization/optimistic concurrency/four-eyes and show committed result, not optimistic false success.
**R09.04 — Replay isolation.** WHEN an authorized analyst branches at chosen event/time with v8, THE SYSTEM SHALL run pure sandbox continuation in separate `Worker.Replay` mode with a PostgreSQL role **unable** to mutate source payment/ledger/case tables, and without exposing a live ledger write interface.
**R09.05 — Counterfactual fidelity.** WHEN v7 and v8 compare, THE SYSTEM SHALL recompute consequences from identical exogenous inputs, include bank liquidity curves and synthetic fraud/false-positive/delay/net effect metrics.
**R09.06 — Alignment.** WHEN a user moves shared Timeline scrubber, THE SYSTEM SHALL show original and alternate values at same logical step or explicitly time-bucketed business timestamp.
**R09.07 — Hash audit.** WHEN a user inspects source after branch, THE SYSTEM SHALL show actual chain verification result, manifest/branch IDs and unchanged live ledger source evidence.
**R09.08 — Failure safety.** WHEN replay cancels/crashes, model missing, or role forbidden, THE SYSTEM SHALL mark incomplete, display reason and leave original posted payments unaffected.
**R09.09 — Demo honesty.** WHEN a user sees metrics/performance, THE SYSTEM SHALL label synthetic sources, model caveats and actual measured speed, no universal 100× claim.
**R09.10 — Outcome finality.** WHEN a synthetic fraud transfer is merely held at the comparison horizon, THE SYSTEM SHALL report it as deferred, never count it as terminally prevented or monetize its principal as avoided exposure.
**R09.11 — Human continuation.** WHEN post-fork recorded analyst actions are replayed under different policy outcomes, THE SYSTEM SHALL apply a pinned deterministic `analyst-continuation-v1` rule with visible evidence for inapplicable/new holds and fail on truly unresolved actions.
**R09.12 — Fresh-run demo.** WHEN a judge begins a second Friday Ring execution, THE SYSTEM SHALL create a new authorized `ScenarioRunId` with equivalent seeded facts and independent durable identifiers; no deletion/reset of existing append-only data.
**R09.13 — Responsive workflows.** WHEN a staff user opens phone/tablet viewports, THE SYSTEM SHALL retain usable navigation, Held Queue decisions, chart/table fallback and touch Timeline seek with accessible loading/error states.

## Non-functional and adversarial requirements

- Relevant source/contract semantics and ownership in `docs/architecture/03-contracts.md` must hold, and no cross-domain dependency may be added. Avoid unnecessary background jobs/abstractions.
- API/auth/realtime commands must validate untrusted input and return typed failures; do not acknowledge persistence or successful replay before actual verification/commit.
- Deterministic or transactional properties require real unit/property/integration evidence; only measured performance can be reported as achieved.
- Work must be done with the actual source repository and tested in the prescribed context, not with invented previous artifacts.

## Exit gate

Five connected screens, real Friday Ring v7/v8 and audit verified; no branch live-ledger writing; test proves source immutable and computed liquidity/metric delta.
