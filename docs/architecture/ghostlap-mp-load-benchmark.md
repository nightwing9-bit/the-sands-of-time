# Ghost Lap multiplayer load and fairness verification

Task 10.5, 17 November 2026. Branch `codex/ghostlap-spec10-0-3` at `400c854b817d6b44e2b1536b7f058cc2b1f6c9e6` with the existing Task 10.5 worktree changes and the independent verification changes.

## A. Verdict

**FAIL.** The four-room loopback capacity target passed. Runs 2–4 passed the full benchmark assertions, and run 1 independently recorded a complete timing and durable-result sample. Competitive fairness failed under equivalent scripted play. In 2P, slot 0 took 73.6% of the win share under the routed policy and 80.1% under the original greedy policy. A focused integration test also confirmed that an input queued after the last drain can receive neither an acknowledgement nor a refusal.

## B. Benchmark environment

Apple M4 MacBook Air, 10 logical cores, 16 GiB, macOS 15.3 build 24D2059, Darwin arm64. The benchmark script selected .NET SDK 10.0.403 and .NET/ASP.NET Core 10.0.14. Both the Realtime build and xUnit load driver used Release. Kestrel ran the built `Hosts.Realtime` entry point in Production on a dynamic loopback port, in a separate process from the driver, with server GC configured by the Web SDK. The driver held 24 SignalR JSON WebSockets over TCP with negotiation skipped, no TLS and no induced latency. The test API issued real guest cookies in the driver process during setup.

Each repetition used an isolated, migrated Testcontainers `postgres:18.6-alpine3.24` database on Docker Desktop 29.8.1. The Realtime role wrote the authority generation, receipts, results and outbox. Redis was configured as a lazy local dependency and the worker and Redis relay were outside this workload. An unrelated PostgreSQL container was already running and was left alone.

Four independent 4P rooms, two spectators each, started in the same countdown window. Each producer ran on a monotonic 30 Hz schedule, chased candles from authoritative snapshots, and used workload seed `1052026` for periodic rapid diagonal turns. Burst presses and releases came from the authoritative charge state. Every room's production seal generated its own distinct seed and match ID. The scripted policy and phase seed stayed fixed across repetitions. Arena seeds and socket timing vary between runs. Each room executed exactly 2,700 logical ticks. The driver elapsed time includes the one-second countdown and observation polling.

## C. Measured performance

The target is 30 Hz, one tick every 33.33 ms. The Release gate enforces p99 tick execution under one tick, p95 lateness under one tick, p99 lateness under two ticks and p99 backlog at most two extra due ticks. The four-room run also requires terminal results, all recipients still present and zero send failures or stalls. For demo responsiveness, use p95 applied-input acknowledgement under two ticks on this loopback profile as an observation target.

| Run.room | Tick execution ms p50/p95/p99/max | Start lateness ms p50/p95/p99/max | Extra due ticks p95/max | Mailbox p95/max | Control queue p95/max | Snapshot B p50/p95/p99/max | WebSocket RX/TX kB/s | Worst ack p95 ms |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1.1 | 0.126/0.198/0.305/7.856 | 0.574/1.542/1.815/14.036 | 0/0 | 4/4 | 0/1 | 1707/1880/1960/2247 | 175.4/20.6 | 26.2 |
| 1.2 | 0.087/0.146/0.227/6.331 | 0.000/0.699/0.939/13.255 | 0/0 | 4/4 | 0/1 | 1705/1882/1945/2143 | 175.1/20.6 | 26.8 |
| 1.3 | 0.082/0.140/0.204/22.132 | 0.034/1.075/1.231/7.959 | 0/0 | 4/4 | 0/1 | 1710/1845/1989/2141 | 175.8/20.6 | 31.6 |
| 1.4 | 0.073/0.128/0.231/17.836 | 0.056/0.668/1.171/20.129 | 0/0 | 4/8 | 0/1 | 1704/1828/1951/2229 | 175.1/20.6 | 33.2 |
| 2.1 | 0.124/0.196/0.317/8.178 | 0.687/1.586/1.807/4.269 | 0/0 | 8/8 | 0/1 | 1703/1834/1950/2100 | 173.6/20.6 | 34.5 |
| 2.2 | 0.084/0.149/0.246/4.990 | 0.054/0.847/0.998/4.396 | 0/0 | 8/8 | 0/1 | 1702/1842/1945/2144 | 171.8/20.6 | 3.4 |
| 2.3 | 0.083/0.140/0.226/1.639 | 0.052/1.131/1.270/6.789 | 0/0 | 4/8 | 0/1 | 1708/1839/1946/2126 | 174.8/20.6 | 7.3 |
| 2.4 | 0.069/0.122/0.222/9.293 | 0.137/0.721/1.120/19.159 | 0/0 | 4/8 | 0/1 | 1710/1850/1971/2142 | 175.4/20.6 | 9.1 |
| 3.1 | 0.121/0.201/0.421/6.035 | 0.533/1.388/1.734/12.631 | 0/0 | 4/8 | 0/1 | 1711/1889/2034/2272 | 176.2/20.7 | 11.5 |
| 3.2 | 0.088/0.151/0.247/5.113 | 0.073/0.948/1.196/9.853 | 0/0 | 4/8 | 0/1 | 1711/1848/1946/2170 | 175.9/20.7 | 14.2 |
| 3.3 | 0.080/0.146/0.474/12.066 | 0.099/0.881/1.216/5.607 | 0/0 | 4/8 | 0/1 | 1706/1856/1951/2170 | 175.5/20.7 | 18.2 |
| 3.4 | 0.073/0.135/0.259/5.640 | 0.092/1.086/1.241/11.829 | 0/0 | 4/8 | 0/1 | 1718/1920/2062/2170 | 176.8/20.7 | 21.3 |
| 4.1 | 0.121/0.200/0.340/7.222 | 0.508/1.410/1.715/33.841 | 0/1 | 4/8 | 0/1 | 1714/1903/2045/2286 | 176.0/20.6 | 10.3 |
| 4.2 | 0.083/0.144/0.244/4.754 | 0.000/0.627/1.136/32.359 | 0/0 | 4/8 | 0/1 | 1714/1915/2039/2179 | 176.4/20.6 | 10.8 |
| 4.3 | 0.083/0.144/0.318/5.012 | 0.127/1.218/1.388/27.558 | 0/0 | 4/8 | 0/1 | 1717/1894/1965/2167 | 176.0/20.6 | 15.5 |
| 4.4 | 0.078/0.143/0.284/4.369 | 0.000/0.849/1.141/25.058 | 0/0 | 4/8 | 0/1 | 1703/1835/2003/2144 | 174.9/20.6 | 17.3 |

