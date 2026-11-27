using System.Diagnostics;
using System.Text.Json;
using GhostLap.Rules;
using GhostLap.Simulation;
using GhostLap.Solo;
using GhostLap.Timeline;
using Marrow.Timeline;
using Npgsql;
using Xunit;
using Xunit.Abstractions;
using static GhostLap.Tests.Solo.SoloDatabase;

namespace GhostLap.Tests.Solo;

/// <summary>
/// Exact historical seek and bounded ghost trace of the fixture original, over the real recorder and PostgreSQL.
/// </summary>
[Trait("Category", "Integration")]
public sealed partial class SoloTraceTests(SoloTraceDatabase db, ITestOutputHelper output) : IClassFixture<SoloTraceDatabase>
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private const long Rich = 1140;

    private static byte[] Bytes(GameState s) => GameStateCodec.Instance.EncodeCanonical(s);

    [Theory]
    [InlineData(0L)]
    [InlineData(1140L)]
    [InlineData(2699L)]
    [InlineData(2700L)]
    public async Task Inspect_restores_the_full_state_and_hash_at_the_boundary_tick(long tick)
    {
        await using var cold = db.NewHost();
        var frame = Ok(await cold.Originals.InspectAsync(db.OwnerA, db.Original.Timeline, tick, Ct));
        var live = db.Run.Trace[(int)tick];
        var restored = Ok(await cold.Originals.RestoreAsync(db.OwnerA, db.Original.Timeline, tick, Ct));

        Assert.True(Bytes(live).AsSpan().SequenceEqual(Bytes(restored.State)), $"full state at {tick}");
        Assert.Equal(restored.StateHash, frame.StateHash);
        if (tick > 0)
        {
            Assert.Equal(db.Run.Receipts[(int)tick - 1].StateHash, frame.StateHash);
        }

        var p = live.Players[0];
        Assert.Equal((tick, p.XFixed, p.YFixed, p.DistanceFixed, p.Charge, p.OverlitDeadlineTick, p.TotalScore, p.Pickups),
            (frame.Tick, frame.XFixed, frame.YFixed, frame.DistanceFixed, frame.Charge, frame.OverlitDeadlineTick, frame.TotalScore, frame.Pickups));
        Assert.Equal((p.MovementPoints, p.PickupPoints, p.PenaltyPoints, p.Bursts, p.BurstHits),
            (frame.MovementPoints, frame.PickupPoints, frame.PenaltyPoints, frame.Bursts, frame.BurstHits));
        Assert.Equal(live.Collected.Length, frame.CollectedCount);
        Assert.Equal(live.ActiveCandleIds.ToArray(), frame.ActiveCandleIds.ToArray());
        Assert.Equal(live.Events.ToArray(), frame.Events.ToArray());
        Assert.Equal(tick == GameRulesV1.RoundTicks, frame.IsFinal);
        if (tick == GameRulesV1.RoundTicks)
        {
            Assert.Equal(db.Original.FinalStateHash, frame.StateHash);
            Assert.Equal(db.Original.Result.Scores[0].TotalScore, frame.TotalScore);
        }

        output.WriteLine($"T={tick}: pos=({frame.XFixed},{frame.YFixed}) charge={frame.Charge} score={frame.TotalScore} " +
                         $"origin={frame.HashOrigin} hash={frame.StateHash}");
    }

    [Fact]
    public async Task T1140_is_nontrivial_and_its_historical_score_is_not_the_final_score()
    {
        var live = db.Run.Trace[(int)Rich];
        var p = live.Players[0];
        var finalScore = db.Original.Result.Scores[0].TotalScore;
        var charged = db.Run.Trace.Count(s => s.Players[0].Charge > 0);
        var overlit = db.Run.Trace.Count(s => s.Players[0].IsOverlit);
        output.WriteLine($"T1140: dist={p.DistanceFixed} remainder={p.DistanceFixed % GameRulesV1.MovementScoreUnitFixed} " +
                         $"collected={live.Collected.Length} charge={p.Charge} score={p.TotalScore} final={finalScore}; " +
                         $"run ticks with charge>0: {charged}, Overlit: {overlit}");
        Assert.NotEqual(0, p.DistanceFixed % GameRulesV1.MovementScoreUnitFixed);
        Assert.True(live.Collected.Length > 0);
        Assert.True(p.Charge > 0);

        await using var cold = db.NewHost();
        var frame = Ok(await cold.Originals.InspectAsync(db.OwnerA, db.Original.Timeline, Rich, Ct));
        Assert.Equal(p.TotalScore, frame.TotalScore);
        Assert.NotEqual(finalScore, frame.TotalScore);
        Assert.True(frame.TotalScore < finalScore);
        Assert.Equal(p.DistanceFixed % GameRulesV1.MovementScoreUnitFixed, frame.DistanceFixed % GameRulesV1.MovementScoreUnitFixed);
        Assert.Equal(live.Collected.Length, frame.CollectedCount);
        Assert.Equal(p.Charge, frame.Charge);
        Assert.Equal(p.IsOverlit, frame.IsOverlit);
    }

    [Fact]
    public async Task A_trace_chunk_is_tick_aligned_deterministic_and_carries_the_real_cues_and_historical_scores()
    {
        // A window around the first candle pickup, widened to include the first charge change if there is one.
        var firstPickup = db.Run.Trace.First(s => s.Events.Any(e => e.Kind == GameEventKind.CandleCollected)).Tick;
        var from = Math.Max(1, firstPickup - 10);
        var finalScore = db.Original.Result.Scores[0].TotalScore;

        await using var coldA = db.NewHost();
        await using var coldB = db.NewHost();
        var watch = Stopwatch.StartNew();
        var a = Ok(await coldA.Originals.TraceAsync(db.OwnerA, db.Original.Timeline, from, 60, Ct));
        var ms = watch.ElapsedMilliseconds;
        var b = Ok(await coldB.Originals.TraceAsync(db.OwnerA, db.Original.Timeline, from, 60, Ct));
        output.WriteLine($"chunk of {a.Frames.Length} ticks from {from}: {ms} ms cold (one host, per-tick engine restores); " +
                         $"first pickup tick {firstPickup}");

        Assert.Equal(JsonSerializer.Serialize(a), JsonSerializer.Serialize(b));
        Assert.Equal((db.Original.Timeline, GhostLapTimelineVersions.V1, GameRulesV1.RulesVersion, GameRulesV1.LayoutVersion),
            (a.Timeline, a.Versions, a.RulesVersion, a.LayoutVersion));
        Assert.Equal((GameRulesV1.RoundTicks, db.Original.FinalStateHash), (a.SourceFinalTick, a.SourceFinalStateHash));
        Assert.Equal(Enumerable.Range(0, 60).Select(i => from + i), a.Frames.Select(f => f.Tick));
        Assert.Equal(from + 60, a.NextTick);

        foreach (var f in a.Frames)
        {
            var live = db.Run.Trace[(int)f.Tick];
            Assert.Equal(live.Players[0].TotalScore, f.TotalScore);
            Assert.Equal(live.Events.ToArray(), f.Events.ToArray());
            Assert.Equal(db.Run.Receipts[(int)f.Tick - 1].StateHash, f.StateHash);
            Assert.NotEqual(finalScore, f.TotalScore);
        }

        Assert.Contains(a.Frames, f => f.Events.Any(e => e.Kind == GameEventKind.CandleCollected && !string.IsNullOrEmpty(e.CandleId)));
        Assert.True(a.Frames.Select(f => f.TotalScore).Distinct().Count() > 1, "scores vary tick by tick");
    }

    [Fact]
    public async Task The_final_chunk_ends_at_2700_with_the_recorded_final_hash_and_no_next_tick()
    {
        await using var cold = db.NewHost();
        var chunk = Ok(await cold.Originals.TraceAsync(db.OwnerA, db.Original.Timeline, 2690, SoloOriginals.MaxTraceTicks, Ct));
        Assert.Equal(Enumerable.Range(2690, 11).Select(i => (long)i), chunk.Frames.Select(f => f.Tick));
        Assert.Null(chunk.NextTick);
        Assert.Equal(db.Original.FinalStateHash, chunk.Frames[^1].StateHash);
        Assert.True(chunk.Frames[^1].IsFinal);
        Assert.All(chunk.Frames.Take(10), f => Assert.False(f.IsFinal));
        Assert.Equal(db.Run.Trace[2699].Players[0].TotalScore, chunk.Frames[9].TotalScore);
        Assert.Equal(db.Original.Result.Scores[0].TotalScore, chunk.Frames[10].TotalScore);
        Assert.Contains(chunk.Frames[10].Events, e => e.Kind == GameEventKind.RoundFinished);
    }

    [Fact]
    public async Task Invalid_ticks_and_oversized_chunks_are_refused()
    {
        await using var host = db.NewHost();
        var id = db.Original.Timeline;
        Assert.Equal(SoloErrorCode.InvalidTick, (await host.Originals.TraceAsync(db.OwnerA, id, -1, 10, Ct)).Error.Code);
        Assert.Equal(SoloErrorCode.InvalidTick, (await host.Originals.TraceAsync(db.OwnerA, id, 2701, 10, Ct)).Error.Code);
        Assert.Equal(SoloErrorCode.InvalidRequest, (await host.Originals.TraceAsync(db.OwnerA, id, 0, 0, Ct)).Error.Code);
        Assert.Equal(SoloErrorCode.InvalidRequest,
            (await host.Originals.TraceAsync(db.OwnerA, id, 0, SoloOriginals.MaxTraceTicks + 1, Ct)).Error.Code);
        Assert.Equal(SoloErrorCode.InvalidTick, (await host.Originals.InspectAsync(db.OwnerA, id, -1, Ct)).Error.Code);
        Assert.Equal(SoloErrorCode.InvalidTick, (await host.Originals.InspectAsync(db.OwnerA, id, 2701, Ct)).Error.Code);
    }

    [Fact]
    public async Task A_foreign_owner_an_unknown_id_and_an_unfinished_run_are_refused_without_revealing_which()
    {
        await using var host = db.NewHost();
        var foreign = Guest();
        var denied = await host.Originals.TraceAsync(foreign, db.Original.Timeline, 1140, 5, Ct);
        var unknown = await host.Originals.TraceAsync(foreign, new TimelineId(Guid.NewGuid()), 1140, 5, Ct);
        Assert.Equal(SoloErrorCode.NotFound, denied.Error.Code);
        Assert.Equal(denied.Error, unknown.Error);
        Assert.Equal(SoloErrorCode.NotFound, (await host.Originals.InspectAsync(foreign, db.Original.Timeline, 0, Ct)).Error.Code);

        // An unfinished run is not an original, so it has no trace, and nobody else can read it.
        var recording = Ok(await host.Originals.StartAsync(foreign, Seed, 1, "trace-partial", Ct));
        await RecordAsync(recording, 30);
        Assert.Equal(SoloErrorCode.NotComplete, (await host.Originals.TraceAsync(foreign, recording.Timeline, 0, 5, Ct)).Error.Code);
        Assert.Equal(SoloErrorCode.NotFound, (await host.Originals.TraceAsync(db.OwnerA, recording.Timeline, 0, 5, Ct)).Error.Code);
    }

    [Fact]
    public async Task A_build_without_the_pinned_ghost_v1_codec_refuses_the_trace_instead_of_reinterpreting()
    {
        var other = GhostLapTimelineVersions.V1Components with
        {
            Versions = GhostLapTimelineVersions.V1 with { ReducerVersion = "ghost-v2" },
        };
        await using var host = db.NewHost([other]);
        var traced = await host.Originals.TraceAsync(db.OwnerA, db.Original.Timeline, 1140, 5, Ct);
        Assert.Equal(SoloErrorCode.IncompatibleVersion, traced.Error.Code);
        Assert.Equal(SoloErrorCode.IncompatibleVersion, (await host.Originals.InspectAsync(db.OwnerA, db.Original.Timeline, 0, Ct)).Error.Code);
        output.WriteLine(traced.Error.Message);
    }

    [Fact]
    public async Task Trace_rejects_broken_catalog_identity_and_unverified_or_corrupt_timeline_data()
    {
        await using var admin = await db.Superuser.OpenConnectionAsync();
        await using var replica = new NpgsqlCommand("SET session_replication_role = replica", admin);
        await replica.ExecuteNonQueryAsync();
        await using var host = db.NewHost();
        var id = db.Original.Timeline;
        var row = Ok(await host.Store.FindAsync(id, Ct));

        async Task RejectAsync(string update, object damaged, object original, SoloErrorCode error, long tick = Rich)
        {
            await using var command = new NpgsqlCommand(update, admin);
            command.Parameters.AddWithValue("id", id.Value);
            var value = command.Parameters.AddWithValue("value", damaged);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
            try
            {
                Assert.Equal(error, (await host.Originals.InspectAsync(db.OwnerA, id, tick, Ct)).Error.Code);
                Assert.Equal(error, (await host.Originals.TraceAsync(db.OwnerA, id, tick, 5, Ct)).Error.Code);
            }
            finally
            {
                value.Value = original;
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }
        }

        await RejectAsync("UPDATE marrow_timeline.histories SET state_schema_version = @value WHERE timeline_id = @id",
            "ghost-state/unknown", GhostLapTimelineVersions.StateSchemaVersion, SoloErrorCode.IncompatibleVersion);
        await RejectAsync("UPDATE marrow_timeline.histories SET status = @value WHERE timeline_id = @id",
            "Recording", "Verified", SoloErrorCode.VerificationFailed);
        await RejectAsync("UPDATE marrow_timeline.checkpoints SET state_hash = @value WHERE timeline_id = @id AND position = 1140",
            new string('0', 64), db.Run.Receipts[(int)Rich - 1].StateHash, SoloErrorCode.CorruptHistory);
        await RejectAsync("UPDATE ghostlap.solo_originals SET final_state_hash = @value WHERE timeline_id = @id",
            new string('0', 64), row.FinalStateHash!, SoloErrorCode.VerificationFailed);
        await RejectAsync("UPDATE ghostlap.solo_originals SET final_tick = @value WHERE timeline_id = @id",
            2701L, row.FinalTick!.Value, SoloErrorCode.VerificationFailed);
        await RejectAsync("UPDATE ghostlap.solo_originals SET layout_version = @value WHERE timeline_id = @id",
            "arena-unknown", row.LayoutVersion, SoloErrorCode.IncompatibleVersion);
        Ok(await host.Originals.InspectAsync(db.OwnerA, id, Rich, Ct));
    }
}
