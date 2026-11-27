# Ghost Lap multiplayer: backend integration and demo-readiness (Spec 10 task 10.6)

Status is recorded in the exit verdict below. The backend evidence uses C# SignalR clients. The browser acceptance gate covers visuals, input feel, prediction and audio.

## 1. Task 10.6 test coverage

| Item | Where |
|---|---|
| Production-composition test (one owner, real transport, no test hook, only the hub route, anonymous refused) | `tests/Integration/GhostLap/GhostLapDemoGateTests.cs` `The_production_composition_has_one_owner_and_no_test_seam` |
| Source scan: no test auth handler, `AllowAnonymous` or test route in `src/Hosts.*`, `src/GhostLap*` | same file, `Production_source_contains_no_test_authentication_or_anonymous_gameplay_route` |
| `ghost-mp-v2` identity through four real sockets and a spectator, result via the hub, DB receipt/result/outbox, then a real rematch | same file, `Rules_identity_ghost_mp_v2_flows_through_four_real_sockets_the_result_and_a_fresh_rematch` |
| Crash after the result commit, successor reads the result | same file, `A_crash_after_the_result_commit_leaves_the_committed_result_readable_from_the_successor` (helper `PgFaultProxy.DownAfterDroppedCommit` added) |
| Real-time 2/3/4-player theory and full 4P rematch (enable with `GHOSTLAP_DEMO_GATE=1`) | `Real_time_matches_complete_over_real_sockets_with_results_and_rematch`, now backed by `GhostLapDemoGateTests.Kestrel.cs` and a separate Production Realtime process |
| Actual post-commit process termination and restart | The 4P Kestrel case kills its Realtime child after both committed outcomes and starts a replacement against the same database |
| Rare events, charge and resync | `GhostLapDemoGateTests.Events.cs`, `Real_hub_delivers_Overlit_cancellation_auto_Burst_and_forfeit_once_with_resync` |
| Expired guest session at handshake | `An_expired_guest_cookie_cannot_open_the_Ghost_Lap_hub` |

Task 10.6 changes are confined to the test harness and documentation. Task 10.5 production changes are catalogued in the benchmark report and ADR-P1-27.

## 2. Production-contract inspection (source read, then pinned by tests)

| Contract | Finding | Evidence |
|---|---|---|
| Realtime DI | `Program.cs` order: `AddMarrowRealtime`, `AddRevenantRealtime`, then `AddGhostLapRealtime`. One `MatchRegistry` is start admission, input sink, presence and resync. Transport is `GhostLapSignalRTransport`; randomness `CryptoRoomRandom`; clock `TimeProvider.System`. `RuntimeCreated` hook is null. | `The_production_composition_has_one_owner_and_no_test_seam`; `GhostLapCompositionTests` (6 tests) |
| API host | `Hosts.Api` registers no Ghost Lap type or route. It only issues the guest cookie (`POST /auth/guest/start`). All gameplay is on `Hosts.Realtime` `/hubs/ghostlap`. | `git status` on `src/Hosts.Api`: unchanged; route list test |
| Routes | `/`, `/health/live`, `/health/ready`, `/hubs/ghostlap`(+negotiate), `/hubs/realtime`(+negotiate). Nothing else mentions Ghost Lap, test or debug. | route assertion, printed as `ROUTES` in the test output |
| Runtime roles | `sands_realtime` holds `marrow_identity_runtime` + `ghostlap_mp_runtime` (generation, receipts, results, `MatchFinalizedV1` outbox inserts). Worker relay login `sands_worker_ghostlap` holds only `ghostlap_outbox_relay`. | `scripts/db/migrate.sh`; `Grants_are_least_privilege...`, `Database_guards_refuse_inconsistent_results...` (10.2/10.4 evidence) |
| Guest identity | Hub policy `GhostLapPolicies.Connect` requires the guest-session scheme and `PrincipalKind.Guest`. A connection without a cookie cannot start. Staff tokens, foreign-secret cookies and revoked sessions are refused. | new anonymous-refused assertion; `Foreign_identities_spectators_staff_and_missing_credentials_cannot_act_over_the_real_hub` |
| Controller ownership | One controlling connection per competitor epoch; a second tab supersedes the first; queued superseded inputs are rejected when the tick applies them. | `A_second_tab_takes_over...`, `A_takeover_mid_match_supersedes...`, `Inputs_queued_before_a_takeover_or_disconnect_are_rejected...` |
| Spectator denial | Transport layer (`spectator_forbidden` on the stream) and application layer (`Registry.Submit` returns `forbidden`). `GhostLapAuthorizer` allows spectators `Read` only. | `A_refresh_takes_control...spectators_are_refused_in_both_layers` |
| Unknown resource kinds | `KindRoutingResourceAuthorizer` sends any kind starting `ghostlap` to Ghost Lap only; Ghost Lap allows exactly `ghostlap-room`, everything else `NoPolicy`. Revenant and probe kinds go to the previously registered authorizer. A later `Replace` of either port stops the host. | `Unknown_forged_and_miscased_kinds_are_denied...`, `Ghost_Lap_kinds_route_to_Ghost_Lap_and_Revenant_kinds_still_reach_the_Revenant_policy`, `A_later_Replace...` |
| Revenant/Marrow preserved | `git status --short -- src/Marrow.* src/Revenant src/Revenant.Contracts src/GhostLap/Rules src/GhostLap/Timeline src/GhostLap/Solo src/Hosts.Api scripts/db/migrations web deploy` printed nothing (also migrations 0001 to 0005). | Kiro run, section 8 |
| No test bypass in production | Source scan finds none of `AllowAnonymous`, `AddScheme<`, `TestAuthHandler`, `X-Test-`, `UseTestServer`, `AuthenticationHandler<` under the four production source trees. The only test seams are public hooks that are null unless a test sets them (`MatchRegistry.RuntimeCreated`, `MatchRuntime.TickObserver`) and test projects. | scan test |

