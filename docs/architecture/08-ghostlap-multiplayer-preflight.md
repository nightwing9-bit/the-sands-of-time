# Ghost Lap multiplayer preflight (Spec 10, task 10.0)

**Status:** preflight evidence and decisions only. No multiplayer code exists. Nothing in Spec 10 is implemented by this note.
**Inspected:** HEAD `2dd9292` ("docs(ghostlap): harden architecture for specs 10-12") plus the uncommitted working tree listed in `docs/kiro-log.md` for this task. SDK 10.0.403 (`source scripts/checks/dotnet-env.sh`). Docker was not running, so no PostgreSQL-backed test ran.
**Decisions:** the sealed multiplayer rules profile is recorded in [ADR-P1-22](../adr/ADR-P1-22-ghostlap-multiplayer-rules-profile.md). Section 3 below repeats it with the evidence behind each line.
**Measurement tests:** `tests/GhostLap/Multiplayer/PreflightSpeedSafetyTests.cs` (6 tests, test-only, no production change).

## 1. Compatibility matrix

"Reuse" means the existing code can be called unchanged. "Add (Ghost Lap)" means new Ghost-Lap-owned code, schema or version. "Add (host)" means composition in `src/Hosts.*` or `scripts/db/migrate.sh` only. No row requires a change to `src/Marrow.*`, `src/Revenant*`, `web/packages/timeline-ui` or any `ghost-v1` byte.

