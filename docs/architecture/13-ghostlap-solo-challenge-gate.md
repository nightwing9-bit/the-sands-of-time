# Task 11.3 — authenticated real-host challenge gate

2026-11-18. Branch `codex/ghostlap-spec10-0-3`, starting checkpoint `1b982c1d356b53ce3dd6a865d49c6fcb0b8e5553`.

**Verdict: PASS WITH MEASURED LIMITATIONS.** Canonical gameplay, ghost isolation, child durability, ownership and representative preparation contention pass. No exact-history/source-child correctness blocker prevents Kiro starting Task 11.4. Keep measured timer/delivery behavior in the Task 11.5 host/browser acceptance; do not treat client frame delivery intervals as measured simulation execution times.

## Retained live run and contention measurements

| Fact | Observed value |
|---|---|
| Serving / cold PID | 43741 / 43834 |
| Original | `01a1ec4b-2f69-700a-b334-84bba89b8403` |
| Source state hash | `b3130f4148afd99f4c5a73b17d9ca63af8237fa0e62816920ecdd99e263674c1` |
| Source record hash | `c33c18a98092ae0b52a2aab065a1de61662c6fd18bb022dd54be8e00ca7f1c35` |
| Raw operation key | `live-challenge-15c851bd75084f52af8c5a83dc23829a` |
| Durable operation key | `challenge.live-challenge-15c851bd75084f52af8c5a83dc23829a` |
| Child | `01a1ec4b-a0c4-73ab-97da-233f7ae3b478` |
| Child state hash | `9861f7e26e123697e2ac109b33970103728884891685987d76420d0f901797aa` |
| Child record hash | `01df462d84c2e03f01b1262445897ca711e4274a0d2bc033dc2c2705868bec25` |
| Parent / classification / save | Original above, fork tick **0**, **Challenge**, **Completed** |
| Identity | `ghost-lap`, `ghost-v1`, `arena-v1`, `ghost-input/1`, `ghost-state/1`, `ghost-context/1`; seed 42, mascot 2 |
| Final living / source score | **3032 / 3325**, delta **−293** |
| Final gameplay / frame count | **T2700**, **2,700** strictly increasing received frames; canonical steps **2,700** |
| Input ingress | **2,699** received, **20** rejected, **2,679** accepted |
| Living cold replay | **57** pickups, **12** Bursts; final position/charge/deadline/score and both hashes agree |
| Start invocation to T2700 | **89.304 s** (nominal 90 s); test including fixtures/restarts/negative checks **120.3904514 s** |

| Period | Preparation wall | Host CPU delta | Working set before → after | Checkpoint reads | Runtime SELECTs |
|---|---:|---:|---:|---:|---:|
| Guest A preparation | 5.856 s | 5,548.8 ms | 135,249,920 → 213,549,056 bytes | 2,711 | 19,001 |
| Guest B preparation while A plays | 5.561 s | 4,786.9 ms | 148,684,800 → 201,768,960 bytes | 2,879 | 20,513 |

Guest B's numbers include Guest A's concurrent canonical recording work in the same host/runtime role. Its checkpoint reads equal A's **168** advancing ticks plus the preparation's **2,711** checkpoint reads. Endpoint working sets are process-wide samples, not retained trace size or a sampled peak. Heap, allocation totals, database pool occupancy and isolated reducer/commit execution percentiles were not instrumented in this process; no precise values are claimed for them. Earlier Task 11.2 allocation measurements remain contextual, not substituted for this run.

| Delivery period | Interval samples | p50 | p95 | p99 | Max | Authoritative tick range |
|---|---:|---:|---:|---:|---:|---|
| Baseline | 328 | 33.14 ms | 37.53 ms | 54.57 ms | 73.48 ms | 121–449 |
| Concurrent preparation | 168 | 33.02 ms | 34.66 ms | 43.60 ms | 79.46 ms | 450–618 |
| Full round | 2,699 | 33.12 ms | 37.53 ms | 45.85 ms | 151.72 ms | 1–2700 |

**Contention decision: PASS.** Preparation did not materially degrade the running game's cadence: p95/p99 improved in this sample, all frames and all logical transitions arrived once, and canonical replay agrees. There is no evidence of a preparation-induced gameplay block or input corruption. Input rejection reasons were not collected separately in this run; no backlog length is claimed. The full-round maximum delivery gap occurred outside Guest B's preparation window. The observed duration is 0.696 s shorter than nominal and delivery has occasional tails; these are bounded host/transport limitations to carry into Task 11.5. This one-run result does not establish worst-case capacity at eight sessions/two preparations, and no broad load certification was attempted.

## Environment and scope

