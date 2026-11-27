using GhostLap.Multiplayer;
using Npgsql;
using Xunit;

namespace Integration.Tests.GhostLap;

public sealed partial class AuthorityAbortVerificationTests
{
    [Fact]
    public async Task Replacement_winning_the_advisory_lock_fences_store_and_direct_sql_inserts()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        await using var replacementSource = RealtimeSource("verify-replacement");
        await using var startSource = RealtimeSource("verify-start");
        await using var directSource = RealtimeSource("verify-direct-start");
        var replacement = new MatchAuthorityStore(replacementSource);
        var starter = new MatchAuthorityStore(startSource);
        var old = Guid.NewGuid();
        var next = Guid.NewGuid();
        await starter.RegisterGenerationAsync(old, ct);
        var existing = Seal(old);
        var stale = Seal(old);
        var directId = Guid.NewGuid();
        await StartReceiptAsync(starter, existing, ct);

        await using var gate = await PauseAtAsync(
            "replacement_wins", "AFTER INSERT ON ghostlap.realtime_generation",
            $"NEW.generation_id = '{next:D}'", 105, ct);
        var registering = replacement.RegisterGenerationAsync(next, ct);
        var replacementPid = await WaitBlockedAsync("verify-replacement", gate.BackendPid, ct);

        var storeInsert = starter.CommitStartReceiptAsync(stale, old, ct);
        // Exercise the migration's fence directly with a valid receipt under the predecessor's identity.
        await using var insert = directSource.CreateCommand("""
            INSERT INTO ghostlap.match_session
              (match_id, room_id, generation_id, round_ordinal, config_version, config_digest, rules_id, layout_id,
               rules_hash, layout_fingerprint, seed, rating_algorithm_id, target_ticks, roster_subjects,
               terminal_decision, terminal_version)
            SELECT @id, @room, generation_id, round_ordinal, config_version, config_digest, rules_id, layout_id,
                   rules_hash, layout_fingerprint, seed, rating_algorithm_id, target_ticks, roster_subjects, 'Started', 0
              FROM ghostlap.match_session WHERE match_id = @source
            """);
        insert.Parameters.AddWithValue("id", directId);
        insert.Parameters.AddWithValue("room", Guid.NewGuid());
        insert.Parameters.AddWithValue("source", existing.MatchId);
        var directInsert = insert.ExecuteNonQueryAsync(ct);
        var storePid = await WaitBlockedAsync("verify-start", replacementPid, ct);
        var directPid = await WaitBlockedAsync("verify-direct-start", replacementPid, ct);
        Assert.Equal("advisory", await WaitEventAsync(storePid));
        Assert.Equal("advisory", await WaitEventAsync(directPid));
        await AssertGenerationAsync(old, "Active:-");
        await AssertGenerationAsync(next, null);
        await AssertReceiptAsync(existing, "Started", "Started:0:-");
        Assert.Null(await ReceiptSnapshotAsync(stale.MatchId));
        Assert.Null(await ReceiptSnapshotAsync(directId));