| Area | What exists (path) | Verdict for multiplayer |
|---|---|---|
| Rules constants | `src/GhostLap/Rules/GameRulesV1.cs`: 30 Hz, 2700 ticks, 1600x900 u world, fixed point 1024/u, square collider half 24 u, cardinal step 10 u/tick, diagonal axis step 7241 fixed, candle half 12 u, charge 0..4, pickup 25, Overlit 240, contact loss 1 / cooldown 45, Burst radius 160 u inclusive, Burst loss 2 / 90 points, 16 u per movement point, hold 6 ticks, late 4 / lead 2 | **Reuse values.** Multiplayer gets its own rules id `ghost-mp-v1` (ADR-P1-22) so `ghost-v1` stays closed. |
| Layout | `src/GhostLap/Rules/ArenaV1.cs`: 9 point-symmetric obstacles, 24 candle sites, sockets for 2P (opposite), 3P (triangle), 4P (corners) | **Reuse** `arena-v1`. Fairness measured in Spec 06 6.1 (worst slot distance ratio 1.154 / 1.307 / 1.158 for 2P/3P/4P over 101 seeds); not re-measured here. |
| Candle schedule | `src/GhostLap/Rules/CandlePlan.cs`: stateless hash of (seed, index, purpose); no player-count or action input | **Reuse.** Independent of roster and pickups, as R10.12 requires. |
| Reducer and phase order | `src/GhostLap/Simulation/GhostLapTransition.cs`: 1 validate/sort/effective input, 2 move (slot order), 3 candle activation, 4 pickups (candle id order, lowest overlapping slot), 5 contact (sorted pairs), 6 manual then auto Burst (slot order, max one per player), 7 `RoundFinished` on tick 2700. Supports 1 to 4 players. | **Reuse as the single simulation**, extended by Ghost Lap: a phase 0 participation step and a forfeited-slot mask through phases 1 to 6 (ADR-P1-22 D2, D9). `ghost-v1` must run the same code with an all-active mask and keep its golden pins. |
| Inactive/forfeited slot | None. `PlayerState` (`src/GhostLap/Simulation/GameModel.cs`) has no connection, forfeit or participation field. Measured: a slot given only the disconnect neutral input still auto-Bursts, hits a rival and collects a candle (`Ghost_v1_neutralized_slot_still_auto_Bursts_and_collects`). | **Add (Ghost Lap):** MP state with a forfeit tick per slot, new state schema `ghost-mp-state/1`. A neutral input alone cannot implement R10.06. |
| State codec / version key | `src/GhostLap/Timeline/GhostLapCodecs.cs`, `GhostLapTimelineVersions.cs`: `ghost-state/1`, `ghost-input/1`, `ghost-context/1`; `GameContextCodec` rejects any rules/layout other than `ghost-v1`/`arena-v1`; `GameContext.RulesVersion` is hard-wired to `GameRulesV1` | **Do not touch.** MP needs its own codec for the hashed MP state (determinism tests, snapshots). MP is not archived, so no new `TimelineVersionKey` is registered. |
| Velocity | Not stored. The effective vector is `HeldMoveX/HeldMoveY`; velocity = vector x step (cardinal 10240, diagonal 7241 per axis). | **Reuse**: snapshot derives velocity from the held vector. Contract text in `03-contracts.md` says "pos/velocity"; this is the existing equivalent, not a gap. |
| Input record | `GameInput(tick, slot, sequence, moveX, moveY, requestBurst)`; no position field | **Reuse** as the payload inside the MP envelope `(matchId, generationId, controllerEpoch, clientTick, seq, move, requestBurst)`. |
| Input admission | `src/GhostLap/Simulation/InputAdmission.cs`: applies to the next server tick on arrival; rejects stale sequence (including pending), `ClientTick` more than 4 behind or 2 ahead, bad vector, unknown slot, round over; `Neutralize` gives an immediate neutral with the next sequence | **Reuse** the policy. **Add (Ghost Lap)** the epoch/connection/role checks around it (R10.03, R10.17). |
| Burst edge | Latch = last accepted `requestBurst`; edge only on accepted inputs; measured: a late-rejected press is recovered once by the next pressed input and never repeats (`A_late_rejected_Burst_press_...`) | **Reuse.** Satisfies "lost packets do not create repeated Burst" at the rules level. |
| Wall collision | `src/GhostLap/Simulation/Movement.cs`: X then Y, each axis limited by the nearest blocker in the collider's lane along the whole path (swept per axis), world clamp | **Reuse.** Measured swept-safe (section 4). |
| Player-player collision | None. Players overlap; contact is an end-of-tick overlap test that drains charge only | **Reuse** (ADR-P1-22 D5). No depenetration exists, so no correction can earn distance. |
| Pickup and contact sampling | End-of-tick AABB overlap only | **Reuse** with the measured graze bound (section 4, ADR-P1-22 D6). |
| Events | `GameState.Events`: this tick's events in phase order | **Reuse**: event id = `(matchId, generationId, tick, index in Events)` (R10.08). |
| Result | `GameState.FinalResult`: totals and a winners list (ties allowed); no placement, forfeit or eligibility | **Add (Ghost Lap):** `MatchResult` with placement classes and rating fields (ADR-P1-22 D12, D13). |
| Marrow Timeline | Used by solo originals/branches only | **Not used** by MP (no per-tick archive). No change. |
| Identity | `src/Marrow.Infrastructure/Identity`: guest principal `guest:{uuid}` stable across sessions (`GuestIdentity.SubjectId`), session cookie, server-side revocation; `GuestSessionService.IsSessionLiveAsync` is public | **Reuse.** Slot binding uses `IActorContext.SubjectId`. |
| Shared hub | `src/Marrow.Infrastructure/Realtime/RealtimeHub.cs`: `/hubs/realtime`, only `Subscribe`/`Unsubscribe`/`GetSnapshot`; "accepts no client-published events"; read streams via `IRealtimeStreamSource` | **Reuse for read-only room/match streams** (spectators, snapshots). **Cannot carry gameplay commands** without a Marrow change. |
| Command ingress | None for Ghost Lap in any production host | **Add (Ghost Lap + host):** a Ghost-Lap-owned hub mapped by `Hosts.Realtime` at its own path. Constraints found below. |
| Hub constraints that also apply to a second hub | `AddMarrowRealtime`: global `RealtimeRateLimitFilter` on every hub invocation (default 10/s, burst 20 per connection, returns a `RealtimeReply` `rate_limited` object), `MaximumParallelInvocationsPerClient = 1`, global `MaximumReceiveMessageSize` 8 KiB; `UseMarrowRealtime` Origin check covers `/hubs/realtime` only; `RealtimeAccessRevalidator` and `RealtimeConnections` track only the shared hub; `RealtimeAccess` is `internal` | The Ghost Lap hub must (a) fit the 10/s input budget or change the host-wide setting knowingly (section 5), (b) return `RealtimeReply`-compatible results so a throttled reply parses, (c) add its own Origin check for its path, (d) revalidate its own connections' sessions and memberships. |
| Hub authorization | One `IResourceAuthorizer` and one `IMembershipAuthorizer` singleton. `AddRevenantRealtime` (`src/Revenant/Api/RevenantRealtime.cs`) installs `RevenantStaffAuthorizer` with `services.Replace` | **Conflict, resolvable by host composition.** A second `Replace` by Ghost Lap would silently remove Revenant's policy (R10.20). `Hosts.Realtime` must register a kind-routing authorizer (`ResourceRef.Kind`, `MembershipScope.Kind`) that delegates Revenant kinds to `RevenantStaffAuthorizer` and Ghost Lap kinds to the Ghost Lap authorizer, default deny. Needs a coexistence test. |
| Publisher | `SignalRRealtimePublisher`: bound to the shared hub, per-stream sequence, awaits each group send, 64 KiB payload | **Reuse for read streams**, called from a fan-out worker, never from the tick step (R10.10). |
| Process exclusivity | `RealtimeInstanceGuard`: session advisory lock `0x4D4152524F575254` on a dedicated non-pooled connection, probe every 5 s, `StopApplication` on loss; public `IsHeld`/`AuthorityLost`; documented overlap of about 10 s after a silent session loss (`07-realtime-room-authority.md`) | **Reuse as the guard** (recommendation for the 10.2 ADR, not decided here). Durable generation fencing is still required because the guard is not a fence. |
| Ghost Lap schema | `scripts/db/migrations/ghostlap/0001_solo_originals.sql`, `0002_solo_branches.sql`; role `ghostlap_runtime` (solo tables only) | **Add (Ghost Lap):** `0003+` for `realtime_generation`, `match_session`, `match_result`, outbox/inbox (`MessagingTables` shape), and a separate least-privilege MP role. |
| Outbox | `PostgresOutboxWriter`, `MessagingTables(schema)`, `AddMarrowOutboxRelayService` (Worker registers `revenant` only) | **Reuse mechanics. Add (host):** Worker relay registration for the `ghostlap` schema (10.4). |
| Deployed grants | `migrate.sh`: `sands_realtime` = `marrow_identity_runtime`; `sands_realtime_ledger` = `revenant_replay_reader`; no deployed login holds any `ghostlap` role (only the E2E test login does) | **Add (host):** grant the new MP role to `sands_realtime` through `provision_login` and add it to `RUNTIME_ROLES`, plus a least-privilege test (Spec 03 deferral register). |
| Hosts | `Hosts.Realtime` references Kernel, Timeline, Infrastructure, Revenant; no GhostLap reference. `Hosts.Api` serves no Ghost Lap route (`TimelineSurfaceIsolationTests` asserts 404) and has no command path to Realtime | **Add (host):** GhostLap reference and registration in `Hosts.Realtime`. Ingress decision in section 2. |