## 3. `ghost-mp-v2` identity flow and historical pins

`MpRulesProfile.CurrentRulesId = "ghost-mp-v2"` on layout `arena-v1`, rules hash `ghost-mp-v2+arena-v1`, state schema `ghost-mp-state/2`.
Followed in one test run with four real sockets and a spectator (`Rules_identity_ghost_mp_v2_flows_...`, Kiro, passed):

1. **Admission:** the sealed round view (`SealedRoundView.RulesId/LayoutId/RulesHash/RatingAlgorithmId/TargetTicks`) and `runtime.Config`.
2. **Receipt:** `ghostlap.match_session` row `Started` with `rules_id=ghost-mp-v2`, `layout_id=arena-v1` before any tick.
3. **Simulation and snapshots:** every `MatchSnapshot.RulesId/LayoutId/GenerationId` on every client frame; tick-0 frame, final frame at tick 2700.
4. **Result:** `GetMatchResult` for every roster principal returns `finalized`, `elo-pair-v1`, durable scores equal to the last authoritative snapshot;
   `match_session` ends `ResultCommitted`; `result_document.rulesId` and `MatchFinalizedV1.result.rulesId` are `ghost-mp-v2`; exactly one result and one outbox row.
5. **Rematch:** new match id, seed, config digest, `round_ordinal` 2, new engine at its sealed spawns with zero score/charge/pickups, adopted by `Resync`,
   own result and outbox row; the first result digest is unchanged afterwards.

`ghost-mp-v1` and `ghost-v1` unchanged: `MpV2GoldenTests`, `CandlePlanTests`, `ReplayDeterminismTests`, `MpReducerTests` and the other golden pins
(33 tests, Kiro, passed) plus the empty frozen-path `git status`. `MpRulesProfile.Supports` still accepts both `ghost-mp-v1` and `ghost-mp-v2`; new rounds seal v2.

The 2P case covered candle collection, Overlit start, manual Burst and hits, contact drain and round completion. The focused event-path test drives a sealed seed with real streamed inputs and asserts the three remaining event kinds under a controlled match clock. See section 9.

## 4. Single-Realtime-host startup and migrations

```
cp .env.example .env     # replace every REPLACE_ME value (passwords letters/digits, at least 24; IDENTITY_MASTER_SECRET at least 32)
docker compose --env-file .env -f deploy/compose/docker-compose.yml up --build
```

- `migrate` (one-shot, `scripts/db/migrate.sh`) applies modules in the order `marrow_timeline`, `marrow_identity`, `revenant`, `ghostlap`, then
  provisions the logins. Ghost Lap migrations: `0001_solo_originals`, `0002_solo_branches`, `0003_mp_authority`, `0004_mp_receipt_registration_lock`,
  `0005_mp_results`. Hosts never migrate; they start only after `migrate` exits 0. Re-run: `docker compose ... run --rm migrate`.