| Run | Host CPU avg/peak % | Host RSS peak/change MiB | Host GC gen0/1/2 | Host GC pause ms | Driver CPU avg/peak % | Driver RSS peak/change MiB | Driver GC gen0/1/2 | Driver GC pause ms | WebSocket RX/TX MB | WebSocket RX/TX kB/s |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | 52.8/62.2 | 195/+25 | 1/0/0 | 20.87 | 54.2/74.0 | 165/+14 | 100/24/4 | 699.12 | 64.062/7.531 | 701.4/82.5 |
| 2 | 45.8/60.3 | 166/+32 | 1/0/0 | 20.15 | 48.3/57.9 | 165/+11 | 101/24/4 | 686.16 | 63.555/7.534 | 695.7/82.5 |
| 3 | 59.9/75.9 | 166/0 | 1/0/0 | 22.73 | 64.2/81.8 | 160/-17 | 100/25/4 | 703.93 | 64.246/7.538 | 704.4/82.7 |
| 4 | 57.3/72.5 | 165/+70 | 1/0/0 | 38.46 | 60.4/74.9 | 164/+19 | 100/24/4 | 703.45 | 64.220/7.526 | 703.2/82.4 |

| Run | Host PID | Driver PID | Driver elapsed s | Snapshot JSON MB | Snapshot frames sent | Hub items received | Simulation accepted | Hub refused | Simulation refused | Unaccounted |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 39953 | 39946 | 91.33 | 55.979 | 32,472 | 43,143 | 43,136 | 7 | 0 | 0 |
| 2 | 40615 | 40606 | 91.35 | 55.894 | 32,460 | 43,156 | 43,148 | 4 | 0 | 4 |
| 3 | 40843 | 40827 | 91.20 | 56.173 | 32,463 | 43,188 | 43,184 | 4 | 0 | 0 |
| 4 | 41041 | 41033 | 91.33 | 56.158 | 32,460 | 43,124 | 43,120 | 4 | 0 | 0 |

The highest p99 tick execution was **0.474 ms** and the highest p99 start lateness was **1.815 ms**. Run 4 room 1 had one catch-up iteration and one start more than one period late, at 33.841 ms. The backlog returned to zero and the room finalized. All other scheduler polls recorded at most one due tick, including the one about to start. Mailboxes stayed at or below eight queued items at sampled tick starts. The highest acknowledged-input p99 across players was 38.1 ms. Every room had all six recipients at the terminal sample. Each spectator received 1,352–1,353 frames and 254–334 events. There were zero skipped snapshot sequences, ack replacements, dropped control rejections, send failures and stalled recipients. Each room recorded six initial resync baselines.

Each run's four cross-match probes were refused as `wrong_match`. Run 1 also received three `no_running_match` responses at the finish. No late, future or stale/duplicate refusals were recorded in these workloads. A sequence replay uses the same `stale_sequence` reason as an older sequence when exercised in focused testing. Four inputs in run 2 room 1 were counted by the hub without a corresponding accepted input or typed refusal. Section F records the reproduction.

Each profile contains 95 complete JSON lines: one start, 93 samples and one final. The largest monotonic sample gap was 1.008 s. Host process deltas use the first and last samples with all 24 connections open. Driver deltas use its own pre-start and end polls. Peaks are sampled and the endpoints include startup and terminal working sets respectively. Runtime and filesystem sampling ran concurrently with the gameplay threads.

Per-tick percentiles are nearest-rank. The 4,096 tick sample window covers each round. The 16,384 snapshot size sample window covers every recipient frame at this load. The per-recipient control queue is sampled once a second and its enqueue high-water mark is recorded. Snapshot and acknowledgement slots use latest-wins replacement. `backlogTicks` counts ticks still due behind the one being started. The scheduler's own maximum counts the current due tick as well. The control queue percentile column shows the worst recipient's sampled p95 in each room. Its maximum column shows the highest enqueue high-water mark. File writes occur on the background reporter. The tick appends numeric samples and never awaits file I/O. The reporter copies each ring under its lock and sorts the copy outside the lock.

Rates use the observed driver duration and include the small setup/control traffic counted by the client's WebSocket wrapper. WebSocket message data includes the SignalR envelope, acknowledgement and streamed input bytes. WebSocket frame headers, TCP/IP overhead and TLS are outside this counter. Host snapshot bytes are measured separately by serializing the snapshot frame on the recipient task. Sampled peak CPU is the highest interval CPU-time delta divided by monotonic elapsed time, expressed as a percentage of one logical core. RSS is process working set. GC pauses are summed by process, so a driver pause cannot be attributed to the host. The driver has no direct server tick for snapshot age.

## D. Capacity, admission and overload

| Envelope | Evidence |
| --- | --- |
| Measured local workload | Four concurrently running 4P matches and eight spectators across 24 connections in Release. All four measured workloads reached terminal results. |
| Configured defaults | 16 concurrent matches and 512 Ghost Lap hub connections. Full utilization of either default remains unverified. |
| Recommended cap for this local demo profile | Set `MaxConcurrentMatches=4` and `MaxTotalConnections=24` when rehearsing the measured workload. A reconnect or extra tab needs an available connection. Retest on the eventual deployment host and transport before applying a capacity claim there. This operational envelope is separate from the release gate. |

The focused cap test held a real PostgreSQL start commit open while three additional starts competed for room capacity. The fifth call was denied while the four slots were reserved or live, and its query returned no receipt. Cancelling the caller waiting on a commit kept the reservation until the database outcome was known. An identical retry produced one runtime and receipt. A cancelled countdown left no reservation or receipt. Orphan abort and normal completion each freed one slot. This test used the real registry with a controlled monotonic clock.

The connection test filled four lobbies with 16 competitors and eight spectators. Two concurrent extra connections closed with `server_busy` in the host log. One spectator disconnect released exactly one place and the same principal reconnected and rejoined while the other 23 stayed connected. This exercised the real hub through TestServer. The cap counts every open Ghost Lap hub socket independently of room membership.

