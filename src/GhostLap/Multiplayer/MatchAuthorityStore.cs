using System.Data;
using GhostLap.Contracts.Multiplayer;
using Marrow.Kernel.Messaging;
using Npgsql;
using NpgsqlTypes;

namespace GhostLap.Multiplayer;

public static class TerminalDecisions
{
    public const string Started = "Started";
    public const string ResultCommitted = "ResultCommitted";
    public const string Aborted = "Aborted";
}

public static class AbortReasons
{
    public const string GenerationRetired = "generation_retired";
    public const string Overload = "overload";
    public const string AuthorityLost = "authority_lost";
    public const string HostStopping = "host_stopping";
    public const string NotActivated = "not_activated";
}

/// <summary>The durable start receipt of one match (<c>ghostlap.match_session</c>). Minimal start identity, not a match log.</summary>
public sealed record MatchReceipt(Guid MatchId, Guid RoomId, Guid GenerationId, string ConfigDigest, string TerminalDecision, int TerminalVersion, string? AbortReason);

public enum StartReceiptOutcome
{
    /// <summary>This call inserted the receipt.</summary>
    Committed,

    /// <summary>An identical receipt (same digest and generation) already exists and is Started: an acknowledgement-loss retry.</summary>
    AlreadyStarted,

    /// <summary>The match id already has a durable terminal decision; it can never run.</summary>
    Terminal,

    /// <summary>A receipt with this match id but a different sealed config or generation exists. Never overwritten.</summary>
    Conflict,

    /// <summary>The generation is retired or unknown: this process may not start matches.</summary>
    GenerationRetired,

    /// <summary>The database could not be reached or the statement failed; nothing is assumed committed.</summary>
    Unavailable,
}

public enum AbortOutcome
{
    /// <summary>This call wrote the Aborted decision.</summary>
    Aborted,

    /// <summary>The receipt already had a terminal decision (Aborted by retirement, or a committed result). It is returned unchanged.</summary>
    AlreadyTerminal,

    /// <summary>The receipt belongs to another generation, or this generation is retired: this process may not decide it.</summary>
    NotAuthority,

    NotFound,

    Unavailable,
}

public sealed record GenerationRegistration(Guid GenerationId, int RetiredGenerations, int AbortedReceipts);

public enum FinalizeOutcome
{
    /// <summary>This call committed the result, the outbox row and the ResultCommitted decision in one transaction.</summary>
    Committed,

    /// <summary>An identical result (same digest) was already committed, for example by an attempt whose acknowledgement was lost.</summary>
    AlreadyCommitted,

    /// <summary>The receipt's durable decision is Aborted (for example by a successor's retirement). No result exists or can exist.</summary>
    Aborted,

    /// <summary>A DIFFERENT result is committed for this match id, or the result names another sealed config. Never overwritten; an alert.</summary>
    Conflict,

    /// <summary>The receipt belongs to another generation, or this generation is retired: this process may not decide it.</summary>
    NotAuthority,

    NotFound,

    /// <summary>The database could not be reached or the commit outcome is unknown. Nothing is assumed; a retry learns the truth.</summary>
    Unavailable,
}

/// <summary>The durable truth of one match: its receipt, and the committed result when the decision is ResultCommitted.</summary>
public sealed record MatchOutcomeRecord(MatchReceipt Receipt, IReadOnlyList<string> RosterSubjects, string? ResultDigest, string? ResultDocumentJson);

