using Marrow.Tests.TestDomains;
using Marrow.Tests.Timeline.Persistence;
using Marrow.Timeline;
using Marrow.Timeline.Internal;
using Npgsql;
using Xunit;
using static Marrow.Tests.Timeline.Engine.EngineTestKit;
using static Marrow.Tests.Timeline.Engine.RowDigest;

namespace Marrow.Tests.Timeline.Engine;

/// <summary>
/// Task 2.4 fork/continue/compare against real PostgreSQL: sibling branches under changed contexts, the
/// inherited prefix under the source context, child cold restore after a server restart, terminal and
/// out-of-range fork positions, idempotent and concurrent fork keys, cancellation and retry, fail-closed
/// version/corruption errors, and aligned comparison (R02.04–R02.08, R02.10, R02.13–R02.15).
/// </summary>
[Trait("Category", "Integration")]
public sealed class ForkContinueCompareTests(TimelinePostgresDatabase db) : IClassFixture<TimelinePostgresDatabase>
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task Sibling_branches_with_changed_contexts_are_independent_and_reproducible_after_a_cold_restart()
    {
        var sourceContext = NextContext(3);
        var sourceSteps = BurstyTicks(1, 10, Key());
        var engine = NewEngine(db.Store, 4, Tick());
        var source = await NewVerifiedSourceAsync(engine, sourceContext, sourceSteps);
        var sourceBefore = await RowsAsync(source);
        var sourceManifest = (await db.Store.GetHistoryAsync(source, Ct)).Value;

        var contextA = new TickContext(1);
        var contextB = new TickContext(5);
        var a = (await engine.ForkAsync(source, 6, Key(), contextA, Ct)).Value;
        var b = (await engine.ForkAsync(source, 6, Key(), contextB, Ct)).Value;
        var zero = (await engine.ForkAsync(source, 0, Key(), contextA, Ct)).Value;
        Assert.NotEqual(a, b);

        // Same suffix inputs for both siblings, so only the child context can make them diverge.
        var suffix = BurstyTicks(7, 6, Key());
        await AppendAllAsync(engine, a, 6, suffix);
        await AppendAllAsync(engine, b, 6, suffix.Take(4).ToList());
        await AppendAllAsync(engine, zero, 0, BurstyTicks(1, 3, Key()));

        var prefix = ReferenceFold(Tick(), TickWorld.Initial(20), sourceContext, sourceSteps.Take(6));
        var atFork = new TickWorldCodec().Decode(prefix[^1].Bytes);
        var expectedA = prefix.Concat(ReferenceFold(Tick(), atFork, contextA, suffix).Skip(1)).ToList();
        var expectedB = prefix.Concat(ReferenceFold(Tick(), atFork, contextB, suffix.Take(4)).Skip(1)).ToList();
        Assert.NotEqual(expectedA[10].Hash, expectedB[10].Hash);

        // Each child persists only its own suffix rows plus one base checkpoint (and own interval checkpoints).
        Assert.Equal(6L, await CountAsync("steps", a));
        Assert.Equal(4L, await CountAsync("steps", b));
        Assert.Equal(3L, await CountAsync("checkpoints", a)); // base 6, 8, 12

        await db.RestartAsync();
        var cold = NewEngine(db.NewColdStore(), 64, Tick());
        var sourceHashes = new List<string>();
        for (var t = 0; t <= 10; t++)
        {
            sourceHashes.Add((await cold.RestoreAsync(source, t, Ct)).Value.StateHash);
        }

        for (var t = 0; t <= 12; t++)
        {
            var restored = (await cold.RestoreAsync(a, t, Ct)).Value;
            Assert.Equal(a, restored.Timeline);
            Assert.Equal(expectedA[t].Hash, restored.StateHash);
            if (t <= 6)
            {
                Assert.Equal(sourceHashes[t], restored.StateHash);
            }
        }

        for (var t = 0; t <= 10; t++)
        {
            Assert.Equal(expectedB[t].Hash, (await cold.RestoreAsync(b, t, Ct)).Value.StateHash);
        }

        Assert.Equal(sourceHashes[0], (await cold.RestoreAsync(zero, 0, Ct)).Value.StateHash);

        Assert.Equal(HistoryStatus.Verified, (await cold.CompleteAsync(a, 12, Ct)).Value.Status);
        Assert.Equal(HistoryStatus.Verified, (await cold.CompleteAsync(b, 10, Ct)).Value.Status);
        Assert.Equal(expectedA[12].Hash, (await cold.ReplayAsync(a, Ct)).Value.FinalStateHash);
        Assert.Equal(expectedB[10].Hash, (await cold.ReplayAsync(b, Ct)).Value.FinalStateHash);

        Assert.Equal(sourceBefore, await RowsAsync(source));
        Assert.Equal(sourceManifest, (await db.Store.GetHistoryAsync(source, Ct)).Value);
        Assert.Equal(10, (await cold.ReplayAsync(source, Ct)).Value.FinalPosition);
    }

    [Fact]
    public async Task Fork_key_is_idempotent_a_changed_context_conflicts_and_concurrent_forks_create_one_branch()
    {
        var engine = NewEngine(db.Store, 64, Tick());
        var source = await NewVerifiedSourceAsync(engine, NextContext(2), BurstyTicks(1, 5, Key()));
        var key = Key();

        var first = await engine.ForkAsync(source, 3, key, new TickContext(1), Ct);
        var retry = await engine.ForkAsync(source, 3, key, new TickContext(1), Ct);
        var changed = await engine.ForkAsync(source, 3, key, new TickContext(4), Ct);
        Assert.Equal(first.Value, retry.Value);
        Assert.Equal(TimelineErrorCode.Conflict, changed.Error.Code);

        // A retry after the child has advanced still returns the same identity.
        await AppendAllAsync(engine, first.Value, 3, BurstyTicks(4, 2, Key()));
        Assert.Equal(first.Value, (await engine.ForkAsync(source, 3, key, new TickContext(1), Ct)).Value);

        var concurrentKey = Key();
        var engines = Enumerable.Range(0, 8).Select(_ => NewEngine(db.NewColdStore(), 64, Tick())).ToList();
        var results = await Task.WhenAll(engines.Select(e => e.ForkAsync(source, 2, concurrentKey, new TickContext(7), Ct)));
        Assert.All(results, r => Assert.True(r.IsSuccess, r.ToString()));
        Assert.Single(results.Select(r => r.Value).Distinct());
        Assert.Equal(1L, await ScalarAsync<long>(db.Owner,
            $"SELECT count(*) FROM marrow_timeline.histories WHERE parent_timeline_id = @p AND operation_key = '{concurrentKey}'", source.Value));
    }

    [Fact]
    public async Task Terminal_and_out_of_range_positions_lifecycle_and_identity_errors_are_typed()
    {
        var engine = NewEngine(db.Store, 64, Tick());
        var sourceSteps = BurstyTicks(1, 4, Key());
        var source = await NewVerifiedSourceAsync(engine, NextContext(2), sourceSteps);

        // Core allows forking at the final position; the child continues at Last + 1.
        var terminal = (await engine.ForkAsync(source, 4, Key(), new TickContext(1), Ct)).Value;
        Assert.Equal(4, (await engine.RestoreAsync(terminal, 4, Ct)).Value.Position);
        Assert.Equal(TimelineErrorCode.InvalidPosition, (await engine.AppendAsync(terminal, 4, TickStep(6), Key(), Ct)).Error.Code);
        Assert.Equal(TimelineErrorCode.Conflict, (await engine.AppendAsync(terminal, 3, TickStep(4), Key(), Ct)).Error.Code);
        Assert.True((await engine.AppendAsync(terminal, 4, TickStep(5), Key(), Ct)).IsSuccess);
        Assert.Equal(TimelineErrorCode.InvalidPosition, (await engine.RestoreAsync(terminal, 6, Ct)).Error.Code);

        Assert.Equal(TimelineErrorCode.InvalidPosition, (await engine.ForkAsync(source, -1, Key(), new TickContext(1), Ct)).Error.Code);
        Assert.Equal(TimelineErrorCode.InvalidPosition, (await engine.ForkAsync(source, 5, Key(), new TickContext(1), Ct)).Error.Code);
        Assert.Equal(TimelineErrorCode.NotFound,
            (await engine.ForkAsync(new TimelineId(Guid.NewGuid()), 0, Key(), new TickContext(1), Ct)).Error.Code);
        Assert.Equal(TimelineErrorCode.Conflict, (await engine.ForkAsync(terminal, 2, Key(), new TickContext(1), Ct)).Error.Code); // Recording

        // The child's logical history includes the inherited prefix: a prefix InputId cannot be reused,
        // but an InputId the source used only after T can.
        var mid = (await engine.ForkAsync(source, 2, Key(), new TickContext(1), Ct)).Value;
        var prefixId = sourceSteps[0].Inputs[0].InputId;
        var laterId = sourceSteps[2].Inputs[0].InputId;
        Assert.Equal(TimelineErrorCode.Conflict,
            (await engine.AppendAsync(mid, 2, TickStep(3, (prefixId, 0, TickAction.Burst, "")), Key(), Ct)).Error.Code);
        Assert.True((await engine.AppendAsync(mid, 2, TickStep(3, (laterId, 0, TickAction.Burst, "")), Key(), Ct)).IsSuccess);

        // Fork of a completed child inside its inherited prefix resolves the original source segment.
        Assert.True((await engine.CompleteAsync(mid, 3, Ct)).IsSuccess);
        var grandchild = (await engine.ForkAsync(mid, 1, Key(), new TickContext(9), Ct)).Value;
        Assert.Equal((await engine.RestoreAsync(source, 1, Ct)).Value.StateHash, (await engine.RestoreAsync(grandchild, 1, Ct)).Value.StateHash);
        Assert.True((await engine.AppendAsync(grandchild, 1, TickStep(2), Key(), Ct)).IsSuccess);
        Assert.Equal(HistoryStatus.Verified, (await engine.CompleteAsync(grandchild, 2, Ct)).Value.Status);

        // Unverified source.
        var recording = (await engine.CreateSourceAsync(TickSource(NextContext(2)), Key(), Ct)).Value;
        Assert.Equal(TimelineErrorCode.Conflict, (await engine.ForkAsync(recording, 0, Key(), new TickContext(1), Ct)).Error.Code);
    }

    [Fact]
    public async Task Missing_reducer_or_corrupted_prefix_fails_closed_and_creates_no_branch()
    {
        var engine = NewEngine(db.Store, 64, Tick());
        var source = await NewVerifiedSourceAsync(engine, NextContext(2), BurstyTicks(1, 6, Key()));

        var onlyV8 = NewEngine(db.Store, 64, TickV8Components());
        Assert.Equal(TimelineErrorCode.IncompatibleVersion, (await onlyV8.ForkAsync(source, 3, Key(), new TickContext(1), Ct)).Error.Code);

        var wideContext = NewEngine(db.Store, 64, Tick(contextCodec: new WideTickContextCodec()));
        Assert.Equal(TimelineErrorCode.IncompatibleVersion, (await wideContext.ForkAsync(source, 3, Key(), new TickContext(1), Ct)).Error.Code);

        await TamperAsync(db.Owner,
            "UPDATE marrow_timeline.step_inputs SET payload = @bytes WHERE timeline_id = @id AND position = 3",
            ("bytes", new TickInputCodec().EncodeCanonical(new TickInput(TickAction.Collect, "forged"))), ("id", source.Value));
        Assert.True((await engine.ForkAsync(source, 2, Key(), new TickContext(1), Ct)).IsSuccess); // prefix before the damage
        Assert.Equal(TimelineErrorCode.CorruptHistory, (await engine.ForkAsync(source, 3, Key(), new TickContext(1), Ct)).Error.Code);
        Assert.Equal(TimelineErrorCode.CorruptHistory, (await engine.ForkAsync(source, 6, Key(), new TickContext(1), Ct)).Error.Code);

        Assert.Equal(1L, await ScalarAsync<long>(db.Owner,
            "SELECT count(*) FROM marrow_timeline.histories WHERE parent_timeline_id = @p", source.Value));
    }

    [Fact]
    public async Task Cancelled_fork_or_child_completion_leaves_nothing_verified_and_a_retry_succeeds()
    {
        var writer = NewEngine(db.Store, 1000, Tick());
        var source = await NewVerifiedSourceAsync(writer, NextContext(2), BurstyTicks(1, 8, Key()));
        var sourceBefore = await RowsAsync(source);
        var key = Key();

        using var forkCts = new CancellationTokenSource();
        var cancellingFork = NewEngine(db.Store, 1000, Tick(reducer: new CancellingTickReducer(forkCts, cancelAtPosition: 3)));
        Assert.Equal(TimelineErrorCode.Cancelled, (await cancellingFork.ForkAsync(source, 6, key, new TickContext(1), forkCts.Token)).Error.Code);
        Assert.Equal(0L, await ScalarAsync<long>(db.Owner,
            "SELECT count(*) FROM marrow_timeline.histories WHERE parent_timeline_id = @p", source.Value));

        var child = (await writer.ForkAsync(source, 6, key, new TickContext(1), Ct)).Value;
        await AppendAllAsync(writer, child, 6, BurstyTicks(7, 4, Key()));

        using var completeCts = new CancellationTokenSource();
        var cancellingComplete = NewEngine(db.Store, 1000, Tick(reducer: new CancellingTickReducer(completeCts, cancelAtPosition: 8)));
        Assert.Equal(TimelineErrorCode.Cancelled, (await cancellingComplete.CompleteAsync(child, 10, completeCts.Token)).Error.Code);
        Assert.Equal(HistoryStatus.Recording, (await db.Store.GetHistoryAsync(child, Ct)).Value.Manifest.Status);

        using var preCancelled = new CancellationTokenSource();
        await preCancelled.CancelAsync();
        Assert.Equal(TimelineErrorCode.Cancelled, (await writer.AppendAsync(child, 10, TickStep(11), Key(), preCancelled.Token)).Error.Code);
        Assert.Equal(TimelineErrorCode.Cancelled, (await writer.ForkAsync(source, 1, Key(), new TickContext(1), preCancelled.Token)).Error.Code);
        Assert.Equal(10, (await db.Store.GetHistoryAsync(child, Ct)).Value.Manifest.LastPosition);

        Assert.Equal(HistoryStatus.Verified, (await writer.CompleteAsync(child, 10, Ct)).Value.Status);
        Assert.Equal(sourceBefore, await RowsAsync(source));
    }

    [Fact]
    public async Task Aligned_compare_returns_verified_states_per_position_with_each_history_status()
    {
        var engine = NewEngine(db.Store, 3, Tick());
        var source = await NewVerifiedSourceAsync(engine, NextContext(3), BurstyTicks(1, 10, Key()));
        var child = (await engine.ForkAsync(source, 5, Key(), new TickContext(1), Ct)).Value;
        await AppendAllAsync(engine, child, 5, BurstyTicks(6, 7, Key()));
        var sibling = (await engine.ForkAsync(source, 5, Key(), new TickContext(6), Ct)).Value;
        await AppendAllAsync(engine, sibling, 5, BurstyTicks(6, 5, Key()));

        var aligned = (await engine.CompareAlignedAsync(source, child, [9, 0, 5, 6, 5], Ct)).Value;
        Assert.Equal([9L, 0L, 5L, 6L, 5L], aligned.Select(x => x.Position));
        foreach (var pair in aligned)
        {
            Assert.Equal(source, pair.Source.Timeline);
            Assert.Equal(child, pair.Alternative.Timeline);
            Assert.Equal(pair.Position, pair.Source.Position);
            Assert.Equal(pair.Position, pair.Alternative.Position);
            Assert.Equal(HistoryStatus.Verified, pair.Source.HistoryStatus);
            Assert.Equal(HistoryStatus.Recording, pair.Alternative.HistoryStatus); // a verified prefix only
            Assert.Equal((await engine.RestoreAsync(source, pair.Position, Ct)).Value.StateHash, pair.Source.StateHash);
            Assert.Equal((await engine.RestoreAsync(child, pair.Position, Ct)).Value.StateHash, pair.Alternative.StateHash);
            if (pair.Position <= 5)
            {
                Assert.Equal(pair.Source.StateHash, pair.Alternative.StateHash);
            }
        }

        Assert.NotEqual(aligned[0].Source.StateHash, aligned[0].Alternative.StateHash);

        var siblings = (await engine.CompareAlignedAsync(child, sibling, [10], Ct)).Value;
        Assert.NotEqual(siblings[0].Source.StateHash, siblings[0].Alternative.StateHash);
        Assert.Empty((await engine.CompareAlignedAsync(source, child, [], Ct)).Value);

        // Regression (FsCheck counterexample): siblings forked at 0 own position 0 but still share the source root.
        var zeroA = (await engine.ForkAsync(source, 0, Key(), new TickContext(1), Ct)).Value;
        var zeroB = (await engine.ForkAsync(source, 0, Key(), new TickContext(2), Ct)).Value;
        var atZero = (await engine.CompareAlignedAsync(zeroA, zeroB, [0], Ct)).Value;
        Assert.Equal(atZero[0].Source.StateHash, atZero[0].Alternative.StateHash);

        Assert.Equal(TimelineErrorCode.InvalidPosition, (await engine.CompareAlignedAsync(source, child, [3, 11], Ct)).Error.Code);
        Assert.Equal(TimelineErrorCode.InvalidPosition, (await engine.CompareAlignedAsync(source, child, [-1], Ct)).Error.Code);
        Assert.Equal(TimelineErrorCode.NotFound,
            (await engine.CompareAlignedAsync(source, new TimelineId(Guid.NewGuid()), [0], Ct)).Error.Code);
        var unrelated = await NewVerifiedSourceAsync(engine, NextContext(3), BurstyTicks(1, 2, Key()));
        Assert.Equal(TimelineErrorCode.Conflict, (await engine.CompareAlignedAsync(source, unrelated, [0], Ct)).Error.Code);

        using var preCancelled = new CancellationTokenSource();
        await preCancelled.CancelAsync();
        Assert.Equal(TimelineErrorCode.Cancelled, (await engine.CompareAlignedAsync(source, child, [1], preCancelled.Token)).Error.Code);
    }

    /// <summary>Source context (shared content-addressed artifacts are fine here: no test tampers with context rows).</summary>
    private static TickContext NextContext(int cooldown) => new(cooldown);

    private static CreateSourceRequest<TickWorld, TickContext> TickSource(TickContext context) =>
        new(Owner, TickV7, TickWorld.Initial(20), context, "rules-7", "seed-1");

    private async Task<TimelineId> NewVerifiedSourceAsync(
        PersistentTimelineEngine<TickWorld, TickInput, TickContext> engine, TickContext context, List<StepRecord<TickInput>> steps)
    {
        var id = (await engine.CreateSourceAsync(TickSource(context), Key(), Ct)).Value;
        await AppendAllAsync(engine, id, 0, steps);
        var completed = await engine.CompleteAsync(id, steps.Count, Ct);
        Assert.True(completed.IsSuccess, completed.ToString());
        return id;
    }

    private static async Task AppendAllAsync(
        PersistentTimelineEngine<TickWorld, TickInput, TickContext> engine, TimelineId id, long after, IReadOnlyList<StepRecord<TickInput>> steps)
    {
        for (var i = 0; i < steps.Count; i++)
        {
            var appended = await engine.AppendAsync(id, after + i, steps[i], Key(), Ct);
            Assert.True(appended.IsSuccess, appended.ToString());
        }
    }

    private Task<long> CountAsync(string table, TimelineId id) =>
        ScalarAsync<long>(db.Owner, $"SELECT count(*) FROM marrow_timeline.{table} WHERE timeline_id = @p", id.Value);

    private Task<string> RowsAsync(TimelineId id) => RowsDigestAsync(db.Owner, id);
}