Controlled overload tests showed that a 700 ms scheduler stall recovered using at most three consecutive ticks per iteration, without skipped ticks or scaled movement. Persistent backlog stopped authority and produced a durable overload abort. A failed abort write yielded an honest unverified state and predecessor retirement resolved it. A separate room advanced while the first was blocked on its abort write. The in-memory unread spectator test sustained 30.00 Hz for 12 seconds and both competitor acknowledgement p95 values were 34 ms. The gated fan-out tests covered real sender backpressure at the transport interface, newest-state recovery and individual recipient timeout. Kernel send-buffer saturation and WAN behavior remain outside this evidence.

## E. Competitive fairness

**Reproducible material slot imbalance.** The audit used 256 deterministic seeds per player count, `1000 + 7919 × index`, for 2P, 3P and 4P. It recorded 9,989 scripted rounds, including the existing greedy fixture and a cardinal shortest-path candle seeker that accounts for obstacles. Mixed-policy matches placed the distinct policy in every slot on the same seed in a seeded randomized order. The separate 600-seal fixture per player count used distinct deterministic keys and observed member-to-slot counts of 299–301 in 2P, 181–213 in 3P and 139–164 in 4P against expectations of 300, 200 and 150. Assigning seats without an obvious identity skew still leaves a seat advantage within a match.

The audit treats a repeated win-share gap of at least 10 percentage points from an equal share, with mean scores differing by at least 5%, as a material scripted signal for review. An even top-score tie splits one win across the tied slots. The routed all-engagement cases are the equivalent-policy comparison.

| Players | Mean score by slot | Routed win share % | Greedy win share % | First pickup share % with routing | Contested pickup wins with routing |
| --- | --- | --- | --- | --- | --- |
| 2 | 2,864 / 2,620 | 73.6 / 26.4 | 80.1 / 19.9 | 52.1 / 47.9 | 515 / 0 |
| 3 | 2,245 / 1,182 / 2,473 | 33.4 / 5.9 / 60.7 | 50.8 / 48.4 / 0.8 | 28.8 / 31.4 / 39.8 | 6,778 / 269 / 0 |
| 4 | 2,038 / 1,890 / 1,941 / 1,919 | 42.0 / 16.8 / 20.3 / 20.9 | 71.1 / 26.2 / 1.2 / 1.6 | 41.0 / 11.2 / 9.2 / 38.6 | 707 / 345 / 339 / 0 |

All 24 sites were reachable from every socket. The 2P sockets and opposite 4P socket pairs had identical all-site distance distributions. Mean nearest opening path lengths by socket were 27.8/29.0 ticks in 2P, 30.6/27.6/27.2 in 3P and 38.9/45.2/44.8/39.0 in 4P. Routed median first pickup ticks were 17/17, 25/29/29 and 28/41/41 respectively.

The earlier 1.307 figure is the worst ratio of socket-to-candle mean path length over the **whole scheduled plan** across its seed set. With that same metric on the 256 audit seeds, the worst ratios were 1.216, 1.349 and 1.226 for 2P, 3P and 4P. In the 64 most uneven 3P seeds, the shortest-mean-path slot scored 122 points more on average than the longest-path slot, and their win shares were 42.2% and 48.4%. In worst seed 1117579 their paths were 63.5 and 85.7 ticks and their scores were 3,027 and 2,077. Geometry matters to opportunity, but mean travel alone did not predict which slot won. The 3P win order changed with the scripted policy.

In all-engagement routed matches, the 2P/3P/4P sets recorded 783/132/181 same-tick Burst preemptions and 1,511/14,152/9,465 contact drains. The focused 3P case confirms the order with two equally charged contenders and a weaker third slot. The third slot loses charge to contact first, then slot 0 fires and cancels slot 1's ready Burst, even with reversed input arrival. Every overlapping contested candle went to the lowest eligible slot across the recorded scenarios. That mechanism and the strong 2P skew reproduced across both seeker policies. The precise fraction of win skew caused by each phase versus path choices and geometry remains unisolated.

| Players | Rotated policy | Rounds | Win share | Mean score ± SD | Movement / candle / penalty points | Pickups per round | Median score minus best rival |
| --- | --- | ---: | ---: | ---: | --- | ---: | ---: |
| 2 | Corner | 512 | 0% | 1,684 ± 18 | 1,687 / 0 / 3 | 0.0 | −2,300 |
| 2 | Lane | 512 | 0% | 1,376 ± 122 | 1,575 / 0 / 199 | 0.0 | −2,592 |
| 2 | Evasion | 512 | 4.6% | 2,583 ± 251 | 1,687 / 1,492 / 596 | 59.7 | −575 |
| 3 | Corner | 768 | 0% | 1,683 ± 18 | 1,687 / 0 / 4 | 0.0 | −1,230 |
| 3 | Lane | 768 | 0% | 1,644 ± 139 | 1,668 / 109 / 133 | 4.4 | −1,299 |
| 3 | Evasion | 768 | 15.4% | 2,034 ± 534 | 1,687 / 1,030 / 683 | 41.2 | −553 |
| 4 | Corner | 1,024 | 0% | 1,683 ± 18 | 1,687 / 0 / 4 | 0.0 | −775 |
| 4 | Lane | 1,024 | 0% | 1,678 ± 114 | 1,668 / 135 / 125 | 5.4 | −764 |
| 4 | Evasion | 1,024 | 15.2% | 1,939 ± 279 | 1,686 / 818 / 565 | 32.7 | −346 |

**Safe farming did not dominate.** Corner and lane farming won none of 4,608 rotated matches against routed engagement, even with the corner policy earning the full 1,687 distance points. The evasive seeker survives on candles but never presses Burst manually. In 2P, its opponents averaged 6.6 Burst hits per round and conceded 59 penalty points, while the evasive policy averaged 0.7 hits and conceded 596 points. Evasion's win share varied by socket from 3.1–6.1% in 2P, 3.5–34.6% in 3P and 10.2–23.2% in 4P. Contact changes charge rather than score directly. Per-seed and per-socket scores, pickups, contact charge lost, Burst hits and auto-Burst counts are in the raw matrix and the strategy summary.

Collision checks covered maximum-speed cardinal and diagonal movement, slides and repeated corner pressure. The MP straight pickup test passed 6,082 open-lane crossings. The preflight endpoint sweeps found zero straight pass-through misses for contact or pickup. The known shallow diagonal corner grazes reached 7 units and follow the documented endpoint overlap semantics. Distance score follows resolved displacement, including zero score for repeated blocked corner input. No collision rule change is warranted from these results.

