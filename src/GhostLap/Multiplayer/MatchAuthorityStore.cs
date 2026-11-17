using System.Data;
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
public sealed class MatchAuthorityStore(NpgsqlDataSource dataSource)
{
    private const string FenceSqlState = "GL002";
    private const long GenerationLockKey = 0x474C_4D50_4745_4E31; // "GLMPGEN1", shared with the insert trigger in migration 0004

    private readonly NpgsqlDataSource _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

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
