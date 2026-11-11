using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using Revenant.Contracts;

namespace Revenant.Payments;

/// <summary>
/// Result of recomputing a run's audit hash chain. <see cref="Assurance"/> states what it proves: tamper evidence inside this
/// database (an edited, removed or reordered row breaks the chain), not external notarization.
/// </summary>
public sealed record AuditVerification(
    ScenarioRunId RunId, bool ChainIntact, long Events, long HeadSequence, string HeadHashHex, long? FirstBrokenSequence, string Assurance)
{
    public const string DatabaseTamperEvidence = "database-tamper-evident; not externally notarized";
}

/// <summary>One audit row as stored (for the reviewer view and tests).</summary>
public sealed record AuditEventView(
    long Sequence, string EventType, string Actor, string CorrelationId, string SubjectRef, long? SubjectVersion, byte[]? RefSha256,
    long RecordedUs, byte[] PreviousHash, byte[] Hash);

/// <summary>
/// Task 7.5: the per-run hash-chained audit (<c>revenant.audit_event</c>). Every fact append and every audited case read or
/// comment inserts one row in the same transaction as the action; PostgreSQL assigns the sequence and computes the chain hash
/// (migration 0005), so the application cannot choose either. Rows carry the verified actor's subject id, the correlation id,
/// the subject reference and version, and the SHA-256 of the referenced canonical bytes. No token, cookie, session id or
/// comment text is ever written to the chain.
/// </summary>
public sealed partial class PaymentLedgerStore
{
    public const string AuditPayloadVersion = "revenant-audit/1";

    private static Task AppendAuditAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, string tenantId, Guid run, string eventType, string actor, string correlationId,
        string subjectRef, long? subjectVersion, byte[]? refSha256, CancellationToken ct) =>
        ExecAsync(connection, tx,
            """
            INSERT INTO revenant.audit_event
              (tenant_id, scenario_run_id, seq, event_type, actor, correlation_id, subject_ref, subject_version, ref_sha256, recorded_us, prev_hash, hash)
            VALUES (@tenant, @run, 0, @type, @actor, @correlation, @subject, @version, @ref, 0, '\x', '\x')
            """, ct,
            ("tenant", tenantId), ("run", run), ("type", eventType), ("actor", actor), ("correlation", correlationId),
            ("subject", subjectRef), ("version", (object?)subjectVersion ?? DBNull.Value), ("ref", (object?)refSha256 ?? DBNull.Value));

    /// <summary>Reads a payment's case for <paramref name="actor"/> and records the read in the audit chain, in one transaction.</summary>
    public async Task<CaseView?> ReadCaseAuditedAsync(
        string tenantId, string actor, string correlationId, ScenarioRunId runId, string intentId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(actor) || !ValidCorrelation(correlationId))
        {
            throw new ArgumentException("An audited read needs the verified actor and a correlation id of 1-200 characters.");
        }
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await SetTenantAsync(connection, tx, tenantId, ct);
        var caseId = await ScalarAsync<Guid?>(connection, tx,
            """
            SELECT c.id FROM revenant.review_case c JOIN revenant.payment p ON p.id = c.payment_id
             WHERE p.scenario_run_id = @run AND p.intent_id = @intent
            """, ct, ("run", runId.Value), ("intent", intentId));
        var view = caseId is { } id ? await ReadCaseAsync(connection, tx, id, ct) : null;
        if (view is not null)
        {
            await AppendAuditAsync(connection, tx, tenantId, runId.Value, "case.read", actor, correlationId, $"case:{view.CaseId:N}",
                view.Version, null, ct);
        }
        await tx.CommitAsync(ct);
        return view;
    }

    public async Task<IReadOnlyList<AuditEventView>> ReadAuditAsync(string tenantId, ScenarioRunId runId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await SetTenantAsync(connection, tx, tenantId, ct);
        var rows = await ReadAuditRowsAsync(connection, tx, runId.Value, ct);
        await tx.CommitAsync(ct);
        return rows;
    }

    /// <summary>
    /// Recomputes the run's chain from its first row: gap-free sequence from 1, each <c>prev_hash</c> equal to the previous row's
    /// hash (32 zero bytes first), each hash equal to SHA-256 over the previous hash and the canonical payload, and the head row
    /// equal to the last row. Any difference reports the first broken sequence.
    /// </summary>
    public async Task<AuditVerification> VerifyAuditChainAsync(string tenantId, ScenarioRunId runId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await SetTenantAsync(connection, tx, tenantId, ct);
        var rows = await ReadAuditRowsAsync(connection, tx, runId.Value, ct);
        long headSeq = 0;
        byte[] headHash = new byte[32];
        await using (var cmd = Command(connection, tx, "SELECT last_seq, last_hash FROM revenant.audit_head WHERE scenario_run_id = @run", ("run", runId.Value)))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                (headSeq, headHash) = (reader.GetInt64(0), reader.GetFieldValue<byte[]>(1));
            }
        }
        await tx.CommitAsync(ct);

        var previous = new byte[32];
        long? broken = null;
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var expected = AuditHash(previous, runId.Value, tenantId, row);
            if (row.Sequence != i + 1 || !row.PreviousHash.AsSpan().SequenceEqual(previous) || !row.Hash.AsSpan().SequenceEqual(expected))
            {
                broken = row.Sequence;
                break;
            }
            previous = row.Hash;
        }
        if (broken is null && (headSeq != rows.Count || !headHash.AsSpan().SequenceEqual(previous)))
        {
            broken = Math.Min(headSeq, rows.Count) + 1;
        }
        return new AuditVerification(runId, broken is null, rows.Count, headSeq, Convert.ToHexStringLower(headHash), broken,
            AuditVerification.DatabaseTamperEvidence);
    }

    /// <summary>The chain hash exactly as migration 0005 computes it.</summary>
    public static byte[] AuditHash(byte[] previousHash, Guid run, string tenantId, AuditEventView row)
    {
        var payload = new StringBuilder(AuditPayloadVersion);
        foreach (var field in new[]
        {
            run.ToString("D"), row.Sequence.ToString(CultureInfo.InvariantCulture), tenantId, row.EventType, row.Actor, row.CorrelationId,
            row.SubjectRef, row.SubjectVersion?.ToString(CultureInfo.InvariantCulture) ?? "",
            row.RefSha256 is { } r ? Convert.ToHexStringLower(r) : "", row.RecordedUs.ToString(CultureInfo.InvariantCulture),
        })
        {
            payload.Append('|').Append(Encoding.UTF8.GetByteCount(field).ToString(CultureInfo.InvariantCulture)).Append(':').Append(field);
        }
        return SHA256.HashData([.. previousHash, .. Encoding.UTF8.GetBytes(payload.ToString())]);
    }

    private static async Task<IReadOnlyList<AuditEventView>> ReadAuditRowsAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid run, CancellationToken ct)
    {
        var rows = new List<AuditEventView>();
        await using var cmd = Command(connection, tx,
            """
            SELECT seq, event_type, actor, correlation_id, subject_ref, subject_version, ref_sha256, recorded_us, prev_hash, hash
              FROM revenant.audit_event WHERE scenario_run_id = @run ORDER BY seq
            """, ("run", run));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new AuditEventView(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetInt64(5), reader.IsDBNull(6) ? null : reader.GetFieldValue<byte[]>(6),
                reader.GetInt64(7), reader.GetFieldValue<byte[]>(8), reader.GetFieldValue<byte[]>(9)));
        }
        return rows;
    }
}