Release `Hosts.Realtime.dll` runs in a separate Production Kestrel process, with actual SignalR WebSockets, genuine signed guest cookies issued by the supported API guest flow, PostgreSQL 18.6 and the migrated `sands_realtime_solo` runtime role. The API guest factory uses the real authentication/session composition. Each test owns an isolated migrated database, so there is no existing compatible original to reopen there. Two completed, replay-verified canonical source fixtures are recorded through `SoloOriginals` with the existing candle-seeking driver (seeds 42 and 43); neither source needs an additional 90-second real-time game. No browser, new schema, public port, queue, background service or alternate replay engine is introduced.

## Correctness and boundaries

The full round calls the actual authenticated `PrepareChallenge`, `StartChallenge`, `StreamChallengeInputs` and `FinishChallenge`. Ready identifies the genuine source and pinned rules/layout/hash. The domain test additionally accesses all 2,701 prepared frames, proves every tick's state/hash/events and checks all chunk identities. Start returns T0; each received frame compares the source tick, position, charge, Overlit/deadline, score, active candles, ordered events and canonical state hash to the fixture. The wire delivers the living state and historical ghost under the same authoritative tick. All 2,700 canonical child steps are independently replay-verified after the serving process is killed and a fresh process reopens the same database.

The deterministic challenge test independently folds the unchanged reducer over accepted living inputs and compares every living state/score to it. Its living T0 equals the source's canonical bytes; the seeded candle opportunities are equal but collected sets diverge. This proves ghost movement, collections and Burst cues never enter living gameplay. The living fixture reaches charge 4, collects 5 candles and Bursts once; source score 3325 versus living 1798, with score divergence at 2,688 ticks. The existing Soulfire tests cover the exact 240-tick auto-Burst boundary without another long game. Repeated input sequences, unknown slots, invalid vectors, late inputs, duplicate start, post-final input and a T2701 commit are rejected.

### Historical I/O versus canonical recording

The counting engine sees preparation's 2,710 public restores and verification replay, then is reset after start. Across all 2,700 living transitions: **0 public RestoreAsync, 0 ReplayAsync, 0 CompareAlignedAsync, 0 ForkAsync; 2,700 AppendAsync calls**. `CommitTickAsync` indexes `PreparedGhostTrace.At(receipt.Tick)` directly. Trace/inspect are absent from this path.

The frozen Timeline engine internally reconstructs the **living child's preceding state** for every canonical append. Those required recording reads are not historical ghost reconstruction. The real-host SQL probe therefore expects one checkpoint read per living append; the baseline and later quiet windows agree with authoritative tick progression (three-call tolerance for query/frame sampling skew). Guest B's preparation adds its own reconstruction reads. The SQL probe alone is not an instrumented per-source count of every public method; the domain engine decorator and unchanged real-host composition provide the source-path proof. Completion and owner-facing result lookup intentionally replay/restore after gameplay ends.

The first measurement attempt incorrectly asserted zero checkpoint SELECTs, stopped at approximately T450 and was corrected in the harness: 330 additional checkpoint reads corresponded exactly to 330 living appends. No production correction was warranted. A subsequent complete live/cold test passed, but its normal console logger omitted successful test output. One additional full round was necessary to retain IDs/hashes/performance output in TRX. These were verification-harness issues, not game failures.

## Save, ownership and resource lifetime

`FinishChallenge(rawClientKey)` resolves `solo_branches` using authenticated owner plus `challenge.<rawClientKey>`, requires Complete/Challenge/T2700, re-verifies the source and restores the child's verified final state through existing `SoloBranches`. This durable contract works after process death; it is independent of the ten-minute in-process retry window. The ID is also durable in `solo_branches` and owner-scoped `SoloBranches.RestoreAsync`. Task 11.5 needs to surface a child catalog/selection interface; it does not need a recovery ledger. A client retaining its operation key can already recover the result through the hub. Losing both the key and local identity requires that future owner catalog, not recovery of an unfinished challenge.

Duplicate finishes return the same child/hashes, a raw WebSocket completion response is consumed and discarded before being delivered to a caller, and subsequent/cold retries return the same result with exactly one branch row. The focused engine decorator also injects loss of the first canonical completion acknowledgement **after the Timeline commit**: the challenge reports Unavailable, has no result, leaves its branch metadata Recording, and a retry verifies and marks the same child Complete. An unreachable solo database yields Pending/Unavailable, never fabricated success. The host's three automatic completion attempts, one-second retry delay and ten-minute pending retention were inspected; the test does not wait ten minutes.

