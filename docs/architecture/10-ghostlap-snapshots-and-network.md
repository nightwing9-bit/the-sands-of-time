# Ghost Lap snapshots, delivery, resync, forfeit and network measurements (Spec 10, task 10.3)

Decisions: `docs/adr/ADR-P1-25-ghostlap-snapshots-fanout-and-forfeit.md`. This document is the wire summary, the measurements and what is NOT proven.

## 1. Wire summary (all additive; ghost-v1 codecs, hashes and Marrow are untouched)

Server to client, on `/hubs/ghostlap`:

| Method | Payload | Delivery |
|---|---|---|
| `Snapshot` | `SnapshotFrame(Snapshot, LifecycleVersion, MatchStatus, You, Events, IsResync)` | latest wins per connection; every 2 ticks (about 15 Hz), plus the final tick, a forfeit and a newly observed disconnect |
| `InputsApplied` | `InputAck(MatchId, ControllerEpoch, Tick, LastAppliedSeq)` | cumulative, per connection, newer replaces older |
| `InputRejected` | `InputRejection(Seq, Code, SequenceFloor?, Tick?)` | reliable, ordered, bounded (64) |
| `MatchControl` | `MatchControlNotice(..., Code, Detail, Tick)` with `forfeited`, `control_overflow` | reliable |
| `ControllerSuperseded`, `RoomClosed` | none | concurrent, time-limited |

Client to server: `StreamInputs` (one stream per connection, unchanged) and `Resync()` returning `GhostLapReply<SnapshotFrame>`.

A `SnapshotFrame` carries: the shared state (`MatchSnapshot`: match id, generation id, rules and layout id, tick, sequence, final flag, per slot position and effective direction, charge, Overlit deadline, score breakdown, `Status` of `active|grace|forfeited`, `GraceEndsTick`, `ForfeitTick`, active candle ids), this connection's view (`RecipientView`: role, slot, controller epoch, `CanControl`, `AppliedSeq` floor), and the events it has not been sent (`EventBatch`: from tick, through tick, truncated flag, items with `(Tick, Index)` identity).

## 2. Ordering and causality rules a client applies (`SnapshotFilter`, reference implementation in `GhostLap.Contracts`)

- Identity is `(MatchId, GenerationId)`, compared for equality. Generation ids are never ordered.
- Inside an identity: `(LifecycleVersion, Sequence)` must not decrease and a pushed frame must raise one of them. A resync may repeat both.
- Another match or generation is dropped unless the frame is a resync, which replaces the baseline (a rematch).
- Events apply once, by `(Tick, Index)`. A truncated batch or a hole is reported as a gap: cues were lost, state is exact.

## 3. Forfeit timing (`MatchEngine.Step`, ADR-P1-22 D5/D6)

`D` = the tick whose controller read first finds the slot unconnected (it gets the server neutral). Ticks `D..D+449` are grace: normal target and collector, pending Overlit auto-Burst still fires. Phase 0 of `F = D+450` forfeits the slot unless a controller is connected at that read. Tests: boundary `D+449`/`D+450`; takeover before `F-1`, at `F` (kept) and `F+1` (forfeited); 80 random connect/disconnect scripts against an independent oracle (first run of 451 unconnected reads), replayed twice to the same hash; Overlit deadline at 300, `D+449` (fires) and `F`, 600 (does not, deadline cleared, charge kept as a frozen stat); frozen slot to tick 2700; a forfeited slot's later items are refused `forfeited` and its connection is told `CanControl = false`.

## 4. Measurements

Environment: macOS 15.3 arm64, Apple M4 (10 cores, 16 GB), .NET SDK 10.0.403, Docker 29.8.1 with PostgreSQL 18.6 (Testcontainers, real receipt). Real Hosts.Realtime and Hosts.Api composition, real guest cookies, real SignalR client connections over the in-memory test server. The client socket is wrapped to add delay (each direction), uniform jitter, and 2 % head-of-line spikes of +120 ms (the way TCP shows a lost segment). Commit base `2dd9292` plus the uncommitted work of tasks 10.0 to 10.3.

**What this is not:** the test server has no TCP stack, kernel buffers or browser. There is no real packet loss (TCP has none above the socket), no OS-level shaping, no real WAN. These numbers show how the protocol behaves under injected conditions. They are not capacity numbers (task 10.5) and not a deployment check (task 10.6).

Two competitors, 25 s of play after a 4 s warm-up (750 ticks), CandleBrain clients that send on change plus a keep-alive every 4th tick. One run per profile, so no variance estimate.

