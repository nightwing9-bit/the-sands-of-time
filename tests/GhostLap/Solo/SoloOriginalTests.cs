using System.Diagnostics;
using FsCheck;
using FsCheck.Fluent;
using GhostLap.Rules;
using GhostLap.Simulation;
using GhostLap.Solo;
using GhostLap.Timeline;
using Marrow.Infrastructure.Identity;
using Marrow.Timeline;
using Npgsql;
using Xunit;
using Xunit.Abstractions;
using static GhostLap.Tests.Solo.SoloDatabase;

namespace GhostLap.Tests.Solo;

/// <summary>
/// Task 6.3: a complete solo original recorded tick by tick through the one Marrow Timeline into a freshly migrated
/// PostgreSQL, completed and replay-verified, then reopened, restored and owner-checked from cold hosts.
/// **Validates: Requirements R06.06, R06.07, R06.11**
/// </summary>
[Trait("Category", "Integration")]
public sealed class SoloOriginalTests(SoloDatabase db, ITestOutputHelper output) : IClassFixture<SoloDatabase>
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static byte[] Bytes(GameState s) => GameStateCodec.Instance.EncodeCanonical(s);

    [Fact]
    public async Task Original_has_every_tick_including_empty_ones_full_checkpoints_and_an_owned_complete_row()
    {
        var run = db.Run;
        var id = db.Original.Timeline.Value;
        var all = run.Trace.SelectMany(s => s.Events).ToList();
        output.WriteLine($"recorded 2700 ticks + complete + verify in {db.RecordingElapsed.TotalSeconds:F1}s; " +
                         $"pickups {all.Count(e => e.Kind == GameEventKind.CandleCollected)}, " +
                         $"bursts {all.Count(e => e.Kind is GameEventKind.BurstManual or GameEventKind.BurstAuto)}, " +
                         $"final {db.Original.FinalState.Players[0].TotalScore}, hash {db.Original.FinalStateHash}");
        Assert.Equal(GameRulesV1.RoundTicks, db.Original.FinalTick);
        Assert.Equal(Bytes(run.Trace[^1]), Bytes(db.Original.FinalState));
        Assert.Equal(run.Receipts[^1].StateHash, db.Original.FinalStateHash);
        Assert.Equal(run.Receipts[^1].RecordHash, db.Original.LastRecordHash);
        Assert.True(db.Original.Result.Scores[0].PickupPoints > 0);

        // Journal rows, read as the superuser: steps 1..2700 contiguous, empty ticks present, inputs exactly as admitted.
        var steps = await Rows<(long Position, int Inputs)>(
            "SELECT position, input_count FROM marrow_timeline.steps WHERE timeline_id = @id ORDER BY position",
            id, r => (r.GetInt64(0), r.GetInt32(1)));
        Assert.Equal(Enumerable.Range(1, GameRulesV1.RoundTicks).Select(i => (long)i), steps.Select(s => s.Position));
        Assert.Equal(run.Steps.Select(s => s.Length), steps.Select(s => s.Inputs));
        Assert.Contains(steps, s => s.Inputs == 0);
        var payloads = await Rows("SELECT payload FROM marrow_timeline.step_inputs WHERE timeline_id = @id ORDER BY position, input_index",
            id, r => (byte[])r[0]);
        Assert.Equal(run.Steps.SelectMany(s => s).Select(GameInputCodec.Instance.EncodeCanonical), payloads);

        // Complete checkpoints at 0 and every 60 ticks, each decoding to the live state of that tick.
        var checkpoints = await Rows<(long Position, byte[] Bytes)>(
            "SELECT position, state_bytes FROM marrow_timeline.checkpoints WHERE timeline_id = @id ORDER BY position",
            id, r => (r.GetInt64(0), (byte[])r[1]));
        Assert.Equal(Enumerable.Range(0, (GameRulesV1.RoundTicks / GhostLapTimelineVersions.CheckpointInterval) + 1)
            .Select(i => (long)i * GhostLapTimelineVersions.CheckpointInterval), checkpoints.Select(c => c.Position));
        Assert.All(checkpoints, c => Assert.Equal(Bytes(run.Trace[(int)c.Position]), c.Bytes));

        // Manifest owner is the verified actor; the Ghost Lap row is Complete with the verified hashes.
        var manifest = await Rows<(string Owner, string Status, string Reducer, string StateSchema)>(
            "SELECT owner_id, status, reducer_version, state_schema_version FROM marrow_timeline.histories WHERE timeline_id = @id",
            id, r => (r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)));
        Assert.Equal((db.OwnerA.SubjectId, "Verified", "ghost-v1", "ghost-state/1"), manifest.Single());
        var row = Ok(await new SoloOriginalStore(db.Superuser).FindAsync(db.Original.Timeline, Ct));
        Assert.Equal(SoloOriginalStatus.Complete, row.Status);
        Assert.Equal((db.OwnerA.SubjectId, Marrow.Kernel.Security.PrincipalKind.Guest), (row.OwnerSubjectId, row.OwnerKind));
        Assert.Equal((2700L, db.Original.FinalStateHash, db.Original.LastRecordHash), (row.FinalTick!.Value, row.FinalStateHash!, row.LastRecordHash!));
    }

    [Fact]
    public async Task Original_survives_a_refresh_into_a_cold_host_and_reverifies_its_hashes()
    {
        await using var cold = db.NewHost();
        var watch = Stopwatch.StartNew();
        var reopened = Ok(await cold.Originals.OpenAsync(db.OwnerA, db.Original.Timeline, Ct));
        output.WriteLine($"cold open (full replay + hash check) {watch.ElapsedMilliseconds} ms");
        Assert.Equal(db.Original.FinalStateHash, reopened.FinalStateHash);
        Assert.Equal(db.Original.LastRecordHash, reopened.LastRecordHash);
        Assert.Equal((SoloDatabase.Seed, (byte)2), (reopened.Seed, reopened.MascotId));
        Assert.Equal(Bytes(db.Run.Trace[^1]), Bytes(reopened.FinalState));
        Assert.Equal(db.Original.Result, reopened.Result, ResultComparer.Instance);

        // Resuming the same operation key after the original exists does not create or append anything.
        var again = Ok(await cold.Originals.StartAsync(db.OwnerA, SoloDatabase.Seed, 2, "fixture-original", Ct));
        Assert.Equal(db.Original.Timeline, again.Timeline);
        Assert.True(again.IsRoundOver);
        Assert.Equal(SoloErrorCode.Conflict, (await again.CommitTickAsync(Ct)).Error.Code);
        Assert.Equal(db.Original.FinalStateHash, Ok(await again.CompleteAsync(Ct)).FinalStateHash);
    }

    [Fact]
    public async Task Restore_from_the_database_at_every_tick_equals_the_uninterrupted_live_state()
    {
        await using var cold = db.NewHost();
        var watch = Stopwatch.StartNew();
        for (var t = 0; t <= GameRulesV1.RoundTicks; t++)
        {
            var restored = Ok(await cold.Originals.RestoreAsync(db.OwnerA, db.Original.Timeline, t, Ct));
            Assert.Equal(t, restored.Position);
            Assert.Equal(HistoryStatus.Verified, restored.HistoryStatus);
            Assert.True(Bytes(db.Run.Trace[t]).AsSpan().SequenceEqual(Bytes(restored.State)), $"tick {t}");
            if (t > 0)
            {
                Assert.Equal(db.Run.Receipts[t - 1].StateHash, restored.StateHash);
            }
        }

        output.WriteLine($"2701 cold restores in {watch.Elapsed.TotalSeconds:F1}s");
        Assert.Equal(SoloErrorCode.InvalidTick, (await cold.Originals.RestoreAsync(db.OwnerA, db.Original.Timeline, 2701, Ct)).Error.Code);
        Assert.Equal(SoloErrorCode.InvalidTick, (await cold.Originals.RestoreAsync(db.OwnerA, db.Original.Timeline, -1, Ct)).Error.Code);
    }

    /// <summary>
    /// R06.07/R06.11: at a tick with charge, collected candles, a held vector mid-motion and a sub-point distance remainder,
    /// the restored state continued with the recorded steps reaches the identical final state, and for any T the very next
    /// tick (score included) equals uninterrupted execution.
    /// </summary>
    [Fact]
    public async Task Restore_mid_motion_with_charge_and_pickups_continues_exactly_like_the_uninterrupted_run()
    {
        var run = db.Run;
        var t = run.Trace.First(s => s.Tick < GameRulesV1.RoundTicks - 1 && s.Players[0] is { Charge: > 0, HoldUntilTick: > 0 } p
                                     && p.DistanceFixed % GameRulesV1.MovementScoreUnitFixed != 0 && s.Collected.Length > 0).Tick;
        output.WriteLine($"rich fork tick {t}: {run.Trace[(int)t].Players[0]}");
        await using var cold = db.NewHost();
        var restored = Ok(await cold.Originals.RestoreAsync(db.OwnerA, db.Original.Timeline, t, Ct)).State;
        var p = restored.Players[0];
        var live = run.Trace[(int)t].Players[0];
        Assert.Equal((live.Charge, live.OverlitDeadlineTick, live.TotalScore, live.DistanceFixed, live.XFixed, live.YFixed),
            (p.Charge, p.OverlitDeadlineTick, p.TotalScore, p.DistanceFixed, p.XFixed, p.YFixed));
        Assert.Equal((live.LastSequence, live.HeldMoveX, live.HeldMoveY, live.HoldUntilTick, live.BurstLatch),
            (p.LastSequence, p.HeldMoveX, p.HeldMoveY, p.HoldUntilTick, p.BurstLatch));
        // ImmutableArray equality is by reference in xUnit, so compare the elements.
        Assert.Equal(run.Trace[(int)t].Collected.ToArray(), restored.Collected.ToArray());
        Assert.Equal(run.Trace[(int)t].ActiveCandleIds.ToArray(), restored.ActiveCandleIds.ToArray());

        // The future schedule comes from the pinned context of the reopened original, not from the live context object.
        var reopened = Ok(await cold.Originals.OpenAsync(db.OwnerA, db.Original.Timeline, Ct));
        var pinned = GameContext.Create(reopened.Seed, 1, [reopened.MascotId]);
        var s = restored;
        while (!s.IsFinal)
        {
            s = GhostLapTransition.Instance.Apply(s, s.Tick + 1, Ordered(run.Steps[(int)s.Tick]), pinned);
            Assert.True(Bytes(run.Trace[(int)s.Tick]).AsSpan().SequenceEqual(Bytes(s)), $"tick {s.Tick}");
        }

        // Property: for any T, restore T from the database and apply recorded step T+1 -> live state T+1 (score included).
        var property = Prop.ForAll(Gen.Choose(0, GameRulesV1.RoundTicks - 1).ToArbitrary(), tick =>
        {
            var at = Ok(cold.Originals.RestoreAsync(db.OwnerA, db.Original.Timeline, tick, Ct).GetAwaiter().GetResult()).State;
            var next = GhostLapTransition.Instance.Apply(at, tick + 1, Ordered(run.Steps[tick]), pinned);
            return next.Players[0].TotalScore == run.Trace[tick + 1].Players[0].TotalScore
                   && Bytes(next).AsSpan().SequenceEqual(Bytes(run.Trace[tick + 1]));
        });
        property.QuickCheckThrowOnFailure();
    }

    [Fact]
    public async Task Only_the_verified_owner_can_open_or_restore_and_denials_do_not_reveal_existence()
    {
        await using var host = db.NewHost();
        var id = db.Original.Timeline;
        var unknown = new TimelineId(Guid.NewGuid());
        var stranger = Guest();
        var staff = Staff();
        foreach (var actor in new[] { stranger, staff, ClaimsActorContext.Anonymous })
        {
            var open = await host.Originals.OpenAsync(actor, id, Ct);
            var restore = await host.Originals.RestoreAsync(actor, id, 1350, Ct);
            Assert.Equal(SoloErrorCode.NotFound, open.Error.Code);
            Assert.Equal(SoloErrorCode.NotFound, restore.Error.Code);
            Assert.Equal((await host.Originals.OpenAsync(db.OwnerA, unknown, Ct)).Error, open.Error);
        }

        // A stranger who reuses the owner's operation key gets their own history, never the owner's.
        var theirs = Ok(await host.Originals.StartAsync(stranger, SoloDatabase.Seed, 2, "fixture-original", Ct));
        Assert.NotEqual(id, theirs.Timeline);
        Assert.Equal(0, theirs.State.Tick);

        // A Ghost Lap Timeline history without a Ghost Lap row (created directly on the engine) is not an original.
        var bare = await host.Engine.CreateSourceAsync(new CreateSourceRequest<GameState, GameContext>(
            db.OwnerA.SubjectId, GhostLapTimelineVersions.V1, GameState.Initial(GameContext.Create(1, 1)), GameContext.Create(1, 1),
            SoloOriginals.RulesHash, SoloOriginals.SeedHash(GameContext.Create(1, 1))), "bare-" + Guid.NewGuid().ToString("N"), Ct);
        Assert.Equal(SoloErrorCode.NotFound, (await host.Originals.OpenAsync(db.OwnerA, bare.Value, Ct)).Error.Code);

        Assert.Equal(SoloErrorCode.Unauthenticated,
            (await host.Originals.StartAsync(ClaimsActorContext.Anonymous, 1, 0, "anon", Ct)).Error.Code);
        Assert.Equal(SoloErrorCode.InvalidRequest, (await host.Originals.StartAsync(db.OwnerA, 1, 0, "bad key/", Ct)).Error.Code);
        Assert.Equal(SoloErrorCode.InvalidRequest, (await host.Originals.StartAsync(db.OwnerA, 1, 4, "mascot", Ct)).Error.Code);
    }

    [Fact]
    public async Task An_unfinished_run_is_never_an_original_and_resumes_after_a_restart_from_what_was_committed()
    {
        var owner = Guest();
        var key = "partial-" + Guid.NewGuid().ToString("N");
        TimelineId id;
        byte[] at30;
        await using (var host = db.NewHost())
        {
            var rec = Ok(await host.Originals.StartAsync(owner, 7, 1, key, Ct));
            id = rec.Timeline;
            await RecordAsync(rec, 30);
            at30 = Bytes(rec.State);
            Assert.Equal(SoloErrorCode.NotComplete, (await rec.CompleteAsync(Ct)).Error.Code);
            Assert.Equal(SoloErrorCode.NotComplete, (await host.Originals.OpenAsync(owner, id, Ct)).Error.Code);
            Assert.Equal(SoloErrorCode.NotComplete, (await host.Originals.RestoreAsync(owner, id, 10, Ct)).Error.Code);
        } // the "server" goes away mid-run

        await using var restarted = db.NewHost();
        var resumed = Ok(await restarted.Originals.StartAsync(owner, 7, 1, key, Ct));
        Assert.Equal(id, resumed.Timeline);
        Assert.Equal(at30, Bytes(resumed.State));
        var seq = resumed.State.Players[0].LastSequence;
        Assert.Equal(InputRejection.StaleSequence, resumed.Submit(new SubmittedInput(0, seq, 31, 1, 0, false)).Error);
        Assert.True(resumed.Submit(new SubmittedInput(0, seq + 1, 31, 1, 0, false)).IsSuccess);
        Assert.Equal(31, Ok(await resumed.CommitTickAsync(Ct)).Tick);
        var row = Ok(await restarted.Store.FindAsync(id, Ct));
        Assert.Equal(SoloOriginalStatus.Recording, row.Status);
        Assert.Null(row.FinalStateHash);
    }

    [Fact]
    public async Task Failed_commits_are_visible_retry_idempotently_and_a_conflicting_duplicate_is_rejected()
    {
        var owner = Guest();
        var key = "retry-" + Guid.NewGuid().ToString("N");
        await using var host = db.NewHost();
        var rec = Ok(await host.Originals.StartAsync(owner, 11, 0, key, Ct));
        Assert.True(rec.Submit(new SubmittedInput(0, 1, 1, 1, 0, false)).IsSuccess);

        // Cancelled before commit: typed failure, no state advance, the tick's step is frozen until it commits.
        using (var cancelled = new CancellationTokenSource())
        {
            await cancelled.CancelAsync();
            Assert.Equal(SoloErrorCode.Cancelled, (await rec.CommitTickAsync(cancelled.Token)).Error.Code);
        }

        Assert.Equal(0, rec.State.Tick);
        Assert.Equal(InputRejection.CommitPending, rec.Submit(new SubmittedInput(0, 2, 1, 0, 1, false)).Error);
        var first = Ok(await rec.CommitTickAsync(Ct));
        Assert.Equal(1, first.Tick);
        Assert.Equal(1, rec.State.Players[0].LastSequence); // the frozen step committed, not the rejected later input

        // Two recorders resumed at the same tick: the same empty step under the same key is an idempotent retry...
        var a = Ok(await host.Originals.StartAsync(owner, 11, 0, key, Ct));
        var b = Ok(await host.Originals.StartAsync(owner, 11, 0, key, Ct));
        var ra = Ok(await a.CommitTickAsync(Ct));
        var rb = Ok(await b.CommitTickAsync(Ct));
        Assert.Equal((ra.Tick, ra.StateHash, ra.RecordHash), (rb.Tick, rb.StateHash, rb.RecordHash));

        // ...but a different payload for an already committed tick is a Conflict and the committed tick is unchanged.
        var c = Ok(await host.Originals.StartAsync(owner, 11, 0, key, Ct));
        var d = Ok(await host.Originals.StartAsync(owner, 11, 0, key, Ct));
        Assert.True(c.Submit(new SubmittedInput(0, 2, 3, 0, 1, false)).IsSuccess);
        var rc = Ok(await c.CommitTickAsync(Ct));
        Assert.True(d.Submit(new SubmittedInput(0, 2, 3, 0, -1, false)).IsSuccess);
        Assert.Equal(SoloErrorCode.Conflict, (await d.CommitTickAsync(Ct)).Error.Code);
        Assert.Equal(2, d.State.Tick);
        var replay = (await host.Engine.ReplayAsync(rec.Timeline, Ct)).Value;
        Assert.Equal((3L, rc.StateHash), (replay.FinalPosition, replay.FinalStateHash));

        // An unreachable/refusing database is a typed Unavailable result, never an exception or a success.
        var wrong = new NpgsqlConnectionStringBuilder(db.AppConnectionString) { Password = "wrong-password-for-test" }.ConnectionString;
        await using var broken = db.NewHost(connectionString: wrong);
        Assert.Equal(SoloErrorCode.Unavailable, (await broken.Originals.StartAsync(owner, 11, 0, key, Ct)).Error.Code);
        Assert.Equal(SoloErrorCode.Unavailable, (await broken.Originals.OpenAsync(db.OwnerA, db.Original.Timeline, Ct)).Error.Code);
    }

    [Fact]
    public async Task A_build_without_the_pinned_ghost_v1_reducer_reports_an_incompatible_version_instead_of_reinterpreting()
    {
        var other = GhostLapTimelineVersions.V1Components with
        {
            Versions = GhostLapTimelineVersions.V1 with { ReducerVersion = "ghost-v2" },
        };
        await using var host = db.NewHost([other]);
        var opened = await host.Originals.OpenAsync(db.OwnerA, db.Original.Timeline, Ct);
        Assert.Equal(SoloErrorCode.IncompatibleVersion, opened.Error.Code);
        output.WriteLine(opened.Error.Message);
        Assert.Equal(SoloErrorCode.IncompatibleVersion, (await host.Originals.RestoreAsync(db.OwnerA, db.Original.Timeline, 60, Ct)).Error.Code);
    }

    [Fact]
    public async Task The_database_keeps_owner_and_completion_facts_immutable_even_for_the_schema_owner()
    {
        var id = db.Original.Timeline.Value;
        foreach (var sql in new[]
                 {
                     "UPDATE ghostlap.solo_originals SET owner_subject_id = 'guest:someone-else' WHERE timeline_id = @id",
                     "UPDATE ghostlap.solo_originals SET final_state_hash = repeat('0', 64) WHERE timeline_id = @id",
                     "DELETE FROM ghostlap.solo_originals WHERE timeline_id = @id",
                 })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => Exec(db.Superuser, sql, id));
            Assert.Equal("GL001", ex.SqlState);
        }

        await using var app = NpgsqlDataSource.Create(db.AppConnectionString);
        var denied = await Assert.ThrowsAsync<PostgresException>(() =>
            Exec(app, "DELETE FROM ghostlap.solo_originals WHERE timeline_id = @id", id));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
        var owner = await Assert.ThrowsAsync<PostgresException>(() =>
            Exec(app, "UPDATE ghostlap.solo_originals SET owner_subject_id = 'x' WHERE timeline_id = @id", id));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, owner.SqlState);
        Assert.Contains("module ghostlap: applying 0001_solo_originals", db.MigrationLog);
    }

    private static IReadOnlyList<OrderedInput<GameInput>> Ordered(GameInput[] inputs) =>
        [.. inputs.Select(g => new OrderedInput<GameInput>($"t{g.Tick}-s{g.Slot}-q{g.Sequence}", g.Slot, g))];

    private static async Task Exec(NpgsqlDataSource source, string sql, Guid id)
    {
        await using var cmd = source.CreateCommand(sql);
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<List<T>> Rows<T>(string sql, Guid id, Func<NpgsqlDataReader, T> read)
    {
        await using var cmd = db.Superuser.CreateCommand(sql);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync();
        var list = new List<T>();
        while (await r.ReadAsync())
        {
            list.Add(read(r));
        }

        return list;
    }

    private sealed class ResultComparer : IEqualityComparer<GameResult>
    {
        public static ResultComparer Instance { get; } = new();

        public bool Equals(GameResult? x, GameResult? y) =>
            x is not null && y is not null && x.Scores.SequenceEqual(y.Scores) && x.Winners.SequenceEqual(y.Winners);

        public int GetHashCode(GameResult obj) => obj.Scores.Length;
    }
}