## 2. Login, ingress and runtime grant path

- **Login owner:** Marrow Identity endpoints mapped by `Hosts.Api` (`MapMarrowIdentityEndpoints`): `POST /auth/guest/start`, `/auth/guest/signout`, `GET /auth/guest/me`; staff under `/auth/staff`. The guest cookie is accepted by `Hosts.Realtime` because both hosts call `AddMarrowIdentity` with the same `Identity__MasterSecret` and read the same `marrow_identity` tables (Compose `deploy/compose/docker-compose.yml`). Verified so far only on localhost, where cookies ignore the port; a split-hostname deployment is unverified (owner: 10.6).
- **Realtime connect policy:** `RealtimePolicies.Connect` (staff bearer or guest session), Origin check for cookie requests on `/hubs/realtime`.
- **Room/gameplay ingress (recommended for 10.1):** every lobby, ready, start, input and rematch command enters `Hosts.Realtime`, the only process that can own a room. `Hosts.Api` gets no room command route, because no API-to-Realtime command path exists and inventing one would create a second ingress to the room owner. `Hosts.Api` may later serve read-only finalized results from PostgreSQL. This satisfies the design's "do not instantiate a competing owner" without a new transport.
- **Grant path:** module migration creates a NOLOGIN Ghost Lap MP role; `scripts/db/migrate.sh` `provision_login sands_realtime REALTIME_DB_PASSWORD "marrow_identity_runtime <new role>"`; the role name is appended to `RUNTIME_ROLES` so a re-run converges. Worker relay grants follow the same path in 10.4.

