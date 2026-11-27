# Spec 11 Task 11.4 — exact fork gate

2026-11-18: **PASS** for the internal service gate. No demonstrated production defect; Kiro's `SoloChallenges.cs` implementation is unchanged by verification. Frozen Marrow, all solo profiles/codecs/goldens, multiplayer and Revenant remain unchanged. Task 11.5 is not implemented here.

## Persisted identities and canonical evidence

The following is captured from the final successful PostgreSQL run, not copied from earlier task scores. All three histories belong to the same authenticated fixture guest. The fixture plays legal inputs through the canonical recorder at test speed; no additional real-time original/challenge was run. Its original suppresses manual Burst from T1700 to exercise automatic Burst.

```text
owner guest:1a534b84-ed46-4cbd-bcb4-b4faf82f4846; seed 42; mascot 2; layout arena-v1; versions TimelineVersionKey { DomainId = ghost-lap, ReducerVersion = ghost-v1, InputSchemaVersion = ghost-input/1, StateSchemaVersion = ghost-state/1, ContextSchemaVersion = ghost-context/1 }
T1140 state hash 7a76dc5e16e9be321695f964a3eb2cbb4d117c5a040c274a7e2e71fd68706f9e; canonical byte length 669
source fingerprint before/after restart f71f876531264c4ee7cf3347a9cf83c8|2700:38afe85c03c55871a2d310d44a16b82e|46:003e7072a3305b5fa6e81c5695afd536|2160:19c52ecc661e96501eb737b82fa4b702|80511086205f440d1fd87e19ef5acb3d
source last record a4754d7eed7bbd121b07425cb6c43293590cdc3caadaed30531ac1129c35776f; X last record 2587df9ea585d40a4e1f443d7622b70856132bc23e47d470b897241c6180f11a; Y last record 9bf9bb149209300d858bd9b57c26a469918aaf9e3fc506194495b1626b390abb
Writer provider disposed; PostgreSQL stopped/started; new restricted runtime provider and empty cache; both histories replay Verified.
source 01a1ecbd-90a6-7ca8-8b52-18ad69562c56 final 351dbb0b9a84f58e90fb847b8ad41c72af4785823239ae633eae457d6a088a5e score 3325
sibling X 01a1ecbd-e44a-776b-bb7c-5107c84b8b36 final 7d661efe4e14206460e6e21b88411f2f69685c5a55056bbac5856ed311353eb6 score 2327 delta -998
sibling Y 01a1ecbe-031a-75ab-bc15-687ac804a4b0 final c83a5f30bc92f8eb6b453d078930eb68b6616ab63298f01de02b54dc8bf0bca9 score 1920 delta -1405
T1140+1 divergence: X 1311 / Y 1311 / source 1311
```

T1140: complete canonical state is 669 bytes; movement remainder 1732, 27 collected candles, score 1311, charge 2, Overlit deadline 0, held-input deadline 1145. Equality uses `GameStateCodec.EncodeCanonical`, covering every codec field: positions, fixed distance, score components, effective input/sequence/hold/latch, collected candles, active candles, contact cooldowns, events and final flags. Context is pinned separately: seed 42, mascot 2, arena-v1; child manifest context/rules/seed hashes equal the source's. The future candle schedule comes from that canonical context, rather than a new game clock.

## Prefix and first-transition proof

`SoloChallenges.BeginFork` → `SoloChallenge.PrepareAsync`/`StartAsync` → `SoloBranches.ForkAsync` → frozen `ITimelineEngine.ForkAsync`. The canonical manifest has parent equal to the source ID and fork position 1140. Both branch catalog rows are owner-bound Complete `Fork` rows. Each child's physical suffix has minimum 1141, maximum 2700 and count 1560; the authentic past is referenced, not duplicated.

`SoloBranches.RestoreAsync` uses frozen Timeline reconstruction. Prefix states at T0/T570/T1140 equal the source's complete bytes. Both children restore exactly the source T1140 bytes and state hash, including after restart. `ReplayAsync` independently verifies each complete ancestor-plus-suffix chain and both final hashes. We do not require branch-specific record envelope hashes to equal the source's.

Every live suffix step is checked against the frozen reducer with that child's own newly admitted inputs, while the prepared ghost hash/score matches the original at the same absolute tick. The first receipt is T1141, not T1140. Recovery restores T0, T1, T570, T1139, T1140, T1141, T1500, T2699 and T2700 and compares aligned scores through `CompareAsync`/`CompareAlignedAsync`. The two children may still have the same score at T1141; full state and canonical lineage, not score alone, prove their correctness. Their independently verified final futures diverge.

