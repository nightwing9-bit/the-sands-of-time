# Ghost Lap lobby, controller epoch and gameplay ingress (Spec 10, task 10.1)

**Task 10.2 update:** the production start admission now commits a durable start receipt and runs the authoritative match (`MatchRegistry`, [ADR-P1-24](../adr/ADR-P1-24-ghostlap-match-authority-and-terminal-fence.md)); statements below that "no match runs" describe the 10.1 state.

**Status (10.1):** implemented and tested locally as described below. **No match runs.** A countdown can end in the start-admission handoff, and nothing in this task commits a start receipt, runs a tick or computes a score (task 10.2). Evidence and commands are in `docs/kiro-log.md`. Decision record: [ADR-P1-23](../adr/ADR-P1-23-ghostlap-realtime-ingress.md).

**Later tasks:** since 10.2 a running match exists (receipt before running, one loop per room); since 10.3 clients receive snapshots, can resync and are delivered to one connection at a time. See [ADR-P1-24](../adr/ADR-P1-24-ghostlap-match-authority-and-terminal-fence.md), [ADR-P1-25](../adr/ADR-P1-25-ghostlap-snapshots-fanout-and-forfeit.md) and `10-ghostlap-snapshots-and-network.md`. The "No match runs" sentence above describes task 10.1 only.

## 1. Where things live

| Piece | Path | Notes |
|---|---|---|
| Wire contracts (DTOs, failure codes, hub/stream names) | `src/GhostLap.Contracts/Multiplayer/GhostLapMultiplayerContracts.cs` | The TypeScript twin is not written yet (10.6 "typed clients"). |
| Room owner (lobby, roster, ready, host, epoch, countdown, admission handoff, ingress identity check) | `src/GhostLap/Multiplayer/ActiveRoomOwner.cs` | Pure domain: BCL, Kernel and Contracts only. No float/clock/random calls (the Spec 06 source scan covers this folder and passes). |
| Sealing, options, startup validation | `RoundSealing.cs`, `MultiplayerOptions.cs`, `MpRulesProfile.cs` | |
| Ports for task 10.2 | `RoomOwnerContracts.cs`: `IMatchStartAdmission`, `IGameplayInputSink` | Defaults: `UnavailableStartAdmission`, `NoRunningMatchSink`. |
| Hub, connection registry, revalidator, Origin check, kind-routing authorizers, room stream source, pumps, startup verifier | `src/Hosts.Realtime/GhostLap/*` | Host composition only; no room state. |
| Room authorizer | `src/GhostLap/Multiplayer/GhostLapAccess.cs` | One decision for the room stream and for resource checks. |

`Hosts.Api` gets **no** room command route. Every lobby and gameplay command enters `Hosts.Realtime`, the only process that can own a room (ADR-P1-02, `07-realtime-room-authority.md`). Read-only room state is the shared realtime stream `ghostlap-room:{roomId:N}` on `/hubs/realtime`; its sequence is the room lifecycle version.

## 2. Room state machine (task 10.1 scope)

`Lobby -> Countdown -> StartAdmitted`, `Countdown -> Lobby` (cancel), `* -> Closed` (expiry/empty). `StartAdmitted` means the 10.2 port accepted the sealed round. It is **not** a running match and no phase named running exists in 10.1.

- Roster: 2 to 4 competitors (`MaxCompetitors`, 4 by default), separate bounded spectators (8), one room per principal, one controlling connection and one epoch per competitor, room-scoped opaque `memberId` (guest principal ids never leave the server).
- Any roster change clears every ready flag. A cancel keeps ready flags.
- Host leaves or disconnects in the Lobby: the earliest-joined connected competitor becomes host. A returning host does not get the role back.
- Countdown cancels on: host cancel, a competitor un-readying, a controller disconnect, a competitor leaving. No competitor can join in Countdown or after admission (`join_closed`); spectators can.
- When the countdown elapses the sweep marks the room "admission in flight" and calls `IMatchStartAdmission` **outside the room lock**. While it is in flight the roster is frozen (cancel, un-ready and leave answer `start_committing`; a dropout is a match-time disconnect for 10.2/10.3, not a countdown cancel). `Unavailable`, an exception or the timeout (`AdmissionTimeoutMilliseconds`) returns the room to the Lobby with `start_unavailable`, ready flags kept. In production today that is always the outcome, which is the honest one.
- Duplicate starts (retry, double click, second tab, 16 parallel requests) return the one sealed round with `detail = already_started`. The admission port is called once.

## 3. Sealed round, and why cancelling cannot re-roll slots

`RoundSealing.Seal` is a pure function of a per-room secret (32 random bytes, never sent), the room id, the round ordinal, the roster and the options.