/// <summary>
/// Ghost Lap's durable authority fences (task 10.2) over the Ghost-Lap-owned <c>ghostlap</c> schema (migration
/// <c>ghostlap/0003_mp_authority.sql</c> and its follow-up), used through the least-privilege role <c>ghostlap_mp_runtime</c>.
/// <list type="bullet">
/// <item><b>Process generation.</b> At most one Active generation (unique partial index). Registering a new one retires every other
/// generation and aborts every Started receipt of a retired generation in the SAME transaction, after first locking those receipt rows.</item>
/// <item><b>Start receipt.</b> The insert trigger takes the registration advisory lock in shared mode, then share-locks and validates
/// the generation row (SQLSTATE GL002 for a retired generation). Committed before the room reports the match as running.</item>
/// <item><b>Terminal fence.</b> Every terminal write locks the receipt row first and then checks the generation (row order: receipt,
/// then generation), the order the task 10.4 finalizer must use as well. Retirement locks the receipt rows before it updates the
/// generation. Whichever transaction locks a receipt row first decides it; the other sees the committed decision.</item>
/// </list>
/// No method is ever called from inside a tick. Failures are typed; driver messages are never logged (they can echo host/user names).
/// </summary>
public sealed class MatchAuthorityStore(NpgsqlDataSource dataSource, Func<NpgsqlTransaction, IOutboxWriter>? outbox = null)
{
    private const string FenceSqlState = "GL002";
    private const string ResultSqlState = "GL003";
    private const long GenerationLockKey = 0x474C_4D50_4745_4E31; // "GLMPGEN1", shared with the insert trigger in migration 0004

    private readonly NpgsqlDataSource _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    /// <summary>
    /// The shared Outbox v1 writer bound to the finalizer's transaction (task 10.4). The host composes <c>PostgresOutboxWriter</c> over
    /// <c>MessagingTables("ghostlap")</c>; this assembly only sees the Kernel port. Null = this store cannot finalize (it never pretends to).
    /// </summary>
    private readonly Func<NpgsqlTransaction, IOutboxWriter>? _outbox = outbox;

    public bool CanFinalize => _outbox is not null;

    /// <summary>Registers <paramref name="generationId"/> as the one Active generation; retires all others and aborts their Started receipts.</summary>
    public async Task<GenerationRegistration> RegisterGenerationAsync(Guid generationId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
        await ExecAsync(connection, tx, "SELECT pg_advisory_xact_lock(@k)", ct, ("k", GenerationLockKey)).ConfigureAwait(false);
        // This separate statement at Read Committed sees every receipt committed by an earlier holder of the shared lock.
        // Lock receipt rows first (the finalizer's order), then generation rows.
        await ExecAsync(connection, tx,
            "SELECT match_id FROM ghostlap.match_session WHERE terminal_decision = 'Started' ORDER BY match_id FOR UPDATE", ct).ConfigureAwait(false);
        var retired = await ExecAsync(connection, tx,
            "UPDATE ghostlap.realtime_generation SET status = 'Retired', retired_at = now(), retired_by = @g WHERE status = 'Active'",
            ct, ("g", generationId)).ConfigureAwait(false);
        await ExecAsync(connection, tx,
            "INSERT INTO ghostlap.realtime_generation (generation_id, status) VALUES (@g, 'Active')", ct, ("g", generationId)).ConfigureAwait(false);
        var aborted = await ExecAsync(connection, tx,
            """
            UPDATE ghostlap.match_session
               SET terminal_decision = 'Aborted', terminal_version = 1, abort_reason = @r, terminal_at = now()
             WHERE terminal_decision = 'Started' AND generation_id <> @g
            """, ct, ("g", generationId), ("r", AbortReasons.GenerationRetired)).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return new GenerationRegistration(generationId, retired, aborted);
    }

    /// <summary>"Active", "Retired", or null when the generation is unknown.</summary>
    public async Task<string?> GenerationStatusAsync(Guid generationId, CancellationToken ct)
    {
        await using var command = _dataSource.CreateCommand("SELECT status FROM ghostlap.realtime_generation WHERE generation_id = @g");
        command.Parameters.AddWithValue("g", generationId);
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
    }

