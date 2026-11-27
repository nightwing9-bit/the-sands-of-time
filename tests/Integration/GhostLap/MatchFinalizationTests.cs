extern alias realtime;
using System.Security.Cryptography;
using GhostLap.Contracts.Multiplayer;
using GhostLap.Multiplayer;
using Integration.Tests.Support;
using Marrow.Infrastructure.Messaging;
using Marrow.Infrastructure.Realtime;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using realtime::GhostLapHost;
using Xunit;
using Xunit.Abstractions;

namespace Integration.Tests.GhostLap;

/// <summary>
/// Task 10.4 over real PostgreSQL 18 migrated by <c>scripts/db/migrate.sh</c>, through the deployed Realtime login (<c>sands_realtime</c>, role
/// <c>ghostlap_mp_runtime</c>). Results are built from real 2700-tick runs of the production engine. Superuser connections only inspect, stage
/// lock interleavings (advisory-lock gates in test-only triggers) and prove what even the schema owner cannot do.
/// </summary>
[Collection(IdentityCollection.Name)]
[Trait("Category", "Integration")]
public sealed partial class MatchFinalizationTests(IdentityDatabase db, ITestOutputHelper output) : IAsyncLifetime
{
    private NpgsqlDataSource _realtime = null!;
    private MatchAuthorityStore _store = null!;

