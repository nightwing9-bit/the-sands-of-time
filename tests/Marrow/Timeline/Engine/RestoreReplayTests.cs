using Marrow.Tests.TestDomains;
using Marrow.Tests.Timeline.Persistence;
using Marrow.Timeline;
using Marrow.Timeline.Internal;
using Xunit;
using static Marrow.Tests.Timeline.Engine.EngineTestKit;

namespace Marrow.Tests.Timeline.Engine;

/// <summary>
/// Task 2.3 restore/replay/hash behavior against real PostgreSQL: cold server restart with pinned historical
/// reducer and context, segment-specific context/reducer for a child suffix, missing/mismatched
/// codec/reducer/context, corrupted stored bytes and hashes, and cancellation
/// (R02.02, R02.03, R02.07, R02.08, R02.12, R02.13, R02.14).
/// </summary>
[Trait("Category", "Integration")]
public sealed class RestoreReplayTests(TimelinePostgresDatabase db) : IClassFixture<TimelinePostgresDatabase>
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static int s_contextSeed = 1000;

    [Fact]
    public async Task Position_zero_is_the_initial_state_and_bounds_and_identity_errors_are_typed()
    {
        var engine = NewEngine(db.Store, 64, Tick());
        var (id, _) = await NewTickSourceAsync(engine, steps: 3);

        var zero = (await engine.RestoreAsync(id, 0, Ct)).Value;
        Assert.Equal(0, zero.Position);
        Assert.Equal(new TickWorldCodec().EncodeCanonical(TickWorld.Initial(20)), new TickWorldCodec().EncodeCanonical(zero.State));
        Assert.Equal(Snapshot(Tick(), TickWorld.Initial(20)).Hash, zero.StateHash);
        Assert.Equal(StateHashOrigin.StoredCheckpoint, zero.HashOrigin);
        Assert.Equal(StateHashOrigin.ReconstructedFromVerifiedChain, (await engine.RestoreAsync(id, 3, Ct)).Value.HashOrigin);

        Assert.Equal(TimelineErrorCode.InvalidPosition, (await engine.RestoreAsync(id, -1, Ct)).Error.Code);
        Assert.Equal(TimelineErrorCode.InvalidPosition, (await engine.RestoreAsync(id, 4, Ct)).Error.Code);
        Assert.Equal(TimelineErrorCode.InvalidPosition, (await engine.RestoreAsync(id, long.MaxValue, Ct)).Error.Code);
        var unknown = new TimelineId(Guid.NewGuid());
        Assert.Equal(TimelineErrorCode.NotFound, (await engine.RestoreAsync(unknown, 0, Ct)).Error.Code);
        Assert.Equal(TimelineErrorCode.NotFound, (await engine.ReplayAsync(unknown, Ct)).Error.Code);
    }

    [Fact]
    public async Task Engine_append_maps_position_identity_and_retry_cases_to_typed_results()
    {
        var engine = NewEngine(db.Store, 64, Tick());
        var (id, _) = await NewTickSourceAsync(engine, steps: 1);
        var key = Key();
        var step2 = TickStep(2, ("r-1", 0, TickAction.Collect, "a"));

        var first = await engine.AppendAsync(id, 1, step2, key, Ct);
        var retry = await engine.AppendAsync(id, 1, step2, key, Ct);
        var changed = await engine.AppendAsync(id, 1, TickStep(2, ("r-1", 0, TickAction.Collect, "b")), key, Ct);
        var gap = await engine.AppendAsync(id, 2, TickStep(4), Key(), Ct);
        var stale = await engine.AppendAsync(id, 1, TickStep(2), Key(), Ct);
        var duplicate = await engine.AppendAsync(id, 2, TickStep(3, ("r-1", 0, TickAction.Burst, "")), Key(), Ct);

        Assert.Equal(first.Value, retry.Value);
        Assert.Equal(TimelineErrorCode.Conflict, changed.Error.Code);
        Assert.Equal(TimelineErrorCode.InvalidPosition, gap.Error.Code);
        Assert.Equal(TimelineErrorCode.Conflict, stale.Error.Code);
        Assert.Equal(TimelineErrorCode.Conflict, duplicate.Error.Code);
        Assert.Equal(2, (await db.Store.GetHistoryAsync(id, Ct)).Value.Manifest.LastPosition);
    }

    [Fact]
    public async Task Cold_server_restart_replays_with_the_pinned_v7_reducer_and_context_not_current_v8()
    {
        var context = NextContext();
        var steps = BurstyTicks(1, 12, "cold");
        var writer = NewEngine(db.Store, 5, Tick());
        var id = (await writer.CreateSourceAsync(TickSource(context), Key(), Ct)).Value;
        for (var i = 0; i < steps.Count; i++)
        {
            Assert.True((await writer.AppendAsync(id, i, steps[i], Key(), Ct)).IsSuccess);
        }

        var before = (await db.Store.GetHistoryAsync(id, Ct)).Value;
        var expected = ReferenceFold(Tick(), TickWorld.Initial(20), context, steps);

        await db.RestartAsync();

        // New process view: v8 is also registered (the "current" version); the history still pins v7.
        var cold = NewEngine(db.NewColdStore(), 64, TickV8Components(), Tick());
        Assert.Equal(before, (await db.Store.GetHistoryAsync(id, Ct)).Value);
        for (var t = 0; t <= steps.Count; t++)
        {
            Assert.Equal(expected[t].Hash, (await cold.RestoreAsync(id, t, Ct)).Value.StateHash);
        }

        var replay = (await cold.ReplayAsync(id, Ct)).Value;
        Assert.Equal(expected[^1].Bytes, new TickWorldCodec().EncodeCanonical(replay.FinalState));
        Assert.Equal(before.Manifest.LastRecordHash, replay.LastRecordHash);
        var v8Fold = ReferenceFold(TickV8Components(), TickWorld.Initial(20), context, steps);
        Assert.NotEqual(v8Fold[^1].Hash, replay.FinalStateHash); // proves the pin, not the newest reducer, was used

        // Only the newer version registered: fails closed, nothing changes.
        var onlyV8 = NewEngine(db.NewColdStore(), 64, TickV8Components());
        Assert.Equal(TimelineErrorCode.IncompatibleVersion, (await onlyV8.RestoreAsync(id, 3, Ct)).Error.Code);
        Assert.Equal(TimelineErrorCode.IncompatibleVersion, (await onlyV8.ReplayAsync(id, Ct)).Error.Code);
        Assert.Equal(TimelineErrorCode.IncompatibleVersion, (await onlyV8.AppendAsync(id, 12, TickStep(13), Key(), Ct)).Error.Code);
        Assert.Equal(TimelineErrorCode.IncompatibleVersion, (await onlyV8.CompleteAsync(id, 12, Ct)).Error.Code);
        Assert.Equal(before, (await db.Store.GetHistoryAsync(id, Ct)).Value);

        Assert.Equal(HistoryStatus.Verified, (await cold.CompleteAsync(id, 12, Ct)).Value.Status);
    }

    [Fact]
    public async Task Child_prefix_uses_ancestor_context_and_reducer_and_suffix_uses_its_own_after_cold_restart()
    {
        var sourceContext = NextContext();
        var childContext = new TickContext(1);
        var sourceSteps = BurstyTicks(1, 9, "src");
        var childSteps = BurstyTicks(7, 6, "kid");
        var engine = NewEngine(db.Store, 4, Tick(), TickV8Components());
        var source = (await engine.CreateSourceAsync(TickSource(sourceContext), Key(), Ct)).Value;
        for (var i = 0; i < sourceSteps.Count; i++)
        {
            Assert.True((await engine.AppendAsync(source, i, sourceSteps[i], Key(), Ct)).IsSuccess);
        }

        Assert.True((await engine.CompleteAsync(source, 9, Ct)).IsSuccess);
        var sourceBefore = (await db.Store.GetHistoryAsync(source, Ct)).Value;

        // Created through the store (not ForkAsync) because v1 ForkAsync keeps the source's version key, and this
        // test needs a child under a different reducer/context schema to prove per-segment resolution.
        var atFork = (await engine.RestoreAsync(source, 6, Ct)).Value;
        var child = (await db.Store.CreateHistoryAsync(new NewHistory(
            Owner, TickV8, new TickContextCodec().EncodeCanonical(childContext), "rules-8", "seed-1",
            new TickWorldCodec().EncodeCanonical(atFork.State), Key(), new ForkOrigin(source, 6)), Ct)).Value.Manifest.Id;
        for (var i = 0; i < childSteps.Count; i++)
        {
            Assert.True((await engine.AppendAsync(child, 6 + i, childSteps[i], Key(), Ct)).IsSuccess);
        }

        var prefix = ReferenceFold(Tick(), TickWorld.Initial(20), sourceContext, sourceSteps.Take(6));
        var suffix = ReferenceFold(TickV8Components(), new TickWorldCodec().Decode(prefix[^1].Bytes), childContext, childSteps);
        var expected = prefix.Concat(suffix.Skip(1)).ToList();

        var cold = NewEngine(db.NewColdStore(), 64, TickV8Components(), Tick());
        for (var t = 0; t <= 12; t++)
        {
            var restored = (await cold.RestoreAsync(child, t, Ct)).Value;
            Assert.Equal(expected[t].Hash, restored.StateHash);
            Assert.Equal(child, restored.Timeline);
            if (t <= 6)
            {
                Assert.Equal((await cold.RestoreAsync(source, t, Ct)).Value.StateHash, restored.StateHash);
            }
        }

        var replay = (await cold.ReplayAsync(child, Ct)).Value;
        Assert.Equal(12, replay.FinalPosition);
        Assert.Equal(expected[12].Hash, replay.FinalStateHash);
        Assert.Equal(HistoryStatus.Recording, replay.Status);
        Assert.Equal(sourceBefore, (await db.Store.GetHistoryAsync(source, Ct)).Value);
        Assert.Equal(9, (await cold.ReplayAsync(source, Ct)).Value.FinalPosition);

        // The child's own suffix cannot be read without its own reducer version, even though the prefix can.
        var v7Only = NewEngine(db.NewColdStore(), 64, Tick());
        Assert.True((await v7Only.RestoreAsync(child, 5, Ct)).IsSuccess);
        Assert.Equal(TimelineErrorCode.IncompatibleVersion, (await v7Only.RestoreAsync(child, 7, Ct)).Error.Code);
        Assert.Equal(TimelineErrorCode.IncompatibleVersion, (await v7Only.ReplayAsync(child, Ct)).Error.Code);
        Assert.Equal(HistoryStatus.Verified, (await cold.CompleteAsync(child, 12, Ct)).Value.Status);
    }

    [Fact]
    public async Task Missing_or_mismatched_codec_or_context_is_IncompatibleVersion_and_a_changed_reducer_is_CorruptHistory()
    {
        var engine = NewEngine(db.Store, 3, Tick());
        var (id, _) = await NewTickSourceAsync(engine, steps: 6);

        var taggedInputs = NewEngine(db.Store, 3, Tick(inputCodec: new TaggedTickInputCodec()));
        Assert.True((await taggedInputs.RestoreAsync(id, 0, Ct)).IsSuccess); // no input is decoded at the base
        Assert.Equal(TimelineErrorCode.IncompatibleVersion, (await taggedInputs.RestoreAsync(id, 5, Ct)).Error.Code);
        Assert.Equal(TimelineErrorCode.IncompatibleVersion, (await taggedInputs.ReplayAsync(id, Ct)).Error.Code);

        var wideContext = NewEngine(db.Store, 3, Tick(contextCodec: new WideTickContextCodec()));
        Assert.Equal(TimelineErrorCode.IncompatibleVersion, (await wideContext.RestoreAsync(id, 0, Ct)).Error.Code);

        var lossyState = NewEngine(db.Store, 3, Tick(stateCodec: new LossyTickWorldCodec()));
        Assert.Equal(TimelineErrorCode.IncompatibleVersion, (await lossyState.RestoreAsync(id, 3, Ct)).Error.Code);

        var changedReducer = NewEngine(db.Store, 3, Tick(reducer: new TickReducerV8()));
        Assert.Equal(TimelineErrorCode.CorruptHistory, (await changedReducer.RestoreAsync(id, 2, Ct)).Error.Code);

        // The pinned context artifact itself disappears.
        var manifest = (await db.Store.GetHistoryAsync(id, Ct)).Value.Manifest;
        await TamperAsync(db.Owner, "DELETE FROM marrow_timeline.context_artifacts WHERE context_ref = @ref", ("ref", manifest.ContextArtifactRef));
        Assert.Equal(TimelineErrorCode.IncompatibleVersion, (await engine.RestoreAsync(id, 0, Ct)).Error.Code);
        Assert.Equal(TimelineErrorCode.IncompatibleVersion, (await engine.ReplayAsync(id, Ct)).Error.Code);
        Assert.Equal(HistoryStatus.Recording, (await db.Store.GetHistoryAsync(id, Ct)).Value.Manifest.Status);
    }

    [Fact]
    public async Task Corrupted_input_bytes_fail_restore_through_that_step_and_completion_marks_the_history_Failed()
    {
        var engine = NewEngine(db.Store, 64, Tick());
        var (id, _) = await NewTickSourceAsync(engine, steps: 6);
        await TamperAsync(db.Owner,
            "UPDATE marrow_timeline.step_inputs SET payload = @bytes WHERE timeline_id = @id AND position = 3",
            ("bytes", new TickInputCodec().EncodeCanonical(new TickInput(TickAction.Collect, "forged"))), ("id", id.Value));

        Assert.True((await engine.RestoreAsync(id, 2, Ct)).IsSuccess);
        Assert.Equal(TimelineErrorCode.CorruptHistory, (await engine.RestoreAsync(id, 3, Ct)).Error.Code);
        Assert.Equal(TimelineErrorCode.CorruptHistory, (await engine.RestoreAsync(id, 6, Ct)).Error.Code);
        Assert.Equal(TimelineErrorCode.CorruptHistory, (await engine.ReplayAsync(id, Ct)).Error.Code);
        var steps = await ScalarAsync<long>(db.Owner, "SELECT count(*) FROM marrow_timeline.steps WHERE timeline_id = @p", id.Value);

        Assert.Equal(TimelineErrorCode.CorruptHistory, (await engine.CompleteAsync(id, 6, Ct)).Error.Code);
        var after = (await db.Store.GetHistoryAsync(id, Ct)).Value;
        Assert.Equal(HistoryStatus.Failed, after.Manifest.Status);
        Assert.Contains("Completion replay failed", after.FailureReason, StringComparison.Ordinal);
        Assert.Equal(steps, await ScalarAsync<long>(db.Owner, "SELECT count(*) FROM marrow_timeline.steps WHERE timeline_id = @p", id.Value));
        Assert.Equal(TimelineErrorCode.Conflict, (await engine.CompleteAsync(id, 6, Ct)).Error.Code);
    }

    [Fact]
    public async Task A_rehashed_forged_checkpoint_a_changed_step_hash_and_a_swapped_context_pin_are_CorruptHistory()
    {
        var engine = NewEngine(db.Store, 2, Tick());

        // Checkpoint at 4 replaced by different valid state bytes with a matching checkpoint hash: only the
        // chain-protected step hash reveals the forgery.
        var (forged, _) = await NewTickSourceAsync(engine, steps: 6);
        var fake = new TickWorldCodec().EncodeCanonical(TickWorld.Initial(99));
        await TamperAsync(db.Owner,
            "UPDATE marrow_timeline.checkpoints SET state_bytes = @bytes, state_hash = @hash WHERE timeline_id = @id AND position = 4",
            ("bytes", fake), ("hash", TimelineHashing.StateHash(TickV7.StateSchemaVersion, fake)), ("id", forged.Value));
        Assert.True((await engine.RestoreAsync(forged, 3, Ct)).IsSuccess);
        Assert.Equal(TimelineErrorCode.CorruptHistory, (await engine.RestoreAsync(forged, 4, Ct)).Error.Code);
        Assert.Equal(TimelineErrorCode.CorruptHistory, (await engine.RestoreAsync(forged, 5, Ct)).Error.Code);

        // A step's recorded state hash rewritten: its record hash no longer matches its envelope.
        var (rehashed, _) = await NewTickSourceAsync(engine, steps: 6);
        await TamperAsync(db.Owner,
            "UPDATE marrow_timeline.steps SET state_hash = @hash WHERE timeline_id = @id AND position = 5",
            ("hash", new string('a', 64)), ("id", rehashed.Value));
        Assert.Equal(TimelineErrorCode.CorruptHistory, (await engine.RestoreAsync(rehashed, 5, Ct)).Error.Code);
        Assert.Equal(TimelineErrorCode.CorruptHistory, (await engine.ReplayAsync(rehashed, Ct)).Error.Code);

        // Manifest pin swapped to another valid artifact: the genesis record binds the original context hash.
        var (swapped, _) = await NewTickSourceAsync(engine, steps: 2);
        var (other, _) = await NewTickSourceAsync(engine, steps: 0);
        var otherManifest = (await db.Store.GetHistoryAsync(other, Ct)).Value.Manifest;
        await TamperAsync(db.Owner,
            "UPDATE marrow_timeline.histories SET context_ref = @ref, context_hash = @hash WHERE timeline_id = @id",
            ("ref", otherManifest.ContextArtifactRef), ("hash", otherManifest.ContextHash), ("id", swapped.Value));
        Assert.Equal(TimelineErrorCode.CorruptHistory, (await engine.RestoreAsync(swapped, 0, Ct)).Error.Code);
        Assert.Equal(TimelineErrorCode.CorruptHistory, (await engine.ReplayAsync(swapped, Ct)).Error.Code);
    }

    [Fact]
    public async Task Cancellation_during_the_fold_returns_Cancelled_and_never_marks_Verified()
    {
        var writer = NewEngine(db.Store, 1000, Tick());
        var (id, _) = await NewTickSourceAsync(writer, steps: 10);

        using var restoreCts = new CancellationTokenSource();
        var restoreReducer = new CancellingTickReducer(restoreCts, cancelAtPosition: 4);
        var restoring = NewEngine(db.Store, 1000, Tick(reducer: restoreReducer));
        Assert.Equal(TimelineErrorCode.Cancelled, (await restoring.RestoreAsync(id, 10, restoreCts.Token)).Error.Code);
        Assert.Equal(4, restoreReducer.Calls); // observed before the very next step

        using var completeCts = new CancellationTokenSource();
        var completing = NewEngine(db.Store, 1000, Tick(reducer: new CancellingTickReducer(completeCts, cancelAtPosition: 7)));
        Assert.Equal(TimelineErrorCode.Cancelled, (await completing.CompleteAsync(id, 10, completeCts.Token)).Error.Code);
        Assert.Equal(HistoryStatus.Recording, (await db.Store.GetHistoryAsync(id, Ct)).Value.Manifest.Status);

        using var preCancelled = new CancellationTokenSource();
        await preCancelled.CancelAsync();
        Assert.Equal(TimelineErrorCode.Cancelled, (await writer.ReplayAsync(id, preCancelled.Token)).Error.Code);
        Assert.Equal(TimelineErrorCode.Cancelled, (await writer.AppendAsync(id, 10, TickStep(11), Key(), preCancelled.Token)).Error.Code);
        Assert.Equal(10, (await db.Store.GetHistoryAsync(id, Ct)).Value.Manifest.LastPosition);

        Assert.Equal(HistoryStatus.Verified, (await writer.CompleteAsync(id, 10, Ct)).Value.Status);
    }

    /// <summary>Distinct cooldowns give each source its own content-addressed context artifact.</summary>
    private static TickContext NextContext() => new(Interlocked.Increment(ref s_contextSeed));

    private static CreateSourceRequest<TickWorld, TickContext> TickSource(TickContext context) =>
        new(Owner, TickV7, TickWorld.Initial(20), context, "rules-7", "seed-1");

    /// <summary>A source with a unique pinned context (so tampering one artifact never touches another history).</summary>
    private async Task<(TimelineId Id, List<StepRecord<TickInput>> Steps)> NewTickSourceAsync(
        PersistentTimelineEngine<TickWorld, TickInput, TickContext> engine, int steps)
    {
        var id = (await engine.CreateSourceAsync(TickSource(NextContext()), Key(), Ct)).Value;
        var records = BurstyTicks(1, steps, Key());
        for (var i = 0; i < records.Count; i++)
        {
            var appended = await engine.AppendAsync(id, i, records[i], Key(), Ct);
            Assert.True(appended.IsSuccess, appended.ToString());
        }

        return (id, records);
    }
}