- `seed = HMAC(secret, "seed/1" | ordinal)`: independent of roster, ready order, cancels and request ids.
- Slot order = ascending `HMAC(secret, "slot/1" | ordinal | subject)` per principal. The relative order of two players is the same in every roster that contains both (property test). Join order, host choice, countdown count and request ids do not enter it. Slot *i* gets arena-v1 spawn socket *i* for the roster size (2/3/4 sockets).
- `matchId` and `configDigest` derive from the full sealed content, so one match id never names two configs. A cancelled and restarted countdown with the same roster re-seals the identical round (tested: 6 mixed cancel/un-ready/reconnect cycles). Only a different roster, a different ordinal (rematch, task 10.4) or a different authority generation changes it.
- Residual, accepted: changing **who is in the room** changes the roster; a colluding group can exclude a player. That is a roster decision, not a roll. The secret is unknown to players, so a revealed config says nothing about a roster not yet tried.
- New countdowns seal `ghost-mp-v2` on `arena-v1` per [ADR-P1-27](../adr/ADR-P1-27-ghostlap-competitive-conflicts-and-terminal-ingress.md). Earlier rounds retain their sealed `ghost-mp-v1`. Other sealed fields include the rules hash string, a SHA-256 layout fingerprint computed from the authored geometry (Ghost Lap-owned, not a Spec 06 recorded hash), seed, `ratingAlgorithmId` (opaque, default `elo-pair-v1`), 2700 ticks, countdown ms, slot/spawn assignment, and a **process-local** authority generation. The durable generation is task 10.2.
- Startup validation (`MultiplayerStartup`, run at host start and by the owner constructor): option ranges, 30 Hz / 2700-tick baseline, 2/3/4 sockets exist, are occupiable, distinct, at least four collider half-extents apart, and 2P/4P sockets are point-symmetric. It checks facts about arena-v1; it does not re-measure the fairness ratios of Spec 06 task 6.1.

## 4. Gameplay command ingress: decision and proof

**Question:** can a Ghost Lap hub carry responsive movement, rapid legal direction changes, Burst and keepalives despite `AddMarrowRealtime`'s global per-invocation filter (10/s, burst 20 per connection) and `MaximumParallelInvocationsPerClient = 1`, without changing Marrow or Revenant limits?

**Findings (code and runs):**
1. `AddMarrowRealtime` registers the filter on the global `HubOptions`, so a second hub inherits it. `HubOptions.HubFilters` is not public, so a hub cannot opt out through a supported API. Reflection into SignalR internals is not acceptable.
2. Per-item invocations are throttled. Exploratory spike (a scratch project, not committed) at 30 invocations/s for 5 s: 69 executed, 81 throttled. The committed test `The_same_traffic_as_per_item_invocations_is_throttled_by_the_shared_filter_with_a_parseable_reply` asserts the pass of the burst allowance, throttling afterwards, and that the throttle reply (`ok`, `code = rate_limited`) deserializes as the Ghost Lap reply type.
3. A SignalR **client-to-server stream** is one invocation. The filter counts it once; items are not invocations. The spike delivered 300 items at 60/s and a second ordinary invocation completed while the stream was open.

**Decision:** gameplay is `StreamInputs(IAsyncEnumerable<GameplayInput>)`, one stream per connection, on a Ghost Lap-owned hub `/hubs/ghostlap`, WebSockets only. Lobby commands are ordinary invocations (rare; the shared filter is appropriate and they re-read the session every time). The item rate is bounded by Ghost Lap itself (`InputBudget`, 120/s sustained, burst 60, close after 240 consecutive over-limit items), upload-stream buffering is SignalR's own (`HubOptions.StreamBufferCapacity`; its backpressure behavior was not separately measured), the 8 KiB message cap applies per item, and rejection notices are themselves rate limited.

**Result: not BLOCKED.**

**Proven locally (real Kestrel-style WebSocket, real filter, real guest cookie):** `Gameplay_stream_carries_30Hz_rapid_turns_burst_and_keepalives_without_any_throttling_by_the_shared_filter`: 405 items in 8.97 s (45.2/s sustained: 30 Hz state, a keepalive every 4th tick, a Burst edge each second, then 3 s at 60 items/s of alternating legal directions), every item reached the sink in order, 0 rejected, no `rate_limited`, and lobby invocations on the same connection kept succeeding. A 5000-item flood is bounded by the Ghost Lap limit and the connection is cut.

**Not proven, owned by later tasks:**
- Browser client: the shared `web/packages/contracts/src/signalr.ts` already connects WebSockets with `skipNegotiation`; upload streaming from `@microsoft/signalr` (`Subject`) is untested here (10.3/10.6).
- Sustained throughput of one stream under real RTT/loss and with four players per room across several rooms (10.3, 10.5). 45 items/s is a single-client local figure, not a capacity number.
- Sequence/tick-window admission, per-slot mailbox and applied acknowledgements (10.2/10.3). Task 10.1 owns identity, role, controlling-connection and epoch only.
- Session liveness for stream items is **not** read from the database per item. It is the periodic revalidator (bound `Realtime:AccessRevalidationSeconds`, plus the 5 s check timeout) and the stream-open check. A revoked session can keep submitting inputs for at most that bound, then the connection is aborted. Lobby commands re-read the session every call.

