# ADR-P1-18 — Tenant-bearing staff actors and database-enforced four-eyes release

- **Status:** Accepted for implementation, Spec 07 task 7.4 (2026-11-11).
- **Scope:** `src/Marrow.Infrastructure/Identity` (tenant claim, session state, seeding), `src/Marrow.Infrastructure/Realtime/RealtimeAccessRevalidation.cs`, `scripts/db/migrations/marrow_identity/0002_staff_tenant_membership.sql`, `src/Revenant` (cases, authorizer, hub stream), `scripts/db/migrations/revenant/0004_cases_four_eyes.sql`, `Hosts.Realtime` composition. `IActorContext`, `IResourceAuthorizer`, `IMembershipAuthorizer`, `Marrow.Timeline` and `timeline-ui` are unchanged.
- **Related:** ADR-P1-06 (atomic Postgres ledger), ADR-P1-17 (finance fact kinds), Spec 03 identity contract (`docs/architecture/03-contracts.md`, Security interfaces v1).

## Context

The frozen `IActorContext` has always carried `TenantId?`, but the identity layer returned `null` for every actor, so task 7.2 took the tenant from host configuration. Task 7.4 needs tenant-bearing staff actors whose tenant and membership are revalidated on every request and hub check, and a four-eyes release that a bug or direct SQL cannot bypass.

## Decision

1. **Tenant is identity server state.** `marrow_identity.staff_tenant_memberships` holds at most one active tenant per staff user (partial unique index); revocation sets `revoked_at` once and is never undone. The access-token handler and the realtime revalidator read it together with the session, roles and MFA state; any tenant, role or MFA claim inside a token is discarded first. `ClaimsActorContext.TenantId` exposes it (staff only; null when there is none or identities disagree). No request field can set it, and there is no self-service grant path (`StaffAuthService.GrantTenantAsync` is for administration/seeding).
2. **Revenant authorization requires the tenant.** `RevenantStaffAuthorizer` denies a staff actor without a tenant, a tenant different from the run's persisted tenant, and a subject revoked from the run (`revenant.scenario_run_staff_revocation`). It implements both shared ports so API handlers and hub subscriptions use one policy. Opening a run uses the actor's tenant; `Revenant:Ledger:TenantId` remains only the tenant the LiveOps background jobs select.
3. **Four-eyes is enforced by PostgreSQL.** A review case is opened with the hold. `case_approval` has `UNIQUE (case_id, subject_id)` (no self-approval), slot 2 must be a supervisor, every approval records a server-verified fresh MFA, slots fill in order. Deferred constraint triggers refuse `Held → Approved` without a Released case whose approvals are complete (RV007) and any case whose status does not match its approvals (RV008). The generic payment transition refuses `Held → Approved` (`ReviewRequired`).
4. **Review and return facts.** As allowed by ADR-P1-17 decision 5 (no V2 Timeline history exists yet), `finance_fact` gains `ReviewDecision` and `ReturnInstruction`, committed under the run cursor in the same transaction as the case/payment step and outbox events.

## Consequences

- A domain that uses the shared owner-match policy (`ResourceAccessRules.EvaluateOwnership`) now compares a real staff tenant: a staff member who holds a tenant membership is refused a staff-owned resource recorded with `TenantId = null`. Guests are unaffected (always null). No current production registration is affected; the E2E fixture host's staff have no membership.
- Development seeding grants the seeded staff `Identity:DevSeed:TenantId` (default `demo-bank`).
- The Realtime host registers the Revenant run stream only when `ConnectionStrings:RevenantLedger` is set; deployed login provisioning is task 7.5.

## Compatibility check (task 7.4, actually performed)

- `IActorContext`, Kernel security ports and Timeline contracts: no source change. `StaffSessionState` gained an optional trailing `TenantId` parameter (source compatible). OpenAPI: semantic comparison against `HEAD` reports zero removed or changed keys; only Revenant ledger paths and schemas were added; `scripts/contracts/generate.sh --check` passed. `ApiContractVersion` unchanged.
- Full `tests/Integration` (identity, MFA, realtime hub, Timeline E2E host, messaging, Revenant ledger) and `tests/Architecture` pass with the change.
