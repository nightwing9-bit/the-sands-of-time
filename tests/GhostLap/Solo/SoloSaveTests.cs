using GhostLap.Rules;
using GhostLap.Solo;
using GhostLap.Timeline;
using Marrow.Timeline;
using Xunit;
using Xunit.Abstractions;
using static GhostLap.Tests.Solo.SoloDatabase;

namespace GhostLap.Tests.Solo;

/// <summary>
/// Task 11.1 (Kiro focused part): the honest save outcome, owner library and retry identity of a solo original, over the
/// real recorder and a real PostgreSQL container. The cold-host reload at host level, the lost-response scenario and real
/// owner/foreign-user reads are NOT run here; they are delegated to Codex.
/// **Validates: Requirements R11.02, R11.03, R11.04, R11.05, R11.12**
/// </summary>
[Trait("Category", "Integration")]
public sealed class SoloSaveTests(SoloDatabase db, ITestOutputHelper output) : IClassFixture<SoloDatabase>
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    /// <summary>
    /// Regression pin, copied from this implementation's own output for seed 42, mascot 2 and <c>CandleSeekingDriver</c>
    /// (not an independent oracle). It must not change while solo <c>ghost-v1</c> is frozen.
    /// </summary>
    private const string PinnedFinalStateHash =
        "b3130f4148afd99f4c5a73b17d9ca63af8237fa0e62816920ecdd99e263674c1";

    [Fact]
    public async Task A_second_independent_run_with_the_same_inputs_reproduces_the_pinned_ghost_v1_final_hash_and_saves_once()
    {
        var owner = Guest();
        await using var host = db.NewHost();
        var recording = Ok(await host.Originals.StartAsync(owner, Seed, 2, "save-original", Ct));
        var run = await RecordAsync(recording, GameRulesV1.RoundTicks);

        var first = await host.Originals.SaveAsync(owner, recording, Ct);
        output.WriteLine($"save status {first.Status}; id {first.Original?.Timeline.Value}; hash {first.Original?.FinalStateHash}; " +
                         $"last record {first.Original?.LastRecordHash}; score {first.Original?.Score}");
        Assert.Equal(SoloSaveStatus.Saved, first.Status);
        var saved = first.Original!;

        // Full original: 2700 ticks, versions pinned, hash equals the live recorder's last receipt and the engine's own replay.
        Assert.Equal(GameRulesV1.RoundTicks, saved.FinalTick);
        Assert.Equal(GhostLapTimelineVersions.V1, saved.Versions);
        Assert.Equal(GameRulesV1.RulesVersion, saved.RulesVersion);
        Assert.Equal(GameRulesV1.LayoutVersion, saved.LayoutVersion);
        Assert.Equal(run.Receipts[^1].StateHash, saved.FinalStateHash);
        Assert.Equal(run.Receipts[^1].RecordHash, saved.LastRecordHash);
        Assert.Equal(run.Trace[^1].Players[0].TotalScore, saved.Score);
        Assert.True((await host.Engine.ReplayAsync(saved.Timeline, Ct)).TryGetValue(out var replay));
        Assert.Equal(HistoryStatus.Verified, replay.Status);
        Assert.Equal(saved.FinalStateHash, replay.FinalStateHash);
        Assert.Equal(saved.LastRecordHash, replay.LastRecordHash);

        // Solo ghost-v1 golden preserved, and a second recording of the same play gives the same canonical hash as the fixture's.
        Assert.Equal(db.Original.FinalStateHash, saved.FinalStateHash);
        Assert.Equal(PinnedFinalStateHash, saved.FinalStateHash);

        // Identical retries return the same identity: same recorder, a re-started recorder for the same key, and a key-only resolve.
        Assert.Equal(first, await host.Originals.SaveAsync(owner, recording, Ct));
        var restarted = Ok(await host.Originals.StartAsync(owner, Seed, 2, "save-original", Ct));
        Assert.Equal(saved.Timeline, restarted.Timeline);
        Assert.Equal(first, await host.Originals.SaveAsync(owner, restarted, Ct));
        await using var cold = db.NewHost();
        var resolved = await cold.Originals.ResolveAsync(owner, "save-original", Ct);
        Assert.Equal(SoloSaveStatus.Saved, resolved.Status);
        Assert.Equal(saved, resolved.Original);

        // Exactly one visible record and one Timeline source for this owner.
        var rows = await RowCount("SELECT count(*) FROM ghostlap.solo_originals WHERE owner_subject_id = @o", owner.SubjectId);
        Assert.Equal(1, rows);
        var list = Ok(await cold.Originals.ListAsync(owner, 10, Ct));
        var only = Assert.Single(list);
        Assert.Equal(saved with { Score = null }, only);
        var verified = Ok(await cold.Originals.GetAsync(owner, saved.Timeline, Ct));
        Assert.Equal(saved, verified);
    }

    [Fact]
    public async Task A_conflicting_retry_of_a_used_key_is_denied_and_the_saved_identity_is_unchanged()
    {
        await using var host = db.NewHost();
        var before = await host.Originals.ResolveAsync(db.OwnerA, "fixture-original", Ct);
        Assert.Equal(SoloSaveStatus.Saved, before.Status);

        var otherSeed = await host.Originals.StartAsync(db.OwnerA, Seed + 1, 2, "fixture-original", Ct);
        Assert.True(otherSeed.IsFailure, "a different seed under a used key must not start or replace a recording");
        var otherMascot = await host.Originals.StartAsync(db.OwnerA, Seed, 3, "fixture-original", Ct);
        Assert.True(otherMascot.IsFailure, "a different mascot under a used key must not start or replace a recording");
        output.WriteLine($"conflicting starts: seed -> {otherSeed.Error}; mascot -> {otherMascot.Error}");

        // The Complete row cannot be completed again with different facts.
        var rewrite = await host.Store.MarkCompleteAsync(db.Original.Timeline, GameRulesV1.RoundTicks, "0".PadLeft(64, '0'), "1".PadLeft(64, '1'), Ct);
        Assert.Equal(SoloErrorCode.Conflict, rewrite.Error.Code);

        var after = await host.Originals.ResolveAsync(db.OwnerA, "fixture-original", Ct);
        Assert.Equal(before, after);
        Assert.Equal(db.Original.FinalStateHash, after.Original!.FinalStateHash);
    }

    [Fact]
    public async Task An_interrupted_partial_run_is_never_listed_resolved_or_saved_as_an_original()
    {
        var owner = Guest();
        await using var host = db.NewHost();
        var recording = Ok(await host.Originals.StartAsync(owner, Seed, 1, "interrupted", Ct));
        await RecordAsync(recording, 30);

        Assert.Empty(Ok(await host.Originals.ListAsync(owner, 10, Ct)));
        var save = await host.Originals.SaveAsync(owner, recording, Ct);
        Assert.Equal(SoloSaveStatus.Failed, save.Status);
        Assert.Equal(SoloErrorCode.NotComplete, save.Error!.Code);
        Assert.Null(save.Original);

        // After a restart (a new host, no recorder) the unfinished run is still not an original.
        await using var cold = db.NewHost();
        var resolved = await cold.Originals.ResolveAsync(owner, "interrupted", Ct);
        Assert.Equal(SoloSaveStatus.Failed, resolved.Status);
        Assert.Equal(SoloErrorCode.NotComplete, resolved.Error!.Code);
        Assert.Empty(Ok(await cold.Originals.ListAsync(owner, 10, Ct)));
        Assert.Equal(SoloErrorCode.NotComplete, (await cold.Originals.OpenAsync(owner, recording.Timeline, Ct)).Error.Code);
        Assert.Equal(1L, await RowCount(
            "SELECT count(*) FROM ghostlap.solo_originals WHERE owner_subject_id = @o AND status = 'Recording'", owner.SubjectId));
    }

    [Fact]
    public async Task The_owner_library_lists_the_fixture_original_with_exact_versions_and_hashes()
    {
        await using var host = db.NewHost();
        var list = Ok(await host.Originals.ListAsync(db.OwnerA, 50, Ct));
        var item = Assert.Single(list, o => o.OperationKey == "fixture-original");
        Assert.Equal(db.Original.Timeline, item.Timeline);
        Assert.Equal(db.Original.FinalStateHash, item.FinalStateHash);
        Assert.Equal(db.Original.LastRecordHash, item.LastRecordHash);
        Assert.Equal(GhostLapTimelineVersions.V1, item.Versions);
        Assert.Equal(GameRulesV1.RoundTicks, item.FinalTick);
        Assert.Null(item.Score);
        var verified = Ok(await host.Originals.GetAsync(db.OwnerA, item.Timeline, Ct));
        Assert.Equal(db.Original.Result.Scores[0].TotalScore, verified.Score);
    }

    [Fact]
    public async Task An_unreachable_database_gives_SavePending_never_Saved()
    {
        var unreachable = new Npgsql.NpgsqlConnectionStringBuilder(db.AppConnectionString) { Host = "127.0.0.1", Port = 1, Timeout = 2 }.ConnectionString;
        await using var host = db.NewHost(connectionString: unreachable);
        var outcome = await host.Originals.ResolveAsync(db.OwnerA, "fixture-original", Ct);
        Assert.Equal(SoloSaveStatus.SavePending, outcome.Status);
        Assert.Null(outcome.Original);
        Assert.Equal(SoloErrorCode.Unavailable, outcome.Error!.Code);
    }

    private async Task<long> RowCount(string sql, string subject)
    {
        await using var cmd = db.Superuser.CreateCommand(sql);
        cmd.Parameters.AddWithValue("o", subject);
        return (long)(await cmd.ExecuteScalarAsync(Ct))!;
    }
}
