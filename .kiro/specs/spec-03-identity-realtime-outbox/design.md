# Spec 03 design — Identity, Realtime and reliable integration

## Preconditions, owned implementation and boundaries

**Prerequisites (completed observable exit gates):** spec-01, spec-02. **This spec owns:** `src/Marrow.Infrastructure` auth/transport/outbox components, `src/Hosts.*` DI, `tests/Integration`. **Do not implement:** No gameplay tick rules, bank-specific permission decisions, domain score calculation, cross-service microservice broker fleet.

**Consumes:** Public contracts from spec-01 (`Marrow.Kernel` pure shared actor/realtime/outbox definitions) and spec-02 (Timeline/history identifiers); inspect only their exported interfaces and relevant existing migrations/tests. **Implements, but does not own, the Kernel public abstractions.** **Exposes:** The implemented behavior and stable public contract of this spec for downstream dependents. No spec may rewrite `Marrow.Timeline` public or `web/packages/timeline-ui` contracts without an approved cross-app ADR and dual-client test.

**Architecture authority:** `docs/architecture/01-master.md` defines dependency direction and state authority, `03-contracts.md` defines exact conceptual interfaces. This design defines this spec's *unique implementation ownership*; prefer whichever private class names fit the repo once validated, but preserve semantics. A superior nonbreaking local algorithm is allowed; shared ABI/persistence schema/version/security authority is not a local choice.

## Implementation strategy, contracts and data flow

### Identity

`marrow_identity` owns principal/session, role/claim and token revocation primitives, including its own Spec-03 migrations run by the Spec-01 single migration executor. The shared pure `IActorContext` contract lives in `Marrow.Kernel`; ASP.NET Identity/JWT/cookie implementations live in `Marrow.Infrastructure`. The same shared infrastructure library also exposes thin ordered messaging and leaderboard read-cache **mechanics** (typed bounded message envelope and Redis sorted-set projection/rebuild helper), while case-comment moderation and game lobby chat, finance alert priorities and gameplay rating formulas remain app-owned. Use ASP.NET Core Identity for staff password, TOTP MFA on staff privileged finance actions, access JWT for API/hub and rotating hashed opaque refresh sessions in HttpOnly Secure SameSite cookies. CSRF/origin rules on cookie-authenticated writes. Seed distinct dev-only operator/supervisor users with noncommitted credentials and fail if production mode uses default secret. For games, issue signed HttpOnly remembered guest cookie + DB session/principal; guest roles do not inherit bank staff policies. Domain (Revenant/GhostLap) resolves actual action authorization/tenant/history owner; shared identity only supplies verified actor context and token/session mechanics. Guest identity can be recovered on refresh, banned/revoked, and is never inferred from client-submitted `ownerId`.

### Realtime

Implement the single Kernel-owned typed `RealtimeEnvelope` with **exact** wire fields `(schemaVersion, streamId, sequence, eventId, serverUtc, kind, payload)`; `sequence` is a decimal string. Do not create `messageId`/`streamSequence`/`serverTime` aliases or a second incompatible envelope. Hub groups and subscribe/unsubscribe enforce owner/membership via domain auth; bounded message payloads and rate limits. A client reconnect fetches snapshot with most recent stream sequence and resumes, not assumes perfect SignalR delivery. Redis supports optional scale-out for **transport** and presence but is not authoritative for active room simulation. Until a lease/fence design exists `Realtime` deployment is one process/replica; fail deployment guard on replica count >1. Financial alerts can publish via outbox; active game room states come directly from authoritative game loop with ephemeral snapshots and durable result at finish.

### Outbox

**Ownership:** `Marrow.Kernel` defines only pure event/inbox identity records and small domain-neutral ports; `Marrow.Infrastructure` implements reusable claim/relay/retry/receipt mechanics. Each **future domain** owns its own outbox/inbox tables, unit of work and transactional insertion. Spec 03 must not generate Revenant/GhostLap tables before those modules exist. Prove the shared pattern now using a **test-owned or Identity-owned** PostgreSQL business row + outbox row in one transaction, with an inbox row and consumer effect in one transaction; later Specs 07/10–12 separately prove real domain atomicity. Use `FOR UPDATE SKIP LOCKED` for claim and bounded retries/poison diagnostics. **Worker→Realtime/API cross-process delivery uses Redis Streams** (at least once), with PostgreSQL outbox retaining pending records until the relay confirms publication, and receiving hosts recording idempotent inbox effects. A direct typed handler is allowed only within a single process, never as a substitute for cross-host delivery. Retry jitter bounded; crash-after-send and crash-after-apply tests mandatory. No distributed transaction or cross-schema FK. Keep user messages out of high-volume matching history store.

## Permissions, concurrency and failure rules

- Every publicly callable operation derives actor/session from verified server auth and uses the domain's ownership/membership/role policy; never trust a submitted principal ID.
- Retry behavior must distinguish idempotent retry from conflicting duplicate payload. A cancellation or unreachable dependency must leave prior durable authority intact and return visible error state.
- Persist state only in the owning module's schema and through frozen contracted ports. No direct cross-domain table mutation, no use of browser-rendered state as authority, no silent change to original source.
- Contract/rules/codec changes are versioned; emit readable incompatibility rather than secretly reinterpret historical data.

## Testing strategy (owned by this spec)

- Refresh token rotation replay rejected and old token revoked
- TOTP absence denies privileged staff operation
- Forged guest ownership or hub group subscription denied
- Identity/test-owned PostgreSQL business commit + its outbox row atomic; Redis outage preserves durable pending outbox and cross-host delivery recovers
- Crash after send and after consume => one inbox business effect
- Room scale=2 forbidden until an owner lease/fence ADR
- Compiled architecture test: public consumer domain references Kernel contracts, not shared Infrastructure; canonical realtime envelope serializes the same field names in API/hub client
- Crash between Redis publication and confirmation can redeliver; receiving `(consumerId,eventId)` inbox prevents a second business effect

## Specific implementation traps

- A syntactically present endpoint or view without real state transitions does not pass.
- Generic framework logic cannot embed money/game rules or clone the shared Timeline engine.
- Public interface changes require updating its producer, typed clients/consumers and corresponding architecture tests in the *same* compatible change, with ADR if consequential.

**Kiro model routing:** Strong for identity privilege and outbox transaction; economical for hub DTO plumbing.