The original's complete manifest/catalog rows and ordered complete step, checkpoint and input rows are fingerprinted before/after play and restart. All fingerprints and both source hashes remain unchanged. Original catalog listing excludes both children, and child IDs cannot be opened as originals.

## Cold recovery, retries and refusals

The integrated test disposes the writer ServiceProvider and Npgsql pool, stops/starts the actual PostgreSQL Testcontainer without replacing its data, and composes a new ServiceProvider, new pool and empty trace cache. All recovery goes through `ghostlap_solo_app`, a LOGIN member only of `marrow_timeline_runtime` and `ghostlap_runtime`, using the authenticated owner. The test OS process remains alive to hold expected bytes, IDs and hashes; this is fresh runtime composition plus an actual database server restart, not a separately launched application executable. No original provider or live session supplies a recovery read.

Both owner-scoped keys (`sib-x`, `sib-y`, server-prefixed `fork.`) resolve the same saved IDs/hashes repeatedly after restart. There is one catalog row per key, still exactly 1560 suffix rows, and no recovery append. Both full canonical replays are Verified. A foreign authenticated guest cannot resolve either key, restore a child or prepare the source; foreign and unknown IDs return non-disclosing NotFound. Existing Challenge ownership/library regressions also pass.

One controlled completion failure commits Timeline completion then loses its acknowledgement. The fork reports Unavailable, stays Running and resolves NotComplete until the existing completion retry verifies and marks the catalog Complete. Repeated completion returns the same result. No false completed result or duplicate append is accepted.

The same key/same source/tick returns the same unadvanced child. A distinct key creates a sibling. A changed tick conflicts before an engine fork. Source binding is tested against a second genuinely recorded/verified original at test speed using the existing legal-input fixture: reusing the key conflicts with zero engine forks. The same client key in the Challenge namespace yields a distinct Challenge child at T0. Challenge resolution never returns a Fork. Interrupted advanced children remain NotComplete and are not silently resumed by the session runner.

T0 forks have exact initial bytes and Fork classification. T2699 requires one transition and completes at T2700; input at T2701 is rejected. T2700 is ForkNotAllowed; -1 and 2701 are InvalidTick, with no branch/session start. Foreign, unknown, unfinished, nested-child and incompatible-version sources fail closed. Narrow admin-only damage probes in the isolated DB restore their values in finally: an unverified manifest and a damaged T1140 checkpoint are refused by fork preparation without creating a branch.

Manual Burst boundary T194 preserves its latch and does not replay the completed action. Active Overlit at T1889 preserves the deadline; the first transition at T1890 produces exactly the source automatic Burst. Source inputs reproduce byte-exact states, hashes, events and score increments over 40/60-tick windows. No clock reset or duplicate event.

## Historical isolation and performance

For each T1140 sibling, counters reset after start show **0 Restore, 0 Replay, 0 CompareAligned, 0 Fork calls** during **1560 appends**. The ghost is indexed from the immutable prepared T0–T2700 trace. Inspect/Trace use these same counted engine ports, and the tick loop calls neither. Canonical recorder internal reads/writes are allowed. The ghost is data only and is never supplied to the reducer; living state equals its own input fold, with no cross-world collision/pickup/charge/Burst effects. The focused Spec 06 branch isolation tests also pass.

Full-trace preparation's prior approximately 5–6 s cost remains accepted; this verification makes no new preparation latency/throughput claim and runs no load or historical reconstruction benchmark.

## Executed checks

SDK 10.0.404; PostgreSQL 18.6-alpine3.24, existing migrations/runtime grants. Commands run from repository root:

```sh
dotnet test tests/GhostLap/GhostLap.Tests.csproj -c Release --filter 'FullyQualifiedName~SoloForkTests' --logger 'trx;LogFileName=spec11-4-forks.trx' --results-directory /tmp/spec11-4-results
dotnet test tests/GhostLap/GhostLap.Tests.csproj -c Release --filter 'FullyQualifiedName~SoloForkTests|FullyQualifiedName~SoloChallengeTests|FullyQualifiedName~SoloGhostTraceCacheTests|FullyQualifiedName~SoloBranchTests' --logger 'trx;LogFileName=spec11-4-focused.trx' --results-directory /tmp/spec11-4-results
dotnet test tests/Marrow/Marrow.Tests.csproj -c Release --filter 'FullyQualifiedName~ForkContinueCompareTests|FullyQualifiedName~ForkPropertyTests|FullyQualifiedName~RestoreReplayTests|FullyQualifiedName~TimelineStoreRestartTests' --logger 'trx;LogFileName=spec11-4-timeline.trx' --results-directory /tmp/spec11-4-results
dotnet test tests/Architecture/Architecture.Tests.csproj -c Release --logger 'trx;LogFileName=spec11-4-architecture.trx' --results-directory /tmp/spec11-4-results
dotnet build tests/GhostLap/GhostLap.Tests.csproj -c Release --no-restore
```