## 5. Controller epoch

Each competitor has one epoch and one controlling connection. A join/attach from a different connection increments the epoch and, under the room lock and before returning, replaces the old connection's binding (`Controlling = false`). Gameplay admission is a lock-free read of that binding plus an epoch comparison, so an item processed after the takeover is refused (`controller_superseded`) whatever epoch it claims, and the old connection gets a `ControllerSuperseded` push. The old tab may keep viewing (`GetRoom`, the room stream) but every control command answers `not_controller`. Closing the *controlling* tab disconnects the competitor even if a viewer tab is open; the viewer must claim control explicitly. Spectator connections have no epoch and every gameplay item is `spectator_forbidden` (also checked at the hub boundary by the owner's role check).

## 6. Authorization composition (R10.20)

The Kernel has one `IResourceAuthorizer` and one `IMembershipAuthorizer`, and `AddRevenantRealtime` installs Revenant's policy with `services.Replace`. `AddGhostLapRealtime` therefore **wraps** whatever is registered last in a scoped kind router (`RouteGhostLapAuthorizers`):

- kind starts with `ghostlap` (any case) -> Ghost Lap only, default deny (unknown or forged `ghostlap-*` never reaches another domain);
- everything else -> the previously registered authorizer (Revenant staff policy, the identity deny-all, or a test policy).

`GhostLapCompositionVerifier` (a hosted service) refuses to start the host if the resolved ports are not the routers (someone replaced them later, e.g. `AddRevenantRealtime` called after `AddGhostLapRealtime`), if a required background service is not registered, or if the rules/layout facts fail. A later domain that replaces a port must call `RouteGhostLapAuthorizers()` again; `RealtimeHostFactory` does this for its probe policies.

## 7. Browser-to-Realtime topology (intended deployment)

- **Identity:** `POST /auth/guest/start` on `Hosts.Api` sets `__Host-sands_guest` (Secure, HttpOnly, SameSite=Lax, Path `/`, **no Domain**). `Hosts.Realtime` shares `Identity__MasterSecret` and the identity tables and authenticates the same cookie on the WebSocket upgrade. A guest cookie signed with another master secret is rejected (tested).
- **One public origin, path routed:** because the cookie name carries the `__Host-` prefix it is host-only, so the SPA, `/auth/*` (Api) and `/hubs/*` (Realtime, WebSocket upgrade) must be reachable on **one hostname and port**. This is the shape of the dev proxy (`web/apps/ghost-lap/vite.config.ts`: `/auth` to Api, `/hubs` to Realtime, `changeOrigin: false`). A split-hostname deployment (api.x vs realtime.x) cannot share the cookie and is unsupported without a new ADR.
- **Origin:** the Ghost Lap hub has its own check (`UseGhostLapRealtime`): exactly one `Origin`, equal to the request's own origin or listed in `GhostLap:Multiplayer:AllowedOrigins`. `Identity:AllowedOrigins` is deliberately not consulted (tested: an origin allowed for the shared hub is refused on the game hub).
- **Single replica** remains guaranteed by the existing advisory-lock guard; the hub refuses commands and the sweep aborts Ghost Lap connections when `AuthorityLost`.

**Proven locally:** cookie issued by the real `Hosts.Api`, accepted by the real `Hosts.Realtime` Ghost Lap hub; Origin matrix; staff token, no credential and foreign-secret cookie refused; revocation (next command refused and connection closed; idle connection closed within the interval; room-stream subscription dropped).

**Needs real deployment validation in 10.6 (not inferred):**
1. A reverse proxy (none exists in the repo) that serves SPA, `/auth` and `/hubs` on one origin, upgrades WebSockets, and has idle/read timeouts above SignalR's keepalive (15 s) and client timeout (30 s).
2. TLS termination: the repository configures no forwarded-headers handling, so behind a TLS-terminating proxy `request.Scheme` is `http` while the browser `Origin` is `https`. The public origin must then be listed in both `Identity:AllowedOrigins` and `GhostLap:Multiplayer:AllowedOrigins`. Unverified.
3. A real browser (`@microsoft/signalr`) opening the gameplay upload stream and surviving tab suspension/reconnect.
4. Compose already sets the single-replica rule; no Ghost Lap setting was added to `deploy/`. Production login grants are unchanged because task 10.1 touches no database (no `ghostlap` migration or role).

## 8. Known limits and follow-ups

- The shared `SignalRRealtimePublisher` keeps a small per-stream sequence record for the life of the process (no remove), so room churn grows it slowly. Marrow is frozen; measure in 10.5.
- Publications and notices use bounded drop-oldest queues; a drop is a sequence gap that subscribers repair with a snapshot (stream contract). The publication pump awaits each group send, so one slow subscriber delays later publications (lobby only; the 10.3 fan-out design must not reuse this for gameplay snapshots).
- Rooms do not survive a process restart (in memory by design; no result is at stake before 10.2).
- `Guest` principals in `RoundSealing` digests are internal; the public view never carries them.
