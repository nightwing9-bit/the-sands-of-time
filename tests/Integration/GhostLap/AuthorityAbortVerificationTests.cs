extern alias realtime;
using GhostLap.Multiplayer;
using Integration.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using realtime::GhostLapHost;
using Xunit;

namespace Integration.Tests.GhostLap;

[Collection(IdentityCollection.Name)]
[Trait("Category", "Integration")]
public sealed partial class AuthorityAbortVerificationTests(IdentityDatabase db)
{
    [Fact]
    public async Task Replacement_host_recovers_all_orphans_and_preserves_existing_terminal_decisions()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        var secret = IdentityApiFactory.NewMasterSecret();
        Guid old;
        SealedRoundConfig[] orphans;
        SealedRoundConfig decided;
        string? decision;
        await using (var predecessor = new RealtimeHostFactory(db, secret))
        {
            var authority = predecessor.Services.GetRequiredService<GhostLapRealtimeAuthority>();
            Assert.True(authority.Registered && authority.IsValid);
            old = authority.GenerationId;
            var store = predecessor.Services.GetRequiredService<MatchAuthorityStore>();
            orphans = [Seal(old), Seal(old)];
            decided = Seal(old);
            foreach (var config in orphans.Append(decided))
            {
                await StartReceiptAsync(store, config, ct);
                await AssertReceiptAsync(config, "absent", "Started:0:-");
            }
            Assert.Equal(AbortOutcome.Aborted, (await store.AbortAsync(decided.MatchId, old, AbortReasons.HostStopping, ct)).Outcome);
            decision = await ReceiptSnapshotAsync(decided.MatchId);
        }

        // These committed receipts were never activated by the predecessor, so shutdown cannot decide them.
        foreach (var config in orphans)
        {
            await AssertReceiptAsync(config, "Started", "Started:0:-");
        }

        Guid replacement;
        SealedRoundConfig replacementOrphan;
        string?[] recovered;
        await using (var successor = new RealtimeHostFactory(db, secret))
        {
            var authority = successor.Services.GetRequiredService<GhostLapRealtimeAuthority>();
            Assert.True(authority.Registered && authority.IsValid);
            replacement = authority.GenerationId;
            await AssertGenerationAsync(old, $"Retired:{replacement:D}");
            await AssertGenerationAsync(replacement, "Active:-");
            var store = successor.Services.GetRequiredService<MatchAuthorityStore>();
            foreach (var config in orphans)
            {
                await AssertReceiptAsync(config, "Started", "Aborted:1:generation_retired");
                var stale = await store.AbortAsync(config.MatchId, old, AbortReasons.Overload, ct);
                Assert.Equal((AbortOutcome.AlreadyTerminal, AbortReasons.GenerationRetired), (stale.Outcome, stale.Receipt?.AbortReason));
            }
            recovered = await Task.WhenAll(orphans.Select(config => ReceiptSnapshotAsync(config.MatchId)));
            Assert.Equal(decision, await ReceiptSnapshotAsync(decided.MatchId));

            var staleStart = Seal(old);
            Assert.Equal(StartReceiptOutcome.GenerationRetired, (await store.CommitStartReceiptAsync(staleStart, old, ct)).Outcome);
            Assert.Null(await ReceiptSnapshotAsync(staleStart.MatchId));

            replacementOrphan = Seal(replacement);
            await StartReceiptAsync(store, replacementOrphan, ct);
            Assert.Equal(AbortOutcome.NotAuthority, (await store.AbortAsync(replacementOrphan.MatchId, old, AbortReasons.Overload, ct)).Outcome);

            // Fail after the orphan UPDATE has executed, so retirement, registration and receipt changes all need to roll back.
            var failedGeneration = Guid.NewGuid();
            await ExecuteAsync("""
                CREATE FUNCTION ghostlap.verify_fail_recovery() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION USING ERRCODE = 'P0001', MESSAGE = 'verification: recovery interrupted after receipt updates';
                END $$;
                CREATE TRIGGER verify_fail_recovery AFTER UPDATE ON ghostlap.match_session FOR EACH STATEMENT
                    EXECUTE FUNCTION ghostlap.verify_fail_recovery();
                """, ct);
            try
            {
                var error = await Assert.ThrowsAsync<PostgresException>(() => store.RegisterGenerationAsync(failedGeneration, ct));
                Assert.Equal(("P0001", "verification: recovery interrupted after receipt updates"), (error.SqlState, error.MessageText));
                await AssertGenerationAsync(replacement, "Active:-");
                await AssertGenerationAsync(failedGeneration, null);
                await AssertReceiptAsync(replacementOrphan, "Started", "Started:0:-");
            }
            finally
            {
                await ExecuteAsync("""
                    DROP TRIGGER verify_fail_recovery ON ghostlap.match_session;
                    DROP FUNCTION ghostlap.verify_fail_recovery();
                    """, CancellationToken.None);
            }
        }