Initial strengthened fork run: 5/5. Final focused run (including subsequent source/key and corruption probes): **20/20**, 0 skipped/failed. Timeline: **17/17**. Architecture: **11/11**. Final incremental Release build (including Hosts.Realtime): **0 warnings/errors**. The preceding test compilation reported three pre-existing xUnit1031 warnings in Multiplayer/LoadInstrumentationTests.cs; those unrelated tests were not changed. Compact counters, test names and full sibling output are retained in [verification.json](../evidence/spec11-4/verification.json). Raw local TRX files remain under `/tmp/spec11-4-results`.

## Minimal Task 11.5 handoff

No new public hub route exists or was authorization-tested for Fork. Extend the existing Challenge lifecycle in `SoloChallengeRegistry` and `GhostLapSoloContracts`; do not add a second hub/runner.

| Journey | Actual internal API / existing DTO and required adaptation |
| --- | --- |
| Select original | `SoloOriginals.ListAsync(actor, limit, ct)` and `OpenAsync(actor, original, ct)`; reuse authenticated owner context, never trust request owner |
| Prepare selected fork | `SoloChallenges.BeginFork(IActorContext actor, TimelineId original, long forkTick)` then `SoloChallenge.PrepareAsync(ct)` → `PreparedGhostTrace`; existing `PrepareChallengeRequest(string OriginalId)` needs selected tick and branch-kind routing; `ChallengePrepared` currently lacks selected fork tick |
| Start | `SoloChallenge.StartAsync(string clientKey, ct)` → `SoloChallengeStarted` (Child, Original, OperationKey, Seed, MascotId, RulesVersion, LayoutVersion, Living, Ghost, Kind, ForkTick); preserve stable client key through `StartChallengeRequest`, expose kind/tick in existing start wire mapping |
| Live | Reuse `Submit(SubmittedInput)`, `Neutralize(byte)`, `CommitTickAsync(ct)` → `SoloChallengeTick`; admit T+1..2700, keep absolute ticks and existing 30Hz runner; `ChallengeFrameView` already pairs same-tick Living/Ghost/Delta |
| Honest finish | `CompleteAsync(ct)` → `SoloChallengeResult`; existing `ChallengeMapping.ToFinish`/`ChallengeFinishView` provide Completed/Pending/Failed, result carries kind, parent tick, hashes and scores |
| Refresh/restart | Route owner+client key to `SoloChallenges.ResolveForkAsync(actor, clientKey, ct)` for Fork and `ResolveAsync` for Challenge; current registry finish falls back only to ResolveAsync |
| Discovery/history | Keep `SoloOriginals.ListAsync` original-only; add owner-facing Challenge/Fork child catalog in 11.5. Existing domain `InspectAsync`, bounded `TraceAsync` (max 300), branch `RestoreAsync`/`CompareAsync` need owner-safe transport adapters |
| Typed failure | Preserve `InvalidTick`, `ForkNotAllowed`, `Conflict`, `NotFound`, `NotComplete`, `IncompatibleVersion`, `CorruptHistory`, `VerificationFailed`, `Unavailable`; existing `SoloMapping.Code` already maps these, with foreign IDs indistinguishable from unknown |
| Visual identity | Propagate prepared source identity/final hash and pinned codec/reducer/layout, plus start seed/mascot/kind/tick and frame state hashes. Existing wire start has rules/layout/seed but lacks explicit kind/tick; do not infer these from final scores |

Explicitly pending for 11.5: authenticated SignalR fork initiation; one real-time T1140→T2700 public-hub continuation; browser tick smoothing/input-admission metrics; historical seek/trace endpoints; owner-facing child catalog; PixiJS rendering/audio. These do not prevent closing internal arbitrary-tick fork correctness.

## Git scope

Checkpoint is limited to `SoloChallenges.cs`, `SoloForkTests.cs`, this evidence, compact test evidence and only the Task 11.4 portions of `kiro-log.md`. Existing specification amendments, assets, audit/recovery files, unrelated log edits and deletions remain in the user's working tree. Commit/push and actual Actions status are reported with the checkpoint; local tests are not a claim of green GitHub CI. No PR is opened.