internal static class RowDigest
{
    /// <summary>Byte-level digest of every immutable row a history owns (steps, inputs, checkpoints).</summary>
    public static async Task<string> RowsDigestAsync(NpgsqlDataSource owner, TimelineId id)
    {
        await using var cmd = owner.CreateCommand("""
            SELECT
              (SELECT count(*) || ':' || coalesce(md5(string_agg(position || ':' || state_hash || ':' || previous_record_hash || ':' || record_hash, ',' ORDER BY position)), '')
                 FROM marrow_timeline.steps WHERE timeline_id = @p)
              || '|' ||
              (SELECT count(*) || ':' || coalesce(md5(string_agg(position || ':' || input_id || ':' || ordinal || ':' || encode(payload, 'hex'), ',' ORDER BY position, input_index)), '')
                 FROM marrow_timeline.step_inputs WHERE timeline_id = @p)
              || '|' ||
              (SELECT count(*) || ':' || coalesce(md5(string_agg(position || ':' || state_hash || ':' || encode(state_bytes, 'hex'), ',' ORDER BY position)), '')
                 FROM marrow_timeline.checkpoints WHERE timeline_id = @p)
            """);
        cmd.Parameters.Add(new NpgsqlParameter("p", id.Value));
        return (string)(await cmd.ExecuteScalarAsync())!;
    }
}
