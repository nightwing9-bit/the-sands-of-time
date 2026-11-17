# ADR-P1-23 — Ghost Lap lobby owner, hub ingress and authorizer composition
- **Status:** Accepted at Spec 10 task 10.1. Implemented for the lobby, countdown and start-admission handoff only. The authoritative start receipt, tick loop and terminal results are task 10.2 and later.
- **Evidence:** `docs/architecture/09-ghostlap-lobby-ingress.md`, `tests/GhostLap/Multiplayer/*`, `tests/Integration/GhostLap/*`, `docs/kiro-log.md`.
- **Compatibility check:** no change to `src/Marrow.*`, `src/Revenant*`, `ghost-v1` rules/codecs/goldens, `web/`, `scripts/` or `deploy/`. One shared test-support file (`tests/Integration/Support/RealtimeServices.cs`) gained a hub-path parameter, a customization hook and one `RouteGhostLapAuthorizers()` call.

## Context
`AddMarrowRealtime` installs one hub (`/hubs/realtime`) that accepts no client commands, a global per-invocation rate filter (10/s, burst 20, `MaximumParallelInvocationsPerClient = 1`) and an Origin check for that path only. `AddRevenantRealtime` replaces the single `IResourceAuthorizer` and `IMembershipAuthorizer`. Ghost Lap needs client commands (lobby, gameplay), its own Origin policy, its own connection revalidation and its own room policy, without weakening any of that.

## Decisions
1. **One owner, no API route.** `ActiveRoomOwner` (in `src/GhostLap`) is the only holder of room state. A thin Ghost Lap hub in `Hosts.Realtime` is the only command ingress. `Hosts.Api` gets no room route. Room state is published read-only on the shared hub as stream `ghostlap-room:{roomId:N}`, sequence = lifecycle version.
2. **Gameplay is a client-to-server stream, not an invocation per item.** The global filter cannot be bypassed through a public API and per-item invocations are throttled (measured). A stream is one invocation, so the existing filter and Revenant's limits stay exactly as they are, and Ghost Lap bounds the item rate itself. If a browser client cannot upload streams, the fallback is to report BLOCKED, not to raise the shared limit.
3. **Controller epoch is enforced by replacing the old connection's binding under the room lock**, so admission after a takeover is a lock-free read that cannot see the old binding as controlling.
4. **Start admission is a port.** `IMatchStartAdmission` is called outside the room lock after the countdown elapses; only `Admitted` moves the room to `StartAdmitted`. The default port always answers `Unavailable`, so no code path in 10.1 claims a running match. Cancel/un-ready/leave are refused while admission is in flight; a failure returns a retryable lobby with the same sealed round.
5. **Slot fairness by keyed per-principal order.** Slots come from `HMAC(roomSecret, ordinal, subject)` order and the seed from `HMAC(roomSecret, ordinal)`, never from join order, host choice, countdown count or request ids. Re-seals are identical for an identical roster.
6. **Authorizer composition by wrapping.** `RouteGhostLapAuthorizers` makes a scoped kind router the registered port and keeps the previously registered authorizer as its fallback. `ghostlap*` kinds are Ghost Lap-only default deny. A startup verifier refuses to run if a later `Replace` removed the routers or a required service is missing.
7. **Separate Origin check and separate revalidation** for `/hubs/ghostlap` (`GhostLap:Multiplayer:AllowedOrigins` only; `Identity:AllowedOrigins` is not consulted). Lobby commands re-read the guest session on every call; gameplay stream items rely on the periodic revalidator (`Realtime:AccessRevalidationSeconds`) plus the controlling-binding check.
8. **Topology:** one public hostname, path-routed (`/auth` to Api, `/hubs` to Realtime), because the guest cookie is `__Host-` prefixed (host-only). Split hostnames are unsupported without a new ADR.
9. **Source discipline:** the Ghost Lap domain assembly keeps the Spec 06 source scan clean (no float, clock reads, ambient random). Token buckets use integer thousandths of a token over `TimeProvider` timestamps.

## Consequences
- Task 10.2 registers real `IMatchStartAdmission` and `IGameplayInputSink` implementations (the `TryAdd` defaults yield) and owns the durable generation and receipt; the sealed config's authority generation is process-local until then.
- Any future domain that replaces either authorizer port must call `RouteGhostLapAuthorizers()` afterwards, or the host will not start.
- Gameplay needs a browser client that uploads a SignalR stream; this is unverified until 10.6.

## Rejected alternatives
- Per-item hub invocations with a raised shared limit: weakens the Revenant-protecting global filter, which Spec 10 forbids.
- Reflection into SignalR options to remove the filter for one hub: depends on private API.
- A raw WebSocket endpoint for gameplay: would duplicate authentication, Origin and revalidation outside the hub pipeline; kept only as the BLOCKED-report fallback.
- A command route on `Hosts.Api` forwarding to Realtime: no existing API-to-Realtime command path; would create a second ingress to the room owner.
- Per-item database session reads on the gameplay stream: wrong cost at 30+ items/s per client.
