# Spec 07 design — Revenant V2: ledger, payments, settlement and cases

## Preconditions, owned implementation and boundaries

**Prerequisites (completed observable exit gates):** spec-03, spec-05. **This spec owns:** `src/Revenant` payment/ledger/case infrastructure and SQL migrations, `src/Revenant.Contracts`, `tests/Revenant` and integration. **Do not implement:** No live money, ML training/inference (08), final UI and policy branch chart (09), invented FX conversion.

**Consumes:** Public contracts from spec-03, spec-05; inspect only their exported interfaces, named migrations and relevant tests. **Exposes:** The implemented behavior and stable public contract of this spec for downstream dependents. No spec may rewrite `Marrow.Timeline` public or `web/packages/timeline-ui` contracts without an approved cross-app ADR and dual-client test.

**Architecture authority:** `docs/architecture/01-master.md` defines dependency direction and state authority, `03-contracts.md` defines exact conceptual interfaces. This design defines this spec's *unique implementation ownership*; prefer whichever private class names fit the repo once validated, but preserve semantics. A superior nonbreaking local algorithm is allowed; shared ABI/persistence schema/version/security authority is not a local choice.

## Implementation strategy, contracts and data flow

### Durable finance model

`revenant` schema: `bank`, `account` (asset accounting, tenant and per-`ScenarioRunId` demo namespace), `payment` (unique `(ScenarioRunId,intentId)` for seeded runs, scoped command idempotency key, amount/currency, version), `payment_transition` append-only, `hold` (active/resolved, referenced payment), `ledger_group` and `ledger_entry` append-only, `case` (version number/assigned actor/status), append-only `case_comment` with verified staff actor/case/time and sanitized text, `approval` (actor and order), `audit_chain`, `outbox`, `inbox` and consistent projector cursor. Payment lifecycle **frozen**: `Initiated→Screened`; `Screened→Held|Cleared|Rejected` (Rejected terminal); `Held→Approved|Rejected|Returned`; `Approved→Cleared|Returned`; `Cleared→Settled|Returned`; `Settled→Returned` only through explicit compensating return posting where business rules allow; `Rejected` and `Returned` terminal. `Held` implies no posted balance movement. Record invalid transitions as rejected command results, not events.

Ledger is USD in Friday Ring; `Money(currency,minorUnits)` supports exact integer minor units, no FX path. Asset balance = sum(debit)−sum(credit) for an asset account; A→B transfer credits A and debits B **within a single balanced per-currency posting group**. The DB deferrable constraint trigger validates group equality **and finalized group completeness** at commit: the relevant posting purpose must have at least its expected two **nonzero** opposite legs, one currency, a valid originating payment/compensation reference and no orphan settled status. In the **same transaction**, the payment may move to `Settled` only when its corresponding balanced group is finalized; an empty/one-sided/zero-value/unlinked group is not sufficient. No bypass flag is allowed. Prevent rows being updated/deleted by app writer; return reversals create new balanced posting groups. For sufficient funds, transaction acquires ordered account row locks, verifies posted less active reservations using locked state or durable projection, and writes ledger+payment status+outbox once. Serializable or repeatable-read with explicit lock & bounded retry according to measured SQL behavior; constrain retries to recognized transient SQL states and preserve idempotency key/unique command ID. Use SKIP LOCKED on worker queue, **not** to skip fund checks.

Screening includes a deterministic, **non-ML** fuzzy sanctions/name-match rule with versioned threshold, review evidence and explicit synthetic list; a fuzzy match is never mislabeled an ML prediction. High-value release: policy config USD >= 10000 triggers dual approval; 1st authorized operator approval puts `AwaitingSecondApproval`; second distinct supervisor MFA submits final approval. This is a case/review state separate from payment status; payment remains Held until release authorized. A lower-value held approval may release with an appropriate single authorized actor (still staff MFA for privileged actions as policy). Every decision optimistic concurrency `case.version`. Tenant scoping: use validated tenant claim and EF filter plus PostgreSQL RLS on **tenant-owned** payment/case tables. Migration-owner credentials are separate from application runtime credentials; runtime is neither table owner nor `BYPASSRLS`, and use `FORCE ROW LEVEL SECURITY` on tables where claimed. Set scoped tenant context with transaction-local `SET LOCAL` (or equivalent parameterized transaction-local session setting) for every tenant transaction; pooled connections cannot inherit a previous actor's tenant. Background jobs explicitly select their authorized tenant context. Two-tenant direct DB and pooled A→B→A tests must show no leakage. **Do not** falsely claim universal isolation for exempt shared/system tables. Reviewer `Audit` verification computes chain continuity and distinguishes DB tamper evidence from external notarization.

### Coupling to V1 and Worker

The spec-05 *pure* reducer remains authority for forecast and counterfactual state evolution. Live workflow translates reducer decisions into domain commands backed by DB; one commit updates actual ledger and projector with matching rule version. Any differences between pure and persisted transitions fail a parity test. Worker processes holds/settlement using durable outbox/claim; after Postgres outage retry safely. All per-run synthetic bank/account rows and opening balances are distinct while the demo uses an equal logical six-bank map/seed; do not reuse a previous run's mutated balances. The `LiveOps` Worker mode holds the necessary limited finance writer credential; **the separate `Replay` process mode of the same Worker binary uses a tenant-scoped restricted database role that reads only authorized source data, permits controlled creation/append/completion of **new** Marrow Timeline branch records via shared ports, and cannot write original ledger/payment/case/settlement tables**, even by direct SQL. Spec 07 defines grants/schema/role boundaries; Spec 09 supplies replay registration and proves write denial. No replay branch is given EF live ledger unit of work (spec-09 later).

## Permissions, concurrency and failure rules

- Every publicly callable operation derives actor/session from verified server auth and uses the domain's ownership/membership/role policy; never trust a submitted principal ID.
- Retry behavior must distinguish idempotent retry from conflicting duplicate payload. A cancellation or unreachable dependency must leave prior durable authority intact and return visible error state.
- Persist state only in the owning module's schema and through frozen contracted ports. No direct cross-domain table mutation, no use of browser-rendered state as authority, no silent change to original source.
- Contract/rules/codec changes are versioned; emit readable incompatibility rather than secretly reinterpret historical data.

## Testing strategy (owned by this spec)

- Deferrable DB trigger refuses unbalanced group even if C# skipped check
- Sum of six banks assets remains conserved for same-currency balanced groups
- Duplicate initiation, approval, settlement and event processing has one business effect
- Two concurrent workers cannot debit account twice or overspend
- Invalid transitions, self-second-approval, MFA absence and cross-tenant reads denied
- Historical corrections via new entries; UPDATE/DELETE privilege denied
- Case version collision resolves with conflict, no lost update

## Specific implementation traps

- A syntactically present endpoint or view without real state transitions does not pass.
- Generic framework logic cannot embed money/game rules or clone the shared Timeline engine.
- Public interface changes require updating its producer, typed clients/consumers and corresponding architecture tests in the *same* compatible change, with ADR if consequential.

**Kiro model routing:** Strong for SQL transaction/trigger/security; economical for controller DTOs.