| RTT / one-way jitter | slot | inputs sent | late / future refusals | ack latency ms p50 / p95 / p99 | aim error ticks min / p95 / max | snapshot age ms p50 / p95 / max | needed correction (share, p95, max) |
|---|---|---|---|---|---|---|---|
| 50 ms / ±8 | 0 | 204 | 2 / 0 | 60 / 69 / 151 | -2 / -1 / +2 | 0 / 33 / 133 | 14.7 %, 10.0 u, 34.1 u |
| 50 ms / ±8 | 1 | 201 | 3 / 0 | 61 / 99 / 182 | -3 / 0 / +1 | 0 / 67 / 133 | 11.5 %, 10.0 u, 34.1 u |
| 150 ms / ±20 | 0 | 226 | 3 / 0 | 169 / 206 / 270 | -4 / 0 / +1 | 67 / 67 / 200 | 25.3 %, 14.1 u, 34.1 u |
| 150 ms / ±20 | 1 | 219 | 3 / 0 | 172 / 207 / 293 | -3 / 0 / +1 | 67 / 133 / 200 | 26.9 %, 14.1 u, 34.1 u |
| 300 ms / ±40 | 0 | 219 | 3 / 3 | 326 / 384 / 437 | -4 / +1 / +2 | 133 / 167 / 300 | 23.5 %, 14.1 u, 34.1 u |
| 300 ms / ±40 | 1 | 220 | 4 / 0 | 328 / 395 / 435 | -4 / +1 / +2 | 133 / 167 / 300 | 21.6 %, 14.1 u, 34.1 u |

Reading it: ack latency is the RTT plus up to one tick (the input waits for the next tick). Snapshot age is one-way delay plus at most one publish interval. "Needed correction" is the position error of a naive dead-reckoning client (its own accepted inputs on top of the previous snapshot, as aimed) against the next snapshot: it is a size for a renderer to smooth, 1 to 3.4 ticks of travel at 10 u per tick, not a defect. Refusals were 1.0 to 2.7 % of inputs sent and all were `late_tick` or `future_tick`; a refused input is replaced by the next one (30 Hz plus the 4-tick keep-alive), and a refused Burst press is repeated until acknowledged. No other rejection code, no foreign or stale frame verdict, no duplicate event, no failed send, scheduler backlog at most 1 tick.

Aim error at 300 ms reaches both window edges (-4 and +2), so the window is used up there: more jitter or longer spikes would raise the refusal share. At 400 ms RTT or worse it should be re-measured before it is believed; none was measured.

Full match: 4 competitors and 1 spectator, 50 ms RTT, ±5 ms jitter, 1 % spikes of +60 ms, real time. 2700 ticks in 89.8 s. Maximum mailbox depth 5 (bound 256), scheduler maximum backlog 1 tick, 0 late ticks, maximum start lateness 13.4 ms, 261 events, 1352 snapshots to every recipient (no skipped snapshot at this speed), 0 failed sends, maximum single send 10.2 ms. The recorded applied inputs replay through the pure reducer to hash `0ec97561e9772e4d…` equal to the server's. Every sent input has exactly one fate (applied or typed refusal), none lost, none doubled. 20 armed Burst presses were checked: each fired once (or lost its charge to another slot's Burst in the same tick, reported as `OverlitCancelled`); no tick has two manual Bursts. Every client applied every event exactly once, in order, and ended on tick 2700 with the final flag and status `pending_finalization`. Frames carry one generation id and a non-decreasing lifecycle. One run; this is correctness evidence, not a timing guarantee.

Slow client: a spectator that read nothing for 12 s while two players played. The tick stayed at 30.00 Hz (360 ticks in 12.00 s), player acknowledgement p95 stayed 25 ms. The in-memory transport never pushed back (229 sends completed to a client that read none), so the cut-by-send-timeout path was not exercised over SignalR. It is proven with the same fan-out code and a gated transport: a blocked recipient receives 2 frames for 300 published snapshots, 0 delay to others, and is aborted alone at the send limit.

Detection (half-open, 1 s ping, 3 s timeout): a stall of 1.2 s was invisible (no grace, B acknowledged again afterwards). A silent connection was observed by the server after 3.8 to 3.9 s of wall time in two runs, which started grace; the returning tab took the slot within the grace. With the production defaults (5 s ping, 15 s timeout) the detection time is expected near 15 s; that was not measured.

Disconnect and forfeit over SignalR: three competitors, one left for good, one left and came back after 300 ticks. The first was shown in grace with `GraceEndsTick = D+450` and forfeited exactly there (D=69, tick 519 in the last run), score frozen, one `forfeited` event; the second kept its slot, learned the sequence floor (the server neutral consumed one number) and was acknowledged. The forfeited member's later tab could join, but only watch.

## 5. Not proven (blockers, not passes)

| Item | Why | Owner |
|---|---|---|
| Real packet loss, real latency, real kernel send buffers | no TCP in the in-memory host; no OS network shaping used | 10.6 |
| Send-timeout cut over a real saturated socket | the test host never applies backpressure | 10.6 |
| Real browser (tab suspension, JS timers, WebSocket close behaviour) | no browser in this task; the TypeScript client does not exist yet | 10.6 |
| Half-open detection time with the production 5 s / 15 s settings | measured only with 1 s / 3 s | 10.6 |
| Capacity: several rooms, GC, fan-out bytes, snapshot size, p99 tick time | one room at a time, no profile | 10.5 |
| Reordered input | impossible on one ordered stream; reorder across a reconnect is covered by the sequence floor and the stale-sequence tests, not by a network test | n/a |
| Result commit, placement, rematch | not part of 10.3 | 10.4 |