- `realtime` must stay at one replica (`replicas: 1`). A second process against the same database refuses to start (exit code 2) because the dedicated
  session advisory lock is already held. Check: `bash scripts/checks/realtime-scale-guard.sh`.
- On start the Realtime host takes the lock, registers a new process generation, retires predecessors and aborts their unfinished receipts in the same
  transaction order the finalizer uses (a committed result survives), then verifies its composition and serves. It never resumes a predecessor's live match.
- The worker relay for `ghostlap.outbox` is inert unless `ConnectionStrings:GhostLapRelay` is set (`WORKER_GHOSTLAP_DB_PASSWORD`). Redis rating
  consumption and Spec 12 delivery are outside this task.
- Ports (127.0.0.1): API 8081, Realtime 8082. Readiness: `GET /health/ready`.
- Capacity defaults are unmeasured safety bounds: 16 concurrent matches, 512 connections, 256 rooms; Codex measured four 4-player rooms with two spectators
  each on loopback (`docs/architecture/ghostlap-mp-load-benchmark.md`). No larger capacity is claimed.

## 5. Guest cookie and host routing

- `POST /auth/guest/start` on `Hosts.Api` sets `__Host-sands_guest` (Secure, HttpOnly, SameSite=Lax, Path `/`, no Domain). The reply carries `subject`.
- `__Host-` makes the cookie host-only, so the SPA, `/auth/*` (API) and `/hubs/*` (Realtime, WebSocket upgrade) must be served from **one public HTTPS origin**.
  The dev proxy `web/apps/ghost-lap/vite.config.ts` does exactly this (`/auth` to 8081, `/hubs` to 8082, `ws: true`, `changeOrigin: false`).
  A split hostname is unsupported without a new ADR.
- Realtime shares `Identity__MasterSecret` and the identity tables, and authenticates the same cookie on the WebSocket upgrade. WebSockets only on `/hubs/ghostlap`.
- Origin: exactly one `Origin` header equal to the request's own origin or listed in `GhostLap:Multiplayer:AllowedOrigins`. `Identity:AllowedOrigins` is not consulted.
- Serve the SPA and API over HTTPS and upgrade the Ghost Lap route to WSS on the same public origin. Support HTTP WebSocket upgrade through the proxy and preserve the public Host and browser Origin. If the upstream's scheme or host differs, explicitly list the public HTTPS origin in `GhostLap:Multiplayer:AllowedOrigins` and `Identity:AllowedOrigins`. Configure trusted forwarding at the proxy boundary and keep proxy idle timeouts comfortably above the 15 s server client timeout. The TLS-terminating proxy and browser heartbeat remain deployment and browser acceptance checks.

## 6. Controller reconnect, spectators, result retrieval

- **Reconnect:** open a new connection with the same guest cookie, `JoinRoom` with the room code (a member reattaches), which supersedes the old epoch.
  Call `Resync` for the current frame and your `RecipientView.AppliedSeq`, set the next input `seq` above it, and open a new input stream.
  Within 450 ticks (15 s) of the server-observed disconnect the slot is kept; at D+450 it is forfeited and the connection is spectator-only for that match.
- **Spectators:** `JoinRoom(code, spectator: true)`. They receive the same snapshots with `You.Role = "spectator"`, `CanControl = false`, may call
  `GetRoom`, `Resync` and `GetMatchResult`, and every gameplay item is refused (`spectator_forbidden`).
- **Result:** `GetMatchResult(matchId)` returns `MatchOutcomeView`. Allowed for a room member or a sealed-roster principal; an outsider or unknown match is
  `result_not_found`. `pending` means the receipt is still `Started` (never an abort). It is read from PostgreSQL, so it works after a Realtime restart.
  After a restart the in-memory room is gone: `Rematch` on it is `room_unavailable`; create a new room.

## 7. Client payload and timing contracts for the browser spec