These are scripted strategy comparisons. Seekers are deterministic and can converge more often than humans, and the evasion policy is one specific response to a nearby Overlit rival. For review, prototype tie priority for simultaneous pickups and Bursts with a deterministic rotation derived from the sealed seed and tick. Compare with a retained `ghost-mp-v1` baseline and use a new multiplayer rules identity for changed tie resolution. Human playtests can measure the practical size of the imbalance and evaluate the variant.

## F. Simulation integrity

Runs 2–4 verified 2,700 tick executions per match, monotonic snapshots with the sealed match ID and generation for all 24 recipients, no repeat event application, the same event keys on every recipient in a room and exactly one round-finished event. Each of the 12 finalized result documents matched the member-visible canonical digest and final snapshot scores. PostgreSQL contained one `ResultCommitted` receipt, a matching final-tick result and one `ghostlap.MatchFinalizedV1` outbox row with the same digest per match. Run 1 captured four more finalized results and four outbox rows before the test assertion stopped. Across all 16 rounds, 1,878 candles, 1,556 contact drains and 349 manual Bursts were exercised. The live script fired ready manual Bursts before the auto deadline. Automatic Burst, duplicate/stale rejection and exact one-shot behavior were exercised by the focused reducer and engine tests.

Focused replay tests verified equal MP hashes from equal inputs, and the frozen solo `ghost-v1` golden passed. The earlier Task 10.3 full-match fixture also replayed applied inputs to the final server hash. The external benchmark checked stored result digests and final scores. It did not capture a full applied-input trace for independent replay of each external round.

**Terminal ingress gap.** Run 2 room 1 received 10,789 stream items. One was refused at ingress, none were refused by the simulation, and 10,784 were accepted by the simulation. The four remaining items had no observed disposition. A controlled-clock integration test parked the driver after publishing tick 2,700 and before the runtime left `running`. `MatchRegistry.Submit` returned success for a well-formed input requesting Burst with a legal aimed tick, and the mailbox retained it after finalization without an applied-input or rejection count. This reproduces the mechanism without relying on network timing. The committed outcome and score stayed truthful. The input was queued at ingress and never admitted by the reducer. The measurement did not identify those four exact input payloads.

## G. Reproduce the verification

Prerequisites are the repository .NET SDK and a reachable Docker daemon. These commands are run from the repository root. Use distinct output directories for each repetition.

```bash
source scripts/checks/dotnet-env.sh
scripts/checks/ghostlap-load-benchmark.sh --rooms 4 --ticks 150 --out TestResults/ghostlap-load/external-smoke
for run in 1 2 3; do
  scripts/checks/ghostlap-load-benchmark.sh --enforce --out "TestResults/ghostlap-load/repeat-$run"
done
mkdir -p TestResults/ghostlap-load
GHOSTLAP_FAIRNESS_SEEDS=256 \
GHOSTLAP_FAIRNESS_OUT="$PWD/TestResults/ghostlap-load/task105-fairness.jsonl" \
dotnet test tests/GhostLap/GhostLap.Tests.csproj -c Release \
  --filter 'FullyQualifiedName~CompetitiveFairnessTests|FullyQualifiedName~MpPhysicsSafetyTests|FullyQualifiedName~PreflightSpeedSafetyTests|FullyQualifiedName~TickSchedulerTests|FullyQualifiedName~SnapshotFanoutTests|FullyQualifiedName~MatchEngineTests|FullyQualifiedName~MpReducerTests|FullyQualifiedName~ReplayDeterminismTests|FullyQualifiedName~LoadInstrumentationTests'
GHOSTLAP_FAIRNESS_SEEDS=256 \
GHOSTLAP_FAIRNESS_LAYOUT_OUT="$PWD/TestResults/ghostlap-load/task105-fairness-layout.jsonl" \
dotnet test tests/GhostLap/GhostLap.Tests.csproj -c Release --filter 'FullyQualifiedName~Scheduled_candle_travel'
dotnet test tests/Integration/Integration.Tests.csproj -c Release \
  --filter 'FullyQualifiedName~Four_match_capacity|FullyQualifiedName~A_full_connection_cap|FullyQualifiedName~Sustained_overload_stops_gameplay|FullyQualifiedName~An_input_after_the_last_drain|FullyQualifiedName~A_spectator_that_stops_reading'
```

Start with a fresh fairness JSONL path because the fixture appends. Benchmark runs recorded here are in `TestResults/ghostlap-load/task105-run-{1,2,3,4}/`, each with `environment.txt`, `summary.json`, `host-profile.jsonl`, `host.log`, `test-output.txt` and a TRX. The driver summary contains the round seeds, result documents, per-recipient bytes, acknowledgements and ingress accounting. The last sample at 24 open connections preserves sender counters before client teardown. Each finalized room also has one outbox record in the capture. The final line reflects graceful host stop.

Recorded results: four-room Release smoke 1 passed, full Release runs 2–4 each 1 passed, and run 1 recorded one test failure after measurement when the receipt assertion queried a nonexistent column. The corrected assertion ran in repetitions 2–4. The focused Ghost Lap group passed 81, the separate layout measurement passed 3, and the refined 600-key slot assignment check reran its three cases successfully. The focused integration group initially passed five of six. The reconnect assertion read the connection count before the hub's asynchronous connect callback. Moving the check after the successful rejoin passed the remaining case. Test details are in `task105-unit/`, `task105-integration/` and the corresponding logs. The 9,989 gameplay rows and 768 geometry rows are in `task105-fairness.jsonl` and `task105-fairness-layout.jsonl`. Local tabulations are in `task105-performance.txt` and `task105-fairness-summary.txt`.

## H. Changes and preservation

- Benchmark entry point and instructions in `scripts/checks/ghostlap-load-benchmark.sh` and `scripts/checks/README.md`.
- Separate Kestrel process, SignalR driver, transport byte counters and target assertions in `tests/Integration/GhostLap/{BenchmarkRealtimeProcess,GhostLapLoadProfileTests,GhostLapGameClient}.cs`.
- Process RSS and PID, full-round snapshot sizing, accepted-input counts and recipient queue high-water marks in `src/Hosts.Realtime/GhostLap/GhostLapLoadReporter.cs` and `src/GhostLap/Multiplayer/{MatchTelemetry,MatchLoop,MatchRegistry,MatchFanout}.cs`.
- Boundary and terminal ingress coverage in `tests/Integration/GhostLap/{AuthorityAbortVerificationTests.Capacity,GhostLapHubTests}.cs`.
- Routed strategy comparisons and collision ordering in `tests/GhostLap/Multiplayer/{CompetitiveFairnessTests,StrategyFixtures,MpPhysicsSafetyTests}.cs`.
- Verification record in this report and `docs/kiro-log.md`.

