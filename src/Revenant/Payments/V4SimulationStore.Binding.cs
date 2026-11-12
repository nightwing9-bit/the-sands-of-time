using Marrow.Timeline;
using Npgsql;
using Revenant.Contracts;
using Revenant.Scenario;
using Revenant.Timeline;

namespace Revenant.Payments;

internal sealed record V4ForkOrigin(ScenarioRunId SourceExecutionRunId, TimelineId SourceTimelineId, long FactSequence,
    long BoundaryMs, string StateHash, string PrefixDigest, string OperationKey);

internal sealed record V4ExecutionBinding(ScenarioRunId ExecutionRunId, ScenarioRunId LineageRunId, TimelineId TimelineId,
    string ContextHash, V4ForkOrigin? Fork = null);

internal sealed record V4StoredFact(ObservedFinanceInput Input, string PrefixDigest)
{
    internal OrderedInput<ObservedFinanceInput> OrderedInput =>
        new(FinanceLedgerTransition.InputId(Input.RunId, Input.Sequence), 0, Input);

    internal ObservedFinanceState Apply(ObservedFinanceState state, ObservedFinanceContext context)
    {
        var next = ObservedFinanceTransition.Instance.Apply(state, Input.Sequence, [OrderedInput], context);
        return next.PrefixDigest == PrefixDigest
            ? next
            : throw new InvalidOperationException("V4 replay disagrees with the stored finance prefix.");
    }
}

internal sealed record V4FinanceHistory(long LastFact, bool IsClosed, IReadOnlyList<V4StoredFact> Facts);

internal sealed partial class V4SimulationStore
{
    internal async Task ReserveForkAsync(string tenant, string owner, ScenarioRun run, ObservedFinanceContext context,
        V4ExecutionBinding request, CancellationToken ct)
    {
        if (request.Fork is null || request.TimelineId != default)
        {
            throw new ArgumentException("A V4 reservation requires a fork origin and no child Timeline.", nameof(request));
        }
        var id = request.ExecutionRunId.Value;
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await BeginAsync(connection, tenant, ct);
        await LockOpeningAsync(tx, id, ct);
        var existing = await ReadBindingAsync(tx, tenant, request.ExecutionRunId, ct);
        if (existing is not null)
        {
            if (existing with { TimelineId = default } != request)
            {
                throw new InvalidOperationException("The V4 execution already has a different fork request.");
            }
            return;
        }
        await InsertScenarioAsync(tx, tenant, owner, run, context, id, ct);
        await InsertBindingAsync(tx, tenant, request, ct);
        await tx.CommitAsync(ct);
    }

    private static Task LockOpeningAsync(NpgsqlTransaction tx, Guid id, CancellationToken ct) =>
        ExecuteAsync(tx, "SELECT pg_advisory_xact_lock(hashtextextended(@id, 0))", ct, ("id", id.ToString("D")));

    private static Task InsertScenarioAsync(NpgsqlTransaction tx, string tenant, string owner, ScenarioRun run,
        ObservedFinanceContext context, Guid id, CancellationToken ct) =>
        ExecuteAsync(tx, """
            INSERT INTO revenant.scenario_run (id, tenant_id, owner_subject_id, seed, scenario_version, manifest_hash, finance_version)
            VALUES (@run, @tenant, @owner, @seed, @scenario, @manifest, @family)
            """, ct, ("run", id), ("tenant", tenant), ("owner", owner), ("seed", run.Manifest.Seed),
            ("scenario", context.ScenarioVersion), ("manifest", context.ManifestSha256), ("family", ObservedFinanceVersion.ReducerVersion));

    private Task InsertBindingAsync(NpgsqlTransaction tx, string tenant, V4ExecutionBinding binding, CancellationToken ct)
    {
        var fork = binding.Fork;
        return ExecuteAsync(tx, """
            INSERT INTO revenant.v4_branch_execution
              (tenant_id, execution_run_id, lineage_run_id, timeline_id, child_context_hash, readiness_status,
               source_execution_run_id, source_timeline_id, fork_fact_seq, fork_boundary_ms, fork_state_hash,
               fork_prefix_digest, fork_operation_key)
            VALUES (@tenant, @run, @lineage, CAST(NULLIF(@timeline, '') AS uuid), @context, @status,
                    CAST(NULLIF(@source, '') AS uuid), CAST(NULLIF(@parent, '') AS uuid), @position, @boundary,
                    decode(NULLIF(@state, ''), 'hex'), decode(NULLIF(@prefix, ''), 'hex'), NULLIF(@operation, ''))
            """, ct, ("tenant", tenant), ("run", binding.ExecutionRunId.Value), ("lineage", binding.LineageRunId.Value),
            ("timeline", binding.TimelineId == default ? "" : binding.TimelineId.Value.ToString("D")),
            ("status", binding.TimelineId == default ? "Pending" : ledger is null ? "SealOnly" : "Ready"), ("context", Convert.FromHexString(binding.ContextHash)),
            ("source", fork?.SourceExecutionRunId.Value.ToString("D") ?? ""),
            ("parent", fork?.SourceTimelineId.Value.ToString("D") ?? ""),
            ("position", (object?)fork?.FactSequence ?? DBNull.Value), ("boundary", (object?)fork?.BoundaryMs ?? DBNull.Value),
            ("state", fork?.StateHash ?? ""), ("prefix", fork?.PrefixDigest ?? ""),
            ("operation", fork?.OperationKey ?? ""));
    }

