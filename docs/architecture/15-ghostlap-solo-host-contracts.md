# Ghost Lap solo host contracts (Spec 11 Task 11.5 handoff to Spec 13)

Status: implemented by Kiro and independently verified by Codex; see [final acceptance evidence](16-ghostlap-spec11-final-acceptance.md) for the exact executed scope and limitations. This note describes what the
hosts send. It does not describe any browser, PixiJS, audio or prediction code, which does not exist yet.

All routes and hub methods use the verified guest cookie. The owner is never a request field. A foreign id, an unknown id, a malformed id and
an unfinished history are the same HTTP `404 {"code":"not_found"}`. Hub preparation returns `not_found` for foreign/unknown resources and `not_complete` for the owner's unfinished source.

## 1. Tick, state and unit meanings

| Term | Meaning |
|---|---|
| tick `n` | State after exactly `n` reducer transitions. Tick 0 is the initial state, tick 2700 the final state. 30 Hz, 90 s. |
| fork tick `T` | 0..2699. The living world starts as the source's exact state at `T`. **The first accepted input is for tick `T + 1`** (`FirstInputTick`). Tick 2700 is inspect-only. |
| Challenge | A fork at `T = 0` that was started through `PrepareChallenge`. Kind `Challenge`. |
| Fork | Started through `PrepareFork`, at any `T` including 0. Kind `Fork`. |
| `XFixed`, `YFixed` | Arena fixed-point position (the reducer's units). Not pixels; the renderer maps them. |
| `DistanceFixed` | Cumulative post-collision distance in fixed units. `MovementPoints = DistanceFixed / MovementScoreUnitFixed`, so the sub-point remainder is kept. |
| score | `TotalScore = MovementPoints + PickupPoints - PenaltyPoints`. The ghost's score is that tick's running score, never its final score. |
| `Charge` | Soulfire 0..4. `Overlit` is `Charge == 4`; `OverlitDeadlineTick` is nonzero exactly then (automatic Burst at that tick). |
| `AckedInputSeq` | Last input sequence the reducer applied (`-1` before any). Reconcile local prediction against this and `Tick`. |

## 2. Hub flow (`/hubs/ghostlap-solo`)

`PrepareChallenge(originalId)` or `PrepareFork(originalId, forkTick)` returns `ChallengePrepared` only when the full 2,701-frame ghost trace is
verified and held (`Phase = Ready`, `Kind`, `ForkTick`, `FirstInputTick`, `FinalStateHash`, `Versions`). Preparation takes about 5 to 6 s on the
measured local setup. There is no artificial delay and no progress push: a client shows its own loading sequence while the invocation is pending.
A failure or cancellation leaves nothing started. `StartChallenge(key)` is accepted only from `Ready` on the same connection and returns
`ChallengeStarted` (Kind, ForkTick, FirstInputTick, seed, mascot, `SourceFinalStateHash`, `Versions`, `Living` and `Ghost` at tick `ForkTick`).
`StreamChallengeInputs` carries `SoloInputItem(Seq, ClientTick, MoveX, MoveY, RequestBurst)`; use `Seq > AckedInputSeq`. `CancelChallenge` discards an
unfinished run (never a result). `FinishChallenge(key)` / `FinishFork(key)` are idempotent and resolve a stored child after a refresh or restart:
`Completed` (with `ChallengeResultView`), `Pending` (retry the same key) or `Failed` (code). Only a verified child is ever `Completed`.

Failure codes added or carried: `fork_not_allowed` (T = 2700), `invalid_tick`, `invalid_request`, `not_found`, `not_ready`, `challenge_active`,
`preparation_busy`, `not_complete`, `version_unsupported`, `corrupt_history`, `verification_failed`, `unavailable`.

## 3. Live frames (`ChallengeFrame`, `ChallengeEnded`)

`ChallengeFrameView(Tick, Living, Ghost, Delta, Final, Seq, Pacing, Missed, MissedTruncated)`.

* `Living` and `Ghost` always describe the same `Tick`. `Delta = Living.TotalScore - Ghost.TotalScore` at that tick.
* `Living` (`SoloFrame`) carries position, `DistanceFixed`, score components, `Charge`, `Overlit`, `OverlitDeadlineTick`, `Bursts`, `BurstHits`,
  held vector, `BurstLatch`, `CollectedCount`, `ActiveCandleIds` (uncollected planned-active candles), `Events` and `AckedInputSeq`.
* `Ghost` (`SoloGhostFrame`) is the verified source at the same tick with the same fields plus the state hash. It is read-only data. It never
  collides, collects, attacks or scores in the living world.
* **One-shot effect identity:** an event is identified by `(run id, Tick, index in Events)` (`ChallengeStarted.ChallengeId` names the run, and the
  ghost's events use the original id). Render each identity at most once. Event kinds are the reducer's names
  (`CandleCollected`, `OverlitStarted`, `OverlitCancelled`, `BurstManual`, `BurstAuto`, `BurstHit`, `ContactDrain`, `RoundFinished`).
* **Pushes are latest-wins.** When the previous push is still in flight a tick's frame is skipped so a slow client never delays a tick. The events
  of skipped ticks are not dropped: the next pushed frame lists them in `Missed` (oldest first, only ticks that had events, at most 64, with
  `MissedTruncated` if older ones were discarded). The final tick's frame is delivered, not coalesced. The ghost can also be re-derived from the
  trace routes below, which a client already holds.
* `Pacing(CommitElapsedMs, ScheduleLagMs, SendElapsedMs, Coalesced)` separates server scheduling from delivery. Commit time is relative to the
  round clock start before the first tick; `ScheduleLagMs` is how far behind the ideal 30 Hz schedule that commit was; `Coalesced` counts ticks skipped since the
  previous pushed frame. Client arrival gaps compared with server `SendElapsedMs` gaps give the delivery jitter. Interpolate positions between
  consecutive pushed ticks using `Tick`, not wall-clock arrival.
* `ChallengeEnded(Reason, Code, Diagnostics)`: `final` (call `FinishChallenge`/`FinishFork`) or `interrupted` (no result). `Diagnostics` has the
  server commit-interval p50/p95/p99/max, worst schedule lag, coalesced frames and the input counters below.

### Input refusals (`InputRejected`)

`SoloInputRejected(Seq, Code, BurstRequested, Class, NextServerTick)`.

| `Class` | Meaning | Client action |
|---|---|---|
| `benign_movement` | Duplicate or stale sequence, late or early tick, frozen commit, for movement or for a Burst whose edge was already accepted. | Ignore; the next input supersedes it. |
| `burst_rejected` | A Burst press whose rising edge the reducer has not seen was refused. The one-shot may be lost. | Resend the press. Never throttled away. |
| `refused` | Malformed (vector), rate limited, round over, no run. | Fix the request. |

`NextServerTick` is the tick the server accepts input for next, for resynchronization. Movement notices are limited to about one per 50 ms. The
input rules themselves (admission window, sequence, hold, Burst edge) are unchanged from `ghost-v1`.

## 4. Owner history routes (HTTP, guest policy, `solo-history/1`)

| Route | Result |
|---|---|
| `GET /api/ghostlap/solo/originals/{id}/inspect/{tick}` | `SoloInspectView`: one verified state, tick 0..2700. |
| `GET /api/ghostlap/solo/originals/{id}/trace?from=&count=&v=` | `SoloTraceView`: `count` 1..300 consecutive states from `from`, `NextTick` null once tick 2700 is included. A whole round is ten consecutive chunks (`from` 0, 300, ..., 2700). Load them once; scrubbing the loaded frames needs no request. |
| `GET /api/ghostlap/solo/children?source=&after=&limit=` | `SoloChildPageView`: the owner's **completed** Challenge and Fork children, newest first, `limit` 1..25, keyset cursor `NextAfter`. Never includes originals or unfinished children. |
| `GET /api/ghostlap/solo/children/{id}` | `ChallengeResultView`: verified child with source scores and hashes. |
| `GET /api/ghostlap/solo/children/{id}/trace?from=&count=&v=` | `SoloChildTraceView`: 1–300 true historical child frames, explicit child/source IDs, Kind, ForkTick, both final hashes, versions and nullable NextTick. Works after refresh/restart, including the inherited prefix. |
| `GET /api/ghostlap/solo/children/{id}/comparison?from=&count=&stride=&v=` | `SoloComparisonView`: points at `from + i * stride` (count 1..300, stride 1..300). |
| `GET /api/ghostlap/solo/originals` | Unchanged: completed originals only. |

Every trace/inspect/comparison response repeats the source identity: original or child id, source final state hash, terminal tick 2700 and
`Versions` (`ghost-lap`, `ghost-v1`, `ghost-input/1`, `ghost-state/1`, `ghost-context/1`, rules `ghost-v1`, layout `arena-v1`). Do not merge
frames whose identity differs. A request that names another `v` gets `400 contract_unsupported`. Status codes: 400 malformed bounds or tick, 401
unauthenticated, 404 foreign/unknown/unfinished, 409 verification or version failure, 429 history-read capacity, 503 database unavailable.

### Comparison semantics

`SoloComparisonPointView.Delta = ChildScore - SourceScore` at the same absolute tick. `AfterFork` is false through the fork tick, where source and
child are the same state (`Delta = 0`, equal parts and state hashes). A comparison that disagrees inside the shared prefix, or whose final hashes do not
match the recorded ones, fails closed (`verification_failed`). Equal scores after the fork do not imply equal states; compare `*StateHash`. The score
parts are `Movement`, `Pickup`, `Penalty`, `Total`, `Pickups`, `Bursts`, `Charge`, `Overlit` and `DistanceFixed`. Use `stride` (for example 10)
to draw a whole-round line graph from one call.

## 5. What is not provided

No client-side game engine, no browser rendering/audio, no cinematic rewind, no sharing, rating or Redis cache, no nested forks, no resume of an unfinished
run. The ghost trace is held per active session in process memory only. The real T1140 to T2700 run, delivery percentiles, bounded saved-child replay and ownership boundaries are recorded in the final acceptance evidence. Browser interpolation remains Spec 13 work.

Solo host pacing uses monotonic 30 Hz deadlines (shared by Original and Challenge/Fork loops), avoiding the cumulative whole-millisecond truncation of a 33.333 ms periodic timer. A stalled commit can lengthen the round; no logical transition is skipped. Long slow-client stalls remain bounded: sends time out after one second, and more than 64 deferred event-bearing ticks set `MissedTruncated`. These are explicit resync boundaries, not a guarantee of indefinite lossless transport.