    public async Task<(StartReceiptOutcome Outcome, MatchReceipt? Receipt)> CommitStartReceiptAsync(
        SealedRoundConfig config, Guid generationId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using (var insert = new NpgsqlCommand(
                             """
                             INSERT INTO ghostlap.match_session
                               (match_id, room_id, generation_id, round_ordinal, config_version, config_digest, rules_id, layout_id, rules_hash,
                                layout_fingerprint, seed, rating_algorithm_id, target_ticks, roster_subjects, terminal_decision, terminal_version)
                             VALUES (@m, @room, @g, @ord, @cv, @digest, @rules, @layout, @rhash, @lfp, @seed, @rating, @ticks, @roster, 'Started', 0)
                             ON CONFLICT (match_id) DO NOTHING
                             """, connection))
            {
                insert.Parameters.AddWithValue("m", config.MatchId);
                insert.Parameters.AddWithValue("room", config.RoomId);
                insert.Parameters.AddWithValue("g", generationId);
                insert.Parameters.AddWithValue("ord", config.RoundOrdinal);
                insert.Parameters.AddWithValue("cv", RoundSealing.ConfigVersion);
                insert.Parameters.AddWithValue("digest", config.ConfigDigest);
                insert.Parameters.AddWithValue("rules", config.RulesId);
                insert.Parameters.AddWithValue("layout", config.LayoutId);
                insert.Parameters.AddWithValue("rhash", config.RulesHash);
                insert.Parameters.AddWithValue("lfp", config.LayoutFingerprint);
                insert.Parameters.AddWithValue("seed", config.Seed);
                insert.Parameters.AddWithValue("rating", config.RatingAlgorithmId);
                insert.Parameters.AddWithValue("ticks", config.TargetTicks);
                insert.Parameters.Add(new NpgsqlParameter("roster", NpgsqlDbType.Array | NpgsqlDbType.Text)
                {
                    Value = config.Slots.OrderBy(s => s.Slot).Select(s => s.Subject).ToArray(),
                });
                if (await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1)
                {
                    return (StartReceiptOutcome.Committed, await FindAsync(connection, null, config.MatchId, lockRow: false, ct).ConfigureAwait(false));
                }
            }
            var existing = await FindAsync(connection, null, config.MatchId, lockRow: false, ct).ConfigureAwait(false);
            if (existing is null)
            {
                return (StartReceiptOutcome.Unavailable, null);
            }
            if (existing.ConfigDigest != config.ConfigDigest || existing.GenerationId != generationId || existing.RoomId != config.RoomId)
            {
                return (StartReceiptOutcome.Conflict, existing);
            }
            return (existing.TerminalDecision == TerminalDecisions.Started ? StartReceiptOutcome.AlreadyStarted : StartReceiptOutcome.Terminal, existing);
        }
        catch (PostgresException e) when (e.SqlState is FenceSqlState or PostgresErrorCodes.ForeignKeyViolation)
        {
            return (StartReceiptOutcome.GenerationRetired, null);
        }
        catch (Exception e) when (e is NpgsqlException or TimeoutException or InvalidOperationException)
        {
            return (StartReceiptOutcome.Unavailable, null);
        }
    }

    /// <summary>
    /// Fenced abort of a Started receipt owned by <paramref name="generationId"/>. Locks the receipt row, then share-locks the
    /// generation row and requires it Active. A receipt that already has a terminal decision is returned unchanged.
    /// </summary>
    public async Task<(AbortOutcome Outcome, MatchReceipt? Receipt)> AbortAsync(Guid matchId, Guid generationId, string reason, CancellationToken ct)
    {
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            var receipt = await FindAsync(connection, tx, matchId, lockRow: true, ct).ConfigureAwait(false);
            if (receipt is null)
            {
                return (AbortOutcome.NotFound, null);
            }
            if (receipt.TerminalDecision != TerminalDecisions.Started)
            {
                return (AbortOutcome.AlreadyTerminal, receipt);
            }
            await using (var generation = new NpgsqlCommand(
                             "SELECT status FROM ghostlap.realtime_generation WHERE generation_id = @g FOR SHARE", connection, tx))
            {
                generation.Parameters.AddWithValue("g", generationId);
                if (receipt.GenerationId != generationId || await generation.ExecuteScalarAsync(ct).ConfigureAwait(false) as string != "Active")
                {
                    return (AbortOutcome.NotAuthority, receipt);
                }
            }
            await ExecAsync(connection, tx,
                """
                UPDATE ghostlap.match_session
                   SET terminal_decision = 'Aborted', terminal_version = 1, abort_reason = @r, terminal_at = now()
                 WHERE match_id = @m
                """, ct, ("m", matchId), ("r", reason)).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return (AbortOutcome.Aborted, receipt with { TerminalDecision = TerminalDecisions.Aborted, TerminalVersion = 1, AbortReason = reason });
        }
        catch (Exception e) when (e is NpgsqlException or TimeoutException or InvalidOperationException)
        {
            return (AbortOutcome.Unavailable, null);
        }
    }

    /// <summary>
    /// The task 10.4 finalizer: ONE transaction that locks the receipt row (<c>FOR UPDATE</c>), requires it Started under
    /// <paramref name="generationId"/> and that generation Active (<c>FOR SHARE</c>, the same order as the fenced abort and the successor's
    /// retirement), then inserts the unique <c>match_result</c>, the one <c>MatchFinalizedV1</c> outbox row (shared Outbox v1 writer on the same
    /// transaction, deterministic event id) and moves the receipt to ResultCommitted. A committed receipt is answered from the database: the same
    /// digest is <see cref="FinalizeOutcome.AlreadyCommitted"/>, a different one is <see cref="FinalizeOutcome.Conflict"/>. Never called from a tick.
    /// </summary>
    public async Task<(FinalizeOutcome Outcome, MatchReceipt? Receipt)> FinalizeAsync(
        MatchResultRecord result, Guid generationId, TimeProvider time, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(time);
        var writerFactory = _outbox ?? throw new InvalidOperationException("This MatchAuthorityStore has no outbox writer; it cannot finalize matches.");
        var document = result.Document;
        var matchId = result.MatchId;
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var tx = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
            var receipt = await FindAsync(connection, tx, matchId, lockRow: true, ct).ConfigureAwait(false);
            if (receipt is null)
            {
                return (FinalizeOutcome.NotFound, null);
            }
            if (receipt.TerminalDecision == TerminalDecisions.ResultCommitted)
            {
                await using var existing = new NpgsqlCommand("SELECT result_digest FROM ghostlap.match_result WHERE match_id = @m", connection, tx);
                existing.Parameters.AddWithValue("m", matchId);
                var digest = await existing.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
                return (digest == result.Digest ? FinalizeOutcome.AlreadyCommitted : FinalizeOutcome.Conflict, receipt);
            }
            if (receipt.TerminalDecision == TerminalDecisions.Aborted)
            {
                return (FinalizeOutcome.Aborted, receipt);
            }
            await using (var generation = new NpgsqlCommand(
                             "SELECT status FROM ghostlap.realtime_generation WHERE generation_id = @g FOR SHARE", connection, tx))
            {
                generation.Parameters.AddWithValue("g", generationId);
                if (receipt.GenerationId != generationId || await generation.ExecuteScalarAsync(ct).ConfigureAwait(false) as string != "Active")
                {
                    return (FinalizeOutcome.NotAuthority, receipt);
                }
            }
            if (receipt.ConfigDigest != document.ConfigDigest || receipt.RoomId.ToString("N") != document.RoomId
                || document.GenerationId != generationId.ToString("N"))
            {
                return (FinalizeOutcome.Conflict, receipt);
            }

            await using (var insert = new NpgsqlCommand(
                             """
                             INSERT INTO ghostlap.match_result
                               (match_id, room_id, generation_id, config_digest, result_schema, result_digest, rating_algorithm_id, roster_subjects,
                                scores, forfeited, placements, final_tick, placement_policy, eligibility_policy, rated_eligible, eligibility_reason,
                                result_document)
                             VALUES (@m, @room, @g, @cfg, @schema, @digest, @rating, @roster, @scores, @forfeited, @placements, @tick, @pp, @ep,
                                     @rated, @reason, @doc)
                             """, connection, tx))
            {
                var players = document.Players.OrderBy(p => p.Slot).ToArray();
                insert.Parameters.AddWithValue("m", matchId);
                insert.Parameters.AddWithValue("room", receipt.RoomId);
                insert.Parameters.AddWithValue("g", generationId);
                insert.Parameters.AddWithValue("cfg", document.ConfigDigest);
                insert.Parameters.AddWithValue("schema", document.Schema);
                insert.Parameters.AddWithValue("digest", result.Digest);
                insert.Parameters.AddWithValue("rating", document.RatingAlgorithmId);
                insert.Parameters.Add(new NpgsqlParameter("roster", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = players.Select(p => p.Subject).ToArray() });
                insert.Parameters.Add(new NpgsqlParameter("scores", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = players.Select(p => p.TotalScore).ToArray() });
                insert.Parameters.Add(new NpgsqlParameter("forfeited", NpgsqlDbType.Array | NpgsqlDbType.Boolean)
                {
                    Value = players.Select(p => p.Outcome == MatchResultContract.OutcomeForfeit).ToArray(),
                });
                insert.Parameters.Add(new NpgsqlParameter("placements", NpgsqlDbType.Array | NpgsqlDbType.Integer)
                {
                    Value = players.Select(p => p.CompetitivePlacement).ToArray(),
                });
                insert.Parameters.AddWithValue("tick", checked((int)document.FinalTick));
                insert.Parameters.AddWithValue("pp", document.PlacementPolicy);
                insert.Parameters.AddWithValue("ep", document.EligibilityPolicy);
                insert.Parameters.AddWithValue("rated", document.RatedEligible);
                insert.Parameters.AddWithValue("reason", document.EligibilityReason);
                insert.Parameters.AddWithValue("doc", MatchResults.DocumentToJson(document));
                await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            await writerFactory(tx).EnqueueAsync(new OutboxMessage(
                MatchResults.FinalizedEventId(matchId),
                MatchResultContract.FinalizedEventType,
                MatchResultContract.FinalizedEventVersion,
                matchId.ToString("D"),
                time.GetUtcNow(),
                result.PayloadJson(),
                matchId.ToString("N")), ct).ConfigureAwait(false);
            await ExecAsync(connection, tx,
                """
                UPDATE ghostlap.match_session
                   SET terminal_decision = 'ResultCommitted', terminal_version = 1, terminal_at = now()
                 WHERE match_id = @m
                """, ct, ("m", matchId)).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return (FinalizeOutcome.Committed, receipt with { TerminalDecision = TerminalDecisions.ResultCommitted, TerminalVersion = 1 });
        }
        catch (PostgresException e) when (e.SqlState == FenceSqlState)
        {
            return (FinalizeOutcome.NotAuthority, null);
        }
        catch (PostgresException e) when (e.SqlState == ResultSqlState)
        {
            return (FinalizeOutcome.Conflict, null);
        }
        catch (Exception e) when (e is NpgsqlException or TimeoutException or InvalidOperationException)
        {
            // Includes a lost commit acknowledgement: the transaction may or may not have committed. The caller retries and the receipt tells.
            return (FinalizeOutcome.Unavailable, null);
        }
    }

    /// <summary>The receipt and, when ResultCommitted, the committed result. Null when the match id is unknown (never reported as aborted).</summary>
    public async Task<MatchOutcomeRecord?> FindOutcomeAsync(Guid matchId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            SELECT s.match_id, s.room_id, s.generation_id, s.config_digest, s.terminal_decision, s.terminal_version, s.abort_reason,
                   s.roster_subjects, r.result_digest, r.result_document
              FROM ghostlap.match_session s LEFT JOIN ghostlap.match_result r USING (match_id)
             WHERE s.match_id = @m
            """, connection);
        command.Parameters.AddWithValue("m", matchId);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }
        var receipt = new MatchReceipt(
            reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetString(3), reader.GetString(4), reader.GetInt32(5),
            reader.IsDBNull(6) ? null : reader.GetString(6));
        return new MatchOutcomeRecord(
            receipt, reader.GetFieldValue<string[]>(7), reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9));
    }

    public async Task<MatchReceipt?> FindReceiptAsync(Guid matchId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        return await FindAsync(connection, null, matchId, lockRow: false, ct).ConfigureAwait(false);
    }

    private static async Task<MatchReceipt?> FindAsync(NpgsqlConnection connection, NpgsqlTransaction? tx, Guid matchId, bool lockRow, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            "SELECT match_id, room_id, generation_id, config_digest, terminal_decision, terminal_version, abort_reason "
            + "FROM ghostlap.match_session WHERE match_id = @m" + (lockRow ? " FOR UPDATE" : ""), connection, tx);
        command.Parameters.AddWithValue("m", matchId);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }
        return new MatchReceipt(
            reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetString(3), reader.GetString(4), reader.GetInt32(5),
            reader.IsDBNull(6) ? null : reader.GetString(6));
    }

    private static async Task<int> ExecAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, tx);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
