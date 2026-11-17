# Spec 11 Task 11.2 solo history gate

**PASS WITH MEASURED LIMITATIONS.** Task 11.3 can load the original's immutable frames before the challenge and then advance the ghost by authoritative tick index at 30 Hz. A loaded frame already carries its fixed-point position, expression state, historical score, active candles and ordered event cues. Indexing the loaded trace also gives exact integer-tick seeks. First-load time and server work are significant if a consumer rebuilds the full trace on each challenge start or seek.

## Source and exactness

The Release test reused the Spec 06 PostgreSQL recorder fixture (seed 42, mascot 2). From tick 1700 onward the driver stopped requesting manual Burst, which let a recorded Overlit deadline trigger auto-Burst. This preserved the Task 11.2 handoff fixture through T1140 and changed the late-run state hashes. All 2,700 steps went through the normal input admission, commit and verification path with no realtime pacing. The test restarted PostgreSQL, opened a fresh provider through Realtime's `AddGhostLapSolo` registration with the restricted runtime login, and independently replayed the source through Timeline.

Pinned identity was `ghost-lap / ghost-v1 / ghost-input/1 / ghost-state/1 / ghost-context/1`, with layout `arena-v1`. Canonical state bytes matched the recorder at T0, T1140, T2699, T2700 and the sampled checkpoint, candle and Burst boundaries.

Measured source ID `01a1eae5-6c05-7ac1-b1b9-90eea854811e`, last record hash `7348619dea2a59d525a98be3a55f319d5193ed83d3ed0ff7f72f5197280c365f`.

| T | Fixed position | Score (movement + pickup − penalty) | Charge / deadline | Collected / active | Canonical state hash |
|---:|---|---|---|---|---|
| 0 | (204800, 460800) | 0 (0 + 0 − 0) | 0 / 0 | 0 / 10 | `d61c3afc08ccfa6b4483f71fb8c05ec845288c73d9015419f63ad881231f5d06` |
| 1140 | (774144, 395264) | 1311 (636 + 675 − 0) | 2 / 0 | 27 / 7 | `7a76dc5e16e9be321695f964a3eb2cbb4d117c5a040c274a7e2e71fd68706f9e` |
| 2699 | (1138546, 584264) | 3324 (1574 + 1750 − 0) | 1 / 0 | 70 / 9 | `4c1c63a355289682c25b6efee93446424d377d73f147fd292c61a3cf20a948b3` |
| 2700 | (1131305, 591505) | 3325 (1575 + 1750 − 0) | 1 / 0 | 70 / 9 | `351dbb0b9a84f58e90fb847b8ad41c72af4785823239ae633eae457d6a088a5e` |

T0 is the initial state. T1140 has a fixed-point distance remainder of 1,732. The first Overlit state is T112 with deadline T352. First manual Burst is T194 with a recorded press, and first auto-Burst is T1890 at the prior state's deadline with no press. The full trace carries eight manual Bursts and three auto-Bursts. Candle `c-0000` expires uncollected at T2. The test checks active candles against the pinned schedule minus collections, and reconstructs the exact collected candle list from ordered cues at every tick. T2700 is the state after the last transition and carries the final score, hash and round-finished cue.

Nine 300-frame chunks and one one-frame terminal chunk covered all 2,701 distinct states in order. All per-tick receipt hashes, inspected samples and repeats matched. An overlapping slice and repeated full-size chunk had identical shared frames. The catalog row, terminal hash and last record hash stayed unchanged, and a separate living recording kept its own state.

## Access and integrity

The service returned identical `NotFound` results for a foreign guest, anonymous actor and unknown ID. An owned unfinished recording returned `NotComplete`. Invalid bounds and counts were rejected. Cancellation was verified both before the call and while a checkpoint query was blocked.

The existing code accepted early reads when the catalog terminal hash was wrong. All successful reads now check the verified terminal state against the completion hash, and reject unsupported catalog rules/layout and invalid terminal ticks. PostgreSQL fault cases also rejected an unsupported persisted state schema, unverified Timeline status and corrupt checkpoint. Canonical restores validate their checkpoint and chain segment. That scope plus the terminal check covers the requested ticks and source identity. The independent full replay checks the complete chain, including segments outside any particular slice.

## Local performance

The reference machine was an Apple M4 with 10 cores and 16 GiB RAM, .NET 10.0.404 SDK, Docker 29.8.1 and PostgreSQL 18.6. The complete read started on a fresh pool after database restart. The first calls labelled cold below used separate providers and fresh dedicated pools after restart, with the PostgreSQL server and test process already warmed by the complete read. Warm values are medians of ten inspections or five 300-frame reads on the same provider. The 60-frame request begins at T1140 and the 300-frame one at T900.

| Operation | Cold service ms | Warm service ms, median (range) | Cold / warm SELECTs per read | PostgreSQL execution ms per read, cold / warm |
|---|---:|---:|---:|---:|
| Inspect T0 | 25.61 | 4.09 (3.90–4.30) | 20 / 16 | 5.49 / 1.28 |
| Inspect T1140 | 16.95 | 3.92 (3.82–4.16) | 20 / 16 | 4.26 / 1.21 |
| Inspect T2699 | 16.23 | 4.42 (4.09–5.15) | 20 / 16 | 3.58 / 1.28 |
| Inspect T2700 | 12.71 | 2.25 (2.14–2.46) | 13 / 9 | 2.92 / 0.62 |
| Trace 60 | 127.33 | — | 433 / — | 39.43 / — |
| Trace 300 | 610.05 | 570.71 (556.94–605.43) | 2,113 / 2,109 | 193.93 / 186.85 |
| Full trace | 5,241.05 | — | 18,994 / — | 1,642.86 / — |

