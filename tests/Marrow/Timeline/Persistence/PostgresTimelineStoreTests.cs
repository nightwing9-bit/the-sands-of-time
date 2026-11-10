using Marrow.Timeline;
using Marrow.Timeline.Internal;
using Npgsql;
using Xunit;
using static Marrow.Tests.Timeline.Persistence.StoreData;

namespace Marrow.Tests.Timeline.Persistence;

/// <summary>
/// Task 2.2 proof against real PostgreSQL: atomic create at position 0, append-only contiguous steps,
/// idempotency/conflicts, concurrent append, controlled status transitions, fork storage, and
/// UPDATE/DELETE/TRUNCATE denial for immutable history (R02.01, R02.04-R02.08, R02.11, R02.14, R02.15).
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgresTimelineStoreTests(TimelinePostgresDatabase db) : IClassFixture<TimelinePostgresDatabase>
{
    private ITimelineStore Store => db.Store;

    [Fact]
    public async Task Migration_is_applied_by_the_spec01_runner_and_a_rerun_is_a_noop()
    {
        Assert.Contains("module marrow_timeline: applying 0001_timeline_journal", db.FirstMigrationOutput, StringComparison.Ordinal);
        Assert.Contains("done: 1 applied, 0 already applied", db.FirstMigrationOutput, StringComparison.Ordinal);

        var rerun = await db.RunMigratorAsync();

        Assert.Contains("done: 0 applied, 1 already applied", rerun, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_commits_manifest_base_checkpoint_and_pinned_context_at_position_0()
    {
        var created = (await Store.CreateHistoryAsync(Source(context: 70), Ct)).Value;
        var m = created.Manifest;

        Assert.Equal(0, m.LastPosition);
        Assert.Equal(0, created.BasePosition);
        Assert.Equal(HistoryStatus.Recording, m.Status);
        Assert.Null(m.ParentId);
        Assert.Equal(StateHash(0), m.LatestStateHash);
        Assert.Equal(created.BaseRecordHash, m.LastRecordHash);
        Assert.Equal(TimelineHashing.ContextArtifactRef(m.ContextHash), m.ContextArtifactRef);
        Assert.Equal(V1, m.Versions);

        var checkpoint = (await Store.ReadNearestCheckpointAsync(m.Id, 0, Ct)).Value;
        Assert.Equal(0, checkpoint.Position);
        Assert.Equal(State(0), checkpoint.CanonicalState);
        var context = (await Store.ReadContextAsync(m.ContextArtifactRef, Ct)).Value;
        Assert.Equal(Context(70), context.CanonicalContext);
        Assert.Equal(V1.ContextSchemaVersion, context.ContextSchemaVersion);
        Assert.Equal(m, (await Store.GetHistoryAsync(m.Id, Ct)).Value.Manifest);
    }

    [Fact]
    public async Task Create_is_idempotent_by_operation_key_and_rejects_a_changed_request()
    {
        var key = Key();
        var first = (await Store.CreateHistoryAsync(Source(key), Ct)).Value;

        var retry = await Store.CreateHistoryAsync(Source(key), Ct);
        var changed = await Store.CreateHistoryAsync(Source(key, context: 99), Ct);
        var otherOwner = await Store.CreateHistoryAsync(Source(key, owner: "owner-b"), Ct);

        Assert.Equal(first.Manifest.Id, retry.Value.Manifest.Id);
        Assert.Equal(TimelineErrorCode.Conflict, changed.Error.Code);
        Assert.NotEqual(first.Manifest.Id, otherOwner.Value.Manifest.Id);
    }

    [Fact]
    public async Task Concurrent_creates_with_one_operation_key_yield_one_history()
    {
        var key = Key();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => Store.CreateHistoryAsync(Source(key), Ct))));

        Assert.All(results, r => Assert.True(r.IsSuccess, r.ToString()));
        Assert.Single(results.Select(r => r.Value.Manifest.Id).Distinct());
        Assert.Equal(1L, await ScalarAsync<long>("SELECT count(*) FROM marrow_timeline.histories WHERE operation_key = @p", key));
    }

    [Fact]
    public async Task Append_accepts_only_the_contiguous_next_position()
    {
        var id = await NewSourceAsync();

        var gap = await Store.AppendStepAsync(StepAt(id, 0, 2), Ct);
        var zero = await Store.AppendStepAsync(StepAt(id, 0, 0), Ct);
        var first = await Store.AppendStepAsync(Step(id, 0), Ct); // zero-input (empty tick) step
        var stale = await Store.AppendStepAsync(Step(id, 0), Ct);
        var repeat = await Store.AppendStepAsync(StepAt(id, 1, 1), Ct);
        var ahead = await Store.AppendStepAsync(Step(id, 5), Ct);
        var missing = await Store.AppendStepAsync(Step(new TimelineId(Guid.NewGuid()), 0), Ct);

        Assert.Equal(TimelineErrorCode.InvalidPosition, gap.Error.Code);
        Assert.Equal(TimelineErrorCode.InvalidPosition, zero.Error.Code);
        Assert.Equal(new AppendReceipt(id, 1, StateHash(1), first.Value.RecordHash), first.Value);
        Assert.Equal(TimelineErrorCode.Conflict, stale.Error.Code);
        Assert.Equal(TimelineErrorCode.InvalidPosition, repeat.Error.Code);
        Assert.Equal(TimelineErrorCode.Conflict, ahead.Error.Code);
        Assert.Equal(TimelineErrorCode.NotFound, missing.Error.Code);

        var m = (await Store.GetHistoryAsync(id, Ct)).Value.Manifest;
        Assert.Equal(1, m.LastPosition);
        Assert.Equal(first.Value.RecordHash, m.LastRecordHash);
        Assert.Equal(1L, await ScalarAsync<long>("SELECT count(*) FROM marrow_timeline.steps WHERE timeline_id = @p", id.Value));
    }

    [Fact]
    public async Task Duplicate_or_conflicting_input_identity_is_rejected_and_same_key_retry_is_idempotent()
    {
        var id = await NewSourceAsync();
        var key = Key();
        var first = await Store.AppendStepAsync(Step(id, 0, key, ("a", 1), ("b", 1)), Ct);

        var retry = await Store.AppendStepAsync(Step(id, 0, key, ("b", 1), ("a", 1)), Ct); // same canonical content
        var changedRetry = await Store.AppendStepAsync(Step(id, 0, key, ("a", 1), ("c", 1)), Ct);
        var withinStep = await Store.AppendStepAsync(Step(id, 1, null, ("x", 1), ("x", 2)), Ct);
        var acrossSteps = await Store.AppendStepAsync(Step(id, 1, null, ("a", 3)), Ct);

        Assert.Equal(first.Value, retry.Value);
        Assert.Equal(TimelineErrorCode.Conflict, changedRetry.Error.Code);
        Assert.Equal(TimelineErrorCode.Conflict, withinStep.Error.Code);
        Assert.Equal(TimelineErrorCode.Conflict, acrossSteps.Error.Code);
        Assert.Equal(1, (await Store.GetHistoryAsync(id, Ct)).Value.Manifest.LastPosition);
    }

    [Fact]
    public async Task Inputs_are_persisted_in_ordinal_then_input_id_order_with_exact_payloads()
    {
        var id = await NewSourceAsync();
        await Store.AppendStepAsync(Step(id, 0, null, ("z", 1), ("b", 2), ("a", 1)), Ct);
        await Store.AppendStepAsync(Step(id, 1), Ct);

        var steps = (await Store.ReadStepsAsync(id, 0, 2, Ct)).Value;

        Assert.Equal(new long[] { 1, 2 }, steps.Select(s => s.Position));
        Assert.Equal(new[] { "a", "z", "b" }, steps[0].Inputs.Select(i => i.InputId));
        Assert.Equal(Payload("z"), steps[0].Inputs[1].Payload);
        Assert.Empty(steps[1].Inputs);
        Assert.Equal(steps[0].RecordHash, steps[1].PreviousRecordHash);
        Assert.Null(TimelineHashing.VerifyChain(0, (await Store.GetHistoryAsync(id, Ct)).Value.BaseRecordHash, steps));
        Assert.Equal(TimelineErrorCode.InvalidPosition, (await Store.ReadStepsAsync(id, 0, 3, Ct)).Error.Code);
    }

    [Fact]
    public async Task Concurrent_appends_at_the_same_position_commit_exactly_one()
    {
        var id = await NewSourceAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(i =>
            Task.Run(() => Store.AppendStepAsync(Step(id, 0, null, ($"input-{i}", 1)), Ct))));

        Assert.Single(results, r => r.IsSuccess);
        Assert.All(results.Where(r => r.IsFailure), r => Assert.Equal(TimelineErrorCode.Conflict, r.Error.Code));
        Assert.Equal(1L, await ScalarAsync<long>("SELECT count(*) FROM marrow_timeline.steps WHERE timeline_id = @p", id.Value));
        Assert.Equal(1L, await ScalarAsync<long>("SELECT count(*) FROM marrow_timeline.step_inputs WHERE timeline_id = @p", id.Value));
        Assert.Equal(1, (await Store.GetHistoryAsync(id, Ct)).Value.Manifest.LastPosition);
    }

    [Fact]
    public async Task Append_checkpoint_is_stored_only_when_it_matches_the_step_state_hash()
    {
        var id = await NewSourceAsync();
        var mismatched = await Store.AppendStepAsync(Step(id, 0) with { CheckpointState = State(42) }, Ct);
        var stored = await Store.AppendStepAsync(Step(id, 0) with { CheckpointState = State(1) }, Ct);
        await Store.AppendStepAsync(Step(id, 1), Ct);

        Assert.Equal(TimelineErrorCode.CorruptHistory, mismatched.Error.Code);
        Assert.True(stored.IsSuccess);
        var nearest = (await Store.ReadNearestCheckpointAsync(id, 2, Ct)).Value;
        Assert.Equal(1, nearest.Position);
        Assert.Equal(State(1), nearest.CanonicalState);
    }

    [Fact]
    public async Task Completion_verifies_the_stored_chain_and_moves_Recording_to_Verified_once()
    {
        var id = await NewSourceAsync();
        for (var p = 0; p < 3; p++)
        {
            await Store.AppendStepAsync(Step(id, p, null, ($"in-{p}", 0)), Ct);
        }

        var wrongHash = await Store.MarkVerifiedAsync(id, 3, StateHash(99), Ct);
        var stale = await Store.MarkVerifiedAsync(id, 2, StateHash(3), Ct);
        var verified = await Store.MarkVerifiedAsync(id, 3, StateHash(3), Ct);
        var retry = await Store.MarkVerifiedAsync(id, 3, StateHash(3), Ct);
        var appendAfter = await Store.AppendStepAsync(Step(id, 3), Ct);

        Assert.Equal(TimelineErrorCode.CorruptHistory, wrongHash.Error.Code);
        Assert.Equal(TimelineErrorCode.Conflict, stale.Error.Code);
        Assert.Equal(HistoryStatus.Verified, verified.Value.Status);
        Assert.Equal(3, verified.Value.FinalPosition);
        Assert.Equal(verified.Value, retry.Value);
        Assert.Equal(TimelineErrorCode.Conflict, appendAfter.Error.Code);
        Assert.Equal(HistoryStatus.Verified, (await Store.GetHistoryAsync(id, Ct)).Value.Manifest.Status);
    }

    [Fact]
    public async Task Failed_is_terminal_auditable_and_retains_history_rows()
    {
        var id = await NewSourceAsync();
        await Store.AppendStepAsync(Step(id, 0), Ct);

        var failed = (await Store.MarkFailedAsync(id, "replay diverged at 1", Ct)).Value;
        var again = (await Store.MarkFailedAsync(id, "other reason", Ct)).Value;

        Assert.Equal(HistoryStatus.Failed, failed.Manifest.Status);
        Assert.Equal("replay diverged at 1", again.FailureReason);
        Assert.Equal(TimelineErrorCode.Conflict, (await Store.AppendStepAsync(Step(id, 1), Ct)).Error.Code);
        Assert.Equal(TimelineErrorCode.Conflict, (await Store.MarkVerifiedAsync(id, 1, StateHash(1), Ct)).Error.Code);
        Assert.Equal(1L, await ScalarAsync<long>("SELECT count(*) FROM marrow_timeline.steps WHERE timeline_id = @p", id.Value));
    }

    public static TheoryData<string> ImmutableStatements =>
    [
        "UPDATE marrow_timeline.steps SET state_hash = record_hash WHERE timeline_id = @p",
        "DELETE FROM marrow_timeline.steps WHERE timeline_id = @p",
        "UPDATE marrow_timeline.step_inputs SET payload = '\\x00' WHERE timeline_id = @p",
        "DELETE FROM marrow_timeline.step_inputs WHERE timeline_id = @p",
        "UPDATE marrow_timeline.checkpoints SET state_bytes = '\\x00' WHERE timeline_id = @p",
        "DELETE FROM marrow_timeline.checkpoints WHERE timeline_id = @p",
        "UPDATE marrow_timeline.context_artifacts SET canonical_bytes = '\\x00' WHERE context_ref = (SELECT context_ref FROM marrow_timeline.histories WHERE timeline_id = @p)",
        "DELETE FROM marrow_timeline.histories WHERE timeline_id = @p",
        "UPDATE marrow_timeline.histories SET owner_id = 'attacker' WHERE timeline_id = @p",
        "UPDATE marrow_timeline.histories SET context_ref = context_ref, rules_hash = 'other' WHERE timeline_id = @p",
        "TRUNCATE marrow_timeline.steps CASCADE",
    ];

    [Theory]
    [MemberData(nameof(ImmutableStatements))]
    public async Task Runtime_role_is_denied_UPDATE_DELETE_and_TRUNCATE_of_history(string sql)
    {
        var id = await NewSourceWithStepAsync();

        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(db.App, sql, id.Value));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    [Theory]
    [MemberData(nameof(ImmutableStatements))]
    public async Task Even_the_schema_owner_cannot_rewrite_history(string sql)
    {
        var id = await NewSourceWithStepAsync();

        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(db.Owner, sql, id.Value));

        Assert.Equal("MT001", ex.SqlState);
        Assert.Equal(1L, await ScalarAsync<long>("SELECT count(*) FROM marrow_timeline.steps WHERE timeline_id = @p", id.Value));
    }

    [Theory]
    [InlineData("UPDATE marrow_timeline.histories SET last_position = 0, latest_state_hash = base_state_hash, last_record_hash = base_record_hash WHERE timeline_id = @p")]
    [InlineData("UPDATE marrow_timeline.histories SET last_position = 2 WHERE timeline_id = @p")]
    [InlineData("UPDATE marrow_timeline.histories SET status = 'Recording' WHERE timeline_id = @p")]
    [InlineData("UPDATE marrow_timeline.histories SET failure_reason = 'x' WHERE timeline_id = @p")]
    public async Task Runtime_lifecycle_updates_are_limited_to_guarded_transitions(string sql)
    {
        var id = await NewSourceWithStepAsync();
        await Store.MarkVerifiedAsync(id, 1, StateHash(1), Ct);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(db.App, sql, id.Value));

        Assert.Equal("MT001", ex.SqlState);
    }

    [Fact]
    public async Task Corrupted_stored_bytes_are_reported_instead_of_trusted()
    {
        var created = (await Store.CreateHistoryAsync(Source(context: Random.Shared.Next(1_000_000, int.MaxValue)), Ct)).Value;
        var id = created.Manifest.Id;
        await Store.AppendStepAsync(Step(id, 0, null, ("payload", 1)), Ct);

        // Simulate storage-level corruption: a superuser session with triggers disabled.
        await ExecuteAsync(db.Owner, """
            SET session_replication_role = replica;
            UPDATE marrow_timeline.checkpoints SET state_bytes = '\x01' WHERE timeline_id = @p;
            UPDATE marrow_timeline.step_inputs SET payload = '\x02' WHERE timeline_id = @p;
            UPDATE marrow_timeline.context_artifacts SET canonical_bytes = '\x03'
              WHERE context_ref = (SELECT context_ref FROM marrow_timeline.histories WHERE timeline_id = @p);
            SET session_replication_role = origin;
            """, id.Value);

        Assert.Equal(TimelineErrorCode.CorruptHistory, (await Store.ReadNearestCheckpointAsync(id, 1, Ct)).Error.Code);
        Assert.Equal(TimelineErrorCode.CorruptHistory, (await Store.ReadContextAsync(created.Manifest.ContextArtifactRef, Ct)).Error.Code);
        Assert.Equal(TimelineErrorCode.CorruptHistory, (await Store.MarkVerifiedAsync(id, 1, StateHash(1), Ct)).Error.Code);
        Assert.Equal(TimelineErrorCode.IncompatibleVersion,
            (await Store.ReadContextAsync(TimelineHashing.ContextArtifactRef(TimelineHashing.GenesisAnchor), Ct)).Error.Code);
        Assert.Equal(HistoryStatus.Recording, (await Store.GetHistoryAsync(id, Ct)).Value.Manifest.Status);
    }

    [Fact]
    public async Task Fork_child_shares_the_verified_prefix_and_owns_only_steps_after_T()
    {
        var source = await NewSourceAsync();
        for (var p = 0; p < 3; p++)
        {
            await Store.AppendStepAsync(Step(source, p, null, ($"src-{p}", 0)), Ct);
        }

        var recordingFork = await Store.CreateHistoryAsync(Fork(source, 1), Ct);
        await Store.MarkVerifiedAsync(source, 3, StateHash(3), Ct);
        var sourceBefore = (await Store.GetHistoryAsync(source, Ct)).Value;
        var sourceSteps = (await Store.ReadStepsAsync(source, 0, 3, Ct)).Value;

        var key = Key();
        var child = (await Store.CreateHistoryAsync(Fork(source, 2, key), Ct)).Value;
        var retry = await Store.CreateHistoryAsync(Fork(source, 2, key), Ct);
        var changed = await Store.CreateHistoryAsync(Fork(source, 2, key, context: 9), Ct);
        var wrongState = await Store.CreateHistoryAsync(Fork(source, 2, baseState: 5), Ct);
        var beyond = await Store.CreateHistoryAsync(Fork(source, 4), Ct);
        var atZero = await Store.CreateHistoryAsync(Fork(source, 0), Ct);

        Assert.Equal(TimelineErrorCode.Conflict, recordingFork.Error.Code);
        Assert.Equal(child.Manifest.Id, retry.Value.Manifest.Id);
        Assert.Equal(TimelineErrorCode.Conflict, changed.Error.Code);
        Assert.Equal(TimelineErrorCode.CorruptHistory, wrongState.Error.Code);
        Assert.Equal(TimelineErrorCode.InvalidPosition, beyond.Error.Code);
        Assert.True(atZero.IsSuccess, atZero.ToString());

        var c = child.Manifest;
        Assert.Equal((source, 2L, 2L, HistoryStatus.Recording), (c.ParentId!.Value, c.ForkPosition!.Value, c.LastPosition, c.Status));
        Assert.Equal(StateHash(2), c.LatestStateHash);
        Assert.Equal(TimelineErrorCode.InvalidPosition, (await Store.ReadStepsAsync(c.Id, 1, 2, Ct)).Error.Code);

        var childStep = await Store.AppendStepAsync(Step(c.Id, 2, null, ("child-3", 0)), Ct);
        Assert.Equal(3, childStep.Value.Position);
        Assert.Equal(TimelineErrorCode.Conflict, (await Store.AppendStepAsync(Step(source, 3), Ct)).Error.Code);

        Assert.Equal(sourceBefore, (await Store.GetHistoryAsync(source, Ct)).Value);
        Assert.Equal(sourceSteps.Select(s => s.RecordHash), (await Store.ReadStepsAsync(source, 0, 3, Ct)).Value.Select(s => s.RecordHash));
        Assert.Equal(3L, await ScalarAsync<long>("SELECT min(position) FROM marrow_timeline.steps WHERE timeline_id = @p", c.Id.Value));
        Assert.Equal(1L, await ScalarAsync<long>("SELECT count(*) FROM marrow_timeline.steps WHERE timeline_id = @p", c.Id.Value));
        Assert.Equal(HistoryStatus.Verified, (await Store.MarkVerifiedAsync(c.Id, 3, StateHash(3), Ct)).Value.Status);
    }

    [Fact]
    public async Task Cancelled_operations_commit_nothing()
    {
        var id = await NewSourceAsync();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var append = await Store.AppendStepAsync(Step(id, 0), cts.Token);
        var create = await Store.CreateHistoryAsync(Source(), cts.Token);

        Assert.Equal(TimelineErrorCode.Cancelled, append.Error.Code);
        Assert.Equal(TimelineErrorCode.Cancelled, create.Error.Code);
        Assert.Equal(0, (await Store.GetHistoryAsync(id, Ct)).Value.Manifest.LastPosition);
    }

    private static CancellationToken Ct => CancellationToken.None;

    private async Task<TimelineId> NewSourceAsync() => (await Store.CreateHistoryAsync(Source(), Ct)).Value.Manifest.Id;

    private async Task<TimelineId> NewSourceWithStepAsync()
    {
        var id = await NewSourceAsync();
        Assert.True((await Store.AppendStepAsync(Step(id, 0, null, ("only", 1)), Ct)).IsSuccess);
        return id;
    }

    private static async Task ExecuteAsync(NpgsqlDataSource source, string sql, object parameter)
    {
        await using var cmd = source.CreateCommand(sql);
        cmd.Parameters.Add(new NpgsqlParameter("p", parameter));
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql, object parameter)
    {
        await using var cmd = db.Owner.CreateCommand(sql);
        cmd.Parameters.Add(new NpgsqlParameter("p", parameter));
        return (T)(await cmd.ExecuteScalarAsync())!;
    }
}