    public Task InitializeAsync()
    {
        _realtime = Source("verify-finalize");
        _store = Store(_realtime);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _realtime.DisposeAsync();

    /// <summary>
    /// **Validates: Requirements 10.13, 10.14** One transaction commits the result, the one MatchFinalizedV1 outbox row and the ResultCommitted
    /// decision. An identical retry (lost acknowledgement) returns the committed result; a different payload is a visible conflict; a later abort
    /// or a successor's retirement leaves the committed result untouched.
    /// </summary>
    [Fact]
    public async Task A_finished_match_commits_result_outbox_and_decision_together_and_retries_are_idempotent()
    {
        var generation = await RegisterAsync();
        var config = Seal(generation, 3);
        await StartAsync(_store, config);
        var record = Play(config, true, true, false);
        Assert.Equal([1, 1, 3], record.Document.Players.Select(p => p.CompetitivePlacement));

        Assert.Equal(FinalizeOutcome.Committed, (await _store.FinalizeAsync(record, generation, TimeProvider.System, default)).Outcome);
        var id = config.MatchId;
        Assert.Equal("ResultCommitted:1:-", await ReceiptAsync(id));
        Assert.Equal((1L, 1L), await CountsAsync(id));
        Assert.Equal(
            $"{record.Digest}|{config.RatingAlgorithmId}|{{1,1,3}}|{{f,f,t}}|t|eligible|{config.ConfigDigest}",
            await db.ScalarAsync<string>(
                """
                SELECT concat_ws('|', result_digest, rating_algorithm_id, placements::text, forfeited::text, rated_eligible, eligibility_reason, config_digest)
                  FROM ghostlap.match_result WHERE match_id = @m
                """, ("m", id)));
        var outbox = await db.ScalarAsync<string>(
            "SELECT concat_ws('|', event_id, event_type, event_version, status, correlation_id) FROM ghostlap.outbox WHERE aggregate_id = @a",
            ("a", id.ToString("D")));
        Assert.Equal($"{MatchResults.FinalizedEventId(id):D}|{MatchResultContract.FinalizedEventType}|1|pending|{id:N}", outbox);
        var payload = MatchResults.PayloadFromJson((await db.ScalarAsync<string>(
            "SELECT payload FROM ghostlap.outbox WHERE aggregate_id = @a", ("a", id.ToString("D"))))!);
        Assert.Equal(record.Digest, payload.ResultDigest);
        Assert.Equal(record.Digest, MatchResults.Digest(payload.Result)); // a consumer can recompute the digest from the event alone
        Assert.Equal(config.RatingAlgorithmId, payload.Result.RatingAlgorithmId);

        Assert.Equal(FinalizeOutcome.AlreadyCommitted, (await _store.FinalizeAsync(record, generation, TimeProvider.System, default)).Outcome);
        var tampered = Variant(record);
        Assert.Equal(FinalizeOutcome.Conflict, (await _store.FinalizeAsync(tampered, generation, TimeProvider.System, default)).Outcome);
        Assert.Equal((1L, 1L), await CountsAsync(id));
        Assert.Equal(record.Digest, await db.ScalarAsync<string>("SELECT result_digest FROM ghostlap.match_result WHERE match_id = @m", ("m", id)));

        var abort = await _store.AbortAsync(id, generation, AbortReasons.Overload, default);
        Assert.Equal((AbortOutcome.AlreadyTerminal, TerminalDecisions.ResultCommitted), (abort.Outcome, abort.Receipt?.TerminalDecision));
        var registration = await _store.RegisterGenerationAsync(Guid.CreateVersion7(), default);
        Assert.Equal(0, registration.AbortedReceipts);
        Assert.Equal("ResultCommitted:1:-", await ReceiptAsync(id));
        Assert.Equal((1L, 1L), await CountsAsync(id));
        // The retired generation can still learn the truth; it cannot change it.
        Assert.Equal(FinalizeOutcome.AlreadyCommitted, (await _store.FinalizeAsync(record, generation, TimeProvider.System, default)).Outcome);
        var outcome = await _store.FindOutcomeAsync(id, default);
        Assert.Equal((TerminalDecisions.ResultCommitted, record.Digest), (outcome!.Receipt.TerminalDecision, outcome.ResultDigest));
        Assert.Equal(record.Digest, MatchResults.Digest(MatchResults.DocumentFromJson(outcome.ResultDocumentJson!)));
        Assert.Null(await _store.FindOutcomeAsync(Guid.NewGuid(), default)); // unknown is NotFound, never "aborted"
    }

    /// <summary>
    /// **Validates: Requirements 10.05, 10.09, 10.14, 10.15** An Aborted receipt (overload abort, or a successor's retirement) never gets a result or
    /// a rankable event, not through the store and not through direct SQL as the owner. A foreign generation cannot decide; an unknown match is NotFound.
    /// </summary>
    [Fact]
    public async Task Aborted_receipts_never_get_a_result_or_a_rankable_event()
    {
        var generation = await RegisterAsync();
        var overloaded = Seal(generation);
        await StartAsync(_store, overloaded);
        var record = Play(overloaded);
        Assert.Equal(AbortOutcome.Aborted, (await _store.AbortAsync(overloaded.MatchId, generation, AbortReasons.Overload, default)).Outcome);
        Assert.Equal(FinalizeOutcome.Aborted, (await _store.FinalizeAsync(record, generation, TimeProvider.System, default)).Outcome);
        Assert.Equal((0L, 0L), await CountsAsync(overloaded.MatchId));
        var direct = await Assert.ThrowsAsync<PostgresException>(() => InsertResultAsOwnerAsync(record, outbox: null, commitReceipt: false));
        Assert.Equal("GL002", direct.SqlState);

        var orphan = Seal(generation);
        await StartAsync(_store, orphan);
        var successor = await RegisterAsync();
        Assert.Equal("Aborted:1:generation_retired", await ReceiptAsync(orphan.MatchId));
        Assert.Equal(FinalizeOutcome.Aborted, (await _store.FinalizeAsync(Play(orphan), generation, TimeProvider.System, default)).Outcome);
        Assert.Equal((0L, 0L), await CountsAsync(orphan.MatchId));

        var foreign = Seal(successor);
        await StartAsync(_store, foreign);
        Assert.Equal(FinalizeOutcome.NotAuthority, (await _store.FinalizeAsync(Play(foreign), generation, TimeProvider.System, default)).Outcome);
        Assert.Equal("Started:0:-", await ReceiptAsync(foreign.MatchId));
        var unknown = Play(Seal(successor));
        Assert.Equal(FinalizeOutcome.NotFound, (await _store.FinalizeAsync(unknown, successor, TimeProvider.System, default)).Outcome);
        Assert.Equal(0L, await GlobalInvariantViolationsAsync());
    }

    /// <summary>
    /// **Validates: Requirements 10.14** Eight concurrent finalizers (four identical, four with a different payload): exactly one commits, the
    /// identical ones see AlreadyCommitted and the different ones fail visibly as Conflict. One result row and one outbox row.
    /// </summary>
    [Fact]
    public async Task Concurrent_and_conflicting_finalizers_commit_exactly_one_result()
    {
        var generation = await RegisterAsync();
        var config = Seal(generation, 4);
        await StartAsync(_store, config);
        var a = Play(config);
        var b = Variant(a);
        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            Task.Run(() => _store.FinalizeAsync(i % 2 == 0 ? a : b, generation, TimeProvider.System, default))));
        var outcomes = attempts.Select(x => x.Outcome).ToArray();
        Assert.Single(outcomes, o => o == FinalizeOutcome.Committed);
        var winner = await db.ScalarAsync<string>("SELECT result_digest FROM ghostlap.match_result WHERE match_id = @m", ("m", config.MatchId));
        for (var i = 0; i < 8; i++)
        {
            var mine = i % 2 == 0 ? a : b;
            Assert.Contains(outcomes[i], mine.Digest == winner
                ? new[] { FinalizeOutcome.Committed, FinalizeOutcome.AlreadyCommitted }
                : new[] { FinalizeOutcome.Conflict });
        }
        Assert.Equal((1L, 1L), await CountsAsync(config.MatchId));
        output.WriteLine("CONCURRENT-FINALIZE " + string.Join(",", outcomes.GroupBy(o => o).Select(g => $"{g.Key}={g.Count()}")));
    }

    /// <summary>
    /// **Validates: Requirements 10.13, 10.14** Real forfeits from the engine: an all-forfeit match finalizes as a valid unrated result
    /// (no_finisher) and a match with one finisher among distinct guests is rated. Both shapes pass the database's placement check.
    /// </summary>
    [Fact]
    public async Task Unrated_all_forfeit_and_rated_single_finisher_results_commit_with_their_eligibility()
    {
        var generation = await RegisterAsync();
        var allForfeit = Seal(generation, 2);
        var single = Seal(generation, 3);
        await StartAsync(_store, allForfeit);
        await StartAsync(_store, single);
        var unrated = Play(allForfeit, false, false);
        var rated = Play(single, true, false, false);
        Assert.Equal([451L, 451L], unrated.Document.Players.Select(p => p.ForfeitTick));
        Assert.Equal(FinalizeOutcome.Committed, (await _store.FinalizeAsync(unrated, generation, TimeProvider.System, default)).Outcome);
        Assert.Equal(FinalizeOutcome.Committed, (await _store.FinalizeAsync(rated, generation, TimeProvider.System, default)).Outcome);
        Assert.Equal("f|no_finisher|{1,1}|{t,t}", await ResultShapeAsync(allForfeit.MatchId));
        Assert.Equal("t|eligible|{1,2,2}|{f,t,t}", await ResultShapeAsync(single.MatchId));
        Assert.Equal((1L, 1L), await CountsAsync(allForfeit.MatchId)); // an unrated result is still a finalized result with its one event
    }

    /// <summary>
    /// **Validates: Requirements 10.13, 10.14, 10.20** Database guards: a result that does not repeat the sealed rating algorithm id or breaks the
    /// placement policy is refused (store answers Conflict, nothing written); a result without its decision and outbox row, or an event whose
    /// digest differs from the result, cannot commit even as the owner; results and events are immutable; grants are least privilege.
    /// </summary>
    [Fact]
    public async Task Database_guards_refuse_inconsistent_results_and_events_and_grants_are_least_privilege()
    {
        var generation = await RegisterAsync();
        var config = Seal(generation, 3);
        await StartAsync(_store, config);
        var record = Play(config, true, true, false);
        var d = record.Document;

        var otherAlgorithm = Rebuild(d with { RatingAlgorithmId = "elo-pair-v9" });
        Assert.Equal(FinalizeOutcome.Conflict, (await _store.FinalizeAsync(otherAlgorithm, generation, TimeProvider.System, default)).Outcome);
        var badPlacement = Rebuild(d with { Players = [d.Players[0] with { CompetitivePlacement = 2 }, d.Players[1], d.Players[2]] });
        Assert.Equal(FinalizeOutcome.Conflict, (await _store.FinalizeAsync(badPlacement, generation, TimeProvider.System, default)).Outcome);
        var forfeitFirst = Rebuild(d with
        {
            Players = [d.Players[0] with { CompetitivePlacement = 2 }, d.Players[1] with { CompetitivePlacement = 2 }, d.Players[2] with { CompetitivePlacement = 1 }],
        });
        Assert.Equal(FinalizeOutcome.Conflict, (await _store.FinalizeAsync(forfeitFirst, generation, TimeProvider.System, default)).Outcome);
        Assert.Equal((0L, 0L), await CountsAsync(config.MatchId));

        var resultOnly = await Assert.ThrowsAsync<PostgresException>(() => InsertResultAsOwnerAsync(record, outbox: null, commitReceipt: false));
        Assert.Equal("GL003", resultOnly.SqlState);
        var wrongEvent = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertResultAsOwnerAsync(record, outbox: Variant(record).PayloadJson(), commitReceipt: true));
        Assert.Equal("GL003", wrongEvent.SqlState);
        await AssertSqlStateAsync(_realtime,
            $"UPDATE ghostlap.match_session SET terminal_decision = 'ResultCommitted', terminal_version = 1, terminal_at = now() WHERE match_id = '{config.MatchId:D}'",
            "GL002");
        Assert.Equal("Started:0:-", await ReceiptAsync(config.MatchId));
        Assert.Equal((0L, 0L), await CountsAsync(config.MatchId));

        // Nothing partial was left behind: the real finalizer still commits.
        Assert.Equal(FinalizeOutcome.Committed, (await _store.FinalizeAsync(record, generation, TimeProvider.System, default)).Outcome);
        var m = config.MatchId.ToString("D");
        await AssertSqlStateAsync(_realtime, $"UPDATE ghostlap.match_result SET rated_eligible = false WHERE match_id = '{m}'", PostgresErrorCodes.InsufficientPrivilege);
        await AssertSqlStateAsync(_realtime, $"DELETE FROM ghostlap.match_result WHERE match_id = '{m}'", PostgresErrorCodes.InsufficientPrivilege);
        await AssertSqlStateAsync(_realtime, $"SELECT payload FROM ghostlap.outbox WHERE aggregate_id = '{m}'", PostgresErrorCodes.InsufficientPrivilege);
        await AssertSqlStateAsync(_realtime, $"UPDATE ghostlap.outbox SET status = 'published' WHERE aggregate_id = '{m}'", PostgresErrorCodes.InsufficientPrivilege);
        await AssertSqlStateAsync(_realtime, "DELETE FROM ghostlap.outbox", PostgresErrorCodes.InsufficientPrivilege);
        await AssertSqlStateAsync(db.Owner, $"UPDATE ghostlap.match_result SET rated_eligible = false WHERE match_id = '{m}'", "GL001");
        await AssertSqlStateAsync(db.Owner, $"DELETE FROM ghostlap.match_result WHERE match_id = '{m}'", "GL001");
        await AssertSqlStateAsync(db.Owner, $"UPDATE ghostlap.outbox SET payload = '{{}}' WHERE aggregate_id = '{m}'", "GL001");
        await AssertSqlStateAsync(db.Owner, $"DELETE FROM ghostlap.outbox WHERE aggregate_id = '{m}'", "GL001");
        await AssertSqlStateAsync(db.Owner, $"UPDATE ghostlap.match_session SET terminal_decision = 'Aborted', abort_reason = 'x' WHERE match_id = '{m}'", "GL002");
        foreach (var login in new[] { db.AppConnectionString, db.WorkerConnectionString })
        {
            await using var other = NpgsqlDataSource.Create(login);
            await AssertSqlStateAsync(other, "SELECT 1 FROM ghostlap.match_result", PostgresErrorCodes.InsufficientPrivilege);
            await AssertSqlStateAsync(other, "SELECT 1 FROM ghostlap.outbox", PostgresErrorCodes.InsufficientPrivilege);
        }
        // The relay role: claim/confirm columns only; no result or receipt access, no payload rewrite.
        await AsRelayAsync($"UPDATE ghostlap.outbox SET attempts = attempts, lease_token = NULL WHERE aggregate_id = '{m}'", null);
        await AsRelayAsync("SELECT count(*) FROM ghostlap.outbox", null);
        await AsRelayAsync("SELECT 1 FROM ghostlap.match_result", PostgresErrorCodes.InsufficientPrivilege);
        await AsRelayAsync("SELECT 1 FROM ghostlap.match_session", PostgresErrorCodes.InsufficientPrivilege);
        await AsRelayAsync($"UPDATE ghostlap.outbox SET payload = '{{}}' WHERE aggregate_id = '{m}'", PostgresErrorCodes.InsufficientPrivilege);
        await AsRelayAsync("SELECT 1 FROM revenant.outbox", PostgresErrorCodes.InsufficientPrivilege);
        Assert.Equal((1L, 1L), await CountsAsync(config.MatchId));
        Assert.Equal(0L, await GlobalInvariantViolationsAsync());
    }

    /// <summary>
    /// **Validates: Requirements 10.05, 10.14, 10.15** Unordered races (16 rounds) between an old generation's finalizer and a successor's
    /// registration: every round ends with exactly one durable decision. ResultCommitted always has its result and outbox row; Aborted has neither.
    /// </summary>
    [Fact]
    public async Task Racing_finalize_and_retirement_always_leave_exactly_one_truthful_decision()
    {
        var tally = new List<string>();
        for (var round = 0; round < 16; round++)
        {
            var old = await RegisterAsync();
            var config = Seal(old);
            await StartAsync(_store, config);
            var record = Play(config);
            Task<(FinalizeOutcome Outcome, MatchReceipt? Receipt)> finalize;
            Task<GenerationRegistration> successor;
            if (round % 2 == 0)
            {
                finalize = Task.Run(() => _store.FinalizeAsync(record, old, TimeProvider.System, default));
                successor = Task.Run(() => _store.RegisterGenerationAsync(Guid.CreateVersion7(), default));
            }
            else
            {
                successor = Task.Run(() => _store.RegisterGenerationAsync(Guid.CreateVersion7(), default));
                await Task.Delay(round % 4 == 1 ? 1 : 5); // give the successor a head start so both commit orders occur
                finalize = Task.Run(() => _store.FinalizeAsync(record, old, TimeProvider.System, default));
            }
            await Task.WhenAll(finalize, successor);
            var decision = await ReceiptAsync(config.MatchId);
            var outcome = (await finalize).Outcome;
            if (decision == "ResultCommitted:1:-")
            {
                Assert.Equal(FinalizeOutcome.Committed, outcome);
                Assert.Equal((1L, 1L), await CountsAsync(config.MatchId));
            }
            else
            {
                Assert.Equal("Aborted:1:generation_retired", decision);
                Assert.Equal(FinalizeOutcome.Aborted, outcome);
                Assert.Equal((0L, 0L), await CountsAsync(config.MatchId));
            }
            tally.Add(outcome.ToString());
        }
        Assert.Equal(0L, await GlobalInvariantViolationsAsync());
        output.WriteLine("FINALIZE-RACE " + string.Join(",", tally.GroupBy(t => t).Select(g => $"{g.Key}={g.Count()}")));
    }

    /// <summary>
    /// **Validates: Requirements 10.20** The executor provisions the Worker's Ghost Lap relay login with <c>ghostlap_outbox_relay</c> only: it can
    /// read and claim outbox rows and nothing else (no result, receipt, solo or Revenant access). Other logins are untouched by this run.
    /// </summary>
    [Fact]
    public async Task The_executor_provisions_a_least_privilege_ghostlap_relay_login()
    {
        var password = "ghostlapRelay" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
        var (exit, log) = await db.TryRunMigratorAsync($"WORKER_GHOSTLAP_DB_PASSWORD={password}");
        Assert.True(exit == 0, log);
        Assert.Contains("runtime login sands_worker_ghostlap: provisioned (ghostlap_outbox_relay)", log, StringComparison.Ordinal);
        Assert.DoesNotContain(password, log, StringComparison.Ordinal);
        Assert.Equal("ghostlap_outbox_relay", await db.ScalarAsync<string>(
            "SELECT string_agg(g.rolname, ',') FROM pg_auth_members m JOIN pg_roles g ON g.oid = m.roleid JOIN pg_roles u ON u.oid = m.member WHERE u.rolname = 'sands_worker_ghostlap'"));
        Assert.Equal("ghostlap_mp_runtime,marrow_identity_runtime", await db.ScalarAsync<string>(
            "SELECT string_agg(g.rolname, ',' ORDER BY g.rolname) FROM pg_auth_members m JOIN pg_roles g ON g.oid = m.roleid JOIN pg_roles u ON u.oid = m.member WHERE u.rolname = 'sands_realtime'"));
        await using var relay = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(db.RealtimeConnectionString)
        {
            Username = "sands_worker_ghostlap",
            Password = password,
        }.ConnectionString);
        await using (var ok = relay.CreateCommand("SELECT count(*) FROM ghostlap.outbox WHERE status = 'pending'"))
        {
            Assert.IsType<long>(await ok.ExecuteScalarAsync());
        }
        foreach (var sql in new[]
                 {
                     "SELECT 1 FROM ghostlap.match_result", "SELECT 1 FROM ghostlap.match_session", "SELECT 1 FROM ghostlap.solo_originals",
                     "SELECT 1 FROM revenant.outbox", "INSERT INTO ghostlap.outbox (event_id) VALUES (gen_random_uuid())",
                 })
        {
            await AssertSqlStateAsync(relay, sql, PostgresErrorCodes.InsufficientPrivilege);
        }
    }

    // ---------------------------------------------------------------- helpers

    internal static MatchAuthorityStore Store(NpgsqlDataSource source) =>
        new(source, tx => new PostgresOutboxWriter(tx, new MessagingTables("ghostlap")));

    private NpgsqlDataSource Source(string app) => NpgsqlDataSource.Create(
        new NpgsqlConnectionStringBuilder(db.RealtimeConnectionString) { ApplicationName = app, CommandTimeout = 10 }.ConnectionString);

    private async Task<Guid> RegisterAsync()
    {
        var generation = Guid.CreateVersion7();
        await _store.RegisterGenerationAsync(generation, default);
        return generation;
    }

    internal static SealedRoundConfig Seal(Guid generation, int players = 2, MultiplayerOptions? options = null) => RoundSealing.Seal(
        RandomNumberGenerator.GetBytes(32), Guid.CreateVersion7(), 1,
        [.. Enumerable.Range(0, players).Select(i => new RosterEntry($"m{i}", "guest:" + Guid.NewGuid()))], options ?? new MultiplayerOptions(), generation);

    private static async Task StartAsync(MatchAuthorityStore store, SealedRoundConfig config) =>
        Assert.Equal(StartReceiptOutcome.Committed, (await store.CommitStartReceiptAsync(config, config.AuthorityGeneration, default)).Outcome);

    /// <summary>All 2700 ticks of the production engine; slot i has a controller unless <paramref name="connected"/> says otherwise (then it forfeits at 451).</summary>
    internal static MatchResultRecord Play(SealedRoundConfig config, params bool[] connected)
    {
        var engine = new MatchEngine(config, new MultiplayerOptions(), TimeProvider.System);
        for (var slot = 0; slot < config.Slots.Count; slot++)
        {
            engine.SetController(new SlotControl(slot, 1, connected.Length == 0 || connected[slot] ? $"c{slot}" : null));
        }
        while (!engine.State.IsFinal)
        {
            engine.Step();
        }
        return MatchResults.Build(config, engine.State, engine.Stats);
    }

    /// <summary>Same match, different payload (one stat changed), valid placements: a conflicting duplicate.</summary>
    private static MatchResultRecord Variant(MatchResultRecord record)
    {
        var d = record.Document;
        return Rebuild(d with { Players = [d.Players[0] with { ChargeLostToContact = d.Players[0].ChargeLostToContact + 1 }, .. d.Players.Skip(1)] });
    }

    private static MatchResultRecord Rebuild(MatchResultDocument d) => new(d, MatchResults.Digest(d));

    private Task<string?> ReceiptAsync(Guid matchId) => db.ScalarAsync<string>(
        "SELECT concat(terminal_decision, ':', terminal_version, ':', coalesce(abort_reason, '-')) FROM ghostlap.match_session WHERE match_id = @m",
        ("m", matchId));

    private async Task<(long Results, long Events)> CountsAsync(Guid matchId) => (
        await db.ScalarAsync<long>("SELECT count(*) FROM ghostlap.match_result WHERE match_id = @m", ("m", matchId)),
        await db.ScalarAsync<long>("SELECT count(*) FROM ghostlap.outbox WHERE aggregate_id = @a", ("a", matchId.ToString("D"))));

    private Task<string?> ResultShapeAsync(Guid matchId) => db.ScalarAsync<string>(
        "SELECT concat_ws('|', rated_eligible, eligibility_reason, placements::text, forfeited::text) FROM ghostlap.match_result WHERE match_id = @m",
        ("m", matchId));

    /// <summary>Across the whole database: results or MatchFinalizedV1 rows whose receipt is not ResultCommitted, or ResultCommitted receipts missing either.</summary>
    internal static Task<long> GlobalInvariantViolationsAsync(IdentityDatabase database) => database.ScalarAsync<long>(
        """
        SELECT (SELECT count(*) FROM ghostlap.match_result r JOIN ghostlap.match_session s USING (match_id) WHERE s.terminal_decision <> 'ResultCommitted')
             + (SELECT count(*) FROM ghostlap.outbox o LEFT JOIN ghostlap.match_session s ON s.match_id::text = o.aggregate_id
                 WHERE o.event_type = 'ghostlap.MatchFinalizedV1' AND s.terminal_decision IS DISTINCT FROM 'ResultCommitted')
             + (SELECT count(*) FROM ghostlap.match_session s WHERE s.terminal_decision = 'ResultCommitted'
                 AND (NOT EXISTS (SELECT 1 FROM ghostlap.match_result r WHERE r.match_id = s.match_id)
                      OR (SELECT count(*) FROM ghostlap.outbox o WHERE o.aggregate_id = s.match_id::text AND o.event_type = 'ghostlap.MatchFinalizedV1') <> 1))
        """);

    private Task<long> GlobalInvariantViolationsAsync() => GlobalInvariantViolationsAsync(db);

    /// <summary>The owner writes a result row by hand (optionally an outbox row and the decision), bypassing the store, then commits.</summary>
    private async Task InsertResultAsOwnerAsync(MatchResultRecord record, string? outbox, bool commitReceipt)
    {
        var d = record.Document;
        var id = record.MatchId;
        await using var connection = await db.Owner.OpenConnectionAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await using (var insert = new NpgsqlCommand(
                         """
                         INSERT INTO ghostlap.match_result (match_id, room_id, generation_id, config_digest, result_schema, result_digest, rating_algorithm_id,
                           roster_subjects, scores, forfeited, placements, final_tick, placement_policy, eligibility_policy, rated_eligible, eligibility_reason,
                           result_document)
                         VALUES (@m, @room, @g, @cfg, @schema, @digest, @rating, @roster, @scores, @forfeited, @placements, @tick, @pp, @ep, @rated, @reason, @doc)
                         """, connection, tx))
        {
            insert.Parameters.AddWithValue("m", id);
            insert.Parameters.AddWithValue("room", Guid.ParseExact(d.RoomId, "N"));
            insert.Parameters.AddWithValue("g", Guid.ParseExact(d.GenerationId, "N"));
            insert.Parameters.AddWithValue("cfg", d.ConfigDigest);
            insert.Parameters.AddWithValue("schema", d.Schema);
            insert.Parameters.AddWithValue("digest", record.Digest);
            insert.Parameters.AddWithValue("rating", d.RatingAlgorithmId);
            insert.Parameters.AddWithValue("roster", d.Players.Select(p => p.Subject).ToArray());
            insert.Parameters.AddWithValue("scores", d.Players.Select(p => p.TotalScore).ToArray());
            insert.Parameters.AddWithValue("forfeited", d.Players.Select(p => p.Outcome == MatchResultContract.OutcomeForfeit).ToArray());
            insert.Parameters.AddWithValue("placements", d.Players.Select(p => p.CompetitivePlacement).ToArray());
            insert.Parameters.AddWithValue("tick", (int)d.FinalTick);
            insert.Parameters.AddWithValue("pp", d.PlacementPolicy);
            insert.Parameters.AddWithValue("ep", d.EligibilityPolicy);
            insert.Parameters.AddWithValue("rated", d.RatedEligible);
            insert.Parameters.AddWithValue("reason", d.EligibilityReason);
            insert.Parameters.AddWithValue("doc", MatchResults.DocumentToJson(d));
            await insert.ExecuteNonQueryAsync();
        }
        if (outbox is not null)
        {
            await new PostgresOutboxWriter(tx, new MessagingTables("ghostlap")).EnqueueAsync(new Marrow.Kernel.Messaging.OutboxMessage(
                MatchResults.FinalizedEventId(id), MatchResultContract.FinalizedEventType, 1, id.ToString("D"), TimeProvider.System.GetUtcNow(),
                outbox, id.ToString("N")), default);
        }
        if (commitReceipt)
        {
            await using var update = new NpgsqlCommand(
                "UPDATE ghostlap.match_session SET terminal_decision = 'ResultCommitted', terminal_version = 1, terminal_at = now() WHERE match_id = @m",
                connection, tx);
            update.Parameters.AddWithValue("m", id);
            await update.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }

    private async Task AsRelayAsync(string sql, string? sqlState)
    {
        await using var connection = await db.Owner.OpenConnectionAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await using (var role = new NpgsqlCommand("SET LOCAL ROLE ghostlap_outbox_relay", connection, tx))
        {
            await role.ExecuteNonQueryAsync();
        }
        await using var command = new NpgsqlCommand(sql, connection, tx);
        if (sqlState is null)
        {
            await command.ExecuteNonQueryAsync();
        }
        else
        {
            var e = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.True(e.SqlState == sqlState, $"{sql}: expected {sqlState}, got {e.SqlState}");
        }
        await tx.RollbackAsync();
    }

    internal static async Task AssertSqlStateAsync(NpgsqlDataSource source, string sql, string sqlState)
    {
        await using var command = source.CreateCommand(sql);
        var e = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.True(e.SqlState == sqlState, $"{sql}: expected {sqlState}, got {e.SqlState}");
    }
}