- Full assembly took **5,245.20 ms** wall clock, including 3.90 ms of UTF-8 JSON serialization. The 60/300-frame samples were 31,186/152,295 bytes (0.07/0.19 ms serialization). The largest observed chunk was 159,096 bytes and all ten totalled 1,383,735 bytes uncompressed. The source occupied 1,057,636 bytes summed across its PostgreSQL rows, including 72,467 bytes of canonical context, checkpoint and input payloads. The row estimate excludes indexes and page overhead.
- The full read used 4,643.72 ms of test-process CPU and allocated 1,305,570,152 managed bytes cumulatively. Managed heap ended 8,634,968 bytes higher, sampled heap at chunk boundaries peaked 10,729,952 bytes above the start, and process working set ended 10,403,840 bytes higher. These process-wide figures include the harness assembling and serializing all ten chunks.
- PostgreSQL work is the runtime login's SELECT count and summed executor time from `pg_stat_statements`. The service timing includes database round trips, canonical restores and frame projection. The harness does not isolate projection from restore, and it measures no network transport or PostgreSQL process CPU.
- Before the terminal integrity fix, the same input fixture took 5,279.54 ms wall for 18,931 SELECTs. The fix adds one seven-SELECT terminal restore for each of the nine nonterminal chunks. The observed wall-time difference is within run-to-run variation.
- In a limited 30 Hz service probe, all 18 independent solo commits overlapped a 300-frame history read. The maximum commit time was 17.97 ms with the read in flight and 14.92 ms on the 18-tick control. Both histories retained their own canonical state. This is a single-writer probe of the shared solo pool. The production scheduler and concurrent multiplayer load have separate acceptance coverage.

## Playback decision and deferred API

Keep single-tick canonical restore and bounded trace chunks for this checkpoint. Start a single bounded load when the user selects an original, keep its frames for the active selection keyed by original ID, pinned version and final hash, and reuse them during the challenge or a scrub. Use local tick indexing after load. A selective seek can request one exact tick if its frame is still pending. Availability of all frames determines when arbitrary local seeking is ready. Task 11.5 owns the transport and the live room contention check. Browser first-load and scrub responsiveness remain untested.

The public Timeline engine can restore one tick, replay to a terminal state, or compare two histories. Replay exposes no per-step samples or inputs. Compare restores both sides separately at each requested position, so comparing a source to itself doubles that reconstruction work. The runtime already pools connections. The internal store has a step-range read, with no supported public way to use it from Ghost Lap. More concurrent restores would retain the measured query and allocation cost. If Task 11.5 needs interactive full reconstruction on demand or its host contention check shows pressure, the compatibility decision is one additive, bounded verified range restore or projection, pinned to the same Timeline manifest. It would load pinned context and the relevant checkpoint once, fold the canonical chain with the frozen reducer, verify each step hash, and preserve stored-checkpoint provenance, order and cancellation. Ghost Lap would retain owner authorization before that call. This checkpoint retains the frozen Timeline contract and adds no cache table or input archive.

Task 11.5 should expose two private typed requests under the existing authenticated originals resource.

| Request | Minimum response |
|---|---|
| Inspect `originalId`, `tick`, `contractVersion` | Source identity and exactly one frame. Tick 0 and terminal tick are valid for inspection. |
| Trace `originalId`, `fromTick`, `count` (1–300), `contractVersion` | Same identity, ordered inclusive frames and nullable `nextTick`. Null marks the final state. |

Derive the actor from the verified guest session and apply the owner policy for every request. Use the external original identifier from the catalog. Carry pinned reducer, codec, rules and layout versions, source final tick and final hash in the identity envelope. The existing library supplies seed and mascot. Frame fields use the frozen integer units, score components, candle IDs and ordered cues, charge and deadline, tick, hash and hash origin. The stable errors should cover authentication, indistinguishable foreign and unknown IDs, invalid ranges, incompatible versions, corrupt or unverified history, cancellation and busy/unavailable responses. Apply bounded read admission. Keep cursor advancement and collection history tied to the same source identity. There is no public trace or inspect route in Task 11.2.

## Reproduction

```sh
mkdir -p TestResults/spec11-2
dotnet test tests/GhostLap/GhostLap.Tests.csproj -c Release \
  --filter 'FullyQualifiedName~SoloTraceTests|FullyQualifiedName~ReplayDeterminismTests|FullyQualifiedName~InputAndScoreReplayTests|FullyQualifiedName~SoulfireRulesTests|FullyQualifiedName~FullGameTests|FullyQualifiedName~MpV2GoldenTests' \
  --logger 'trx;LogFileName=trace-gate.trx' --results-directory "$PWD/TestResults/spec11-2"
dotnet test tests/Integration/Integration.Tests.csproj -c Release \
  --filter 'FullyQualifiedName~GhostLapSoloHostSmokeTests' \
  --logger 'trx;LogFileName=host-smoke.trx' --results-directory "$PWD/TestResults/spec11-2"
```

The service/reducer gate passed 58 tests, including 13 trace cases. The separate Realtime smoke passed all three checks of the surrounding real guest cookie and solo hub composition.
