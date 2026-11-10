# Spec 07 — Revenant V2: ledger, payments, settlement and cases

**Basket:** 3 | **Dependencies:** spec-03, spec-05 | **Owner paths:** `src/Revenant` payment/ledger/case infrastructure and SQL migrations, `src/Revenant.Contracts`, `tests/Revenant` and integration

## User value and outcome

Promote spec-05 pure financial rules into genuine durable transactional payment lifecycle, balanced ledger, controlled holds, review/four-eyes release and auditable outbox.

## In scope / excluded

**In scope:** Implement the named outcome including its authoritative state and integration tests as detailed below. **Explicitly excluded:** No live money, ML training/inference (08), final UI and policy branch chart (09), invented FX conversion.

**Existing versus new:** This is a planning package, not evidence of existing implementation. A greenfield repo has none of these files. When working in an existing repo, inspect named owned paths and dependent public contracts first; adapt *private* implementation to existing real code without rewriting frozen interfaces. Public interface owner is the spec named by `docs/architecture/03-contracts.md`.

## EARS-style functional requirements and acceptance criteria

**R07.01 — Balanced posting.** WHEN a settlement posting group commits, THE SYSTEM SHALL PostgreSQL deferrable constraint trigger verify per-currency debits equal credits and refuse unbalanced or incomplete group.
**R07.02 — Legal state machine.** WHEN a payment transition is requested, THE SYSTEM SHALL accept only version-defined legal transition and reject illegal or repeated effect idempotently.
**R07.03 — Immutable accounting.** WHEN a payment is corrected/returned, THE SYSTEM SHALL record compensating ledger entries and new events, never overwrite historical journal postings.
**R07.04 — Causal availability.** WHEN a held payment reserves outgoing liquidity, THE SYSTEM SHALL show lower available balance but unchanged posted asset until authorized settlement; release or reject unwinds reservation once.
**R07.05 — High-value control.** WHEN a staff user requests high-value release, THE SYSTEM SHALL require separate authorized supervisor with MFA, and deny self-approval or unapproved settlement.
**R07.06 — Settlement collision.** WHEN two workers or duplicate initiations race, THE SYSTEM SHALL serialize/lock needed aggregate state, settle once, avoid insufficient funds and double debit.
**R07.07 — Audit trace.** WHEN a case is read or decision made, THE SYSTEM SHALL retain actor/event/correlation/version and hash-chained audit references without leaking sensitive session material.
**R07.08 — Tenant/ownership.** WHEN one staff tenant tries another tenant bank/case, THE SYSTEM SHALL deny via domain policy with Postgres RLS defense on tenant-scoped tables where adopted and verify isolation.
**R07.09 — Outbox integrity.** WHEN a ledger transaction commits, THE SYSTEM SHALL publish payment/hold/settlement domain events with same transaction outbox and idempotent read projections.
**R07.10 — Currency precision.** WHEN amount is valid for currency, THE SYSTEM SHALL round/parse explicit minor units and refuse unsupported cross-currency transfer rather than invent rate.
**R07.11 — Complete settlement group.** WHEN a payment becomes `Settled`, THE SYSTEM SHALL commit the legal nonzero, same-currency, linked, balanced posting legs and `Settled` status atomically; an empty, one-legged, zero-value or unlinked group cannot represent a settlement.
**R07.12 — Run-aware durable identity.** WHEN a Friday Ring demo runs again with the same seed, THE SYSTEM SHALL scope synthetic bank/account state, posted/reserved balances and payment/business idempotency to `ScenarioRunId` (including `(ScenarioRunId,intentId)`) and retain both runs' append-only ledger/audit records.
**R07.13 — Executable tenant restriction.** WHEN tenant-scoped payments/cases are accessed through pooled runtime connections, THE SYSTEM SHALL enforce PostgreSQL RLS with transaction-local tenant context and deny cross-tenant access, without claiming all system tables are tenant-isolated.

## Non-functional and adversarial requirements

- Relevant source/contract semantics and ownership in `docs/architecture/03-contracts.md` must hold, and no cross-domain dependency may be added. Avoid unnecessary background jobs/abstractions.
- API/auth/realtime commands must validate untrusted input and return typed failures; do not acknowledge persistence or successful replay before actual verification/commit.
- Deterministic or transactional properties require real unit/property/integration evidence; only measured performance can be reported as achieved.
- Work must be done with the actual source repository and tested in the prescribed context, not with invented previous artifacts.

## Exit gate

Clean Postgres migration and hot-account concurrency proof pass, one legitimate and one ring payment traverse held→decision→settlement, DB balance/parity/audit/outbox are verifiable.