Two independent guests exercise all five challenge methods. Foreign/unknown prepare responses are identical NotFound. Foreign start has no Ready selection, streaming has no owned run, finish is NotFound, and cancellation cannot affect the owner. These contracts take no caller-provided owner fields; start takes only a key, stream only inputs, cancel no arguments. Unknown/malformed and unprepared cases also pass the composition smoke. An invalid cookie fails WebSocket authentication. Five independently connected sessions revoked through supported guest signout cannot prepare/start/stream/finish/cancel new operations. The bounded existing stream/connection lifecycle is retained; there is no per-tick or distributed revocation system. Existing original guest/session tests and solo interruption tests pass.

Short real-host checks cover repeated preparation, cancellation after a committed frame, double cancellation, disconnect, no false completed child and inability to restart a cancelled selection. Domain checks cover preparation cancellation at restore 40, no partial start/fork, successful retry, matching-buffer reuse without reads and foreign/unknown denial. Cache tests bind reuse to owner, source, rules/layout and verified final hash; capacity is 4, idle expiry 5 minutes, active lease eviction is forbidden. Host admission is at most 8 challenge sessions and 2 preparations. Completion/cancellation release leases; idle cache entries expire lazily on subsequent cache access and cannot grow beyond capacity. No indefinite active trace accumulation was observed or introduced.

## Visual-data handoff

`SoloGhostFrame` already carries exact position, charge, Overlit/deadline, historical score components, authoritative tick, active candle IDs, ordered collection/expiration/manual/auto-Burst events, state hash and final marker. `ChallengeFrameView` carries the independent living `SoloFrame`, same-tick delta and source ghost. Ready/start/result supply source identity and pinned rules/layout; all codecs are fixed to `GhostLapTimelineVersions.V1`. Task 11.5 should surface that complete version key explicitly in its identity envelope, and expose living `WorldFrame.Events`/active candle IDs where browser effects require them. Fixed-point distance is available in domain `SoloTraceFrame`, but is **not** a field of the current `SoloGhostFrame` wire DTO despite the earlier Kiro summary. No predicted/fake history or visual cue is applied as gameplay. No progress stream or artificial preparation delay is required.

## Reproduction and preservation

```sh
dotnet test tests/Integration/Integration.Tests.csproj -c Release --filter FullyQualifiedName~Full_challenge_with_concurrent --logger 'trx;LogFileName=challenge-live.trx' --results-directory TestResults/spec11-3
dotnet test tests/Integration/Integration.Tests.csproj -c Release --filter 'FullyQualifiedName~Challenge_short_host|FullyQualifiedName~GhostLapSoloHostSmokeTests|FullyQualifiedName~GuestSessionTests' --logger 'trx;LogFileName=challenge-host.trx' --results-directory TestResults/spec11-3
dotnet test tests/GhostLap/GhostLap.Tests.csproj -c Release --filter 'FullyQualifiedName~SoloChallengeTests|FullyQualifiedName~SoloGhostTraceCacheTests|FullyQualifiedName~SoloTraceTests|FullyQualifiedName~SoloBranchTests|FullyQualifiedName~ReplayDeterminismTests|FullyQualifiedName~SoulfireRulesTests|FullyQualifiedName~InputAndScoreReplayTests' --logger 'trx;LogFileName=challenge-domain.trx' --results-directory TestResults/spec11-3
dotnet test tests/Architecture/Architecture.Tests.csproj -c Release
dotnet build src/Hosts.Realtime/Hosts.Realtime.csproj -c Release -warnaserror
```

Focused domain/replay suite: **63/63**, 71 seconds; host/lifecycle/guest suite: **13/13**, 42 seconds (short challenge case 36.527 seconds); architecture **11/11**, 969 ms; Release Realtime build **0 warnings, 0 errors**, 2.54 seconds. Three existing xUnit1031 warnings in multiplayer `LoadInstrumentationTests` appear when compiling GhostLap.Tests; that suite was not run or changed. One initial compile of the new assertion omitted `.Code`; the harness was corrected before the passing focused run.

No reproduced Task 11.3 production defect required a change. Added targeted verification and reused Kiro's implementation. Frozen solo rules/codecs/goldens, Marrow, multiplayer and Revenant production files are unchanged. No unrelated Revenant suite or multiplayer certification rerun. All unrelated pre-existing edits remain in the working tree; only the Task 11.3 portion of the mixed Kiro log is checkpointed. Spec 11's broader rewritten planning files remain unstaged along with Spec 08/12 and unrelated recovery/design assets. No Task 11.4 implementation was started.

## Checkpoint

The focused checkpoint uses commit message `feat(ghostlap): verify live solo ghost challenges`; its SHA is reported by the accompanying final response and Git history. Push uses the existing feature branch without force. The CI workflow triggers on main pushes and pull requests, not this feature-branch push. `gh run list --branch codex/ghostlap-spec10-0-3` returned zero runs before checkpointing; zero runs is not green CI. No PR is created.