Marrow, Revenant, solo `ghost-v1` behavior and golden fixtures, and `ghost-mp-v1` gameplay and rules identity were preserved. Existing user and Kiro worktree changes were kept. No commit or push.

## I. Outstanding issues

- **High, competitive fairness.** Equivalent 2P seekers show a large, repeatable slot advantage with both policies. Review versioned priority for contested pickups and Burst, rerun the rotated seed matrix and confirm the effect with human play.
- **Medium, final-tick input feedback.** Gameplay ingress can enqueue an input after the last authoritative drain. Make the terminal mailbox boundary atomic with ingress or give any remaining item a typed terminal refusal. Preserve the existing receipt, result and outbox contracts. Verify the controlled-clock case and stream accounting again.
- **Verification limit.** The claimed capacity applies to this loopback profile. Validate the target demo deployment and real-socket slow recipient behavior before extending it. Full default capacity and Redis relay delivery remain unmeasured.

## J. Task 10.6 decision

**Hold Task 10.6.** The strongest blocker is the reproduced equivalent-policy seat imbalance, independent of the successful timing gate. Resolve the rules version decision and terminal input feedback before granting the gate. Task 10.6 was not started.

## Admission and telemetry configuration

All values are range-checked by `MultiplayerStartup.Validate` at host start; a bad value stops the host. The configured defaults are administrative bounds. The measured local envelope is four matches and 24 Ghost Lap hub connections.

| Key | Default | Range | Effect |
|---|---|---|---|
| `MaxConcurrentMatches` | 16 | 1 to 1024 | Matches running at once in this process (live runtimes plus starts whose receipt commit is in flight). A start beyond it is refused **before any receipt is written**; the room stays in its lobby and may retry. Independent of `MaxRooms` (lobby rooms). |
| `MaxTotalConnections` | 512 | 1 to 100 000, at least `MaxConnectionsPerPrincipal` | Open Ghost Lap hub connections for the process. A connection beyond it is closed at connect time with reason `server_busy` in host log event 4101 before it can join a room. |
| `MaxRooms` (existing) | 256 | 1 to 4096 | Lobby rooms. |
| `MaxSpectators` (existing) | 8 | 0 to 64 | Spectators per room. |
| `MaxConnectionsPerPrincipal`, `MaxConnectionsPerMember` (existing) | 6, 3 | 1 to 32, 1 to 8 | Per guest and per room member. |
| `MailboxCapacity`, `MaxInputsPerSlotPerTick`, `InputsPerSecond`, `InputBurst` (existing) | 256, 8, 120, 60 | see `MultiplayerStartup` | Input admission bounds. |
| `SnapshotIntervalTicks`, `SendTimeoutMilliseconds`, `ControlQueueCapacity` (existing) | 2, 3000, 64 | see `MultiplayerStartup` | Snapshot cadence and per-recipient bounds. |
| `CatchUpTicksPerIteration`, `LateAlarmTicks`, `OverloadSustainedMilliseconds`, `OverloadMaxBacklogTicks` (existing) | 3, 2, 2000, 150 | see `MultiplayerStartup` | Scheduler catch-up and overload abort. |
| `MeasureSnapshotBytes` | false | bool | Serialize each snapshot frame once per recipient to record its size. Costs CPU on the recipient's own sender task. On for benchmark runs only. |
| `ProfileOutputPath` | empty | at most 400 chars | When set, the host appends one JSON line (`start`, `sample` every `ProfileIntervalSeconds`, `final`) to this local file. Empty: nothing is written. No network endpoint exposes it. Use a path only an operator can write. |
| `ProfileIntervalSeconds` | 5 | 1 to 3600 | Sample cadence. |

---

# Task 10.5 remediation

The sections above preserve the original FAIL and measurements. The following results apply to the repaired build at the same repository checkpoint, with Task 10.5 worktree changes.

## A. Verdict

**PASS.** The material seat advantage was corrected in the versioned multiplayer rules. Independent equivalent-policy holdouts met the original win-share and mean-score thresholds. Controlled tests settled the terminal race, and three full Release repetitions accounted for every streamed input with no pending mailbox entries at finalization.

## B. Root cause

The baseline metrics and reducer agree on two production order effects. Fixed pickup priority gave every mutually eligible candle to the lowest slot, and a lower slot's same-tick Burst consumed another eligible slot's chance to fire. In a paired 32-seed routed 2P diagnostic, slot 0's win share was 71.9% in v1, 65.6% with pickup arbitration alone, 56.2% with simultaneous Burst alone and 50.0% with both. The contact-only ablation had the same 71.9% win share. In 4P, slot 0 moved from 42.2% in the diagnostic to 31.2% with all three interventions. A focused 3P state shows why contact still needed a shared snapshot. Sequential comparisons can create a new weak side in a pair whose charges were equal at phase start. Contact was a smaller contributor to the observed wide-run skew.

The ablations reuse production movement, candle plan, scoring and phase code with internal phase switches. Each alternative reacts to its own authoritative state as it evolves. This is a paired-seed counterfactual, with identical starting states and policy logic. Reversing the list of distinct-slot inputs did not change the legacy historical replay hashes or the v2 simultaneous-case hashes.

The earlier routed fixture gave direction preferences to fixed slots. With 3P, two with the same preference coalesced onto the same routes for roughly 1,744 of 2,700 ticks. The third overlapped either of them only about 320 ticks and had far fewer contested candle opportunities. Its 87.5% diagnostic win share persisted with fair arbitration because the two followers still shared opportunities. Rotating only the routing preferences while holding the physical spawns fixed moved the dominant seat from slot 2 to slot 1 to slot 0, with 87.5%, 90.6% and 90.6% win shares respectively. The clustered pair followed the preferences, too. Swapping spawn coordinates while retaining the original per-slot policy did not resolve the legacy score suppression.

The old greedy fixture keyed its detour to slot parity and could continue trying blocked directions. Its paired 3P followers overlapped for roughly 2,053 ticks and all three averaged 116–120 blocked ticks. Rotating only that detour preference moved the high win share between all three physical sockets at 92.2%, 87.5% and 84.4%. The corrected greedy probe rotates direction preferences independently of the seat and picks a legal detour when blocked. Routed probes use a shared obstacle-aware algorithm with direction preferences rotated independently of the sockets. The perturbed policy also varies the bias toward nearby candle targets. All report attempted and blocked movement, same-tick contest eligibility, charge and Burst data by slot, and split equal-score wins.

