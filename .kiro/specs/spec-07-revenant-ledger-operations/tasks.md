# Spec 07 implementation tasks — Revenant V2: ledger, payments, settlement and cases

**Execute tasks in listed order.** Dependencies: spec-03, spec-05. Only check a box after actual changed files, tests and observed proof exist. Tasks are bounded work packages, **not AI chat prompts**. The user-supplied context is a requirements baseline, not permission to invent completed code. For actual code inspection, open the relevant named paths plus narrow dependent contract/migration/tests; follow a genuine dependency when discovered, but do not load the entire repo automatically.

## Ordered implementation tasks

- [ ] **7.1 Migrations, balances and DB constraints**
  - **Precondition/input & inspect:** `revenant` SQL migrations, existing spec-05 Money/pure posting. Completed prerequisite spec exit gate must already have evidence.
  - **Produce/modify:** ledger/group/entries and deferrable balance **and completeness** checks, insert-only app grants, separation of migration/runtime and future replay-read role.
  - **Forbidden changes:** application-only balancing, mutable posting rows. Do not change frozen shared public contracts without ADR.
  - **Verification:** real Postgres unbalanced, empty, one-leg, zero-value, missing-link and valid group commits; settled-without-group denied, compensation/concurrency valid.
  - **Done when:** DB itself rejects imbalanced postings.

- [ ] **7.2 Payment machine and idempotent intake**
  - **Precondition/input & inspect:** spec-05 transitions/contracts, API and integration ports. Completed prerequisite spec exit gate must already have evidence.
  - **Produce/modify:** full legal transition matrix, incoming intent idempotency scoped to `ScenarioRunId` for synthetic repeat runs, transactional state+outbox. Define the additive finance version and focused compatibility ADR from the design, including ordered canonical facts, restartable Timeline projection and the minimum future-evidence boundary (concrete ML evidence belongs to 08). Own production Revenant API/LiveOps registration, existing Timeline/domain stores and REST/OpenAPI/generated frontend contracts; compose domain staff workflow authorization through existing ownership/membership ports rather than a generic owner-only check.
  - **Forbidden changes:** parallel replacement reducer or cross-schema FK. Do not change frozen shared public contracts without ADR.
  - **Verification:** FsCheck legal state sequences & conflicting duplicate intents; two equal-seed distinct run IDs coexist with identical starting bank balances **in separate account namespaces**, without key collision or original ledger deletion.
  - **Done when:** one legal state and one effect per command.

- [ ] **7.3 Holds, settlement and safe retries**
  - **Precondition/input & inspect:** worker queues and locked accounts, domain state. Completed prerequisite spec exit gate must already have evidence.
  - **Produce/modify:** reservation, batch settlement, rollback/return posting and SKIP LOCKED.
  - **Forbidden changes:** unchecked retry or stale available-balance cache. Do not change frozen shared public contracts without ADR.
  - **Verification:** 2 workers/hot-account Testcontainers, insufficient funds.
  - **Done when:** balance conserved and settlement single.

- [ ] **7.4 Case workflows and four-eyes**
  - **Precondition/input & inspect:** Identity actor/MFA, `case` version, hold policy. Completed prerequisite spec exit gate must already have evidence.
  - **Produce/modify:** optimistic concurrency, actor-authenticated case comments, two distinct approvers, high-value threshold and deterministic fuzzy-name screening. Commit ordered canonical review/return facts with their live action/outbox. Extend or compose existing authentication ports for validated tenant-bearing staff actors and fresh membership/tenant revalidation, including relevant hub paths; never accept client tenant/role overrides.
  - **Forbidden changes:** self-approval, client-supplied role/tenant override. Do not change frozen shared public contracts without ADR.
  - **Verification:** two real authenticated tenant-bearing staff sessions complete distinct-authority approval; concurrent edits, comment permissions, fuzzy-match fixture and cross-tenant denial; revoked membership/tenant access denied at API and relevant hub paths. Postgres RLS uses transaction-local context and pooled A→B→A isolation with runtime role without `BYPASSRLS`.
  - **Done when:** privileged release only after proper sequence.

- [ ] **7.5 Audit and scenario integration**
  - **Precondition/input & inspect:** audit chain/version, Worker outbox, V1 compatibility fixtures and 7.2 versioned pure state; Spec-03/05 handoffs in `docs/architecture/02-spec-map.md`. Completed prerequisite spec exit gate must already have evidence.
  - **Produce/modify:** hash proof, transaction/action evidence, six-bank live-source parity after projection catch-up/reconciliation. Close financial domain runtime grants/login mappings and outbox/inbox/relay recovery handoffs using existing Marrow mechanisms: safe trimming preserves pending/unread entries, bounded retention, poison/dead-letter requeue through deduplication, measured `appendfsync always` throughput and operator re-publish procedure for Redis-volume loss (outside automatic-delivery guarantees).
  - **Forbidden changes:** live ledger exposed to branch runner. Do not change frozen shared public contracts without ADR.
  - **Verification:** through actual API/LiveOps registrations and least-privilege runtime logins, Friday Ring actual DB balances match the versioned pure source on the same recorded inputs. A hold→distinct-authority approvals→settlement→permitted compensating return interleaved with another intent preserves stable action order; crash after live commit before Timeline projection, idempotent catch-up and cold replay reproduce recorded decisions/statuses and financial effects before source verification. Check correct source/branch version keys and unchanged V1 golden bytes/hashes; alternative-policy parity is not required. Replay role direct attempts to write live ledger/payment tables are denied in PostgreSQL. During implementation, narrowly scope the slice-only no-database-driver assertion to pure finance code as domain persistence is introduced; preserve reducer purity, ABI/hash and architecture checks.
  - **Done when:** real financial causality survives persistence.

## Spec exit verification (blocking)

Clean Postgres migration and hot-account concurrency proof pass, one legitimate and one ring payment traverse held→decision→settlement, DB balance/parity/audit/outbox are verifiable.

Minimum proof includes successful targeted automated tests and relevant integration observation **actually performed**. Record real result paths, commands, commit IDs, encountered defects and measured Kiro credit use (if observable) in `docs/kiro-log.md`; if any check cannot run, mark blocker and leave task/spec incomplete. Do **not** infer a passing test from this document.