## 3. Sealed multiplayer rules profile (summary of ADR-P1-22)

| # | Decision | Basis |
|---|---|---|
| D1 | Identity `ghost-mp-v1` + layout `arena-v1`; every `GameRulesV1` constant reused unchanged | R10.12; no measurement justifies tuning |
| D2 | Tick order: phase 0 participation (apply forfeits scheduled for this tick), then ghost-v1 phases 1 to 7 with forfeited slots masked | ghost-v1 order is already defined for 1 to 4 players; only the participation step is missing |
| D3 | No wall occlusion for Burst | ADR-P1-09, `BurstRadiusFixed` comment |
| D4 | Same-tick ties keep ghost-v1 slot priority (lowest slot wins a contested candle; lower slot's Burst resolves first and can cancel a higher slot's Overlit). Slot indices are assigned to players by a seeded permutation sealed at countdown, never by join order or host choice. Measured in 10.5; any change is `ghost-mp-v2` | Spec 06 is not silent on this order; R10.19 forbids unmeasured rule changes |
| D5 | Players pass through each other; contact is end-of-tick overlap; no depenetration | `GhostLapTransition` phase 5, project context §5.5.1 "both keep moving" |
| D6 | Pickup and contact stay end-of-tick overlap tests, with the measured miss bound in section 4 | measured envelope |
| D7 | Score coefficients unchanged; movement ceiling 1687 points per round (2700 x 10240 / 16384, floored); corner farming is a 10.5 measurement, not a change now | R10.19 |
| D8 | Disconnect: server `Neutralize` for the next tick D; grace = ticks D..D+449; a takeover processed before tick D+450 is resolved keeps the slot; otherwise forfeit at tick F = D+450; F > 2700 never forfeits | ADR-P1-11, R10.06 |
| D9 | Forfeited slot from tick F: no input, no movement, cannot collect, no contact pair (no drain, no cooldown), cannot fire or be hit, pending Overlit deadline cleared without a Burst; score and stats frozen; reconnect after F is spectator-only | R10.06 |
| D10 | A Burst key held across a reconnect gives one rising edge on the first accepted input (latch was released by `Neutralize`); accepted | at most one Burst, needs Overlit |
| D11 | Terminal: tick 2700, all phases including a last-tick auto-Burst, then finish; slots not forfeited by tick 2700 are finishers | ghost-v1 phase 7, R10.13 |
| D12 | Placement: finishers above forfeits; higher score first within a class; equal score in a class shares a placement; slot order is display-only. Eligibility policy `mp-eligibility-v1`: rated only with at least one finisher and at least two distinct eligible guest principals at start; service abort is unrated and never a forfeit | R10.13, design §4 |
| D13 | `ratingAlgorithmId = "elo-pair-v1"` sealed in the countdown config, stored on the receipt, result and `MatchFinalizedV1`; Ghost Lap never interprets it | R10.02, Spec 12 R12.09 |
| D14 | Timing: 30 Hz logical ticks, 2700 ticks, 3 s wall-clock countdown before tick 1, snapshot every 2 ticks, catch-up at most 3 ticks per scheduler iteration; overload threshold is a deployment setting | requirements NFR |
| D15 | 2 to 4 players only; solo (1 player) never enters an MP room or a rating | game-authority rule |

## 4. Max-speed safety (R10.11)

**Hypothesis:** at the single legal speed of ghost-v1 there is no tunnelling through walls and no pass-through miss of a pickup or a contact; the only sampling gaps are shallow diagonal corner grazes.

`d_max` from the constants: cardinal 10240 fixed per tick; diagonal 7241 per axis, `IntegerSqrt(2 x 7241²) = 10240`. There is no acceleration, so this is the only speed. Measured by `Maximum_per_tick_displacement_...`: d_max = 10240 (10.000 u/tick), per-axis max 10240, thinnest obstacle 40960 (40 u), pickup window 73728 (72 u), contact window 98304 (96 u). Stale travel after the last accepted input = 6 x 10240 = 61440 (60 u, 1.25 collider widths).