Opening geometry still affects the first pickup. The three-player farthest-to-nearest mean-path ratios in the original report remain valid. The crossed policy experiments excluded a fixed socket trap as the cause of the severe 3P skew. Seat-balanced holdouts test for residual outcomes without changing the arena. Higher first-pickup opportunity in 3P and 4P remains visible in those tests.

## C. Gameplay changes

New countdowns and rematches seal `ghost-mp-v2` and `arena-v1`. Contested candles use deterministic seeded arbitration over eligible participants. Contact compares shared post-pickup charges and retains per-pair cooldown. Burst commits all legal same-tick manual/auto intents before applying hits. Seeded arbitration never reads a principal, arrival order or clock. The world still uses the existing movement, candle, contact and Burst constants, and the same score and placement rules.

The resolver selects by sealed identity. V2 hashes include the rules ID and the `ghost-mp-state/2` schema. Existing MP v1 configs and hashes keep their original ordering rules and schema. V1 snapshots and results retain their identity. Solo entry point, codec, golden, histories and the underlying arena remain preserved. Three full-round v1 hashes taken from the original audit and the solo golden passed. The new 2/3/4-player goldens include contested candles, simultaneous manual and auto Bursts, shared contact, final-score placement ties, forfeit exclusion and arrival-order-independent replay. The match ID/config digest, receipt, snapshot, canonical result and outbox carry the sealed identity through the existing transactional path. [ADR-P1-27](../adr/ADR-P1-27-ghostlap-competitive-conflicts-and-terminal-ingress.md)

## D. Terminal input repair

The bounded channel writer completes before tick 2,700 begins its final drain. The channel linearizes completion with successful writes. Items admitted before that close go through normal final-tick validation, so a legal Burst press/release still executes once and duplicates and stale inputs get ordinary reasons. A write after closing returns `no_running_match`. When an early exit ends the sole reader, the runtime closes the writer and settles every queued item as a typed refusal before any terminal database write. If the reducer fails before committing the tick, its provisionally accepted batch is refused and sequence floors point to the last committed world.

The controlled-clock regression paused exactly where the original bug occurred. The input was refused with the runtime still reporting `running`. A PostgreSQL barrier repeated the assertion during pending finalization and the final state hash matched the durable result. Concurrent producers raced a full 16-item mailbox and all successful writes settled. Focused overload and simulated guard-loss cases drained 16 queued inputs without applying any. The real-host stream results below reconcile hub receives, hub refusals, applied inputs and tick-level refusals per room and assert an empty final mailbox. There is no persistent input archive.

## E. Independent holdout

Each player count used 512 new seeds of the form `91234567 + 104729 × index` for indices 0–511, across both versions and every policy below. Persona rotation is balanced across physical slots. The corrected policies are routed, perturbed routed, and greedy with the detour fix. The original greedy fixture remains in the evidence as a diagnostic of seat-dependent scripted behavior. In total the recorded main holdout contains 15,744 rounds, including 128 seeds per count for each mixed role rotated through every socket. These seeds were not used to choose the rules.

| Players | Equivalent policy | V1 win share by slot % | V2 win share by slot % | V2 mean score by slot | V1 → V2 mean-score disparity |
| --- | --- | --- | --- | --- | --- |
| 2 | Routed | 76.1 / 23.9 | 49.3 / 50.7 | 2,642 / 2,631 | 10.2% → 0.4% |
| 2 | Perturbed | 74.7 / 25.3 | 50.0 / 50.0 | 2,631 / 2,623 | 10.0% → 0.3% |
| 2 | Greedy | 83.8 / 16.2 | 48.3 / 51.7 | 2,555 / 2,567 | 20.4% → 0.5% |
| 3 | Routed | 43.9 / 30.7 / 25.4 | 33.8 / 30.4 / 35.8 | 2,154 / 2,158 / 2,165 | 6.8% → 0.5% |
| 3 | Perturbed | 40.9 / 34.2 / 24.9 | 30.4 / 35.5 / 34.1 | 2,145 / 2,185 / 2,180 | 5.4% → 1.9% |
| 3 | Greedy | 58.4 / 26.8 / 14.8 | 30.1 / 33.4 / 36.5 | 2,026 / 2,060 / 2,060 | 25.0% → 1.6% |
| 4 | Routed | 28.2 / 27.7 / 23.4 / 20.6 | 19.1 / 28.1 / 26.2 / 26.6 | 1,953 / 1,982 / 1,949 / 1,985 | 4.1% → 1.8% |
| 4 | Perturbed | 35.9 / 20.9 / 21.7 / 21.5 | 29.4 / 24.0 / 20.4 / 26.2 | 1,993 / 1,960 / 1,956 / 1,985 | 6.0% → 1.9% |
| 4 | Greedy | 55.6 / 21.5 / 11.4 / 11.5 | 22.9 / 23.1 / 23.4 / 30.5 | 1,823 / 1,793 / 1,810 / 1,845 | 27.2% → 2.9% |

The largest win-share departure in the main seat-balanced holdout was 5.9 percentage points in 4P routed play. The largest mean-score disparity, defined as largest mean divided by smallest mean minus one, was 2.9%. For 2P routed, the paired win-share shift for slot 0 was −26.8 pp with an approximate 95% half-width of 4.0 pp. Per-slot routed 95% half-widths were about 3.4–4.3 pp. The 128-seed cohorts varied. One 4P routed cohort had slot 0 at 14.1%, and a different one had slot 1 at 35.2%. The full sets remained within the fixed ten-point threshold. Original greedy v2 still produced 6.1/87.5/6.4 in 3P and a 5.9% score disparity in 4P. Those are retained as diagnostic counterexamples. The crossed greedy and routing tests demonstrate that the extreme 3P effect follows scripted preferences rather than physical seat.

| Players | V2 routed first-pickup share % | Contested wins/opportunities by slot | Mean contact charge lost by slot | Mean manual Bursts by slot |
| --- | --- | --- | --- | --- |
| 2 | 48.8 / 51.2 | 593/1,160 · 567/1,160 | 1.6 / 1.6 | 6.1 / 6.1 |
| 3 | 28.5 / 28.1 / 43.4 | 581/1,171 · 541/1,082 · 543/1,115 | 2.2 / 2.2 / 2.2 | 5.6 / 5.5 / 5.6 |
| 4 | 42.6 / 7.1 / 9.5 / 40.8 | 485/1,019 · 459/930 · 487/994 · 511/977 | 2.5 / 2.5 / 2.7 / 2.7 | 4.5 / 4.6 / 4.5 / 4.6 |

