# Realtime room authority invariant (Spec 03, task 3.5)

Applies ADR-P1-02 and ADR-P1-11 in `04-decisions.md`. This note records how the rule is enforced. It does not change any decision.

## Invariant

Exactly one Realtime process runs per deployment (per PostgreSQL database). Each active room and stream therefore has exactly one in-process authority. Redis may carry transport or presence data, but it is never room authority, and no Redis or SignalR backplane makes a second replica safe. Running more than one Realtime replica is forbidden until an ADR in `docs/adr/` defines an owner lease with fencing tokens and room affinity, and tests prove it.

## Enforcement

| Layer | Mechanism | Fails when | Failure |
|---|---|---|---|
| Deployment config | `scripts/checks/realtime-scale-guard.sh` resolves the Compose model with `docker compose config` (anchors, override files, `!reset`) and inspects it with `jq`. Runs in CI (`scripts` job). | `services.realtime` is missing; more than one service is named `realtime` or builds `deploy/docker/Realtime.Dockerfile`; `scale` or `deploy.replicas` is not 1; `deploy.mode: global`; the Compose model is invalid | exit 1, names each violation |
| Runtime startup | `RealtimeInstanceGuard` (hosted service registered by `AddMarrowRealtime`) takes PostgreSQL session advisory lock `0x4D4152524F575254` ("MARROWRT") on a dedicated non-pooled connection (`application_name=marrow-realtime-instance-lock`) before the web server accepts connections. It retries for `Realtime:InstanceLockAcquireSeconds` (default 15 s, so a restart can outlast the old session). | another process holds the lock (e.g. `docker compose up --scale realtime=2`, `docker compose run realtime`, a second host on another machine) or PostgreSQL cannot be reached | log event 3208 (Critical), `RealtimeInstanceRefusedException`, process exit code 2, no hub connection is ever accepted |
| Runtime while running | The guard runs `SELECT 1` on the lock connection every `Realtime:InstanceLockCheckSeconds` (default 5 s). | the lock session is gone (PostgreSQL restart, network loss, terminated backend) | log event 3209 (Critical), every open hub connection is aborted at once, then the host stops with exit code 1; Compose `restart: on-failure:5` restarts it and it takes the lock again |
| Transport bounds | `MarrowRealtimeOptionsValidator` (validated on start): inbound message 256 B to 1 MiB (default 8 KiB, SignalR closes the connection when exceeded), outbound payload 256 B to 1 MiB (default 64 KiB), per-connection token bucket over 0 and up to 1000/s (default 10/s) with burst 1 to 1000 (default 20), 1 to 1024 subscriptions per connection (default 32). | a value is out of range | host start fails with `OptionsValidationException` naming the key |

Rate-limited hub calls return the typed `rate_limited` reply and log event 3202. All guard logs carry reason codes only, never connection strings, tokens or cookies.

## Known limits

- **v1 deployment guard, not a fenced lease.** The advisory lock is a start guard with loss detection. It is **not** an owner lease or fencing token, and it does not give mutually exclusive authority during a network partition. If PostgreSQL ends the lock session while the old process cannot see that (for example a half-open TCP connection), a new process can take the lock and the old one notices only when its next probe fails: up to `InstanceLockCheckSeconds` plus the probe's command timeout (same value), about 10 s with the defaults. During that window both processes could serve. Nothing in v1 writes room state under the lock, so no durable state can be corrupted by the overlap, but it is the reason multi-replica operation stays forbidden. It is not support for failover. Per-room ownership with fencing tokens (writes rejected for a stale owner epoch) belongs to **Spec 10** (its requirements already forbid multi-replica tick ownership without fencing); it needs an ADR in `docs/adr/` and tests before any replica count above 1.
- Advisory locks are scoped to one database. Two deployments that point at different databases are not detected as duplicates. Each one has its own authority, which is acceptable because they share no room state.
- The config guard covers Compose files only. No Kubernetes or other orchestrator manifests exist yet. A future manifest needs its own guard, and the runtime lock covers it in the meantime.
- Values are safety bounds. They are not measured capacity.

## Revocation of open hub connections

SignalR authenticates a connection once and never removes a group membership by itself. The hub therefore does not trust the connect-time principal or an old group membership:

