using System.Security.Cryptography;
using GhostLap.Multiplayer;
using Integration.Tests.Support;
using Npgsql;
using Xunit;

namespace Integration.Tests.GhostLap;

/// <summary>
/// Task 10.2 durable fences over real PostgreSQL 18 migrated by <c>scripts/db/migrate.sh</c>, through the deployed Realtime login
/// (<c>sands_realtime</c>, whose only Ghost Lap role is <c>ghostlap_mp_runtime</c>). Superuser connections only inspect and prove guards.
/// </summary>
[Collection(IdentityCollection.Name)]
[Trait("Category", "Integration")]
public sealed class MatchAuthorityStoreTests(IdentityDatabase db) : IAsyncLifetime
{
    private NpgsqlDataSource _realtime = null!;
    private MatchAuthorityStore _store = null!;

    public Task InitializeAsync()
    {
        _realtime = NpgsqlDataSource.Create(db.RealtimeConnectionString);
        _store = new MatchAuthorityStore(_realtime);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _realtime.DisposeAsync();

    private static SealedRoundConfig Seal(Guid generation, int players = 2, int ordinal = 1)
    {
        var roster = Enumerable.Range(0, players).Select(i => new RosterEntry($"m{i}", "guest:" + Guid.NewGuid().ToString("D"))).ToArray();
        return RoundSealing.Seal(RandomNumberGenerator.GetBytes(32), Guid.CreateVersion7(), ordinal, roster, new MultiplayerOptions(), generation);
    }

    private async Task<Guid> RegisterAsync()
    {
        var generation = Guid.CreateVersion7();
        await _store.RegisterGenerationAsync(generation, default);
        return generation;
    }

    /// <summary>
    /// **Validates: Requirements 10.02, 10.05, 10.15** Receipt before running, idempotent retry, conflicting duplicate refused,
    /// predecessor retirement aborts unfinished receipts atomically, a retired generation can neither start nor decide a match.
    /// </summary>
    [Fact]
    public async Task Receipts_are_idempotent_and_a_successor_generation_retires_and_aborts_its_predecessors_atomically()
    {
        var first = await RegisterAsync();
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM ghostlap.realtime_generation WHERE status = 'Active'"));
        var config = Seal(first, players: 3);
        var (committed, receipt) = await _store.CommitStartReceiptAsync(config, first, default);
        Assert.Equal(StartReceiptOutcome.Committed, committed);
        Assert.Equal((TerminalDecisions.Started, 0, first), (receipt!.TerminalDecision, receipt.TerminalVersion, receipt.GenerationId));
        Assert.Equal(StartReceiptOutcome.AlreadyStarted, (await _store.CommitStartReceiptAsync(config, first, default)).Outcome);
        Assert.Equal(StartReceiptOutcome.Conflict, (await _store.CommitStartReceiptAsync(config with { ConfigDigest = new string('a', 64) }, first, default)).Outcome);
        Assert.Equal(
            string.Join(",", config.Slots.Select(s => s.Subject)),
            await db.ScalarAsync<string>("SELECT array_to_string(roster_subjects, ',') FROM ghostlap.match_session WHERE match_id = @m", ("m", config.MatchId)));
        Assert.Equal(config.RatingAlgorithmId, await db.ScalarAsync<string>(
            "SELECT rating_algorithm_id FROM ghostlap.match_session WHERE match_id = @m", ("m", config.MatchId)));

        // A committed abort of another receipt of the first generation must survive the retirement unchanged.
        var aborted = Seal(first);
        await _store.CommitStartReceiptAsync(aborted, first, default);
        Assert.Equal(AbortOutcome.Aborted, (await _store.AbortAsync(aborted.MatchId, first, AbortReasons.Overload, default)).Outcome);

        var second = Guid.CreateVersion7();
        var registration = await _store.RegisterGenerationAsync(second, default);
        Assert.True(registration.RetiredGenerations >= 1);
        Assert.True(registration.AbortedReceipts >= 1);
        Assert.Equal("Retired", await _store.GenerationStatusAsync(first, default));
        Assert.Equal("Active", await _store.GenerationStatusAsync(second, default));
        var after = await _store.FindReceiptAsync(config.MatchId, default);
        Assert.Equal((TerminalDecisions.Aborted, AbortReasons.GenerationRetired, 1), (after!.TerminalDecision, after.AbortReason, after.TerminalVersion));
        Assert.Equal(AbortReasons.Overload, (await _store.FindReceiptAsync(aborted.MatchId, default))!.AbortReason);

        // The retired generation: no new start, and no decision (it gets the committed one back).
        Assert.Equal(StartReceiptOutcome.GenerationRetired, (await _store.CommitStartReceiptAsync(Seal(first), first, default)).Outcome);
        var stale = await _store.AbortAsync(config.MatchId, first, AbortReasons.Overload, default);
        Assert.Equal((AbortOutcome.AlreadyTerminal, AbortReasons.GenerationRetired), (stale.Outcome, stale.Receipt!.AbortReason));
        Assert.Equal(StartReceiptOutcome.GenerationRetired, (await _store.CommitStartReceiptAsync(config, first, default)).Outcome);

        // The successor's own receipts work; another generation cannot abort them; an unknown generation cannot start.
        var fresh = Seal(second);
        Assert.Equal(StartReceiptOutcome.Committed, (await _store.CommitStartReceiptAsync(fresh, second, default)).Outcome);
        Assert.Equal(AbortOutcome.NotAuthority, (await _store.AbortAsync(fresh.MatchId, first, AbortReasons.Overload, default)).Outcome);
        Assert.Equal(AbortOutcome.Aborted, (await _store.AbortAsync(fresh.MatchId, second, AbortReasons.Overload, default)).Outcome);
        Assert.Equal(StartReceiptOutcome.Terminal, (await _store.CommitStartReceiptAsync(fresh, second, default)).Outcome);
        Assert.Equal(AbortOutcome.NotFound, (await _store.AbortAsync(Guid.NewGuid(), second, AbortReasons.Overload, default)).Outcome);
        Assert.Equal(StartReceiptOutcome.GenerationRetired, (await _store.CommitStartReceiptAsync(Seal(Guid.NewGuid()), Guid.NewGuid(), default)).Outcome);
    }

    /// <summary>
    /// **Validates: Requirements 10.05** Start-versus-retirement races (both orders, 24 rounds): after both transactions, no Started receipt
    /// exists under a retired generation. The insert either failed (retirement first) or committed and was aborted by the retirement.
    /// </summary>
    [Fact]
    public async Task A_start_racing_a_successor_never_leaves_a_live_receipt_under_a_retired_generation()
    {
        var outcomes = new List<StartReceiptOutcome>();
        for (var round = 0; round < 24; round++)
        {
            var old = await RegisterAsync();
            var config = Seal(old);
            // Alternate which transaction is launched first so both commit orders occur.
            Task<(StartReceiptOutcome, MatchReceipt?)> start;
            Task<GenerationRegistration> successor;
            if (round % 2 == 0)
            {
                start = Task.Run(() => _store.CommitStartReceiptAsync(config, old, default));
                successor = Task.Run(() => _store.RegisterGenerationAsync(Guid.CreateVersion7(), default));
            }
            else
            {
                successor = Task.Run(() => _store.RegisterGenerationAsync(Guid.CreateVersion7(), default));
                await Task.Delay(round % 4 == 1 ? 0 : 2);
                start = Task.Run(() => _store.CommitStartReceiptAsync(config, old, default));
            }
            await Task.WhenAll(start, successor);
            outcomes.Add((await start).Item1);
            var receipt = await _store.FindReceiptAsync(config.MatchId, default);
            if ((await start).Item1 == StartReceiptOutcome.Committed)
            {
                Assert.Equal((TerminalDecisions.Aborted, AbortReasons.GenerationRetired), (receipt!.TerminalDecision, receipt.AbortReason));
            }
            else
            {
                Assert.Equal(StartReceiptOutcome.GenerationRetired, (await start).Item1);
                Assert.Null(receipt);
            }
        }
        Assert.Equal(0L, await db.ScalarAsync<long>(
            "SELECT count(*) FROM ghostlap.match_session s JOIN ghostlap.realtime_generation g USING (generation_id) "
            + "WHERE s.terminal_decision = 'Started' AND g.status = 'Retired'"));
        Console.WriteLine("START-RACE " + string.Join(",", outcomes.GroupBy(o => o).Select(g => $"{g.Key}={g.Count()}")));
    }

    /// <summary>
    /// **Validates: Requirements 10.05, 10.15** An abort by the old generation racing its retirement (24 rounds): exactly one terminal
    /// decision is written, whichever locks the receipt row first, and the loser sees it instead of overwriting it.
    /// </summary>
    [Fact]
    public async Task An_old_generation_abort_racing_retirement_yields_exactly_one_terminal_decision()
    {
        var winners = new List<string>();
        for (var round = 0; round < 24; round++)
        {
            var old = await RegisterAsync();
            var config = Seal(old);
            await _store.CommitStartReceiptAsync(config, old, default);
            var abort = Task.Run(() => _store.AbortAsync(config.MatchId, old, AbortReasons.Overload, default));
            var successor = Task.Run(() => _store.RegisterGenerationAsync(Guid.CreateVersion7(), default));
            await Task.WhenAll(abort, successor);
            var receipt = await _store.FindReceiptAsync(config.MatchId, default);
            Assert.Equal((TerminalDecisions.Aborted, 1), (receipt!.TerminalDecision, receipt.TerminalVersion));
            var (outcome, seen) = await abort;
            if (outcome == AbortOutcome.Aborted)
            {
                Assert.Equal(AbortReasons.Overload, receipt.AbortReason);
            }
            else
            {
                Assert.Equal(AbortOutcome.AlreadyTerminal, outcome);
                Assert.Equal(AbortReasons.GenerationRetired, receipt.AbortReason);
                Assert.Equal(AbortReasons.GenerationRetired, seen!.AbortReason);
            }
            winners.Add(receipt.AbortReason!);
        }
        Console.WriteLine("ABORT-RACE " + string.Join(",", winners.GroupBy(w => w).Select(g => $"{g.Key}={g.Count()}")));
    }

    /// <summary>
    /// **Validates: Requirements 10.20** Least privilege: the Realtime login may insert and change lifecycle columns only; it cannot delete,
    /// rewrite sealed identity, read the solo tables or touch Revenant. The API and Worker logins cannot read multiplayer authority rows.
    /// Even the schema owner cannot delete a receipt or rewrite its identity.
    /// </summary>
    [Fact]
    public async Task Grants_are_least_privilege_and_the_receipt_identity_is_immutable_even_for_the_owner()
    {
        var generation = await RegisterAsync();
        var config = Seal(generation);
        await _store.CommitStartReceiptAsync(config, generation, default);
        var m = config.MatchId.ToString("D");
        foreach (var sql in new[]
                 {
                     $"DELETE FROM ghostlap.match_session WHERE match_id = '{m}'",
                     "DELETE FROM ghostlap.realtime_generation",
                     "TRUNCATE ghostlap.match_session",
                     $"UPDATE ghostlap.match_session SET seed = 1 WHERE match_id = '{m}'",
                     $"UPDATE ghostlap.match_session SET generation_id = generation_id WHERE match_id = '{m}'",
                     "UPDATE ghostlap.realtime_generation SET started_at = now()",
                     "SELECT 1 FROM ghostlap.solo_originals",
                     "SELECT 1 FROM ghostlap.solo_branches",
                     "SELECT 1 FROM revenant.payment",
                     "CREATE TABLE ghostlap.x (a int)",
                 })
        {
            await AssertSqlStateAsync(_realtime, sql, PostgresErrorCodes.InsufficientPrivilege);
        }
        // Allowed columns, forbidden transitions: refused by the fence trigger.
        await AssertSqlStateAsync(_realtime, $"UPDATE ghostlap.match_session SET terminal_decision = 'ResultCommitted', terminal_version = 1, terminal_at = now() WHERE match_id = '{m}'", "GL002");
        await AssertSqlStateAsync(_realtime, $"UPDATE ghostlap.realtime_generation SET status = 'Active' WHERE generation_id = '{generation:D}'", "GL002");
        await AssertSqlStateAsync(_realtime, $"INSERT INTO ghostlap.realtime_generation (generation_id, status) VALUES ('{Guid.NewGuid():D}', 'Active')", "23505");

        foreach (var login in new[] { db.AppConnectionString, db.WorkerConnectionString })
        {
            await using var other = NpgsqlDataSource.Create(login);
            await AssertSqlStateAsync(other, "SELECT 1 FROM ghostlap.match_session", PostgresErrorCodes.InsufficientPrivilege);
            await AssertSqlStateAsync(other, "SELECT 1 FROM ghostlap.realtime_generation", PostgresErrorCodes.InsufficientPrivilege);
        }

        await AssertSqlStateAsync(db.Owner, $"UPDATE ghostlap.match_session SET seed = 1 WHERE match_id = '{m}'", "GL002");
        await AssertSqlStateAsync(db.Owner, $"DELETE FROM ghostlap.match_session WHERE match_id = '{m}'", "GL001");
        await AssertSqlStateAsync(db.Owner, $"UPDATE ghostlap.match_session SET terminal_decision = 'Aborted', terminal_version = 1, abort_reason = 'x', terminal_at = now(), room_id = gen_random_uuid() WHERE match_id = '{m}'", "GL002");
        Assert.Equal(TerminalDecisions.Started, (await _store.FindReceiptAsync(config.MatchId, default))!.TerminalDecision);
    }

    private static async Task AssertSqlStateAsync(NpgsqlDataSource source, string sql, string sqlState)
    {
        await using var command = source.CreateCommand(sql);
        var e = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.True(e.SqlState == sqlState, $"{sql}: expected {sqlState}, got {e.SqlState}");
    }
}