All main v2 holdout policies recorded zero preempted manual Bursts. Eligible pairs can still exchange simultaneous hits, and tactics can still prevent opponents from becoming Overlit. In 2P routed play the contested results were 593 and 567 wins of 1,160 opportunities per slot. Movement scores in routed play remained near the 1,687-point ceiling, with about 0.1–1.6 blocked ticks per slot and round. The raw summary includes first pickup, per-slot means and standard deviations, score components, charge loss, manual vs auto Bursts, attempted and blocked moves, four seed cohorts and paired same-seed changes.

Neither corner farming nor lane farming won any of 2,304 mixed rounds against routed engagement. Evasion remained viable in individual seeds but lost more often than engagement across all rotations. No scoring compensation was added. The additional nearest-only control varies route ties independently of seats without biasing the candle target cost. Across 512 seeds per count and per version, its v2 win shares were 52.8/47.2, 31.8/32.4/35.7 and 23.9/24.9/23.0/28.1 for 2P, 3P and 4P. V2 mean-score gaps were 0.94%, 0.76% and 1.45%. Its paired v1 win shares were 75.8/24.2, 42.6/31.2/26.2 and 29.8/25.6/21.8/22.9.

## F. Real-host performance and terminal outcomes

Separate Release Kestrel and SignalR driver processes used the same M4, .NET 10.0.14 and isolated Testcontainers PostgreSQL setup as the original audit. All rooms used v2 at 30 Hz with 24 connected clients and the original benchmark workload seed. CPU is sampled as percent of one logical core. The driver observed just over 91 seconds including countdown and polling. Transport totals count SignalR payload bytes in the client's WebSocket, including a small control/setup component.

| Run.room | Execution ms p50/p95/p99/max | Lateness ms p50/p95/p99/max | Extra due ticks p95/max | Mailbox p95/max | Control queue max | Snapshot B p50/p95/p99/max | RX/TX kB/s | Worst ack p95 ms |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1.1 | 0.107 / 0.219 / 1.182 / 38.372 | 0.470 / 1.430 / 1.832 / 11.734 | 0/0 | 4/8 | 1 | 1717 / 1891 / 2016 / 2273 | 176.0/20.6 | 32.6 |
| 1.2 | 0.080 / 0.178 / 0.890 / 35.347 | 0.000 / 0.807 / 1.289 / 10.268 | 0/0 | 4/8 | 1 | 1709 / 1837 / 1966 / 2160 | 175.3/20.6 | 32.8 |
| 1.3 | 0.079 / 0.172 / 0.658 / 30.861 | 0.038 / 1.092 / 1.394 / 15.828 | 0/0 | 4/12 | 1 | 1709 / 1843 / 1954 / 2146 | 175.1/20.6 | 4.1 |
| 1.4 | 0.065 / 0.157 / 0.566 / 29.278 | 0.013 / 0.664 / 1.163 / 13.855 | 0/0 | 4/12 | 1 | 1718 / 1881 / 2010 / 2274 | 176.0/20.6 | 5.3 |
| 2.1 | 0.107 / 0.198 / 0.539 / 27.418 | 0.592 / 1.337 / 1.820 / 31.705 | 0/0 | 4/8 | 1 | 1709 / 1842 / 1972 / 2189 | 175.5/20.6 | 30.6 |
| 2.2 | 0.078 / 0.151 / 0.639 / 25.474 | 0.000 / 0.724 / 1.080 / 30.171 | 0/0 | 4/8 | 1 | 1701 / 1833 / 2014 / 2140 | 174.9/20.6 | 29.9 |
| 2.3 | 0.075 / 0.167 / 0.390 / 22.634 | 0.065 / 0.947 / 1.275 / 26.046 | 0/0 | 4/8 | 1 | 1708 / 1837 / 1956 / 2264 | 175.3/20.6 | 32.4 |
| 2.4 | 0.067 / 0.135 / 0.473 / 16.542 | 0.095 / 0.704 / 1.232 / 23.685 | 0/0 | 4/8 | 1 | 1708 / 1842 / 1953 / 2128 | 175.3/20.6 | 32.9 |
| 3.1 | 0.102 / 0.197 / 0.605 / 7.915 | 0.368 / 1.368 / 1.616 / 19.325 | 0/0 | 4/16 | 1 | 1714 / 1877 / 2033 / 2274 | 176.0/20.6 | 30.4 |
| 3.2 | 0.074 / 0.151 / 0.475 / 7.424 | 0.000 / 0.867 / 1.210 / 16.691 | 0/0 | 4/12 | 1 | 1706 / 1848 / 1972 / 2164 | 175.3/20.6 | 32.3 |
| 3.3 | 0.068 / 0.142 / 0.519 / 14.889 | 0.099 / 1.185 / 1.265 / 13.640 | 0/0 | 8/12 | 1 | 1703 / 1834 / 1943 / 2361 | 171.7/20.6 | 33.7 |
| 3.4 | 0.064 / 0.128 / 0.410 / 17.731 | 0.075 / 0.759 / 1.165 / 11.780 | 0/0 | 4/16 | 1 | 1717 / 1847 / 2024 / 2375 | 175.6/20.6 | 4.2 |

| Run | Driver elapsed s | Host CPU avg/peak % | Host RSS peak/change MiB | Host GC gen0/1/2 | Host GC pause ms | Driver CPU avg/peak % | Driver RSS peak/change MiB | Driver GC gen0/1/2 | Driver GC pause ms | RX/TX MB | RX/TX kB/s |
| --- | ---: | --- | --- | --- | ---: | --- | --- | --- | ---: | --- | --- |
| 1 | 91.35 | 47.2/77.2 | 136/+25 | 1/0/0 | 18.0 | 48.1/59.6 | 153/+18 | 102/25/4 | 631.9 | 64.163/7.527 | 702.4/82.4 |
| 2 | 91.35 | 56.1/78.4 | 140/+19 | 1/0/0 | 19.4 | 55.8/67.7 | 152/-4 | 101/24/4 | 557.4 | 64.038/7.528 | 701.0/82.4 |
| 3 | 91.32 | 48.8/73.8 | 138/-3 | 1/0/0 | 16.6 | 50.8/61.2 | 150/+10 | 101/24/4 | 659.5 | 63.797/7.533 | 698.6/82.5 |

