# Spec 11 Task 11.5 — independent final acceptance

Verification date: 18 November 2026 (Asia/Kolkata). Branch: `codex/ghostlap-spec10-0-3`; starting checkpoint: `2bde33e6443aab7c6748a9af804030c952254f84`.

## A. Verdict

**PASS WITH DOCUMENTED BROWSER/DEPLOYMENT LIMITATIONS.** Final focused security/regression results passed, as recorded below. The real live Fork, four-player overlap, actual serving-process restart, canonical child replay, both complete historical frame reads and comparisons have executed successfully. This is backend acceptance; browser rendering and deployment remain separate.

Evidence is composite, not a claim that every attempted command exited green. The initial driver incorrectly readied players before later joins cleared their readiness. The next attempt reproduced a production pacing defect. The corrected-clock run passed its live and cold-history assertions, then failed a test expectation (`not_found` versus the existing owner's `not_complete` for an unfinished source). That expectation was corrected and security was separated into its own passing fact with cheap canonical fixtures; the checked-in live fact now ends after cold verification. The live fact was not replayed just to obtain a green runner summary: all of its retained gate assertions had executed before the stale security assertion. No additional live original, T0 Challenge, full multiplayer match, fairness/load matrix or unrelated Revenant suite was run.

## B. End-to-end Fork

Production Release Kestrel in a separate OS process (PID **15210**), real SignalR WebSockets, API-issued guest cookies, migrated PostgreSQL **18.6**, runtime login `sands_realtime_solo`. The seed-42 canonical recorder created the source without real-time pacing. The complete source states were retained independently by the driver.

| Identity | Actual value |
|---|---|
| Original | `01a1ecf6-c563-7efe-8869-799ecf14fc93` |
| Child | `01a1ecf7-04e7-76ca-a752-8c3e4c09d3c8` |
| Durable operation | `fork.acceptance-96e384b0ef2a44348fd44d2c14eedc11` |
| Source T1140 state hash | `7a76dc5e16e9be321695f964a3eb2cbb4d117c5a040c274a7e2e71fd68706f9e` |
| Source final state hash | `b3130f4148afd99f4c5a73b17d9ca63af8237fa0e62816920ecdd99e263674c1` |
| Source final record hash | `c28f434d8f1f1fad6caa8c7a5fd68befbd6fed2316bcbf362c30b6d752cba1e6` |
| Child final state hash | `ddb75a67f1905c63d270c6dc8753dbb2c223562e5f91625482eda329d0006ffa` |
| Child final record hash | `78c6a1f3bdbe18b7476a7aedb6f50f602c8236e3e97a1e5d4e7f74e311444179` |
| Versions | `ghost-lap / ghost-v1 / ghost-input/1 / ghost-state/1 / ghost-context/1`; rules `ghost-v1`, layout `arena-v1` |
| Seed / mascot | `42 / 2` |

Guest A listed and inspected the owned source, prepared `Fork / T1140 / FirstInputTick=1141`, then started only after `Ready`. Preparation: **5,678.58 ms**. Start returned matching living/ghost projections; an independent canonical restore of the persisted child's T1140 matched **all source state bytes**, including fields omitted from renderer DTOs.

Exactly **1,560** authoritative commits and received frames, T1141–T2700, no T1140 replay or T2701. Database step count and min/max positions were checked after restart. Every received ghost state hash, position, charge, Overlit/deadline, score, candle IDs and ordered cues matched the independently retained original at that absolute tick. Delta was checked on every received frame. The driver steered toward available candles with alternate detours and selected manual Burst presses. Final source score **3325**, child **3009**, delta **−316**; distinct final states and independently verified child prove an alternative future. Nine Burst-request inputs were admitted; child Burst count rose from 5 at the fork to 14 at completion.

Client stopwatch before Start invocation to final arrival: **52.351990 s**; first-to-final arrival: **52.257657 s**. These include different startup boundaries; server cadence is reported separately below.

## C. Cold recovery

The first Kestrel was stopped. A fresh Production Kestrel process against the same database was launched; different PID was asserted. Guest A resumed the same identity through `/auth/guest/start` and used the original cookie on the new process. Original catalog, child catalog/detail and `FinishFork` by the original owner-scoped key returned the same saved identity/result. `FinishChallenge` did not resolve the Fork key; Guest B could not resolve it.

Independent frozen Timeline `ReplayAsync` returned `Verified`, position 2700 and the exact child final state/record hashes. The child owned **1560** canonical steps at **1141..2700**. The source's persisted history, steps, checkpoint bytes, input rows and Original metadata were fingerprinted before play and asserted unchanged after completion and after comparisons. The run's late harness assertion prevented its final summary line; it did not invalidate the previously executed restart/replay assertions.

## D. Historical read

Original: all **2701** frames via ten bounded HTTP chunks, `from=0,300,...,2700`, `count=300`. Stable identity/versions, ordered T0–T2700, exact continuations and terminal null; every frame checked against retained canonical state/hash/events. Repeated 1100–1199 chunk and independent T1140 inspect were identical. Total **5705.62 ms**.

The implementation already offered `/children/{id}/comparison?from=&count=&stride=&v=`: a bounded score-curve operation existed, so no new comparison engine or endpoint was needed. **271** true samples, stride 10, ending at T2700: **1051.60 ms**, **3399 SELECTs**. This is preparation work, never a slider-frame/game-loop operation.

The missing operation was: after refresh, select a saved child and obtain its historical positions, candles and cues for replay. Child detail only returned final metadata; comparison only returned scores/hash samples; Original routes correctly refused a child ID. Added the minimal owner-only `GET /api/ghostlap/solo/children/{id}/trace?from=&count=&v=` over existing `SoloBranches.RestoreAsync`. `SoloChildTraceView` carries both IDs/final hashes, Kind, ForkTick, pinned versions, terminal/from/count/next and `SoloGhostFrame[]`. Limits **1–300**, shared four-slot read limiter, completed/verified child and source terminal identity checks. No new table/cache/Timeline port.

All **2701 child frames** read in ten chunks after restart in **6132.88 ms**. Prefix T1140 exactly matched the original; selected child frames were independently restored and verified; terminal hash matched the saved child. Domain/host regressions cover bounds, inheritance, continuation, owner isolation and unfinished/original IDs.

| Tick | Actual original score | Actual child score | Signed delta |
|---:|---:|---:|---:|
| 0 | 0 | 0 | 0 |
| 1140 | 1311 | 1311 | 0 |
| 1141 | 1311 | 1311 | 0 |
| 1920 | 2404 | 2151 | −253 |
| 2699 | 3324 | 3009 | −315 |
| 2700 | 3325 | 3009 | −316 |

At T1140 both had movement 636 + pickup 675 − penalty 0, distance 10,421,956 fixed units, 27 pickups, charge 2 and five Bursts. At T1920 source movement/pickup were 1104/1300; child 1051/1100. T1141 already had different state hashes despite equal scores. Each comparison was checked against independently restored child and retained source states. Six points including independent restores: **77.87 ms**; individual combined measurements **8.69–19.83 ms** (includes driver validation, not a pure HTTP microbenchmark).

## E. Presentation contract

`ChallengeFrameView` exposes same-tick living/ghost positions, absolute tick, true scores and delta. Living/ghost frames carry Soulfire, Overlit/deadline, movement/pickup/penalty/distance, active candle IDs, held input/latch, pickup/Burst counts and ordered canonical cues. Started/prepared/result carry source/version identity; live events refer to those stable run identities. `Seq`, `Missed`, `MissedTruncated` preserve explicit frame/event ordering.

Focused test invokes the actual production push/slot/drain methods with a controlled sender and real frozen-reducer pickup/manual Burst cues plus the existing deterministic terminal auto-Burst fixture. It holds a push, advances canonical fixtures, ends while the slot is held, releases it and checks every living/ghost cue in order with unique `(run,tick,index)` identities; subsequent drain has no duplicates. Buffer bound is 64 event-bearing ticks with explicit truncation. This certifies bounded deferred-push retention, not unbounded reliable delivery: sends time out after one second and overflow requires resync. No event broker or persistence layer was added.

## F. Input and timing

Corrected live run: **1559 received/admitted inputs, 0 rejected** in each of `benign_movement`, `burst_rejected`, `refused`; **9 Burst-request admissions**. Prior-clock run: 24 benign refusals, no Burst/other refusal; all were movement during commit admission timing. Focused real-host boundary test separately exercises stale sequence (`benign_movement`), late Burst edge (`burst_rejected`) and malformed vector (`refused`), then retries a late refused press from a genuinely Overlit source and checks the actual single `BurstManual` cue and consumed charge. Charge/latch rule refusals are not transport loss.

| Milliseconds | p50 | p95 | p99 | max |
|---|---:|---:|---:|---:|
| Server commit intervals (delivered frames) | 33.463 | 38.665 | 50.152 | 92.918 |
| Server send invocation intervals | 33.462 | 38.666 | 50.154 | 92.918 |
| Client reception intervals | 33.476 | 38.643 | 50.305 | 93.307 |
| Server schedule lag | 211.438 | 313.061 | 316.023 | 331.850 |

Server diagnostics independently reported 1560 commits, interval p50/p95/p99/max **33.463/38.657/49.954/92.918 ms**; percentile interpolation/index convention accounts for the small difference. Zero coalesced live frames. Pacing `SendElapsedMs` is sampled before invoking the asynchronous sender, not a hardware/network transmission-completion timestamp. Server times share one monotonic origin; reception times share a separate client stopwatch. Only within-clock gaps are compared; no absolute one-way network latency claim.

Reproduced defect: the old nominal 33.333 ms `PeriodicTimer` produced **51525.276 ms** for 1560 transitions and roughly −475 ms terminal schedule error. This establishes a real ~1% acceleration, consistent with Task 11.3's short 89.304 s observation, beyond stopwatch boundaries. Minimal correction: shared `SoloTickClock` uses monotonic 30 Hz deadlines and rounds each remaining sleep up; both solo Original and Challenge/Fork host waits use it. Stalled clock slots can extend a round without dropping a logical transition. Frozen reducers/input windows/codecs untouched. Corrected real Fork is no longer early; focused 90-tick clock regression distinguishes this from the old approximately 2970 ms clock.

## G. Concurrency

One authenticated **four-player ghost-mp-v2 room**, **23.118 s** bounded overlap in the same serving process as the living Fork. Countdown included; authoritative snapshots advanced **T0–T598/600**. Each player submitted **583 inputs, zero rejected**. Snapshot publication is about 15 Hz for 30 Hz simulation: per-client delivery p50 **66.764–66.797 ms**, p95 **68.119–68.144 ms**, p99 **73.299–76.240 ms**, max **78.551–79.589 ms**. No stalled flow, lost canonical progression or visible shared-pool exhaustion. Room members cleanly left through the existing contract. Solo source/child verification and isolated multiplayer match identities remained correct. No extra full match or capacity certification.

## H. Security and composition

**Passed** in a separate real Kestrel security fact (45 s). Executed assertions include actual foreign=unknown HTTP bodies/status, no child catalog disclosure, invalid/tampered/expired/revoked cookie refusal, revoked Fork hub calls, unfinished source refusal, corrupt terminal hash on an isolated completed source, unsupported source/child identities and incompatible comparison. Accepted source remains unmodified. Dedicated login is queried for actual current user, intended role memberships and non-superuser status. DI has no privileged migration-login fallback. A separate solo-disabled Kestrel retained authenticated multiplayer room creation. Existing guest-session and owner-policy suites are reused.

## I. Cost and limitations

Three completed children (live T1140 child plus two cheap one-transition fixtures): **55.53 ms, 28 SELECTs**, bounded catalog `limit=1..25` with keyset continuation. No catalog cache needed. Comparison/trace costs above are appropriate once per selection/loading sequence; browser must retain prepared frames/samples. No historical reconstruction belongs on a slider animation frame or the live 30 Hz loop.

Quiet live interval: **1250 checkpoint reads for 1250 living transitions**, matching required canonical Append reconstruction, with no extra ghost history reads. Existing counting-engine tests independently check zero public restore/replay/aligned-compare/fork calls after preparation/start. Production ghost playback is prepared-array indexing. Required living canonical reads are not misclassified as ghost loads.

Remaining limitations: observed sub-100 ms local delivery tail and up to 332 ms cumulative schedule delay; bounded best-effort delivery under slow readers; no browser 60 FPS, device input, touch/audio, TLS/WSS proxy, WAN or production-network acceptance. Test databases are isolated ephemeral fixtures; identities/hashes above are evidence, not durable demo data in the user's real deployment. The user's `.env` was not modified.

## J. Preservation

No `GhostLap/Rules`, `Simulation`, `Timeline` codec, Marrow contract/engine or Revenant production change; no multiplayer physics/profile/scheduler change. Absolute T0–T2700 semantics, fixed-point movement remainder, distance scoring, seeded candles, Soulfire 0–4, manual Burst and 240-tick Overlit auto-Burst stay frozen. Existing `ghost-v1` source golden remained `b3130f...674c1`. No Spec 12/13 implementation, renderer or social/cache infrastructure.

## K. Commands, tests and corrections

Executed from repository root with .NET SDK **10.0.404** (global minimum 10.0.402):

```sh
dotnet test tests/Integration/Integration.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~Fork_final_acceptance --logger 'console;verbosity=detailed'
dotnet test tests/Integration/Integration.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~Fork_security_real|FullyQualifiedName~Deferred_sender|FullyQualifiedName~Solo_clock|FullyQualifiedName~GhostLapSoloHistoryHostTests|FullyQualifiedName~GhostLapSoloHostSmokeTests|FullyQualifiedName~GhostLapCompositionTests|FullyQualifiedName~GuestSessionTests' --logger 'console;verbosity=detailed'
dotnet test tests/GhostLap/GhostLap.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~SoloHistoryCatalogTests|FullyQualifiedName~SoloChallengeTests|FullyQualifiedName~SoloForkTests|FullyQualifiedName~SoloTraceTests|FullyQualifiedName~SoloSaveTests|FullyQualifiedName~Simulation|FullyQualifiedName~Rules|FullyQualifiedName~MpV2GoldenTests' --logger 'console;verbosity=minimal'
dotnet test tests/Architecture/Architecture.Tests.csproj -c Release --no-restore --logger 'console;verbosity=minimal'
dotnet build src/Hosts.Realtime/Hosts.Realtime.csproj -c Release --no-restore -warnaserror
```

Initial focused host/composition/deferred tests **14/14**; architecture **11/11**; Release host **0 warnings, 0 errors**. Final focused host/security/composition/guest/session tests **24/24**; domain/catalog/Challenge/Fork/trace/save/rules/simulation/multiplayer goldens **109/109**. The late-Burst retry check passed. Clock regression: **90 ticks in 3000.756 ms**. Deferred-sender regression: **73 real living cues plus corresponding ghost cues**, ordered and unique. Domain compilation emitted three existing xUnit1031 warnings in untouched `Multiplayer/LoadInstrumentationTests.cs`; Release production host stayed warning-free. Sanitized local measured output is under ignored `TestResults/spec11-5/`; raw temporary logs are not committed because Testcontainers debug output includes ephemeral credentials.

Production corrections: (1) missing saved-child visual-frame read operation → bounded authorized trace adapter, verified by full post-restart frame read and domain/HTTP regression; (2) measured early host pacing → monotonic wait correction, verified by corrected real-time Fork and focused clock test. Driver-only corrections: all players ready after joining, and owner's unfinished-source hub error expectation. No frozen rules were altered to satisfy either driver issue.

## L. Git checkpoint

Final diff inspected; focused checkpoint details are recorded in the final chat response and Git history. This document belongs to that checkpoint (a commit cannot contain its own SHA). Only Task 11.5 integration, focused tests, handoff/evidence and the Task 11.5 log entry are eligible. Unrelated Spec 08/11/12 rewrites, assets, deleted design input and recovery documents remain outside the checkpoint. Before checkpoint, `gh run list --branch codex/ghostlap-spec10-0-3 --limit 5 --json databaseId,status,conclusion,headSha,url` returned **[]**. The CI workflow triggers on PR events and pushes to main, not this feature branch; zero runs is **not green CI**. Push and exact-head run status are reported in the final chat response.

## M. Spec 13 handoff

Exact API/DTO table and semantics: [host contracts](15-ghostlap-solo-host-contracts.md). Authentication comes from the guest cookie; never send owner identity as authority.

- Hub `/hubs/ghostlap-solo`: `PrepareFork(PrepareForkRequest)`, `StartChallenge(StartChallengeRequest)`, `StreamChallengeInputs(SoloInputItem)`, `FinishFork(key)`, `CancelChallenge`; Challenge remains separate `PrepareChallenge`/`FinishChallenge`.
- Original HTTP `/api/ghostlap/solo/originals`, `/{id}/inspect/{tick}`, `/{id}/trace?from=&count=&v=` → `SoloOriginalView[]`, `SoloInspectView`, `SoloTraceView`.
- Child HTTP `/api/ghostlap/solo/children?source=&after=&limit=`, `/{id}`, `/{id}/trace?from=&count=&v=`, `/{id}/comparison?from=&count=&stride=&v=` → `SoloChildPageView`, `ChallengeResultView`, **`SoloChildTraceView`**, `SoloComparisonView`.
- `solo-history/1`; trace count 1–300; curve count/stride 1–300; catalog limit 1–25. Respect identity/hash/version and nullable continuation.
- Await complete `Ready`, then start. Fork living state is source state T; first input T+1. Frame `Living` and `Ghost` always share absolute tick. Delta is child minus original at that tick.
- Retain preparation data for scrubbing, graph redraw and ghost playback. Interpolate visual movement using tick identity. Deduplicate cues by `(runId,tick,index)` and consume ordered `Missed`; do not render charge/score from interpolated fictional state. Acknowledge truncation/slow-reader resync explicitly.
- Browser-only acceptance remains: 60 FPS rendering/interpolation, legal press responsiveness under physical input, missed-frame effects, rewind montage, audio/touch and deployed WSS behavior. No such experience is certified here.

## N. Next-step decision

**Spec 11 is functionally complete as a private, durable, renderer-ready backend timeline product.** Acceptance is based on the successful live/cold gate assertions plus passing independent focused security, sender, clock and regression tests, with the failed intermediate driver attempts explicitly retained above. Visual execution and browser/deployment acceptance belong to Spec 13.