    internal async Task<V4ExecutionBinding> ReadBindingAsync(string tenant, ScenarioRunId execution, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await BeginAsync(connection, tenant, ct);
        var binding = await ReadBindingAsync(tx, tenant, execution, ct);
        return binding is not null && binding.TimelineId != default
            ? binding : throw new V4ScheduleException(V4ScheduleError.RunNotFound);
    }

    private static async Task<V4ExecutionBinding?> ReadBindingAsync(NpgsqlTransaction tx, string tenant,
        ScenarioRunId execution, CancellationToken ct)
    {
        await using var cmd = Sql(tx, """
            SELECT lineage_run_id, timeline_id, child_context_hash, source_execution_run_id, source_timeline_id,
                   fork_fact_seq, fork_boundary_ms, fork_state_hash, fork_prefix_digest, fork_operation_key
              FROM revenant.v4_branch_execution WHERE execution_run_id = @run AND tenant_id = @tenant
            """, ("run", execution.Value), ("tenant", tenant));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }
        string Hash(int column) => Convert.ToHexStringLower(reader.GetFieldValue<byte[]>(column));
        var fork = reader.IsDBNull(3) ? null : new V4ForkOrigin(new(reader.GetGuid(3)), new(reader.GetGuid(4)),
            reader.GetInt64(5), reader.GetInt64(6), Hash(7), Hash(8), reader.GetString(9));
        return new(execution, new(reader.GetGuid(0)), reader.IsDBNull(1) ? default : new(reader.GetGuid(1)), Hash(2), fork);
    }

    internal async Task CloseBoundSealHistoryAsync(string tenant, ScenarioRunId execution, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await BeginAsync(connection, tenant, ct);
        var row = await LockRunAsync(tx, tenant, execution, ct);
        if (row.Status == "Closed")
        {
            return;
        }

        RequireOpen(row);
        await ExecuteAsync(tx, """
            UPDATE revenant.run_cursor SET closed_fact_seq = last_fact_seq
            WHERE scenario_run_id = @run AND tenant_id = @tenant
            """, ct, ("run", execution.Value), ("tenant", tenant));
        await tx.CommitAsync(ct);
    }

    internal async Task<V4FinanceHistory> ReadFinanceHistoryAsync(string tenant, ScenarioRunId execution, ScenarioRunId lineage,
        CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        await SetTenantAsync(tx, tenant, ct);

        long last;
        bool closed;
        await using (var cursor = Sql(tx, """
            SELECT last_fact_seq, closed_fact_seq FROM revenant.run_cursor WHERE scenario_run_id = @run AND tenant_id = @tenant
            """, ("run", execution.Value), ("tenant", tenant)))
        await using (var row = await cursor.ExecuteReaderAsync(ct))
        {
            if (!await row.ReadAsync(ct))
            {
                throw new V4ScheduleException(V4ScheduleError.RunNotFound);
            }
            last = row.GetInt64(0);
            closed = !row.IsDBNull(1);
        }

        return new(last, closed, await ReadFactsAsync(tx, tenant, execution, lineage, ct));
    }

    private static async Task<List<V4StoredFact>> ReadFactsAsync(NpgsqlTransaction tx, string tenant, ScenarioRunId execution,
        ScenarioRunId lineage, CancellationToken ct, long through = long.MaxValue)
    {
        await using var facts = Sql(tx, """
            SELECT fact_seq, effective_time_ms, canonical, canonical_sha256, v4_prefix_digest, kind, schema_version, evidence_schema, evidence_sha256
              FROM revenant.finance_fact WHERE scenario_run_id = @run AND tenant_id = @tenant AND fact_seq <= @through ORDER BY fact_seq
            """, ("run", execution.Value), ("tenant", tenant), ("through", through));
        await using var reader = await facts.ExecuteReaderAsync(ct);
        var rows = new List<V4StoredFact>();
        while (await reader.ReadAsync(ct))
        {
            var kind = Enum.Parse<ObservedFactKind>(reader.GetString(5));
            var bytes = reader.GetFieldValue<byte[]>(2);
            if (!System.Security.Cryptography.SHA256.HashData(bytes).AsSpan().SequenceEqual(reader.GetFieldValue<byte[]>(3)))
            {
                throw new InvalidOperationException("V4 canonical fact payload digest mismatch.");
            }
            var input = new ObservedFinanceInput(lineage, reader.GetInt64(0), reader.GetInt64(1), kind,
                reader.GetString(6), [.. bytes], reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : Convert.ToHexStringLower(reader.GetFieldValue<byte[]>(8)));
            ObservedFinanceInputCodec.Validate(input);
            rows.Add(new(input, Convert.ToHexStringLower(reader.GetFieldValue<byte[]>(4))));
        }
        return rows;
    }
}
