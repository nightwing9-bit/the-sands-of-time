using System.Collections.Immutable;
using GhostLap.Rules;
using GhostLap.Simulation;
using GhostLap.Solo;
using GhostLap.Tests.Simulation;
using GhostLap.Timeline;
using Marrow.Infrastructure.Identity.Policy;
using Marrow.Kernel.Results;
using Marrow.Kernel.Security;
using Marrow.Timeline;
using Xunit;
using Xunit.Abstractions;
using static GhostLap.Tests.Solo.SoloDatabase;

namespace GhostLap.Tests.Solo;

/// <summary>An engine decorator that counts every Timeline call, so a test can prove which calls a code path makes.</summary>
internal sealed class CountingEngine(ITimelineEngine<GameState, GameInput, GameContext> inner) : ITimelineEngine<GameState, GameInput, GameContext>
{
    private int _restore;
    private int _replay;
    private int _fork;
    private int _compare;
    private int _append;

    public int Restores => Volatile.Read(ref _restore);

    public int Replays => Volatile.Read(ref _replay);

    public int Forks => Volatile.Read(ref _fork);

    public int Compares => Volatile.Read(ref _compare);

    public int Appends => Volatile.Read(ref _append);

    /// <summary>Called with the running restore count after each restore starts (for cancellation at a chosen point).</summary>
    public Action<int>? OnRestore { get; set; }

    public bool LoseNextCompletionReply { get; set; }

    public void Reset()
    {
        _restore = _replay = _fork = _compare = _append = 0;
    }

    public Task<Result<TimelineId, TimelineError>> CreateSourceAsync(
        CreateSourceRequest<GameState, GameContext> source, string operationKey, CancellationToken ct) =>
        inner.CreateSourceAsync(source, operationKey, ct);

    public Task<Result<AppendReceipt, TimelineError>> AppendAsync(
        TimelineId history, long expectedLastPosition, StepRecord<GameInput> nextStep, string operationKey, CancellationToken ct)
    {
        Interlocked.Increment(ref _append);
        return inner.AppendAsync(history, expectedLastPosition, nextStep, operationKey, ct);
    }

    public async Task<Result<CompletionReceipt, TimelineError>> CompleteAsync(TimelineId history, long expectedLastPosition, CancellationToken ct)
    {
        var result = await inner.CompleteAsync(history, expectedLastPosition, ct);
        if (result.IsSuccess && LoseNextCompletionReply)
        {
            LoseNextCompletionReply = false;
            throw new Npgsql.NpgsqlException("Injected lost acknowledgement after Timeline completion committed.");
        }

        return result;
    }

    public Task<Result<VerifiedState<GameState>, TimelineError>> RestoreAsync(TimelineId history, long position, CancellationToken ct)
    {
        var count = Interlocked.Increment(ref _restore);
        OnRestore?.Invoke(count);
        return inner.RestoreAsync(history, position, ct);
    }

    public Task<Result<VerifiedReplay<GameState>, TimelineError>> ReplayAsync(TimelineId history, CancellationToken ct)
    {
        Interlocked.Increment(ref _replay);
        return inner.ReplayAsync(history, ct);
    }

    public Task<Result<TimelineId, TimelineError>> ForkAsync(
        TimelineId source, long forkPosition, string operationKey, GameContext pinnedBranchContext, CancellationToken ct)
    {
        Interlocked.Increment(ref _fork);
        return inner.ForkAsync(source, forkPosition, operationKey, pinnedBranchContext, ct);
    }

    public Task<Result<IReadOnlyList<AlignedState<GameState>>, TimelineError>> CompareAlignedAsync(
        TimelineId source, TimelineId alternative, IReadOnlyList<long> positions, CancellationToken ct)
    {
        Interlocked.Increment(ref _compare);
        return inner.CompareAlignedAsync(source, alternative, positions, ct);
    }
}

