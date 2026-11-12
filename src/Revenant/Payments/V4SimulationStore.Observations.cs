using Npgsql;
using Revenant.Contracts;
using Revenant.Observations;

namespace Revenant.Payments;

internal sealed partial class V4SimulationStore
{
    private sealed record ProjectionRow(V4SealIdentity Seal, string Entity, byte[] Bytes);

    internal async Task MaterializeBanksAsync(string tenant, ScenarioRunId execution, IReadOnlyList<V4BankObservation> rows,
        CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await BeginAsync(connection, tenant, ct);
        await WriteProjectionsAsync(tx, tenant, execution, "v4_bank_observation", "bank_id", V4BankObservation.Schema,
            rows.Select(row => new ProjectionRow(row.Seal, row.BankId, row.Encode())).ToArray(), ct);
        await tx.CommitAsync(ct);
    }

    internal async Task<V4BankObservation?> ReadBankAsync(string tenant, ScenarioRunId execution, string bank, long boundary, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await BeginAsync(connection, tenant, ct);
        await using var query = Sql(tx, """
            SELECT canonical, projection_digest, seal_fact_seq, sealed_prefix_digest
            FROM revenant.v4_bank_observation
            WHERE tenant_id = @tenant AND execution_run_id = @run AND bank_id = @bank AND boundary_ms = @boundary AND schema_id = @schema
            """, ("tenant", tenant), ("run", execution.Value), ("bank", bank), ("boundary", boundary), ("schema", V4BankObservation.Schema));
        await using var reader = await query.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }
        var row = V4BankObservation.Decode(reader.GetFieldValue<byte[]>(0), Convert.ToHexStringLower(reader.GetFieldValue<byte[]>(1)));
        if (row.Seal.ExecutionRunId != execution || row.BankId != bank || row.Seal.BoundaryMs != boundary
            || row.Seal.FactSequence != reader.GetInt64(2) || row.Seal.PrefixDigest != Convert.ToHexStringLower(reader.GetFieldValue<byte[]>(3)))
        {
            throw new InvalidOperationException("Bank observation metadata differs from its canonical record.");
        }
        return row;
    }

    internal async Task MaterializeAccountsAsync(string tenant, ScenarioRunId execution, IReadOnlyList<V4AccountObservation> rows,
        CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await BeginAsync(connection, tenant, ct);
        await WriteProjectionsAsync(tx, tenant, execution, "v4_account_activity_observation", "account_id", V4AccountObservation.Schema,
            rows.Select(row => new ProjectionRow(row.Seal, row.AccountId, row.Encode())).ToArray(), ct);
        await tx.CommitAsync(ct);
    }

    internal async Task<V4AccountObservation?> ReadAccountActivityAsync(string tenant, ScenarioRunId execution, string account,
        long boundary, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await BeginAsync(connection, tenant, ct);
        await using var query = Sql(tx, """
            SELECT canonical, projection_digest, seal_fact_seq, sealed_prefix_digest
            FROM revenant.v4_account_activity_observation
            WHERE tenant_id = @tenant AND execution_run_id = @run AND account_id = @account AND boundary_ms = @boundary AND schema_id = @schema
            """, ("tenant", tenant), ("run", execution.Value), ("account", account), ("boundary", boundary), ("schema", V4AccountObservation.Schema));
        await using var reader = await query.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var row = V4AccountObservation.Decode(reader.GetFieldValue<byte[]>(0), Convert.ToHexStringLower(reader.GetFieldValue<byte[]>(1)));
        if (row.Seal.ExecutionRunId != execution || row.AccountId != account || row.Seal.BoundaryMs != boundary
            || row.Seal.FactSequence != reader.GetInt64(2) || row.Seal.PrefixDigest != Convert.ToHexStringLower(reader.GetFieldValue<byte[]>(3)))
        {
            throw new InvalidOperationException("Account activity metadata differs from its canonical record.");
        }
        return row;
    }

    // Both identifiers are module-owned constants, never request values.
    private static async Task WriteProjectionsAsync(NpgsqlTransaction tx, string tenant, ScenarioRunId execution,
        string table, string key, string schema, ProjectionRow[] rows, CancellationToken ct)
    {
        if (rows.Any(r => r.Seal.ExecutionRunId != execution)
            || rows.Select(r => (r.Entity, r.Seal.BoundaryMs)).Distinct().Count() != rows.Length)
        {
            throw new InvalidOperationException("Projection rows must have unique keys and belong to the physical execution.");
        }

        const string input = "unnest(@keys::text[], @boundaries::bigint[], @positions::bigint[], @prefixes::bytea[], @contents::bytea[]) r(entity, boundary, position, prefix, content)";
        foreach (var batch in rows.OrderBy(r => r.Seal.BoundaryMs).ThenBy(r => r.Entity, StringComparer.Ordinal).Chunk(512))
        {
            (string, object)[] args = [
                ("tenant", tenant), ("run", execution.Value), ("schema", schema),
                ("keys", batch.Select(r => r.Entity).ToArray()), ("boundaries", batch.Select(r => r.Seal.BoundaryMs).ToArray()),
                ("positions", batch.Select(r => r.Seal.FactSequence).ToArray()),
                ("prefixes", batch.Select(r => Convert.FromHexString(r.Seal.PrefixDigest)).ToArray()),
                ("contents", batch.Select(r => r.Bytes).ToArray())];
            await ExecuteAsync(tx, $"""
                INSERT INTO revenant.{table}
                  (tenant_id, execution_run_id, {key}, boundary_ms, schema_id, seal_fact_seq, sealed_prefix_digest, canonical, projection_digest)
                SELECT @tenant, @run, r.entity, r.boundary, @schema, r.position, r.prefix, r.content, sha256(r.content)
                FROM {input}
                ON CONFLICT (execution_run_id, {key}, boundary_ms, schema_id) DO NOTHING
                """, ct, args);
            // A separate statement sees any row a concurrent exact-key insert committed.
            await using var check = Sql(tx, $"""
                SELECT count(*) FROM {input}
                JOIN revenant.{table} p ON p.execution_run_id = @run AND p.tenant_id = @tenant AND p.schema_id = @schema
                  AND p.{key} = r.entity AND p.boundary_ms = r.boundary AND p.seal_fact_seq = r.position
                  AND p.sealed_prefix_digest = r.prefix AND p.canonical = r.content AND p.projection_digest = sha256(r.content)
                """, args);
            if ((long)(await check.ExecuteScalarAsync(ct))! != batch.Length)
            {
                throw new InvalidOperationException("Conflicting observation for a previously materialized seal key.");
            }
        }
    }
}
