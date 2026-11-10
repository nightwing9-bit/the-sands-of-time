# Spec 05 design — Revenant V1: seeded six-bank causal vertical slice

## Preconditions, owned implementation and boundaries

**Prerequisites (completed observable exit gates):** spec-02, spec-03, spec-04. **This spec owns:** `src/Revenant`, `src/Revenant.Contracts`, `web/apps/revenant`, `tests/Revenant`. **Do not implement:** No bank ledger DB posting yet (07 owns), no trained ML.NET inference (08), no five polished screens (09), no real bank transactions.

**Consumes:** Public contracts from spec-02, spec-03, spec-04; inspect only their exported interfaces, named migrations and relevant tests. **Exposes:** The implemented behavior and stable public contract of this spec for downstream dependents. No spec may rewrite `Marrow.Timeline` public or `web/packages/timeline-ui` contracts without an approved cross-app ADR and dual-client test.

**Architecture authority:** `docs/architecture/01-master.md` defines dependency direction and state authority, `03-contracts.md` defines exact conceptual interfaces. This design defines this spec's *unique implementation ownership*; prefer whichever private class names fit the repo once validated, but preserve semantics. A superior nonbreaking local algorithm is allowed; shared ABI/persistence schema/version/security authority is not a local choice.

## Implementation strategy, contracts and data flow

### Frozen pure finance kernel

`Money` is currency code + integer minor units; scenario uses USD only, no implicit FX. `PaymentIntent` owns unique upstream identity, original timestamp, monotonically assigned event sequence, source/destination six-bank account IDs, amount, **synthetic** fraud truth and feature inputs; all delivered to `FinanceTransition` as exogenous facts. Domain-only state includes per-bank asset balance, reservations/held amounts, payment statuses, policy-related velocity windows and counters for labelled fraud prevented/legitimate delay; the initial `Transition` is final shared **pure business logic** used by spec-07 live processing and spec-09 branch projection. Do not write a disposable simulation with an unrelated rule engine.

Payment statuses for this slice: `Initiated→Screened→Held|Cleared`, `Held→Approved|Rejected|Returned`, `Approved→Cleared`, `Cleared→Settled|Returned`. Full final legal transition matrix and posting transaction controls in 07. In the pure projection, `Held` reserves outgoing availability but does not yet move posted balance; `Settled` moves asset balance from bank A to bank B, totals conserved within USD. Implement source bank available >= amount check (sequential deterministic ordering) and show a rejected/returned path with explicit reason. Run explicitly frozen `policy-v7` screening: hold when `velocity15m >= 8 AND amountMinor >= 100000` (USD), or hard sanctions hit. Run `policy-v8`: hold when `velocity15m >= 5 OR (newBeneficiary AND amountMinor >= 150000)`, or hard sanctions hit. `velocity15m` counts the payer's preceding and current exogenous intents in a deterministic 15-minute event-time window ordered by logical sequence; same-timestamp ties use inputId. V7/V8 configurations, thresholds, time-window semantics and feature schema have distinct immutable content hashes. Scenario generator must produce a known labelled ring whose decisions diverge; any further tuning requires a new policy version. A policy difference must not be “random v8 has better results.” Basic screening/feature evidence no learned model in this spec. A later ML model is an interchangeable version-pinned score provider, not replacement of transition kernel. A branch must replay from fork with retained exogenous intents; original derived decisions remain evidence but are not injected as branch actions.

### Scenario and proof

Create six actual bank IDs and at least a seeded ring chain A→B→C→... with mule-labelled transfers and interleaved legitimate payments. Scenario manifest holds seed, banks initial balances, labels/feature schema, version and hash, **and a distinct `ScenarioRunId`** identifying each run. Each run also owns a separate synthetic six-bank/account *state namespace and initial balance snapshot*; merely generating new payment IDs against already-changed accounts is **not** repeatability. Stable intra-run payment IDs and idempotency/business keys are namespaced by `(ScenarioRunId,intentId)`; equal seed/version produces equivalent facts excluding run identity, but no repeated run deletes or mutates an earlier source. Support deterministic replay to chosen event position including a real 09:00 business timestamp selection; logical event sequence is authority. Source policy v7 decision state and branches are checkpointed through shared engine. Metrics initially defined explicitly: `fraudPreventedCount` = labelled synthetic fraud that v7 settled and v8 **terminally rejects or returns without ever settling** (a held/pending fraud is `fraudDeferredAtHorizonCount`, excluded from prevented exposure); `legitimateDelayedCount` = labelled legitimate whose settlement is later/unsettled under v8 than v7 by comparison horizon; `falsePositiveCostMinor` = USD 250 minor units per newly held/rejected labelled legitimate intent (`counterfactual-cost-v1` scenario assumption); `incrementalDelayCostMinor` = USD 10 minor units × whole additional minutes held past the original (capped at 120 minutes per payment in comparison horizon); `netEffectMinor` = synthetic **terminally prevented** fraud exposure minus these two costs, clearly documented assumptions; report pending fraud separately without monetizing its principal as avoided. Never claim these are real banking economic losses. Extend metric definitions with ML in 08, preserving named versions.

V1 UI: React list of six bank balances with directed activity and a small dual trajectory; branch button connected to API and shared scrubber. Live data here is deterministic scenario playback, not bank ledger posting. This slice is deliberately **real and causally correct**, not all final infrastructure. 07 promotes exact domain kernels to transactional real ledger; no rewrite of policy/Timeline adapters.

## Permissions, concurrency and failure rules

- Every publicly callable operation derives actor/session from verified server auth and uses the domain's ownership/membership/role policy; never trust a submitted principal ID.
- Retry behavior must distinguish idempotent retry from conflicting duplicate payload. A cancellation or unreachable dependency must leave prior durable authority intact and return visible error state.
- Persist state only in the owning module's schema and through frozen contracted ports. No direct cross-domain table mutation, no use of browser-rendered state as authority, no silent change to original source.
- Contract/rules/codec changes are versioned; emit readable incompatibility rather than secretly reinterpret historical data.

## Testing strategy (owned by this spec)

- Same seeded input produces byte-identical ordered records and financial state hash
- Policy v7/v8 at same fork differ on actual decision, not UI constants
- Sum of six bank asset balances conserved for same USD currency in projection
- Source journal/hash/decision facts unmodified after branch cancellation
- Unauthorized bank staff does not get branch permission; no live ledger writes exist

## Specific implementation traps

- A syntactically present endpoint or view without real state transitions does not pass.
- Generic framework logic cannot embed money/game rules or clone the shared Timeline engine.
- Public interface changes require updating its producer, typed clients/consumers and corresponding architecture tests in the *same* compatible change, with ADR if consequential.

**Kiro model routing:** Strong for causal reducer and counterfactual alignment; economical for six-bank view.