/// <summary>
/// Task 11.3: race the original ghost in a fresh full challenge, over the real recorder, Timeline and PostgreSQL. Rounds are driven
/// tick by tick without waiting for real time.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SoloChallengeTests(SoloDatabase db, ITestOutputHelper output) : IClassFixture<SoloDatabase>
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private sealed record Rig(SoloHost Host, CountingEngine Engine, SoloOriginals Originals, SoloBranches Branches, SoloChallenges Challenges);

    private static Rig NewRig(SoloDatabase db, SoloGhostTraceCache? cache = null)
    {
        var host = db.NewHost();
        var engine = new CountingEngine(host.Engine);
        var authorizer = new OwnerResourceAuthorizer(new SoloOwnershipLookup(host.Store));
        var originals = new SoloOriginals(engine, host.Store, authorizer);
        var branches = new SoloBranches(engine, host.Store, authorizer);
        return new Rig(host, engine, originals, branches, new SoloChallenges(originals, branches, host.Store, authorizer, cache));
    }

    private static byte[] Bytes(GameState s) => GameStateCodec.Instance.EncodeCanonical(s);

    [Fact]
    public async Task Preparation_ends_Ready_with_the_exact_verified_trace_at_every_tick()
    {
        var rig = NewRig(db);
        await using var _ = rig.Host;
        var challenge = rig.Challenges.Begin(db.OwnerA, db.Original.Timeline);
        Assert.Equal(SoloChallengePhase.Preparing, challenge.Phase);
        Assert.False(challenge.HoldsTrace);

        var trace = Ok(await challenge.PrepareAsync(Ct));

        Assert.Equal(SoloChallengePhase.Ready, challenge.Phase);
        Assert.True(challenge.HoldsTrace);
        Assert.Equal((db.Original.Timeline, "ghost-v1", db.Original.FinalStateHash, GameRulesV1.RoundTicks + 1),
            (trace.Original, trace.Versions.ReducerVersion, trace.FinalStateHash, trace.FrameCount));

        // Tn is the state after n transitions: every frame equals the independently recorded live state of the same tick.
        for (var tick = 0; tick <= GameRulesV1.RoundTicks; tick++)
        {
            var live = db.Run.Trace[tick].Players[0];
            var f = trace.At(tick);
            Assert.Equal((long)tick, f.Tick);
            Assert.Equal((live.XFixed, live.YFixed, live.TotalScore, live.Charge, live.OverlitDeadlineTick),
                (f.XFixed, f.YFixed, f.TotalScore, f.Charge, f.OverlitDeadlineTick));
            if (tick > 0)
            {
                Assert.Equal(db.Run.Receipts[tick - 1].StateHash, f.StateHash);
            }
        }

        var finalScore = db.Original.Result.Scores[0].TotalScore;
        Assert.Equal(finalScore, trace.At(GameRulesV1.RoundTicks).TotalScore);
        Assert.True(trace.At(1140).TotalScore < finalScore, "the T1140 ghost score is the historical score, not the final one");
        Assert.Equal(db.Run.Trace[1140].Players[0].TotalScore, challenge.GhostAt(1140).TotalScore);
        Assert.Equal(0, trace.At(0).TotalScore);
        Assert.Throws<ArgumentOutOfRangeException>(() => trace.At(GameRulesV1.RoundTicks + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => trace.At(-1));
        output.WriteLine($"prepared {trace.FrameCount} frames; T0 {trace.At(0).StateHash[..8]} T1140 {trace.At(1140).StateHash[..8]} " +
                         $"T2700 {trace.At(2700).StateHash[..8]}; {rig.Engine.Restores} restores, {rig.Engine.Replays} replay(s)");
        challenge.Cancel();
        Assert.False(challenge.HoldsTrace);
    }

    [Fact]
    public async Task Cancelled_preparation_starts_nothing_a_retry_succeeds_and_a_matching_trace_is_reused_without_history_reads()
    {
        var cache = new SoloGhostTraceCache();
        var rig = NewRig(db, cache);
        await using var _ = rig.Host;
        using var cts = new CancellationTokenSource();
        rig.Engine.OnRestore = n =>
        {
            if (n == 40)
            {
                cts.Cancel();
            }
        };

        var cancelled = rig.Challenges.Begin(db.OwnerA, db.Original.Timeline);
        var result = await cancelled.PrepareAsync(cts.Token);
        Assert.True(result.IsFailure);
        Assert.Equal(SoloErrorCode.Cancelled, result.Error.Code);
        Assert.Equal(SoloChallengePhase.Cancelled, cancelled.Phase);
        Assert.False(cancelled.HoldsTrace);
        Assert.Equal(0, cache.Count);
        Assert.InRange(rig.Engine.Restores, 40, 41);

        // No partial challenge: it cannot start, nothing is forked, no child row exists.
        var start = await cancelled.StartAsync("cancelled-start", Ct);
        Assert.Equal(SoloErrorCode.Conflict, start.Error.Code);
        Assert.Equal(0, rig.Engine.Forks);
        Assert.Equal(0L, await CountBranchesAsync("challenge.cancelled-start"));

        // Retry is a new challenge and prepares the complete trace.
        rig.Engine.OnRestore = null;
        var retry = rig.Challenges.Begin(db.OwnerA, db.Original.Timeline);
        var first = Ok(await retry.PrepareAsync(Ct));
        Assert.Equal(SoloChallengePhase.Ready, retry.Phase);
        Assert.Equal(1, cache.Count);
        Assert.Equal(1, cache.LeasedCount);

        // A matching prepared trace is reused: same instance, zero Timeline reads (the first preparation did read).
        Assert.True(rig.Engine.Restores >= GameRulesV1.RoundTicks + 1 && rig.Engine.Replays >= 1);
        rig.Engine.Reset();
        var again = rig.Challenges.Begin(db.OwnerA, db.Original.Timeline);
        var second = Ok(await again.PrepareAsync(Ct));
        Assert.Same(first, second);
        Assert.Equal((0, 0), (rig.Engine.Restores, rig.Engine.Replays));
        Assert.Equal(1, cache.Count);

        // Release: both challenges drop their lease; the idle trace can then be dropped.
        retry.Cancel();
        again.Cancel();
        Assert.Equal(0, cache.LeasedCount);
        Assert.False(retry.HoldsTrace || again.HoldsTrace);
        cache.ReleaseIdle();
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public async Task A_foreign_unknown_or_unready_original_never_starts_and_forks_nothing()
    {
        var cache = new SoloGhostTraceCache();
        var rig = NewRig(db, cache);
        await using var _ = rig.Host;

        var foreign = rig.Challenges.Begin(Guest(), db.Original.Timeline);
        var f = await foreign.PrepareAsync(Ct);
        var unknown = rig.Challenges.Begin(db.OwnerA, new TimelineId(Guid.NewGuid()));
        var u = await unknown.PrepareAsync(Ct);
        Assert.Equal((SoloErrorCode.NotFound, SoloErrorCode.NotFound), (f.Error.Code, u.Error.Code));
        Assert.Equal((SoloChallengePhase.Failed, SoloChallengePhase.Failed), (foreign.Phase, unknown.Phase));
        Assert.Equal(f.Error.Message, u.Error.Message);
        Assert.Equal(0, rig.Engine.Restores);
        Assert.Equal(0, cache.Count);

        foreach (var c in new[] { foreign, unknown, rig.Challenges.Begin(db.OwnerA, db.Original.Timeline) })
        {
            Assert.Equal(SoloErrorCode.Conflict, (await c.StartAsync("not-ready", Ct)).Error.Code);
        }

        Assert.Equal(0, rig.Engine.Forks);
        Assert.Equal(0L, await CountBranchesAsync("challenge.not-ready"));
        Assert.Equal(SoloErrorCode.InvalidRequest, (await rig.Challenges.Begin(db.OwnerA, db.Original.Timeline).StartAsync("bad key!", Ct)).Error.Code);
    }

    [Fact]
    public async Task A_full_challenge_races_the_exact_ghost_with_no_history_reads_and_finishes_a_verified_challenge_child()
    {
        var sourceBefore = await SourceFingerprintAsync();
        var rig = NewRig(db);
        await using var _ = rig.Host;
        var challenge = rig.Challenges.Begin(db.OwnerA, db.Original.Timeline);
        Ok(await challenge.PrepareAsync(Ct));

        // Start: the living world is the original's exact tick zero, and the candle plan is the source's.
        var started = Ok(await challenge.StartAsync("full-round", Ct));
        Assert.Equal(SoloChallengePhase.Running, challenge.Phase);
        Assert.Equal("challenge.full-round", started.OperationKey);
        Assert.Equal((SoloDatabase.Seed, (byte)2, db.Original.Timeline), (started.Seed, started.MascotId, started.Original));
        Assert.NotEqual(db.Original.Timeline, started.Child);
        Assert.True(Bytes(db.Run.Trace[0]).AsSpan().SequenceEqual(Bytes(challenge.LivingState!)), "living T0 equals source T0");
        Assert.Equal((0L, db.Run.Trace[0].Players[0].XFixed, db.Run.Trace[0].Players[0].YFixed),
            (started.Ghost.Tick, started.Ghost.XFixed, started.Ghost.YFixed));
        var context = challenge.LivingContext!;
        for (var t = 0; t <= GameRulesV1.RoundTicks; t += 30)
        {
            Assert.Equal(db.Run.Context.Plan.ActiveIdsAt(t).ToArray(), context.Plan.ActiveIdsAt(t).ToArray());
        }

        // From here on gameplay must make no history-restoration call (the counter is live: preparation did read the history).
        Assert.True(rig.Engine.Restores >= GameRulesV1.RoundTicks + 1 && rig.Engine.Forks == 1);
        rig.Engine.Reset();
        Assert.Equal(InputRejection.UnknownSlot, challenge.Submit(new SubmittedInput(1, 1, 1, 0, 0, false)).Error);
        Assert.Equal(InputRejection.InvalidVector, challenge.Submit(new SubmittedInput(0, 1, 1, 2, 0, false)).Error);
        Assert.Equal(InputRejection.LateTick, challenge.Submit(new SubmittedInput(0, int.MaxValue, -100, 0, 0, false)).Error);
        Assert.True((await challenge.StartAsync("duplicate-start", Ct)).IsFailure);
        var driver = new AlternateRouteDriver();
        var reference = GameState.Initial(context);
        var maxCharge = 0;
        var lastDelta = 0;
        var diverged = 0;
        SoloChallengeTick? last = null;
        while (!challenge.LivingState!.IsFinal)
        {
            var state = challenge.LivingState!;
            var inputs = driver.Inputs(state, context);
            foreach (var g in inputs)
            {
                var submitted = new SubmittedInput(g.Slot, g.Sequence, g.Tick, g.MoveX, g.MoveY, g.RequestBurst);
                Assert.True(challenge.Submit(submitted).IsSuccess);
                Assert.Equal(InputRejection.StaleSequence, challenge.Submit(submitted).Error);
            }

            var tick = Ok(await challenge.CommitTickAsync(Ct));
            last = tick;
            reference = GhostLapTransition.Instance.Apply(reference, tick.Tick, BranchFixture.Ordered(inputs), context);

            // The living world is the unchanged reducer, independent of the ghost.
            var p = reference.Players[0];
            Assert.Equal((p.XFixed, p.YFixed, p.TotalScore, p.Charge), (tick.Living.XFixed, tick.Living.YFixed, tick.Living.TotalScore, tick.Living.Charge));
            maxCharge = Math.Max(maxCharge, tick.Living.Charge);

            // The ghost is the source at exactly this tick (no off-by-one) and never the final score.
            var source = db.Run.Trace[(int)tick.Tick].Players[0];
            Assert.Equal(tick.Tick, tick.Ghost.Tick);
            Assert.Equal((source.XFixed, source.YFixed, source.TotalScore, source.Charge), (tick.Ghost.XFixed, tick.Ghost.YFixed, tick.Ghost.TotalScore, tick.Ghost.Charge));
            Assert.Equal(db.Run.Receipts[(int)tick.Tick - 1].StateHash, tick.Ghost.StateHash);
            Assert.Equal(tick.Living.TotalScore - source.TotalScore, tick.Delta);
            Assert.Equal(db.Run.Trace[(int)tick.Tick].Events.ToArray(), tick.Ghost.Events.ToArray());
            if (tick.Living.TotalScore != tick.Ghost.TotalScore)
            {
                diverged++;
            }

            lastDelta = tick.Delta;
            if (tick.Tick is 1140 or 2700)
            {
                output.WriteLine($"T{tick.Tick}: ghost ({tick.Ghost.XFixed},{tick.Ghost.YFixed}) score {tick.Ghost.TotalScore}; " +
                                 $"living ({tick.Living.XFixed},{tick.Living.YFixed}) score {tick.Living.TotalScore}; delta {tick.Delta}");
            }
        }

        // Zero history reads during gameplay: only appends of the living history happened.
        Assert.Equal((0, 0, 0, 0), (rig.Engine.Restores, rig.Engine.Replays, rig.Engine.Compares, rig.Engine.Forks));
        Assert.Equal(GameRulesV1.RoundTicks, rig.Engine.Appends);
        Assert.NotNull(last);
        Assert.True(last.IsFinal);
        Assert.Equal(db.Original.Result.Scores[0].TotalScore, last.Ghost.TotalScore);
        Assert.True(diverged > 0, "the living score diverges from the ghost's");
        Assert.True(maxCharge > 0);

        // Independent collections over the same candle opportunities.
        var living = challenge.LivingState!;
        Assert.NotEqual(db.Run.Trace[^1].Collected.Select(c => c.Id).ToArray(), living.Collected.Select(c => c.Id).ToArray());
        Assert.True(living.Collected.Length > 0 && db.Run.Trace[^1].Collected.Length > 0);

        Assert.Equal(SoloErrorCode.Conflict, (await challenge.CommitTickAsync(Ct)).Error.Code);
        Assert.Equal(InputRejection.RoundOver, challenge.Submit(new SubmittedInput(0, int.MaxValue, 2701, 1, 0, false)).Error);
        Assert.Equal(2700L, challenge.LivingState.Tick);

        // The canonical completion committed, but its reply was lost. Do not report a result until a retry verifies it.
        rig.Engine.LoseNextCompletionReply = true;
        var uncertain = await challenge.CompleteAsync(Ct);
        Assert.Equal(SoloErrorCode.Unavailable, uncertain.Error.Code);
        Assert.Null(challenge.Result);
        Assert.Equal(SoloChallengePhase.Running, challenge.Phase);
        var incompleteRow = Ok(await rig.Host.Store.FindBranchAsync(started.Child, Ct));
        Assert.Equal(SoloOriginalStatus.Recording, incompleteRow.Status);
        var result = Ok(await challenge.CompleteAsync(Ct));

        Assert.Equal(SoloChallengePhase.Completed, challenge.Phase);
        Assert.False(challenge.HoldsTrace);
        Assert.Equal(SoloBranchKind.Challenge, result.Kind);
        Assert.Equal((db.Original.Timeline, 0L, (long)GameRulesV1.RoundTicks), (result.Original, result.ForkTick, result.FinalTick));
        Assert.Equal(reference.FinalResult!.Scores[0].TotalScore, result.LivingScore);
        Assert.Equal(lastDelta, result.Delta);
        Assert.Equal(result.LivingScore - result.SourceScore, result.Delta);
        Assert.Equal(db.Original.FinalStateHash, result.SourceFinalStateHash);
        Assert.Equal(db.Original.LastRecordHash, result.SourceLastRecordHash);
        Assert.NotEqual(db.Original.FinalStateHash, result.ChildFinalStateHash);

        // Independent verification of the child through the engine, and the source is untouched.
        var replay = (await rig.Host.Engine.ReplayAsync(result.Child, Ct)).Value;
        Assert.Equal((HistoryStatus.Verified, result.ChildFinalStateHash, result.ChildLastRecordHash, (long)GameRulesV1.RoundTicks),
            (replay.Status, replay.FinalStateHash, replay.LastRecordHash, replay.FinalPosition));
        Assert.True(Bytes(replay.FinalState).AsSpan().SequenceEqual(Bytes(living)));
        var child0 = Ok(await rig.Branches.RestoreAsync(db.OwnerA, result.Child, 0, Ct));
        Assert.True(Bytes(db.Run.Trace[0]).AsSpan().SequenceEqual(Bytes(child0.State)), "child prefix at fork tick 0 is the source's initial state");
        Assert.Equal(sourceBefore, await SourceFingerprintAsync());
        Assert.Equal(db.Original.FinalStateHash, Ok(await rig.Originals.OpenAsync(db.OwnerA, db.Original.Timeline, Ct)).FinalStateHash);

        // Never an original: the library lists only the source, the child id is not an original, the branch row says Challenge.
        var listed = Ok(await rig.Originals.ListAsync(db.OwnerA, 100, Ct));
        Assert.DoesNotContain(listed, o => o.Timeline == result.Child);
        Assert.Contains(listed, o => o.Timeline == db.Original.Timeline);
        Assert.Equal(SoloErrorCode.NotFound, (await rig.Originals.OpenAsync(db.OwnerA, result.Child, Ct)).Error.Code);
        Assert.Equal(SoloErrorCode.NotFound, (await rig.Host.Store.FindByOwnerKeyAsync(db.OwnerA.SubjectId, "challenge.full-round", Ct)).Error.Code);
        var row = Ok(await rig.Host.Store.FindBranchAsync(result.Child, Ct));
        Assert.Equal((SoloOriginalStatus.Complete, SoloBranchKind.Challenge, db.OwnerA.SubjectId), (row.Status, SoloBranchKinds.Of(row), row.OwnerSubjectId));
        Assert.Equal(SoloErrorCode.NotFound, (await rig.Branches.RestoreAsync(Guest(), result.Child, 0, Ct)).Error.Code);

        // Idempotent completion; a finished challenge cannot tick again.
        Assert.Equal(result, Ok(await challenge.CompleteAsync(Ct)));
        Assert.Equal(SoloErrorCode.Conflict, (await challenge.CommitTickAsync(Ct)).Error.Code);

        // A cold host (refresh/restart, lost response) resolves the same verified challenge by key; strangers cannot.
        var cold = NewRig(db);
        await using var __ = cold.Host;
        var resolved = Ok(await cold.Challenges.ResolveAsync(db.OwnerA, "full-round", Ct));
        Assert.Equal((result.Child, result.Original, result.Kind, result.OperationKey, result.ForkTick, result.FinalTick),
            (resolved.Child, resolved.Original, resolved.Kind, resolved.OperationKey, resolved.ForkTick, resolved.FinalTick));
        Assert.Equal((result.ChildFinalStateHash, result.ChildLastRecordHash, result.SourceFinalStateHash, result.SourceLastRecordHash),
            (resolved.ChildFinalStateHash, resolved.ChildLastRecordHash, resolved.SourceFinalStateHash, resolved.SourceLastRecordHash));
        Assert.Equal((result.LivingScore, result.SourceScore, result.Delta), (resolved.LivingScore, resolved.SourceScore, resolved.Delta));
        Assert.Equal(SoloErrorCode.NotFound, (await cold.Challenges.ResolveAsync(Guest(), "full-round", Ct)).Error.Code);
        Assert.Equal(SoloErrorCode.NotFound, (await cold.Challenges.ResolveAsync(db.OwnerA, "no-such-key", Ct)).Error.Code);
        output.WriteLine($"child {result.Child.Value:D} living {result.LivingScore} vs source {result.SourceScore} (delta {result.Delta}); " +
                         $"child hash {result.ChildFinalStateHash}; source hash {result.SourceFinalStateHash}; " +
                         $"{diverged} of {GameRulesV1.RoundTicks} ticks differ in score; max charge {maxCharge}; pickups {reference.Players[0].Pickups}, bursts {reference.Players[0].Bursts}");
    }

    [Fact]
    public async Task A_cancelled_running_challenge_releases_its_trace_and_leaves_no_result_or_original()
    {
        var cache = new SoloGhostTraceCache();
        var rig = NewRig(db, cache);
        await using var _ = rig.Host;
        var challenge = rig.Challenges.Begin(db.OwnerA, db.Original.Timeline);
        Ok(await challenge.PrepareAsync(Ct));
        var started = Ok(await challenge.StartAsync("abandoned", Ct));
        for (var i = 0; i < 5; i++)
        {
            Ok(await challenge.CommitTickAsync(Ct));
        }

        challenge.Cancel();

        Assert.Equal(SoloChallengePhase.Cancelled, challenge.Phase);
        Assert.False(challenge.HoldsTrace);
        Assert.Equal(0, cache.LeasedCount);
        Assert.Null(challenge.Result);
        Assert.Equal(SoloErrorCode.Conflict, (await challenge.CompleteAsync(Ct)).Error.Code);
        Assert.Equal(SoloErrorCode.Conflict, (await challenge.CommitTickAsync(Ct)).Error.Code);
        var row = Ok(await rig.Host.Store.FindBranchAsync(started.Child, Ct));
        Assert.Equal(SoloOriginalStatus.Recording, row.Status);
        Assert.Equal(SoloErrorCode.NotComplete, (await rig.Challenges.ResolveAsync(db.OwnerA, "abandoned", Ct)).Error.Code);
        Assert.DoesNotContain(Ok(await rig.Originals.ListAsync(db.OwnerA, 100, Ct)), o => o.Timeline == started.Child);
        Assert.Equal(db.Original.FinalStateHash, Ok(await rig.Originals.OpenAsync(db.OwnerA, db.Original.Timeline, Ct)).FinalStateHash);
    }

    private async Task<long> CountBranchesAsync(string operationKey)
    {
        await using var cmd = db.Superuser.CreateCommand("SELECT count(*) FROM ghostlap.solo_branches WHERE operation_key = @key");
        cmd.Parameters.AddWithValue("key", operationKey);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>Every persisted fact of the source original (manifest, step/checkpoint/input digests, Ghost Lap row), as the superuser.</summary>
    private async Task<string> SourceFingerprintAsync()
    {
        await using var cmd = db.Superuser.CreateCommand("""
            SELECT h.last_position || '|' || h.latest_state_hash || '|' || h.last_record_hash || '|' || h.status || '|' || h.updated_at::text
              || '|' || (SELECT count(*) || ':' || md5(string_agg(position || state_hash || record_hash || operation_key, ',' ORDER BY position))
                         FROM marrow_timeline.steps WHERE timeline_id = @id)
              || '|' || (SELECT count(*) || ':' || md5(string_agg(position || state_hash || encode(sha256(state_bytes), 'hex'), ',' ORDER BY position))
                         FROM marrow_timeline.checkpoints WHERE timeline_id = @id)
              || '|' || (SELECT count(*) || ':' || md5(string_agg(input_id || encode(sha256(payload), 'hex'), ',' ORDER BY position, input_index))
                         FROM marrow_timeline.step_inputs WHERE timeline_id = @id)
              || '|' || (SELECT status || final_tick || final_state_hash || last_record_hash FROM ghostlap.solo_originals WHERE timeline_id = @id)
            FROM marrow_timeline.histories h WHERE h.timeline_id = @id
            """);
        cmd.Parameters.AddWithValue("id", db.Original.Timeline.Value);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }
}

/// <summary>The bounded in-memory trace cache: owner-bound reuse, leases, capacity and idle expiry (no database).</summary>
public sealed class SoloGhostTraceCacheTests
{
    private const string Hash = "aa";

    private sealed class ManualTime : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => 1000;

        public override long GetTimestamp() => Interlocked.Read(ref _ticks);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, (long)by.TotalMilliseconds);
    }

    private static PreparedGhostTrace Trace(TimelineId id, string hash = Hash)
    {
        var frames = Enumerable.Range(0, GameRulesV1.RoundTicks + 1).Select(t => new SoloTraceFrame(
            t, 0, 0, 0, 0, false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, false, 0, ImmutableArray<string>.Empty, ImmutableArray<GameEvent>.Empty,
            hash, default, t == GameRulesV1.RoundTicks)).ToArray();
        return new PreparedGhostTrace(id, GameRulesV1.RulesVersion, GameRulesV1.LayoutVersion, hash, frames);
    }

    private static SoloOriginalRecord Row(TimelineId id, string hash = Hash) => new(
        id, "guest:a", PrincipalKind.Guest, null, "k", GameRulesV1.RulesVersion, GameRulesV1.LayoutVersion, 1, 0,
        SoloOriginalStatus.Complete, GameRulesV1.RoundTicks, hash, "r");

    [Fact]
    public void Reuse_requires_the_same_owner_original_versions_and_verified_final_hash()
    {
        var cache = new SoloGhostTraceCache();
        var id = new TimelineId(Guid.NewGuid());
        using var lease = cache.TryAdd("guest:a", Trace(id))!;

        using (var reused = cache.TryLease("guest:a", Row(id)))
        {
            Assert.NotNull(reused);
            Assert.Same(lease.Trace, reused.Trace);
        }

        Assert.Null(cache.TryLease("guest:b", Row(id)));
        Assert.Null(cache.TryLease("guest:a", Row(new TimelineId(Guid.NewGuid()))));
        Assert.Null(cache.TryLease("guest:a", Row(id, "bb")));
        Assert.Null(cache.TryLease("guest:a", Row(id) with { LayoutVersion = "arena-v0" }));
    }

    [Fact]
    public void Capacity_is_bounded_leased_traces_are_never_evicted_and_idle_ones_expire()
    {
        var time = new ManualTime();
        var cache = new SoloGhostTraceCache(2, TimeSpan.FromMinutes(1), time);
        var (a, b, c) = (new TimelineId(Guid.NewGuid()), new TimelineId(Guid.NewGuid()), new TimelineId(Guid.NewGuid()));
        var leaseA = cache.TryAdd("o", Trace(a))!;
        var leaseB = cache.TryAdd("o", Trace(b))!;

        Assert.Null(cache.TryAdd("o", Trace(c)));
        Assert.Equal((2, 2), (cache.Count, cache.LeasedCount));

        leaseA.Dispose();
        leaseA.Dispose();
        using var leaseC = cache.TryAdd("o", Trace(c));
        Assert.NotNull(leaseC);
        Assert.Equal(2, cache.Count);
        Assert.Null(cache.TryLease("o", Row(a)));

        leaseB.Dispose();
        leaseC.Dispose();
        Assert.Equal(2, cache.Count);
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(0, cache.Count);
    }
}