| Run | Receives | Applied | Hub refused | Tick refused | Unaccounted | Final pending |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 43,124 | 43,112 | 12 | 0 | 0 | 0 |
| 2 | 43,124 | 43,120 | 4 | 0 | 0 | 0 |
| 3 | 43,156 | 43,144 | 12 | 0 | 0 | 0 |

The 28 ingress refusals include 12 deliberate cross-match probes and 16 terminal refusals. Each run had four finalized results, one outbox event for each and correct match and generation identity in every recipient frame. The 12 rooms each recorded 2,700 logical ticks and complete profiles. No room showed sustained backlog. Six cumulative acknowledgements were coalesced across the 12 rooms, with zero skipped snapshots, dropped control notices, send failures or stalled recipients. The worst acknowledgement p95 was 33.7 ms.

Worst p99 execution rose from the original 0.474 ms to **1.182 ms**, with 0.639 ms and 0.605 ms in the next runs. The worst p99 start lateness was **1.832 ms**, against the original 1.815 ms. Execution p95 stayed at or below 0.219 ms. Two rooms in run 1 recorded an execution maximum above one period, peaking at 38.372 ms. The scheduler recorded no extra due ticks in any repetition. Capacity passed the original target with the 30 Hz tick rate unchanged. These process measurements do not isolate the cost of v2 phase code from scheduling and GC.

## G. Commands and retained evidence

Run from the repository root. The original `task105-run-*` and original fairness logs remain under `TestResults/ghostlap-load`. Use a separate output directory for reruns.

```bash
source scripts/checks/dotnet-env.sh
out="$PWD/TestResults/ghostlap-load/remediation"
mkdir -p "$out"
GHOSTLAP_ABLATION_OUT="$out/ablation.jsonl" dotnet test tests/GhostLap/GhostLap.Tests.csproj -c Release --filter 'FullyQualifiedName~Phase_ablation'
GHOSTLAP_PERSONA_OUT="$out/personas-crossed.jsonl" dotnet test tests/GhostLap/GhostLap.Tests.csproj -c Release --filter 'FullyQualifiedName~Rotate_script_preferences'
GHOSTLAP_HOLDOUT_OUT="$out/holdout.jsonl" dotnet test tests/GhostLap/GhostLap.Tests.csproj -c Release --filter 'FullyQualifiedName~Independent_holdout'
GHOSTLAP_NEAREST_OUT="$out/nearest.jsonl" dotnet test tests/GhostLap/GhostLap.Tests.csproj -c Release --filter 'FullyQualifiedName~Nearest_only_holdout'
dotnet test tests/GhostLap/GhostLap.Tests.csproj -c Release --filter 'FullyQualifiedName~MpV2GoldenTests|FullyQualifiedName~TerminalIngressTests|FullyQualifiedName~ForfeitGraceTests|FullyQualifiedName~MpReducerTests|FullyQualifiedName~MpPhysicsSafetyTests|FullyQualifiedName~RoundSealingTests|FullyQualifiedName~LobbyStateMachineTests|FullyQualifiedName~ReplayDeterminismTests|FullyQualifiedName~TickSchedulerTests|FullyQualifiedName~Plan_source_has_no_clock'
dotnet test tests/Integration/Integration.Tests.csproj -c Release --filter 'FullyQualifiedName~Inputs_after_the_last_drain|FullyQualifiedName~A_filled_mailbox|FullyQualifiedName~Four_match_capacity|FullyQualifiedName~Sustained_overload_stops_gameplay|FullyQualifiedName~Rematch_after_a_finalized_match|FullyQualifiedName~A_refresh_takes_control'
scripts/checks/ghostlap-load-benchmark.sh --rooms 4 --ticks 150 --out "$out/smoke"
for repeat in 1 2 3; do
  scripts/checks/ghostlap-load-benchmark.sh --enforce --out "$out/run-$repeat"
done
```

Final focused unit and regression filter passed 105 cases. Focused integration passed eight cases. The ablation, crossed preference, main holdout and nearest-only holdout each passed their evidence test. The external smoke and all three full runs passed. In `TestResults/ghostlap-load/remediation/`, the raw matrices are `ablation.jsonl`, `personas-crossed.jsonl`, `holdout.jsonl` and `nearest.jsonl`. The per-slot tabulations are in `fairness-summary.txt`. The benchmark directories `run-1`, `run-2` and `run-3` contain `host-profile.jsonl`, `summary.json`, environment and host/test logs, and TRX files. `performance-summary.txt` reconciles the retained runs. Focused TRX files are in `unit/` and `integration/`.

## H. Changes and preservation

- Production version selection and provenance in `src/GhostLap/Multiplayer/{ActiveRoomOwner,RoundSealing,MpRulesProfile,MpSimulation,MatchFeed,MatchResults}.cs`.
- Shared phase dispatch and v2 conflict logic in `src/GhostLap/Simulation/{GhostLapTransition,GhostLapCompetitivePhases,SeededContest}.cs`. The historical solo transition entry point and legacy phase behavior were preserved.
- Terminal admission, settlement and telemetry in `src/GhostLap/Multiplayer/{MatchLoop,MatchRegistry,MatchTelemetry}.cs`.
- Fixture instrumentation and evidence tests in `tests/GhostLap/Multiplayer/{StrategyFixtures,FairnessRemediationTests,MpV2GoldenTests,TerminalIngressTests,LobbyStateMachineTests}.cs`.
- Focused real-database and benchmark assertions in `tests/Integration/GhostLap/{AuthorityAbortVerificationTests.Capacity,MatchFinalizationTests.Hosts,GhostLapLoadProfileTests}.cs`.
- ADR-P1-27, the lobby ingress version note, this report and `docs/kiro-log.md`.

Marrow, Revenant, the solo codec and historical fixtures, MP v1 result interpretation, rating identity, authority fences, migrations, and the finalization transaction were preserved. The existing working-tree changes were kept. Tracker checkboxes, commits and pushes were untouched.

## I. Remaining limitations

The evidence is local loopback with scripted clients. WAN, browser and deployment capacity remain unverified. Human play can assess how often coordinated followers and tactical Burst patterns arise and how the opening geometry feels. Opening access remains unequal in some seeds and socket pairs, while the equivalent-policy outcome gate is evaluated on sustained scores and wins. Configured admission defaults above 4 rooms and 24 connections remain outside the measured load.

## J. Task 10.6 decision

**GO for Task 10.6.** The engineering gate for competitive outcomes, terminal input disposition, deterministic versioning, four-room Release capacity and durable finalization passed. The browser integration itself remains a separate task and was not started.