        await using var next = new RealtimeHostFactory(db, secret);
        var third = next.Services.GetRequiredService<GhostLapRealtimeAuthority>();
        Assert.True(third.Registered && third.IsValid);
        await AssertGenerationAsync(old, $"Retired:{replacement:D}");
        await AssertGenerationAsync(replacement, $"Retired:{third.GenerationId:D}");
        await AssertGenerationAsync(third.GenerationId, "Active:-");
        await AssertReceiptAsync(replacementOrphan, "Started", "Aborted:1:generation_retired");
        Assert.Equal(recovered, await Task.WhenAll(orphans.Select(config => ReceiptSnapshotAsync(config.MatchId))));
        Assert.Equal(decision, await ReceiptSnapshotAsync(decided.MatchId));
        await AssertNoResultStorageAsync();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Abort_and_replacement_serialize_for_a_receipt_visible_to_the_startup_scan(bool abortFirst)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;
        await using var abortSource = RealtimeSource("verify-abort");
        await using var replacementSource = RealtimeSource("verify-replacement");
        var abortStore = new MatchAuthorityStore(abortSource);
        var replacementStore = new MatchAuthorityStore(replacementSource);
        var old = Guid.NewGuid();
        var next = Guid.NewGuid();
        await abortStore.RegisterGenerationAsync(old, ct);
        var config = Seal(old);
        await StartReceiptAsync(abortStore, config, ct);
        await using var replacementGate = await PauseAtAsync(
            "terminal_order", "AFTER INSERT ON ghostlap.realtime_generation", $"NEW.generation_id = '{next:D}'", 102, ct);

        // Park the winner after it owns the receipt, but before it can get the generation lock.
        await using var blocker = await db.Owner.OpenConnectionAsync(ct);
        await using var tx = await blocker.BeginTransactionAsync(ct);
        await using (var hold = new NpgsqlCommand(
            "SELECT 1 FROM ghostlap.realtime_generation WHERE generation_id = @g FOR NO KEY UPDATE", blocker, tx))
        {
            hold.Parameters.AddWithValue("g", old);
            await hold.ExecuteNonQueryAsync(ct);
        }

        var winner = abortFirst ? "verify-abort" : "verify-replacement";
        var loser = abortFirst ? "verify-replacement" : "verify-abort";
        Task<(AbortOutcome Outcome, MatchReceipt? Receipt)> abort;
        Task<GenerationRegistration> registration;
        if (abortFirst)
        {
            abort = abortStore.AbortAsync(config.MatchId, old, AbortReasons.Overload, ct);
            await WaitBlockedAsync(winner, blocker.ProcessID, ct);
            registration = replacementStore.RegisterGenerationAsync(next, ct);
        }
        else
        {
            registration = replacementStore.RegisterGenerationAsync(next, ct);
            await WaitBlockedAsync(winner, blocker.ProcessID, ct);
            abort = abortStore.AbortAsync(config.MatchId, old, AbortReasons.Overload, ct);
        }

        await WaitBlockedAsync(loser, await BackendPidAsync(winner), ct);
        await AssertGenerationAsync(old, "Active:-");
        await AssertGenerationAsync(next, null);
        await AssertReceiptAsync(config, "Started", "Started:0:-");
        await tx.RollbackAsync(ct);