        await gate.ReleaseAsync(ct);
        Assert.Equal(new GenerationRegistration(next, 1, 1), await registering);
        Assert.Equal(StartReceiptOutcome.GenerationRetired, (await storeInsert).Outcome);
        var rejected = await Assert.ThrowsAsync<PostgresException>(async () => { await directInsert; });
        Assert.Equal("GL002", rejected.SqlState);
        Assert.Contains($"generation {old:D} is not active", rejected.MessageText);
        await AssertGenerationAsync(old, $"Retired:{next:D}");
        await AssertGenerationAsync(next, "Active:-");
        await AssertReceiptAsync(existing, "Started", "Aborted:1:generation_retired");
        Assert.Null(await ReceiptSnapshotAsync(stale.MatchId));
        Assert.Null(await ReceiptSnapshotAsync(directId));
        await StartReceiptAsync(starter, Seal(next), ct);
    }

    [Fact]
    public async Task Rolled_back_insert_releases_the_advisory_lock_without_leaving_a_receipt()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        await using var replacementSource = RealtimeSource("verify-replacement");
        await using var startSource = RealtimeSource("verify-start");
        var replacement = new MatchAuthorityStore(replacementSource);
        var starter = new MatchAuthorityStore(startSource);
        var old = Guid.NewGuid();
        var next = Guid.NewGuid();
        await starter.RegisterGenerationAsync(old, ct);
        var existing = Seal(old);
        await StartReceiptAsync(starter, existing, ct);
        var rolledBack = Seal(old);

        await using var gate = await PauseAtAsync(
            "start_rollback", "AFTER INSERT ON ghostlap.match_session", $"NEW.match_id = '{rolledBack.MatchId:D}'",
            106, ct, failAfterRelease: true);
        var inserting = starter.CommitStartReceiptAsync(rolledBack, old, ct);
        var startPid = await WaitBlockedAsync("verify-start", gate.BackendPid, ct);
        var registering = replacement.RegisterGenerationAsync(next, ct);
        var replacementPid = await WaitBlockedAsync("verify-replacement", startPid, ct);
        Assert.Equal("advisory", await WaitEventAsync(replacementPid));
        await AssertGenerationAsync(old, "Active:-");
        await AssertGenerationAsync(next, null);
        Assert.Null(await ReceiptSnapshotAsync(rolledBack.MatchId));

        await gate.ReleaseAsync(ct);
        Assert.Equal(StartReceiptOutcome.Unavailable, (await inserting).Outcome);
        Assert.Equal(new GenerationRegistration(next, 1, 1), await registering);
        Assert.Null(await ReceiptSnapshotAsync(rolledBack.MatchId));
        await AssertReceiptAsync(existing, "Started", "Aborted:1:generation_retired");
        await AssertGenerationAsync(old, $"Retired:{next:D}");
        await AssertGenerationAsync(next, "Active:-");
    }

    [Fact]
    public async Task Concurrent_normal_starts_share_the_lock_and_identical_retries_are_idempotent()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        await using var startSource = RealtimeSource("verify-start");
        await using var otherSource = RealtimeSource("verify-other-start");
        var starter = new MatchAuthorityStore(startSource);
        var other = new MatchAuthorityStore(otherSource);
        var generation = Guid.NewGuid();
        await starter.RegisterGenerationAsync(generation, ct);
        var held = Seal(generation);
        var independent = new[] { Seal(generation), Seal(generation) };

        await using var gate = await PauseAtAsync(
            "shared_starts", "AFTER INSERT ON ghostlap.match_session", $"NEW.match_id = '{held.MatchId:D}'", 107, ct);
        var inserting = starter.CommitStartReceiptAsync(held, generation, ct);
        var startPid = await WaitBlockedAsync("verify-start", gate.BackendPid, ct);
        var duplicate = other.CommitStartReceiptAsync(held, generation, ct);
        var duplicatePid = await WaitBlockedAsync("verify-other-start", startPid, ct);
        Assert.Equal("transactionid", await WaitEventAsync(duplicatePid));

        // Two different IDs can commit while both attempts for the first ID are still in flight.
        await Task.WhenAll(independent.Select(config => StartReceiptAsync(other, config, ct))).WaitAsync(TimeSpan.FromSeconds(5), ct);
        foreach (var config in independent)
        {
            await AssertReceiptAsync(config, "absent", "Started:0:-");
        }
        Assert.Null(await ReceiptSnapshotAsync(held.MatchId));
        Assert.False(duplicate.IsCompleted);

        await gate.ReleaseAsync(ct);
        Assert.Equal(StartReceiptOutcome.Committed, (await inserting).Outcome);
        Assert.Equal(StartReceiptOutcome.AlreadyStarted, (await duplicate).Outcome);
        await AssertReceiptAsync(held, "absent", "Started:0:-");
        foreach (var config in independent)
        {
            Assert.Equal(StartReceiptOutcome.AlreadyStarted, (await other.CommitStartReceiptAsync(config, generation, ct)).Outcome);
        }
        await AssertGenerationAsync(generation, "Active:-");
    }
}
