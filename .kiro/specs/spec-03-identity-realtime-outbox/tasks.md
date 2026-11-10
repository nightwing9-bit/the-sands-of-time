# Spec 03 implementation tasks — Identity, Realtime and reliable integration

**Execute tasks in listed order.** Dependencies: spec-01, spec-02. Only check a box after actual changed files, tests and observed proof exist. Tasks are bounded work packages, **not AI chat prompts**. The user-supplied context is a requirements baseline, not permission to invent completed code. For actual code inspection, open the relevant named paths plus narrow dependent contract/migration/tests; follow a genuine dependency when discovered, but do not load the entire repo automatically.

## Ordered implementation tasks

- [ ] **3.1 Staff/guest auth and session migration**
  - **Precondition/input & inspect:** existing Spec-01 migration executor, `Marrow.Kernel` actor contract, ASP.NET Identity endpoints and only the `marrow_identity` migration area. Completed prerequisite spec exit gate must already have evidence.
  - **Produce/modify:** Spec-03-owned `marrow_identity` migration(s), implementation of Kernel `IActorContext`, rotating refresh, remembered guest session and TOTP privileged staff policy; use the single migration executor.
  - **Forbidden changes:** hardcoded demo passwords, shared guest/staff authorization. Do not change frozen shared public contracts without ADR.
  - **Verification:** auth integration: expired/replay refresh, MFA, CSRF and revocation.
  - **Done when:** two principal types preserve strict roles.

- [ ] **3.2 Domain policy interface and enforcement**
  - **Precondition/input & inspect:** Kernel `IActorContext`, narrow domain policy ports/temporary denial fixtures (later domain handlers do not yet exist). Completed prerequisite spec exit gate must already have evidence.
  - **Produce/modify:** resource ownership and membership authorization ports + defaults deny.
  - **Forbidden changes:** allow arbitrary client owner ID or reusable bank policy in game. Do not change frozen shared public contracts without ADR.
  - **Verification:** two-owner forbidden history and cross-tenant tests.
  - **Done when:** unauthorized principal cannot read others.

- [ ] **3.3 Durable outbox/inbox**
  - **Precondition/input & inspect:** Spec-01 migration runner, Kernel outbox/inbox identity contracts, `Marrow.Infrastructure` relay and an Identity-owned or isolated test-owned PostgreSQL unit of work; **do not assume later domain UoWs/migrations exist**.
  - **Produce/modify:** reusable outbox claim/retry/dispatch and inbox mechanics; one small real Identity/test-owned atomic business+outbox test fixture. Each later domain owns its own table and UoW wiring, not Spec 03.
  - **Forbidden changes:** separate save that can lose event; promise exactly-once delivery. Do not change frozen shared public contracts without ADR.
  - **Verification:** real Postgres transaction rollback/commit, Redis Streams cross-host outage/restart, crash-after-publication and duplicate consumer apply; exactly one business effect via durable inbox.
  - **Done when:** business effect once despite redelivery.

- [ ] **3.4 Typed hub and gap recovery**
  - **Precondition/input & inspect:** `src/Hosts.Realtime`, `web/packages/contracts` hub schemas. Completed prerequisite spec exit gate must already have evidence.
  - **Produce/modify:** Kernel-owned canonical envelope `(schemaVersion,streamId,sequence,eventId,serverUtc,kind,payload)` and typed hub clients, group policy, stream sequence and snapshot/refetch protocol.
  - **Forbidden changes:** hub score authority or scale-out without owner lease. Do not change frozen shared public contracts without ADR.
  - **Verification:** real hub reconnect/order/forged spectator membership, serialized field agreement and source sequence gap recovery.
  - **Done when:** typed reconnect is loss-tolerant.

- [ ] **3.5 Deployment guard and quality gate**
  - **Precondition/input & inspect:** Compose realtime scale config, health, tests. Completed prerequisite spec exit gate must already have evidence.
  - **Produce/modify:** one-Realtime-instance invariant and bounded rate/size limits.
  - **Forbidden changes:** implied multi-host room ownership. Do not change frozen shared public contracts without ADR.
  - **Verification:** configuration attempt >1 replica fails explicit guard.
  - **Done when:** room authority invariant documented and enforced.

## Spec exit verification (blocking)

Staff MFA and guest remember flow work; unauthorized resource/hub access denied; transactional duplicate outbox proof passes with real services; room scale guard effective.

Minimum proof includes successful targeted automated tests and relevant integration observation **actually performed**. Record real result paths, commands, commit IDs, encountered defects and measured Kiro credit use (if observable) in `docs/kiro-log.md`; if any check cannot run, mark blocker and leave task/spec incomplete. Do **not** infer a passing test from this document.
