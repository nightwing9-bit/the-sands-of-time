# Spec 09 design — Revenant V2: isolated counterfactual, five functional screens and audit

## Preconditions, owned implementation and boundaries

**Prerequisites (completed observable exit gates):** spec-04, spec-05, spec-07, spec-08. **This spec owns:** `src/Revenant` replay application/Worker isolation, `web/apps/revenant`, `tests/E2E`, read-only audit projections. **Do not implement:** No live-money promise, arbitrary policy comparison not tied to same upstream facts, separate Timeline control, custom unlicensed assets.

**Consumes:** Public contracts from spec-04, spec-05, spec-07, spec-08; inspect only their exported interfaces, named migrations and relevant tests. **Exposes:** The implemented behavior and stable public contract of this spec for downstream dependents. No spec may rewrite `Marrow.Timeline` public or `web/packages/timeline-ui` contracts without an approved cross-app ADR and dual-client test.

**Architecture authority:** `docs/architecture/01-master.md` defines dependency direction and state authority, `03-contracts.md` defines exact conceptual interfaces. This design defines this spec's *unique implementation ownership*; prefer whichever private class names fit the repo once validated, but preserve semantics. A superior nonbreaking local algorithm is allowed; shared ABI/persistence schema/version/security authority is not a local choice.

## Implementation strategy, contracts and data flow

### Worker boundary, no live posting capability

Run the existing `Hosts.Worker` binary in a distinct **`Replay` process mode** (`FinanceReplayRunner`), separate from `LiveOps`. Replay accepts authorized `(sourceId,position,policyRef,operationKey)` from API, verifies source version/ownership, pulls canonical *exogenous* intents, ordered external action facts and pinned feature/model evidence, restores pure financial state from a shared Marrow checkpoint and calls the deterministic reducer registered for that history version using spec-08 recorded evidence. Its DI graph **must not contain** `ILiveLedgerWriter`/settlement DbContext write transaction or live account repository. The Replay process has a separate DB credential with tenant/actor-authorized SELECT access to historical payment/journal/ledger/feature sources and INSERT/UPDATE on `revenant.counterfactual_*` results; it also receives **only the Marrow Timeline branch-write privileges** needed for new immutable-prefix child histories through the Timeline port (never original verified-source updates/appends). All writes to original payment/ledger/case/settlement tables fail under PostgreSQL grants. The privileged LiveOps credential is not configured or available inside Replay, even for raw SQL. No second Worker codebase is created. Store result in `revenant.counterfactual_run` / `counterfactual_point` tables isolated from live journal/ledger, manifest with source/policy/version/assumptions and state hashes. A Worker crash leaves `Pending|Running|Failed|Verified` explicit; duplicate operation key returns same job; cancellation never marks verified. Source original chain/ledger row count and hash checked before/after. Replaying source in read-only mode verifies original recorded actual outcomes; alternate recomputes from upstream facts and externally committed actions under versioned **`analyst-continuation-v1`**: replay an original recorded analyst action only when the same stable payment/case identity exists and the action's legality/preconditions still hold in the branch; otherwise record a visible `InapplicableUnderBranch` evidence item (do not silently drop it). Newly held payments remain `PendingReview` through the chosen comparison horizon; do **not** invent an analyst approval. An ambiguous/unmodelled externally consequential action is an explicit `UnsupportedManualAction` replay failure. Sort original actions by the same stable logical order and pin this policy version in the branch context. Inference supplied by pinned spec-08 evidence.

Replay resolves the source's versioned reducer: V1 remains supported unchanged within V1; reviewed/model-enabled runs consume 07's ordered intent/review/return contract and 08's canonical evidence. Do not add review/ML semantics to frozen Spec-05 V1 by context substitution. Source and branch inherit matching reducer/schema keys from creation. Predictors run outside the pure reducer only to prepare new branch evidence from branch causal features; later cold replay uses exact recorded evidence without inference. Source evidence cannot be reused merely by payment ID. Apply `analyst-continuation-v1` to the recorded ordered action facts, preserving explicit inapplicability and unresolved-action failure.

### Financial metrics and boundary definitions

