# Spec 03 — Identity, Realtime and reliable integration

**Basket:** 1 | **Dependencies:** spec-01, spec-02 | **Owner paths:** `src/Marrow.Infrastructure` auth/transport/outbox components, `src/Hosts.*` DI, `tests/Integration`

## User value and outcome

Reusable identity/session, typed SignalR transport, bounded message/sorted-set cache primitives, at-least-once outbox/inbox and room/presence primitives that protect app-owned authorization.

## In scope / excluded

**In scope:** Implement the named outcome including its authoritative state and integration tests as detailed below. **Explicitly excluded:** No gameplay tick rules, bank-specific permission decisions, domain score calculation, cross-service microservice broker fleet.

**Existing versus new:** This is a planning package, not evidence of existing implementation. A greenfield repo has none of these files. When working in an existing repo, inspect named owned paths and dependent public contracts first; adapt *private* implementation to existing real code without rewriting frozen interfaces. Public interface owner is the spec named by `docs/architecture/03-contracts.md`.

## EARS-style functional requirements and acceptance criteria

**R03.01 — Staff login.** WHEN a legitimate staff user authenticates, THE SYSTEM SHALL issue short-lived access token and rotating secured refresh session with privileged MFA enforcement and server-side revocation.
**R03.02 — Guest identity.** WHEN a new game visitor starts, THE SYSTEM SHALL create or resume a signed server-backed guest session without requiring staff credentials or account registration.
**R03.03 — Resource isolation.** WHEN a guest/staff guesses another history or tenant ID, THE SYSTEM SHALL the application policy handler deny read/write regardless of request-provided owner.
**R03.04 — Event atomicity.** WHEN a domain business transaction commits, THE SYSTEM SHALL its outbox row commit atomically and consumer apply business effects once even after duplicate delivery.
**R03.05 — Relay failure.** WHEN Redis disappears or worker stops after publication, THE SYSTEM SHALL pending outbox work stays durable and retries; poison entries surface diagnostically rather than silently vanish.
**R03.06 — Typed transport.** WHEN a hub client subscribes or reconnects, THE SYSTEM SHALL deliver versioned stream messages and snapshot/refetch on sequence gap with membership and rate limit checks.
**R03.07 — Match ownership.** WHEN a room starts, THE SYSTEM SHALL exactly one registered Realtime loop is allowed; configuration disallows ambiguous scaled ownership.
**R03.08 — Cross-identity separation.** WHEN a guest uses finance path or staff uses game room without membership, THE SYSTEM SHALL reject according to domain-provided policy rather than global role bypass.
**R03.09 — Audit hygiene.** WHEN authentication/outbox failures are logged, THE SYSTEM SHALL record correlation and sanitized error without token/cookie/PII leakage.
**R03.10 — Legal contract ownership.** WHEN domain code consumes an actor, realtime or outbox abstraction, THE SYSTEM SHALL resolve the pure public contract from dependency-free `Marrow.Kernel`, never from implementation-only `Marrow.Infrastructure`.
**R03.11 — Reusable outbox proof without future schemas.** WHEN the shared outbox is tested in Spec 03, THE SYSTEM SHALL use an Identity-owned or test-owned PostgreSQL unit of work/schema; later domain specs own their real tables and transaction-specific atomicity tests.
**R03.12 — Host-boundary delivery.** WHEN an outbox event must reach another host process, THE SYSTEM SHALL publish/consume via Redis Streams with durable outbox retries and consumer inbox deduplication; a direct typed handler is allowed only within one process.

## Non-functional and adversarial requirements

- Relevant source/contract semantics and ownership in `docs/architecture/03-contracts.md` must hold, and no cross-domain dependency may be added. Avoid unnecessary background jobs/abstractions.
- API/auth/realtime commands must validate untrusted input and return typed failures; do not acknowledge persistence or successful replay before actual verification/commit.
- Deterministic or transactional properties require real unit/property/integration evidence; only measured performance can be reported as achieved.
- Work must be done with the actual source repository and tested in the prescribed context, not with invented previous artifacts.

## Exit gate

Staff MFA and guest remember flow work; unauthorized resource/hub access denied; transactional duplicate outbox proof passes with real services; room scale guard effective.