| Interaction | Method in code | Measured (this task) | Finding |
|---|---|---|---|
| Wall and world edge | Swept per axis: X leg, then Y leg, each stopped by the nearest blocker in its lane | 10,816,976 moves (every unit-grid start in the world plus 200,000 off-grid fixed-point starts, 8 directions): 0 swept-box overlaps, every end position occupiable | No tunnelling at any speed below the lane logic's limits; independent of step size. |
| Candle pickup | End-of-tick overlap | 2,865,696 start/direction cases around all 24 sites on a 0.5 u grid: 0 cardinal misses, 19,376 diagonal corner grazes, max missed depth 7168 fixed (7.0 u) on the grid; analytic bound < 7241 (one diagonal axis step) | A path through a candle is always sampled. A diagonal can clip a pickup corner by less than 7.07 u per axis without collecting. Accepted for `ghost-mp-v1` (D6); readability checked in 10.5. |
| Player contact | End-of-tick overlap | 2,193,628 cases (relative start offsets on a 0.5 u grid, all 64 direction pairs, open floor) against a continuous straight-line reference: 0 pass-through misses, 16,416 corner grazes, max missed depth 7168 fixed (7.0 u) on the grid; the test asserts the analytic bound min(per-axis relative displacement) ≤ 14482 | A head-on or crossing pass is always sampled. Corner grazes of about 7 u are missed. Accepted (D6). |
| Burst | Instant radius check at the tick | not sampled over time | No sweep needed. |
| Distance score | `IntegerSqrt` of the displacement that happened; blocked axis earns 0 | existing `MovementTests`, `InputAndScoreReplayTests` | No depenetration or teleport path exists to earn from. A diagonal into a wall slides at 7241 per tick (70.7 percent speed); feel check in 10.5. |

## 5. Input-window profile hypothesis `ghost-mp-net-v0` (not frozen)

Pinned semantics (reused from ghost-v1): an admitted input applies to the next server tick on arrival; `clientTick` is only a freshness filter (late 4, lead 2 ticks); the highest admitted sequence per slot per tick sets movement; acknowledgements report received / admitted (with typed rejection) / applied tick.

Client offset estimate: `serverTickNow ≈ snapshot.serverTick + round(RTT/2 / 33.3 ms)`, RTT from the applied-input acknowledgement; the client tags `clientTick = serverTickNow + 1`. The 7-tick acceptance band (233 ms) tolerates an estimate error of -4..+2 ticks.

Send policy: send on any change of vector or Burst state, and a keepalive of the current state every 4 ticks (133 ms) while non-neutral. That is 7.5 messages/s sustained, under the shared hub's 10/s per-connection limit, with the burst of 20 absorbing quick turns. A keepalive delayed beyond 200 ms lets the hold expire and the player stops; that is the honest outcome, bounded by 60 u of stale travel.

Transport facts that shape loss tests: SignalR WebSocket runs over TCP, so a live connection delays messages rather than dropping them; a dropped connection triggers the disconnect path (D8). Loss and jitter tests in 10.3 must therefore inject delay, reorder across reconnects and disconnects, not only packet drops. The profile is frozen only after the 50/150/300 ms RTT measurements in 10.3. **Task 10.3 result:** the window, hold and cadence are kept unchanged and the client estimate is amended to a closed loop; see ADR-P1-25 and `docs/architecture/10-ghostlap-snapshots-and-network.md`.

## 6. Open items and blockers

No item blocks task 10.2 on contract or rules grounds. The following are owned by later tasks and must not be reported as passed:

| Item | Status | Owner |
|---|---|---|
| PostgreSQL-backed Ghost Lap tests (`tests/GhostLap/Solo/*`) | NOT RUN in this task: Docker daemon not running. They contain no `ghost-v1` golden hash assertion (the two pins are pure tests and passed). | rerun when Docker is available |
| Kind-routing authorizer so Ghost Lap does not replace Revenant's hub policy | design known, not built | 10.1/10.2, verified 10.6 |
| Ghost Lap hub Origin check, session revalidation, rate-limit fit | not built | 10.1/10.3 |
| Generation fence and receipt tables, MP role and `sands_realtime` grant | not built | 10.2/10.4 |
| Slot-priority bias (D4) and corner farming (D7) | decision recorded, effect unmeasured | 10.5 |
| Network profile values (section 5) | hypothesis | 10.3 |
| Guest cookie across separate API/Realtime hostnames | unverified (localhost only) | 10.6 |
| Revocation sweep cost at room scale (Spec 03 deferral) | unmeasured | 10.5 |