Compare matching scenario horizon, per event and per minute bucket (business timestamp is display alignment only). `fraudPrevented` counts synthetic-labelled suspect transfers that v7 settled and v8 **terminally rejected/returned before settlement**, not a still-held payment; separately report `fraudDeferredAtHorizon` for pending holds, excluded from principal savings; `legitimateDelayed` counts labelled legitimate with later settlement vs baseline by horizon; `falsePositiveCostMinor` = 250 USD minor units per newly held/rejected labelled legitimate; `incrementalDelayCostMinor` = 10 USD minor units per additional whole minute delay relative to v7, capped at 120 minutes/payment, with uncompleted alternatives counted through comparison horizon; `fraudExposureAvoidedMinor` sums only **terminally prevented (never-settled)** synthetic fraud principal in the alternate, `netEffectMinor` = exposure avoided − false-positive costs − incremental delay costs (all under synthetic `counterfactual-cost-v1` assumptions); display gross counts separately to prevent hiding tradeoffs. Unmodelled losses or causal downstream costs cannot be added arbitrarily. A balance/available-vs-posted overlay must distinguish reservations from settled money; a shortfall alert depends on computed scenario+forecast output.

### Complete screen routing

- **Control Tower:** six-bank arcs and live actor/source-labelled activity, balances and alert pulse; request state from DB/API, SignalR only as refresh hint with sequence.
- **Held Queue:** case list filters, actual risk/anomaly features/ML evidence, approve/reject, four-eyes confirmation status and stale-case conflict UI.
- **Liquidity Fan:** chart with computed point/interval, alert and affected bank; distinguish model forecast from actual balances.
- **Rewind Mode:** one shared scrubber, event-to-time adapter, v7/v8 chosen policy, async job progress/cancellation, side-by-side plot and metrics, branch permalink if authorized.
- **Audit:** timeline history hash verification API, legal posting/event chain, run provenance and optional read-only CSV export as time permits (export not required if not in locked source).

Load the **existing** `design-inputs/skills1 (1).md` only during Revenant frontend tasks. The product's locked demo theme is **dark/obsidian by default**; preserve applicable typography, editorial/data hierarchy and interactions from the paper-primary design file as **optional light mode** guidance without overriding backend semantics. An “Original ledger unchanged” seal compares the source's own pre/post hash, **not** source versus divergent branch hashes. All illustrative financial values remain nonbinding; semantic, working screens proceed with neutral placeholders otherwise. Make chart colors/legend accessible and data derived from actual points. A staff-authorized `CreateFreshFridayRingRun(seed,scenarioVersion)` allocates `ScenarioRunId` and run-scoped payment/idempotency IDs; two runs are equivalent in scenario facts **and fresh initial per-run bank/account balances**, never collide and remain independently auditable (no TRUNCATE/reset). Playwright: staff log in, create **two** fresh Friday Ring runs, hold and first/second approval, see settlement/forecast, seek 09:00, v8 branch, compare and audit, intentionally cancel a run, verify no ledger changes. Include narrow phone/tablet checks for navigation, Held Queue decisions, chart/table fallback and touch seek.

## Permissions, concurrency and failure rules

- Every publicly callable operation derives actor/session from verified server auth and uses the domain's ownership/membership/role policy; never trust a submitted principal ID.
- Retry behavior must distinguish idempotent retry from conflicting duplicate payload. A cancellation or unreachable dependency must leave prior durable authority intact and return visible error state.
- Persist state only in the owning module's schema and through frozen contracted ports. No direct cross-domain table mutation, no use of browser-rendered state as authority, no silent change to original source.
- Contract/rules/codec changes are versioned; emit readable incompatibility rather than secretly reinterpret historical data.

## Testing strategy (owned by this spec)

- Original journal and real ledger row count/hash unchanged after branch and cancellation
- v8 difference caused by actual rule/inference, same upstream payment intents
- Synthetic metrics derived from labelled **terminal outcomes**, pending deferred cases excluded from avoided exposure; post-fork human approval creates a deterministic, auditable continuation
- Four-eyes denial and case version conflict reflected in UI
- Forecast/settlement curves reconcile with source and branch state
- Playwright visit and perform main action on every one of five screens

## Specific implementation traps

- A syntactically present endpoint or view without real state transitions does not pass.
- Generic framework logic cannot embed money/game rules or clone the shared Timeline engine.
- Public interface changes require updating its producer, typed clients/consumers and corresponding architecture tests in the *same* compatible change, with ADR if consequential.

**Kiro model routing:** Strong for replay isolation and metrics; economical for screens after contracts freeze.