- Every `Subscribe`/`GetSnapshot` re-reads the session from `marrow_identity` (staff: session live, user not disabled, roles and MFA freshness from the database; guest: session not revoked or expired, guest not banned) before the domain authorizer runs. A dead session closes the connection.
- `RealtimeAccessRevalidator` re-checks every open connection every `Realtime:AccessRevalidationSeconds` (default 10 s, allowed 1 to 60): a dead session closes the connection (event 3210); a stream the domain no longer allows is removed from its group (event 3211), and later snapshot calls are denied like any forbidden stream. A check that does not finish within 5 s (identity database or domain lookup unavailable) also closes the connection. Up to 8 connections are checked in parallel.
- Staff connections also close when the access token expires (`CloseOnAuthenticationExpiration`, 10 min default).

**Bound:** after a revocation commits (guest sign-out or ban, staff logout or admin revocation, user disabled, role removed, domain membership removed), a connection stops receiving that stream within `AccessRevalidationSeconds` plus the check time, normally under 10 s plus a database round trip. With N connections a sweep takes about N/8 round trips, so very large connection counts lengthen the bound; this is acceptable for the single v1 process and is to be measured under real room traffic in Spec 10. Events published inside the bound can still be delivered. This applies equally across hosts because the API and Realtime share the identity database; it was observed with live WebSocket connections in `RealtimeHubTests.Revoked_sessions_bans_and_removed_memberships_stop_delivery_to_live_connections` (1 s interval, all revocations effective in about 1 s).

## Outbox delivery guarantee (Spec 03 mechanics)

At-least-once delivery from a committed outbox row to every consumer group, and at most one committed business effect per `(consumer_id, event_id)` through the transactional inbox. Exactly-once network delivery is not promised, and there is no global or per-aggregate ordering across retries, redelivery or reclaim. Consumers must not depend on order.

| Failure | Result |
|---|---|
| Producer transaction rolls back | No business row and no outbox row. |
| Redis unreachable, or refusing writes server-wide (`LOADING` after restart, `MISCONF` on a full AOF disk, `OOM`, `READONLY`, `NOAUTH`/`NOPERM`, ...) | Row stays `pending`, attempts unchanged, retried with bounded jittered backoff. Never poisoned by an outage. |
| Relay crashes before `XADD`, or after `XADD` before the `published` update commits | Lease lapses, the row is published again; the inbox drops the duplicate. |
| Redis restarts or is killed after acknowledging `XADD` | Entry, consumer group and pending deliveries survive: Compose runs `appendonly yes`, `appendfsync always` on the `redis-data` volume, so the write is in the AOF before Redis replies. |
| Consumer crashes before its transaction commits | Nothing applied; the entry stays pending and is reclaimed after `ClaimIdleTime`. |
| Consumer crashes after commit, before `XACK` | Redelivered; the inbox row makes it a duplicate, then it is acknowledged. |
| Duplicate or concurrent delivery | Inbox primary key: one transaction applies, the other sees `Duplicate`. |
| Message-specific publish error (e.g. `WRONGTYPE`) | Retried up to `MaxAttempts`, then `poisoned`; the row is kept and listed by `ListPoisonedAsync`, log event `outbox.poisoned`. |
| Consumer effect keeps failing or entry malformed | After `MaxDeliveries` copied to `<stream>:dead:<consumer>` and acknowledged; log event `inbox.dead_lettered`. Never silently dropped. |

**Outside the supported model:** loss of the Redis data volume, or a host/power failure that loses fsynced data (storage failure). PostgreSQL marks a row `published` when Redis accepts the entry, so such a loss loses entries that were published but not yet consumed, with no automatic replay. Recovery would be an operator-driven re-publish of `published` rows newer than the loss (the rows keep `stream_entry_id` and `published_at`); building that tool belongs to the first domain that needs it (Spec 07 for financial effects, Spec 12 for social/ranking consumers). `appendfsync always` costs one fsync per Redis write; it was kept because nothing has been measured that justifies weakening it, and Redis throughput under it must be measured with real consumers (Specs 07/12).

**Not yet built (owners in `02-spec-map.md`, "Spec 03 deferral register"):** stream trimming, manual requeue of poisoned or dead-lettered work, retention. Rule for whoever builds them: never trim an entry that any consumer group still has pending or has not yet read (trim only below the minimum of every group's last-delivered and pending IDs), and requeue only through the outbox/inbox path so the inbox still deduplicates.

## Evidence

Integration tests in `tests/Integration/Realtime/RealtimeDeploymentGuardTests.cs` exercise both guards against real PostgreSQL and the real resolved Compose model; `RealtimeHubTests` covers revocation; `tests/Integration/Messaging/OutboxInboxTests.cs` covers every row of the outbox table above except storage loss. Results of each run are recorded in `docs/kiro-log.md`.