Reference implementations (C#): `tests/Integration/GhostLap/GhostLapGameClient.cs` (`GameClient`) and `src/GhostLap.Contracts/Multiplayer/SnapshotFilter.cs`.
Wire names are in `GhostLapWire`. These are contracts to implement, not claims that a browser client exists.

**Connection.** `HubConnection` to `/hubs/ghostlap`, WebSockets, `skipNegotiation` allowed, same-origin cookie. Client `keepAliveInterval` must be at most 5 s
(server `HubClientTimeout` 15 s closes a silent connection, including idle spectators).

**Lobby (ordinary invocations, shared rate filter 10/s burst 20):** `CreateRoom`, `JoinRoom(code, spectator)`, `SetReady`, `StartCountdown`,
`CancelCountdown`, `LeaveRoom`, `TransferHost`, `GetRoom`, `Rematch`. All take a `RequestId` (idempotency key) and return `GhostLapReply<T>{Ok, Code, Value}`.
The sealed round (`SealedRoundView`) appears in `RoomView.Round` once the countdown starts (3 s). `RoomView.MatchStatus = "running"` means the receipt committed.

**Gameplay input.** One client-to-server stream per connection: `StreamInputs(IAsyncEnumerable<GameplayInput>)`.
`GameplayInput { MatchId, ControllerEpoch, ClientTick, Seq, MoveX, MoveY, RequestBurst }`, `MoveX/MoveY` in {-1,0,1}, no position, score or time claims.

| Contract | Value | Source |
|---|---|---|
| Simulation | 30 Hz, 2,700 ticks | `GameRulesV1` |
| `ClientTick` window (`ghost-mp-net-v0`) | accepted from 4 ticks behind to 2 ahead of the next server tick; else `late_tick` / `future_tick` carrying the loop's tick | `GameRulesV1.InputMaxLateTicks/InputMaxLeadTicks` |
| Aim estimate | `snapshot.Tick + round((msSinceSnapshot + rttMs)/33.3 + bias) + 1`, bias corrected from acknowledgements and from rejection `Tick` (target error about -1) | `GameClient.AimTick/UpdateBias` |
| `Seq` | strictly increasing per slot above the server floor (`RecipientView.AppliedSeq`, `InputRejection.SequenceFloor`); a stale seq is `stale_sequence` | `ForfeitGraceTests`, `A_refresh_takes_control...` |
| Hold | a connected player with no new accepted input keeps the last direction for 6 ticks, then neutral; stale or duplicate items never renew it | `GameRulesV1.InputHoldTicks` |
| Burst | edge-triggered once; a press and release inside one tick fires one Burst; a late rejected press is not repeated | `MatchEngineTests`, `PreflightSpeedSafetyTests` |
| Send pattern used in tests | on change, plus a keepalive every 4th tick while moving; `Burst` on its own item | `GameClient.PlayAsync` |
| Ingress rate | 120 items/s sustained, burst 60; disconnect after 240 over-limit items; at most 8 inputs per slot per tick | `MultiplayerOptions` |
| Grace / forfeit | disconnect neutralizes at once; slot kept for ticks D..D+449; forfeited in phase 0 of D+450 | `MpRulesProfile.GraceTicks` |

**Server pushes.** `Snapshot(SnapshotFrame)` about every 2 ticks (about 15 Hz, latest-wins per recipient, gaps in `Sequence` are normal);
`InputsApplied(InputAck{MatchId, ControllerEpoch, Tick, LastAppliedSeq})` cumulative; `InputRejected(InputRejection)`;
`MatchControl(MatchControlNotice)` (`forfeited`, `control_overflow`); `ControllerSuperseded`; `RoomClosed`.
`SnapshotFrame { Snapshot, LifecycleVersion, MatchStatus, You(Role, Slot, ControllerEpoch, CanControl, AppliedSeq), Events, IsResync }`.
`PlayerSnapshot` carries fixed-point position (1 unit = 1024), `MoveX/MoveY` (the prediction basis), charge, `OverlitDeadlineTick`, the score breakdown
(movement, pickup, penalty, total), `Status` (`active`, `grace`, `forfeited`), `ForfeitTick`, `GraceEndsTick`, `LastSequence`.

**Client rules (SnapshotFilter).** Identity is `(MatchId, GenerationId)` by equality only. Within an identity, order by `(LifecycleVersion, Sequence)`; never
lower either. A frame of another match or generation is discarded unless `IsResync`. After the room view shows a new `Round.MatchId`, adopt a baseline for that identity. The first push for a new fan-out recipient carries `IsResync=true`. The `Resync` invocation also returns the current authoritative baseline, including the seat, epoch and sequence floor. Events are keyed `(Tick*1024 + Index)`, applied once, `EventBatch.Truncated` means cues were lost (state is exact).
Events: `candle_collected`, `overlit_started`, `contact_drain`, `burst_manual`, `burst_auto`, `burst_hit`, `overlit_cancelled`, `round_finished`, `forfeited`.
Local prediction and interpolation are nonauthoritative; corrections come from snapshots.

**Heartbeat/suspension.** A tab that stops sending does not stop the match: the six-tick hold ends, the server sees a disconnect only when the connection
closes or the 15 s client timeout elapses. A browser must reconnect and `Resync` as above. Browser heartbeat behaviour is owned by the browser spec.

**Result.** `GetMatchResult` returns `MatchOutcomeView{Decision, ResultDigest, RatedEligible, RatingAlgorithmId, Players[Slot, MemberId, Outcome,
CompetitivePlacement, TotalScore, stats]}`. Placement is competition ranking (ties share, then skip). No principal ids are returned.

**Measured network behaviour (10.3, in-memory transport with injected delay, not real packet loss):** see the 10.3 log entry and
`docs/architecture/10-ghostlap-snapshots-and-network.md`. The window above is provisional until a real browser measures it.

## 8. Commands

### Kiro (run in this task, Apple M4, macOS 15.3, SDK 10.0.403, Docker, Testcontainers PostgreSQL 18)

```
source scripts/checks/dotnet-env.sh
dotnet build tests/Integration/Integration.Tests.csproj -c Debug
dotnet test tests/Integration/Integration.Tests.csproj --no-build --filter "FullyQualifiedName~GhostLapDemoGateTests"
GHOSTLAP_DEMO_GATE=1 dotnet test tests/Integration/Integration.Tests.csproj --no-build --filter "DisplayName~Real_time_matches_complete&DisplayName~players: 2,"
dotnet test tests/GhostLap/GhostLap.Tests.csproj --filter "FullyQualifiedName~MpV2GoldenTests|FullyQualifiedName~CandlePlanTests|FullyQualifiedName~ReplayDeterminismTests|FullyQualifiedName~MpReducerTests|FullyQualifiedName~GoldenTests"
dotnet test tests/Integration/Integration.Tests.csproj --no-build --filter "FullyQualifiedName~GhostLapCompositionTests"
dotnet test tests/Architecture/Architecture.Tests.csproj
git status --short -- src/Marrow.Kernel src/Marrow.Timeline src/Marrow.Infrastructure src/Revenant src/Revenant.Contracts src/GhostLap/Rules src/GhostLap/Timeline src/GhostLap/Solo web deploy scripts/db/migrations src/Hosts.Api
```

### Codex Release verification

Run from the repository root. The documented theory filters select one case each. The 4P case plays both rounds in the same room, then exercises process replacement.

```bash
source scripts/checks/dotnet-env.sh
out="$PWD/TestResults/ghostlap-demo"
mkdir -p "$out"
dotnet build tests/Integration/Integration.Tests.csproj -c Release > "$out/build.log" 2>&1
export GHOSTLAP_LOAD_HOST_DLL="$PWD/src/Hosts.Realtime/bin/Release/net10.0/Hosts.Realtime.dll"
GHOSTLAP_DEMO_GATE=1 dotnet test tests/Integration/Integration.Tests.csproj -c Release --no-build --filter "DisplayName~Real_time_matches_complete&DisplayName~players: 3," --logger "console;verbosity=detailed" --logger "trx;LogFileName=3p.trx" --results-directory "$out" > "$out/3p.console.log" 2>&1
GHOSTLAP_DEMO_GATE=1 dotnet test tests/Integration/Integration.Tests.csproj -c Release --no-build --filter "DisplayName~Real_time_matches_complete&DisplayName~players: 4," --logger "console;verbosity=detailed" --logger "trx;LogFileName=4p.trx" --results-directory "$out" > "$out/4p.console.log" 2>&1
```

The process launcher uses the actual Release `Hosts.Realtime` entry point in the Production environment with isolated Testcontainers PostgreSQL. The guest API factory uses the real API composition to issue guest cookies. The C# clients explicitly send the guest cookie and Origin over loopback WebSockets. The launcher uses a one-second countdown, test-duration empty-room expiry and local profiling. The server generates and seals each round seed normally. The controlled rare-event test pins a separate room seed for deterministic actions.

## 9. R10.01–R10.20 traceability

Earlier task references point to the retained `docs/kiro-log.md` entries and their reports. The backend gate reuses the verified 10.0–10.5 fairness, load, fault and replay evidence.

| Requirement | Evidence | Status |
|---|---|---|
| R10.01 Roster | `LobbyStateMachineTests` and `GhostLapHubTests` cover caps, distinct identities, spectators and host transfer. The Kestrel cases used distinct authenticated guests. | PASS |
| R10.02 Countdown and sealing | `RoundSealingTests`, `GhostLapMatchTests`, Kestrel sealed config and PostgreSQL Started receipt at tick zero. | PASS |
| R10.03 Input authority | `MatchEngineTests`, `TerminalIngressTests`, takeover/spoof checks, 10.5 cross-room probes and 10.6 old-match rejection on the rematch stream. | PASS |
| R10.04 Single deterministic world | Prior 2P, new Kestrel 3P/4P and 4P rematch through tick 2,700. `GhostLapSnapshotTests` and `MpV2GoldenTests` cover deterministic replay. | PASS |
| R10.05 Ownership and fencing | `MatchAuthorityStoreTests`, `AuthorityAbortVerificationTests`, guard-loss race in `MatchFinalizationTests.Hosts`, migration 0004 and retired predecessor confirmed after physical restart. | PASS |
| R10.06 Disconnect and forfeit | `ForfeitGraceTests`, 10.3 vanish/return/half-open evidence and controlled 10.6 forfeit with spectator delivery, frozen state and typed refusal after rejoin. | PASS |
| R10.07 Hold and Burst | `MatchEngineTests`, `ForfeitGraceTests`, `PreflightSpeedSafetyTests` and controlled 10.6 one-shot manual and automatic Burst with charge and score checks. | PASS |
| R10.08 Responsiveness contract | `SnapshotFanoutTests`, 10.3 snapshot/ack/latency work, actual rematch resync, event cursor and replay de-duplication. Browser prediction remains a presentation check. | PASS |
| R10.09 Tick-time correctness | `TickSchedulerTests` 700 ms stall, bounded catch-up and overload abort. New Kestrel rounds recorded 2,700 tick executions and no sustained backlog. | PASS |
| R10.10 Slow-client isolation | `SnapshotFanoutTests`, 10.3 non-reading spectator and 10.5 four rooms/24 clients. Closed 10.6 rooms released all recipients and connections. | PASS |
| R10.11 Speed and geometry | `PreflightSpeedSafetyTests`, `MpPhysicsSafetyTests`, `MpV2GoldenTests`, 10.0/10.2 collision evidence and 10.5 held-out play. | PASS |
| R10.12 Fairness and versions | ADR-P1-27, `FairnessRemediationTests` holdouts, version carried through wire/receipt/result/outbox and frozen `ghost-v1`/MP v1 golden pins. | PASS |
| R10.13 Placements | `MatchResultTests` ties, finishers above forfeits and eligibility. Kestrel durable standings match authoritative scores and competition placements. | PASS |
| R10.14 Durable result once | `MatchFinalizationTests` transaction, idempotency, conflicts, immutability and unique result/outbox. Kestrel independently checked the same durable digest for every recipient and distinct outbox IDs. | PASS |
| R10.15 Crash and uncertain commits | `MatchFinalizationTests.Hosts` storage cuts and guard-loss races, 10.6 lost-COMMIT-ACK test and a separate actual post-commit `SIGKILL` plus restart. | PASS |
| R10.16 Rematch | Same authenticated 4P group and connections, fresh ID/seed/config/state/receipt, independent result/outbox, unchanged original and no old-match frames accepted as new play. | PASS |
| R10.17 Spectator restraint | `GhostLapHubTests`, `GhostLapSnapshotTests`, Kestrel spectator views plus controlled rare-event delivery and refusal of spectator gameplay. | PASS |
| R10.18 Observable performance | Three 10.5 Release runs, 12 completed matches, 24 sockets per run, worst p99 1.182 ms execution and 1.832 ms start lateness, 129,404 inputs reconciled. | PASS |
| R10.19 Gameplay quality | 10.5 held-out fairness and incentive evidence, 10.6 rapid turn/candle/contact/Burst play and geometry checks pass on the backend. Human keyboard feel and rendered pickup/Burst readability are owned by visual acceptance. | DEFERRED (presentation) |
| R10.20 Cross-domain access | Production composition/source scan, `GhostLapCompositionTests`, real API-issued cookies, hub origin/foreign/staff/revoked/expired cases, earlier role grants and preserved Marrow/Revenant routes. | PASS |

## 10. Release match and recovery results

Apple M4, macOS 15.3, SDK 10.0.403, ASP.NET Core/.NET 10.0.14, Docker 29.8.1 and isolated Testcontainers PostgreSQL 18. The prior 2P result used the Debug test server's WebSocket transport. Each new row is a separate 30 Hz round on loopback Kestrel. All finished at tick 2,700 with a backlog peak of one and zero late ticks.

| Round | Round seconds | Max start lateness | Pickups / manual Bursts / hits / contact drains | Received = applied + refused | Outcome |
|---|---:|---:|---|---|---|
| 2P (Kiro, prior) | 89.9 | 8.16 ms | 111 / 26 / 6 / 8 | Prior evidence | Finalized, one result and outbox |
| 3P | 90.0 | 13.22 ms | 115 / 20 / 12 / 64 | 2,187 = 2,187 + 0 | Finalized, winner slot 1 (2,699) |
| 4P | 90.0 | 14.93 ms | 128 / 21 / 19 / 74 | 2,896 = 2,896 + 0 | Finalized, winner slot 0 (2,257) |
| 4P rematch | 90.0 | 29.22 ms | 124 / 18 / 19 / 121 | 2,920 = 2,919 + 1 | Finalized, winner slot 3 (2,241) |

The sole refused input in the new rounds deliberately named the prior match from the rematch's stream. Every new round ended with zero pending inputs. Every competitor and spectator saw the same terminal state and ordered event keys with no duplicate application. The host emitted `RoomClosed` and returned to zero live matches, reserved starts, attached recipients and open connections. Finalized match metadata is retained temporarily by design.

| Round | Match ID | Seed | Result digest | Outbox event ID |
|---|---|---|---|---|
| 3P | `bb7bdc3e1826039bf88b130fa7efe7c8` | `7811786264015929304` | `9a8dab6067a54d91e6ab9e20b1ffe01cac5022da52030724649cc310b40ecdfe` | `80dbb053-7fc8-8d90-aadb-063b0a43abc6` |
| 4P | `2a5771f7ce09e5ede2af6d4ca79dfefe` | `4828374549700571368` | `e894a633b6164bbaeaa8ea5f199afaaf64e19bc931ce04c1716e644e21b58170` | `d4298c2e-6ff2-836a-a4dc-ae95fe642fda` |
| 4P rematch | `df4599aaaf42094cbaab1478f1fe371a` | `4794641871599178909` | `a80d761a98dab7b8036513e4dbe449997394de281c8ec3aa460854f35b05473c` | `1be2b7b1-9d00-812b-ac6c-08a5c5367324` |

The rematch reused all four competitors' authenticated connections. The new Started receipt sealed ordinal 2, a new config digest and a new seed. The tick-zero state matched its sealed spawns and planned candles and had zero score, pickups, charge, Bursts and Overlit deadlines. All four slots received acknowledgements and scored. The original result document and outbox payload remained byte-identical in the stored comparison after the rematch.

The 4P test killed the Realtime child with SIGKILL after observing both committed outcomes and checking room/connection cleanup. Its replacement started on the same PostgreSQL database, retired the old generation and served both original digests to a roster member. The result documents and outbox payloads/IDs survived unchanged. The earlier guard-loss races exercised a predecessor with its ordinary PostgreSQL connections still alive and proved it cannot contradict the surviving result. Separately, `A_crash_after_the_result_commit_leaves_the_committed_result_readable_from_the_successor` cuts the COMMIT reply through a PostgreSQL TCP proxy, observes an honest `unverified` result and verifies recovery after host disposal. The physical SIGKILL occurred later than the ambiguous COMMIT interval.

Raw evidence is in `TestResults/ghostlap-demo/`: Release build logs, console logs and TRX, per-case `rounds.json` and `host-profile.jsonl`, original and replacement `host.log`, and the earlier harness-failure logs. Raw files are ignored by Git. Task 10.5 retains its independent fairness and three 24-connection load runs in `TestResults/ghostlap-load/remediation/`. Task 10.5 reconciled 129,404 inputs and found a maximum 5.9 percentage-point win-share deviation and a 2.9% mean-score gap in the main seat-balanced holdout. Corner and lane farming won zero of 2,304 mixed rotations.

## 11. Focused results and backend exit

`Real_hub_delivers_Overlit_cancellation_auto_Burst_and_forfeit_once_with_resync` uses the production hub, tick loop, reducer and fan-out with a controlled match clock and sealed seed `2078438126824520019`. The players collected real candles and traversed charge values 0–4. A manual Burst at tick 257 hit for 90 points, drained the target from charge 4 to 2, and cancelled its pending tick-309 Overlit deadline. The shooter rearmed at tick 352 and auto-Burst once at tick 592, exactly 240 ticks later. All recipients saw the event keys once. Controller takeover and repeated resync preserved the authoritative state and cursor. The other competitor disconnected at D=593, retained its slot through D+449 and forfeited at D+450=1,043. The spectator saw the forfeit and had its own input refused. The returning forfeited slot had its input refused at the next tick and its world state stayed frozen. The focused replay and reducer tests also exercise exclusion as an actor and a target.

The focused Release integration run passed 20 cases. The gated real-time theory appeared as one skip in that run, with the separately executed 3P/4P cases reported above. The focused Ghost Lap unit run passed 75 cases, including historical pins, ingress disposition, placement, collision and forfeit. Architecture passed 11 cases.

```bash
source scripts/checks/dotnet-env.sh
out="$PWD/TestResults/ghostlap-demo"
unset GHOSTLAP_DEMO_GATE
dotnet build tests/Integration/Integration.Tests.csproj -c Release > "$out/build-focused.log" 2>&1
integration='FullyQualifiedName~GhostLapDemoGateTests|FullyQualifiedName~GhostLapCompositionTests|FullyQualifiedName~Foreign_identities_spectators_staff_and_missing_credentials|FullyQualifiedName~The_Ghost_Lap_hub_checks_Origin|FullyQualifiedName~A_revoked_guest_session_is_denied|FullyQualifiedName~A_second_tab_takes_over|FullyQualifiedName~A_refresh_takes_control|FullyQualifiedName~Revenant_and_shared_hub_rate_limits|FullyQualifiedName~Gameplay_stream_carries_30Hz|FullyQualifiedName~The_hub_serves_durable_outcomes_to_roster'
goldens='FullyQualifiedName~MpV2GoldenTests|FullyQualifiedName~CandlePlanTests|FullyQualifiedName~ReplayDeterminismTests|FullyQualifiedName~MpReducerTests|FullyQualifiedName~GoldenTests|FullyQualifiedName~MatchEngineTests|FullyQualifiedName~ForfeitGraceTests|FullyQualifiedName~TerminalIngressTests|FullyQualifiedName~MpPhysicsSafetyTests|FullyQualifiedName~PreflightSpeedSafetyTests|FullyQualifiedName~MatchResultTests'
dotnet test tests/Integration/Integration.Tests.csproj -c Release --no-build --filter "$integration" --logger "console;verbosity=detailed" --logger "trx;LogFileName=focused-integration.trx" --results-directory "$out" > "$out/focused-integration.log" 2>&1
dotnet test tests/GhostLap/GhostLap.Tests.csproj -c Release --filter "$goldens" --logger "trx;LogFileName=focused-ghostlap.trx" --results-directory "$out" > "$out/focused-ghostlap.log" 2>&1
dotnet test tests/Architecture/Architecture.Tests.csproj -c Release --logger "trx;LogFileName=architecture.trx" --results-directory "$out" > "$out/architecture.log" 2>&1
```

Early harness failures exposed incorrect expectations about pre-active candles at tick zero, retained terminal metadata, pushed rematch baselines, and clock/input ordering. The focused takeover test also assumed an invocation reply could never be overtaken by a newer pushed snapshot. The test corrections preserve both valid delivery orders and the production implementation. Failing attempts and their passing replacements are retained.

**PASS WITH DOCUMENTED BROWSER/DEPLOYMENT LIMITATIONS.** Spec 10 is complete for backend use by separate history and visual integration work. Browser keyboard feel, sprites, rendering, audio, interpolation and client keepalive under suspension await browser acceptance. Public HTTPS/WSS through a real TLS proxy, WAN behavior and larger capacity settings await deployment evidence. The local measured capacity is four rooms and 24 real connections. Redis relay consumption and rating are downstream.