        await WaitBlockedAsync("verify-replacement", replacementGate.BackendPid, ct);
        var committedAbort = abortFirst ? await ReceiptSnapshotAsync(config.MatchId) : null;
        if (abortFirst)
        {
            await AssertReceiptAsync(config, "Started", "Aborted:1:overload");
        }
        await replacementGate.ReleaseAsync(ct);
        var result = await abort;
        var registered = await registration;
        var expectedReason = abortFirst ? AbortReasons.Overload : AbortReasons.GenerationRetired;
        Assert.Equal((abortFirst ? AbortOutcome.Aborted : AbortOutcome.AlreadyTerminal, expectedReason), (result.Outcome, result.Receipt?.AbortReason));
        Assert.Equal(new GenerationRegistration(next, 1, abortFirst ? 0 : 1), registered);
        await AssertReceiptAsync(config, "Started", $"Aborted:1:{expectedReason}");
        await AssertGenerationAsync(old, $"Retired:{next:D}");
        await AssertGenerationAsync(next, "Active:-");
        if (abortFirst)
        {
            Assert.Equal(committedAbort, await ReceiptSnapshotAsync(config.MatchId));
        }
        var durableDecision = await ReceiptSnapshotAsync(config.MatchId);
        Assert.Equal(AbortOutcome.AlreadyTerminal, (await abortStore.AbortAsync(config.MatchId, old, AbortReasons.HostStopping, ct)).Outcome);
        Assert.Equal(durableDecision, await ReceiptSnapshotAsync(config.MatchId));
    }

    [Fact]
    public async Task Receipt_committing_after_the_startup_scan_does_not_deadlock_with_abort()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        await using var insertSource = RealtimeSource("verify-start");
        await using var replacementSource = RealtimeSource("verify-replacement");
        await using var abortSource = RealtimeSource("verify-abort");
        var starter = new MatchAuthorityStore(insertSource);
        var replacement = new MatchAuthorityStore(replacementSource);
        var aborter = new MatchAuthorityStore(abortSource);
        var old = Guid.NewGuid();
        var next = Guid.NewGuid();
        await starter.RegisterGenerationAsync(old, ct);
        var config = Seal(old);
        var decided = Seal(old);
        await StartReceiptAsync(starter, decided, ct);
        Assert.Equal(AbortOutcome.Aborted, (await aborter.AbortAsync(decided.MatchId, old, AbortReasons.HostStopping, ct)).Outcome);
        var originalDecision = await ReceiptSnapshotAsync(decided.MatchId);

        await using var insertGate = await PauseAtAsync("start", "AFTER INSERT ON ghostlap.match_session", $"NEW.match_id = '{config.MatchId:D}'", 101, ct);
        await using var replacementGate = await PauseAtAsync("replacement", "AFTER INSERT ON ghostlap.realtime_generation", $"NEW.generation_id = '{next:D}'", 102, ct);

        var inserting = starter.CommitStartReceiptAsync(config, old, ct);
        var insertPid = await WaitBlockedAsync("verify-start", insertGate.BackendPid, ct);
        var registering = replacement.RegisterGenerationAsync(next, ct);
        // Keep the original schedule: start replacement while the insert is invisible, then release the insert.
        // With the fix, replacement waits at the advisory lock before scanning for receipts.
        var registrationPid = await WaitBlockedAsync("verify-replacement", insertPid, ct);
        var registrationWait = await WaitEventAsync(registrationPid);
        Assert.Null(await ReceiptSnapshotAsync(config.MatchId));
        await insertGate.ReleaseAsync(ct);
        Assert.Equal(StartReceiptOutcome.Committed, (await inserting).Outcome);

        // Startup now owns the generation update lock, but has not reached the final receipt UPDATE.
        registrationPid = await WaitBlockedAsync("verify-replacement", replacementGate.BackendPid, ct);
        await AssertGenerationAsync(old, "Active:-");
        await AssertGenerationAsync(next, null);
        await AssertReceiptAsync(config, "in-flight insert", "Started:0:-");

        var aborting = aborter.AbortAsync(config.MatchId, old, AbortReasons.Overload, ct);
        await WaitBlockedAsync("verify-abort", registrationPid, ct);
        await replacementGate.ReleaseAsync(ct);

        // Let a registration SQL failure (including 40P01) surface directly. An abort deadlock would return Unavailable.
        Assert.Equal(new GenerationRegistration(next, 1, 1), await registering);
        var abort = await aborting;
        Assert.True(abort.Outcome == AbortOutcome.AlreadyTerminal && abort.Receipt?.AbortReason == AbortReasons.GenerationRetired,
            $"Generation {old:D} -> {next:D}, match {config.MatchId:D} originally Started: abort returned {abort.Outcome} / {abort.Receipt}.");
        Assert.Equal("advisory", registrationWait);
        await AssertGenerationAsync(old, $"Retired:{next:D}");
        await AssertGenerationAsync(next, "Active:-");
        await AssertReceiptAsync(config, "Started", "Aborted:1:generation_retired");
        Assert.Equal(originalDecision, await ReceiptSnapshotAsync(decided.MatchId));
        var stale = Seal(old);
        Assert.Equal(StartReceiptOutcome.GenerationRetired, (await starter.CommitStartReceiptAsync(stale, old, ct)).Outcome);
        Assert.Null(await ReceiptSnapshotAsync(stale.MatchId));
    }

    private static SealedRoundConfig Seal(Guid generation) => RoundSealing.Seal(
        new byte[32], Guid.NewGuid(), 1,
        [new("a", "guest:" + Guid.NewGuid()), new("b", "guest:" + Guid.NewGuid())], new MultiplayerOptions(), generation);

    private static async Task StartReceiptAsync(MatchAuthorityStore store, SealedRoundConfig config, CancellationToken ct)
    {
        var start = await store.CommitStartReceiptAsync(config, config.AuthorityGeneration, ct);
        Assert.True(start.Outcome == StartReceiptOutcome.Committed,
            $"Generation {config.AuthorityGeneration:D}, match {config.MatchId:D}: expected a committed start, observed {start.Outcome} / {start.Receipt}.");
    }

    private async Task AssertReceiptAsync(SealedRoundConfig config, string original, string expected)
    {
        var actual = await db.ScalarAsync<string>(
            """
            SELECT concat(terminal_decision, ':', terminal_version, ':', coalesce(abort_reason, '-'))
              FROM ghostlap.match_session
             WHERE match_id = @m AND generation_id = @g AND config_digest = @digest
            """, ("m", config.MatchId), ("g", config.AuthorityGeneration), ("digest", config.ConfigDigest));
        Assert.True(actual == expected, $"Generation {config.AuthorityGeneration:D}, match {config.MatchId:D}, originally {original}: expected {expected}, observed {actual ?? "absent or wrong sealed identity"}.");
    }

    private Task<string?> ReceiptSnapshotAsync(Guid matchId) => db.ScalarAsync<string>(
        "SELECT to_jsonb(s)::text FROM ghostlap.match_session s WHERE match_id = @m", ("m", matchId));

    private Task<string?> GenerationStateAsync(Guid id) => db.ScalarAsync<string>(
        "SELECT status || ':' || coalesce(retired_by::text, '-') FROM ghostlap.realtime_generation WHERE generation_id = @g", ("g", id));

    private async Task AssertGenerationAsync(Guid id, string? expected)
    {
        var observed = await GenerationStateAsync(id);
        Assert.True(expected == observed, $"Generation {id:D}: expected {expected ?? "absent"}, observed {observed ?? "absent"}.");
    }

    // Task 10.4 added the result and outbox tables. What these abort tests must keep proving is that no abort path ever produced a result or a
    // MatchFinalizedV1 event: across the database, every result/event belongs to a ResultCommitted receipt, and no rating table exists here.
    private async Task AssertNoResultStorageAsync()
    {
        Assert.Equal(0L, await MatchFinalizationTests.GlobalInvariantViolationsAsync(db));
        Assert.Equal(0L, await db.ScalarAsync<long>(
            """
            SELECT count(*) FROM ghostlap.match_result r JOIN ghostlap.match_session s USING (match_id) WHERE s.terminal_decision = 'Aborted'
            """));
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM pg_tables WHERE schemaname = 'ghostlap' AND tablename ~ 'rating'"));
    }

    private NpgsqlDataSource RealtimeSource(string app) => NpgsqlDataSource.Create(
        new NpgsqlConnectionStringBuilder(db.RealtimeConnectionString) { ApplicationName = app, CommandTimeout = 10 }.ConnectionString);

    private Task<int> BackendPidAsync(string app) => db.ScalarAsync<int>(
        "SELECT pid FROM pg_stat_activity WHERE application_name = @app AND state = 'active'", ("app", app));

    private Task<string?> WaitEventAsync(int pid) => db.ScalarAsync<string>(
        "SELECT wait_event FROM pg_stat_activity WHERE pid = @pid", ("pid", pid));

    private async Task<int> WaitBlockedAsync(string app, int blockerPid, CancellationToken ct)
    {
        var blockedPid = 0;
        await UntilAsync(async () =>
            (blockedPid = await db.ScalarAsync<int>(
                "SELECT pid FROM pg_stat_activity WHERE application_name = @app AND @pid = ANY(pg_blocking_pids(pid))",
                ("app", app), ("pid", blockerPid))) != 0, $"{app} never waited on PostgreSQL backend {blockerPid}", ct);
        return blockedPid;
    }

    private static async Task UntilAsync(Func<Task<bool>> condition, string message, CancellationToken ct)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            while (!await condition())
            {
                await Task.Delay(10, wait.Token);
            }
        }
        catch (OperationCanceledException) when (wait.IsCancellationRequested)
        {
            throw new TimeoutException(message);
        }
    }

    private async Task<DatabaseGate> PauseAtAsync(
        string name, string triggerEvent, string condition, int key, CancellationToken ct, bool failAfterRelease = false)
    {
        // The production SQL and schema guards perform all writes. The gate stages the transaction and can inject a rollback.
        var trigger = $"verify_{name}";
        var table = triggerEvent.Split(" ON ")[1];
        var cleanup = $"DROP TRIGGER {trigger} ON {table}; DROP FUNCTION ghostlap.{trigger}()";
        await ExecuteAsync($"""
            CREATE FUNCTION ghostlap.{trigger}() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                PERFORM pg_advisory_xact_lock(642219, {key});
                {(failAfterRelease ? "RAISE EXCEPTION 'verification: receipt insert interrupted';" : "")}
                RETURN NEW;
            END $$;
            CREATE TRIGGER {trigger} {triggerEvent} FOR EACH ROW WHEN ({condition})
                EXECUTE FUNCTION ghostlap.{trigger}();
            """, ct);
        var connection = await db.Owner.OpenConnectionAsync(ct);
        var tx = await connection.BeginTransactionAsync(ct);
        await using var hold = new NpgsqlCommand("SELECT pg_advisory_xact_lock(642219, @key)", connection, tx);
        hold.Parameters.AddWithValue("key", key);
        await hold.ExecuteNonQueryAsync(ct);
        return new DatabaseGate(connection, tx, () => ExecuteAsync(cleanup, CancellationToken.None));
    }

    private async Task ExecuteAsync(string sql, CancellationToken ct)
    {
        await using var command = db.Owner.CreateCommand(sql);
        command.CommandTimeout = 12;
        await command.ExecuteNonQueryAsync(ct);
    }

    private sealed class DatabaseGate(NpgsqlConnection connection, NpgsqlTransaction tx, Func<Task> cleanup) : IAsyncDisposable
    {
        public int BackendPid => connection.ProcessID;

        public Task ReleaseAsync(CancellationToken ct) => tx.RollbackAsync(ct);

        public async ValueTask DisposeAsync()
        {
            await using (connection)
            {
                await tx.DisposeAsync();
            }
            await cleanup();
        }
    }
}
