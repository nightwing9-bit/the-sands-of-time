using System.Diagnostics;
using System.Security.Cryptography;
using Npgsql;
using Revenant.Contracts;
using Revenant.Intelligence;
using Marrow.Kernel.Results;
using Revenant.Timeline;

namespace Revenant.Payments;

/// <summary>R2's internal dispatcher and journal. A handler can classify only a bounded, no-ledger-effect R2 test operation.
/// The handler is supplied by the test assembly. The production composition has no V4 handler or public V4 opener.</summary>
internal sealed partial class V4SimulationStore(NpgsqlDataSource source,
    Func<V4ScheduledCommand, CancellationToken, Task<V4CommandOutcome?>>? testHandler = null, PaymentLedgerStore? ledger = null,
    Lazy<Result<BehavioralRiskScorerV2, BehavioralRiskFailureV2>>? model = null)
{
    private const string CommandColumns = """
        command_seq, command_key, command_class, command_kind, payload_schema, canonical_payload, payload_sha256,
        requested_time_ms, admitted_time_ms, due_time_ms, status, effective_time_ms, outcome_code, outcome_sha256
        """;

    private sealed record SchedulerRow(long Watermark, long Clock, long NextCommand, string Status, string CommandDigest);

    internal Task OpenTestRunAsync(string tenant, string owner, ScenarioRun run, ObservedFinanceContext context, CancellationToken ct) =>
        OpenRunAsync(tenant, owner, run, context, null, ct);

    internal async Task OpenRunAsync(string tenant, string owner, ScenarioRun run, ObservedFinanceContext context,
        V4ExecutionBinding? binding, CancellationToken ct, ObservedFinanceState? opening = null)
    {
        var initial = ObservedFinanceReducer.Initial(run, context);
        if (initial.IsFailure)
        {
            throw new ObservedFinanceTransitionException(initial.Error);
        }

        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await BeginAsync(connection, tenant, ct);
        var id = binding?.ExecutionRunId.Value ?? run.RunId.Value;
        var start = binding?.Fork?.BoundaryMs ?? context.OriginMs;
        var position = binding?.Fork?.FactSequence ?? 0;
        V4ExecutionBinding? existing = null;
        if (binding is not null)
        {
            await LockOpeningAsync(tx, id, ct);
            existing = await ReadBindingAsync(tx, tenant, binding.ExecutionRunId, ct);
            if (existing == binding)
            {
                return;
            }
            if (existing is not null && (existing.TimelineId != default || existing != binding with { TimelineId = default }))
            {
                throw new InvalidOperationException("V4 execution already has a different binding.");
            }
        }

        if (existing is null)
        {
            await InsertScenarioAsync(tx, tenant, owner, run, context, id, ct);
            if (binding is not null)
            {
                await InsertBindingAsync(tx, tenant, binding, ct);
            }
        }
        else
        {
            await ExecuteAsync(tx, """
                UPDATE revenant.v4_branch_execution SET timeline_id = @timeline, readiness_status = @status
                WHERE execution_run_id = @run AND tenant_id = @tenant
                """, ct, ("timeline", binding!.TimelineId.Value), ("status", ledger is null ? "SealOnly" : "Ready"), ("run", id), ("tenant", tenant));
        }
        await ExecuteAsync(tx, """
            INSERT INTO revenant.run_cursor (scenario_run_id, tenant_id, last_event_time_ms, last_fact_seq, last_intent_sequence)
            VALUES (@run, @tenant, @start, @seq, @intent)
            """, ct, ("run", id), ("tenant", tenant), ("start", start), ("seq", position),
            ("intent", opening?.Ledger.LastIntentSequence ?? 0));
        byte[]? openingBytes = null;
        if (binding is not null)
        {
            var checkpoint = opening ?? (binding.Fork is null ? initial.Value
                : throw new InvalidOperationException("A V4 child needs the verified opening checkpoint."));
            await PaymentLedgerStore.MaterializeV4OpeningAsync(connection, tx, tenant, owner, run, binding, checkpoint, ct);
            openingBytes = ObservedFinanceStateCodec.Instance.EncodeCanonical(checkpoint);
        }
        await ExecuteAsync(tx, """
            INSERT INTO revenant.v4_scheduler_run (run_id, tenant_id, origin_ms, watermark_exclusive_ms, clock_ms, next_command_seq,
               status, scheduler_contract_id, command_completion_digest, canonical_context)
            VALUES (@run, @tenant, @origin, @start, @start, 1, 'Open', @contract, @digest, @context)
            """, ct, ("run", id), ("tenant", tenant), ("origin", context.OriginMs), ("start", start), ("contract", ObservedFinanceVersion.SchedulerContract),
            ("digest", Convert.FromHexString(V4SimulationScheduler.InitialCommandDigest(new(id)))),
            ("context", ObservedFinanceContextCodec.Instance.EncodeCanonical(context)));
        if (openingBytes is not null)
        {
            await ExecuteAsync(tx, """
                INSERT INTO revenant.v4_finance_opening (run_id, tenant_id, canonical_state) VALUES (@run, @tenant, @state)
                """, ct, ("run", id), ("tenant", tenant), ("state", openingBytes));
        }
        await tx.CommitAsync(ct);
    }

    internal async Task<V4ScheduledCommand> AdmitAsync(string tenant, ScenarioRunId run, V4CommandRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Key) || !Enum.IsDefined(request.Class) || string.IsNullOrWhiteSpace(request.Kind)
            || string.IsNullOrWhiteSpace(request.PayloadSchema) || request.CanonicalPayload.IsDefaultOrEmpty)
        {
            throw new V4ScheduleException(V4ScheduleError.InvalidCommand);
        }

        var payload = request.CanonicalPayload.ToArray();
        var hash = SHA256.HashData(payload);
        var hashHex = Convert.ToHexStringLower(hash);
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await BeginAsync(connection, tenant, ct);
        var row = await LockRunAsync(tx, tenant, run, ct);
        var prior = await ReadCommandAsync(tx, tenant, run, ct, request.Key);
        if (prior is not null)
        {
            var old = prior.Request;
            if (old.Class != request.Class || old.Kind != request.Kind || old.PayloadSchema != request.PayloadSchema
                || old.RequestedTimeMs != request.RequestedTimeMs || old.DueTimeMs != request.DueTimeMs
                || prior.PayloadSha256 != hashHex || !old.CanonicalPayload.AsSpan().SequenceEqual(payload))
            {
                throw new V4ScheduleException(V4ScheduleError.ConflictingCommand);
            }
            return prior;
        }

        RequireOpen(row);
        if (request.DueTimeMs < row.Clock)
        {
            throw new V4ScheduleException(V4ScheduleError.InvalidCommand);
        }
        if (row.NextCommand == long.MaxValue || request.DueTimeMs >= ObservedFinanceVersion.LastBoundaryMs)
        {
            throw new V4ScheduleException(V4ScheduleError.SequenceOrTimeOverflow);
        }

        await ExecuteAsync(tx, """
            INSERT INTO revenant.v4_scheduled_command (run_id, tenant_id, command_seq, command_key, command_class, command_kind,
              payload_schema, canonical_payload, payload_sha256, requested_time_ms, admitted_time_ms, due_time_ms, status)
            VALUES (@run, @tenant, @seq, @key, @class, @kind, @schema, @payload, @sha, @requested, @admitted, @due, 'Pending')
            """, ct, ("run", run.Value), ("tenant", tenant), ("seq", row.NextCommand), ("key", request.Key), ("class", request.Class.ToString()),
            ("kind", request.Kind), ("schema", request.PayloadSchema), ("payload", payload), ("sha", hash),
            ("requested", request.RequestedTimeMs), ("admitted", row.Clock), ("due", request.DueTimeMs));
        await tx.CommitAsync(ct);
        return new(run, tenant, row.NextCommand, request, hashHex, row.Clock, V4CommandStatus.Pending, null, null, null);
    }

    internal Task<V4Step> AdvanceOneStepAsync(string tenant, ScenarioRunId run, CancellationToken ct) => AdvanceAsync(tenant, run, null, ct);

    internal Task<V4Step> SealAsync(string tenant, ScenarioRunId run, long boundary, CancellationToken ct) => AdvanceAsync(tenant, run, boundary, ct);

    private async Task<V4Step> AdvanceAsync(string tenant, ScenarioRunId run, long? requestedBoundary, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await BeginAsync(connection, tenant, ct);
        var row = await LockRunAsync(tx, tenant, run, ct);

        if (requestedBoundary is { } requested && requested <= row.Watermark)
        {
            var duplicate = await ReadSealAsync(tx, run, requested, ct);
            return duplicate is null ? new(V4StepKind.NotNextBoundary) : new(V4StepKind.Sealed, Seal: duplicate);
        }

        RequireOpen(row);
        if (row.Watermark > long.MaxValue - ObservedFinanceVersion.BucketMs)
        {
            throw new V4ScheduleException(V4ScheduleError.SequenceOrTimeOverflow);
        }
        var boundary = ObservedFinanceVersion.NextBoundary(row.Watermark);
        if (requestedBoundary is { } target && target != boundary)
        {
            return new(V4StepKind.NotNextBoundary);
        }

        var command = await ReadCommandAsync(tx, tenant, run, ct);
        if (command is not null && command.Request.DueTimeMs < boundary)
        {
            if (command.Status == V4CommandStatus.RetryableBlocked)
            {
                return new(V4StepKind.Blocked, command);
            }
            if (requestedBoundary is not null)
            {
                return new(V4StepKind.WorkDue, command);
            }

            var outcome = ledger is not null
                ? await DispatchFinanceAsync(tx, tenant, run, command, ct)
                : testHandler is null ? null : await testHandler(command, ct);
            if (outcome is null)
            {
                return new(V4StepKind.UnsupportedHandler, command);
            }

            var outcomeHash = V4SimulationScheduler.OutcomeHash(outcome);
            long? effective = outcome.Status == V4CommandStatus.Completed ? command.Request.DueTimeMs : null;
            await ExecuteAsync(tx, """
                UPDATE revenant.v4_scheduled_command
                   SET status = @status, effective_time_ms = @effective, outcome_code = @code, outcome_sha256 = @hash
                 WHERE run_id = @run AND command_seq = @seq
                """, ct, ("status", outcome.Status.ToString()), ("effective", effective.HasValue ? effective.Value : DBNull.Value),
                ("code", outcome.Code), ("hash", Convert.FromHexString(outcomeHash)), ("run", run.Value), ("seq", command.Sequence));
            await tx.CommitAsync(ct);
            if (ledger is not null)
                V4FinanceMetrics.Dispatch.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return new(outcome.Status == V4CommandStatus.RetryableBlocked ? V4StepKind.Blocked : V4StepKind.ProcessedCommand,
                command with { Status = outcome.Status, EffectiveTimeMs = effective, OutcomeCode = outcome.Code, OutcomeSha256 = outcomeHash });
        }

        var seal = await AppendSealAsync(tx, tenant, run, row, boundary, ct);
        await tx.CommitAsync(ct);
        return new(V4StepKind.Sealed, Seal: seal);
    }

    internal async Task RetryAsync(string tenant, ScenarioRunId run, string commandKey, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await BeginAsync(connection, tenant, ct);
        RequireOpen(await LockRunAsync(tx, tenant, run, ct));
        var head = await ReadCommandAsync(tx, tenant, run, ct);
        if (head?.Request.Key != commandKey || head.Status != V4CommandStatus.RetryableBlocked)
        {
            throw new V4ScheduleException(V4ScheduleError.InvalidRetry);
        }

        await ExecuteAsync(tx, """
            UPDATE revenant.v4_scheduled_command SET status = 'Pending', outcome_code = NULL, outcome_sha256 = NULL
            WHERE run_id = @run AND command_seq = @seq
            """, ct, ("run", run.Value), ("seq", head.Sequence));
        await tx.CommitAsync(ct);
    }

    internal async Task<bool> CanCloseAsync(string tenant, ScenarioRunId run, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await BeginAsync(connection, tenant, ct);
        var row = await LockRunAsync(tx, tenant, run, ct);
        await using var command = Sql(tx, """
            SELECT NOT EXISTS (
              SELECT 1 FROM revenant.v4_scheduled_command
              WHERE run_id = @run AND (status IN ('Pending', 'RetryableBlocked') OR due_time_ms >= @watermark))
            """, ("run", run.Value), ("watermark", row.Watermark));
        return row.Status == "Open" && (bool)(await command.ExecuteScalarAsync(ct))!;
    }

    internal async Task<ObservedFinanceContext> ReadPinnedContextAsync(string tenant, ScenarioRunId run, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await BeginAsync(connection, tenant, ct);
        await using var query = Sql(tx, """
            SELECT canonical_context FROM revenant.v4_scheduler_run WHERE run_id = @run AND tenant_id = @tenant
            """, ("run", run.Value), ("tenant", tenant));
        var bytes = (byte[]?)await query.ExecuteScalarAsync(ct) ?? throw new V4ScheduleException(V4ScheduleError.RunNotFound);
        return ObservedFinanceContextCodec.Instance.Decode(bytes);
    }

    internal async Task<ObservedFinanceState> RebuildAsync(string tenant, ScenarioRun run, ObservedFinanceContext context, CancellationToken ct)
    {
        var initial = ObservedFinanceReducer.Initial(run, context);
        var state = initial.TryGetValue(out var opened) ? opened : throw new ObservedFinanceTransitionException(initial.Error);
        var history = await ReadFinanceHistoryAsync(tenant, run.RunId, run.RunId, ct);
        foreach (var seal in history.Facts)
        {
            state = seal.Apply(state, context);
        }
        if (state.LastFact != history.LastFact)
        {
            throw new InvalidOperationException("V4 SQL facts disagree with the run cursor.");
        }
        return state;
    }

    private static async Task<ObservedFinanceInput> AppendSealAsync(NpgsqlTransaction tx, string tenant, ScenarioRunId run,
        SchedulerRow row, long boundary, CancellationToken ct)
    {
        long last;
        string prefix;
        ScenarioRunId lineage;
        await using (var query = Sql(tx, """
            SELECT c.last_fact_seq, c.last_event_time_ms, c.closed_fact_seq,
                   coalesce(f.v4_prefix_digest, b.fork_prefix_digest), coalesce(b.lineage_run_id, c.scenario_run_id)
              FROM revenant.run_cursor c LEFT JOIN revenant.finance_fact f
                ON f.scenario_run_id = c.scenario_run_id AND f.fact_seq = c.last_fact_seq
              LEFT JOIN revenant.v4_branch_execution b ON b.execution_run_id = c.scenario_run_id AND b.tenant_id = c.tenant_id
             WHERE c.scenario_run_id = @run AND c.tenant_id = @tenant FOR UPDATE OF c
            """, ("run", run.Value), ("tenant", tenant)))
        await using (var reader = await query.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct) || !reader.IsDBNull(2) || reader.GetInt64(1) >= boundary)
            {
                throw new InvalidOperationException("V4 finance cursor cannot certify this boundary.");
            }
            last = reader.GetInt64(0);
            lineage = new(reader.GetGuid(4));
            prefix = last == 0 ? ObservedFinanceReducer.InitialDigest(lineage) : Convert.ToHexStringLower(reader.GetFieldValue<byte[]>(3));
        }

        if (last == long.MaxValue)
        {
            throw new V4ScheduleException(V4ScheduleError.SequenceOrTimeOverflow);
        }
        var input = ObservedFinanceInput.Seal(new(lineage, boundary, row.Watermark, last, prefix, row.CommandDigest));
        var bytes = input.Payload.ToArray();
        await ExecuteAsync(tx, """
            UPDATE revenant.run_cursor SET last_fact_seq = @seq, last_event_time_ms = @time WHERE scenario_run_id = @run
            """, ct, ("seq", input.Sequence), ("time", boundary), ("run", run.Value));
        await ExecuteAsync(tx, """
            INSERT INTO revenant.finance_fact
              (tenant_id, scenario_run_id, fact_seq, kind, schema_version, canonical, canonical_sha256, effective_time_ms)
            VALUES (@tenant, @run, @seq, 'ObservationSeal', @schema, @canonical, @sha, @time)
            """, ct, ("tenant", tenant), ("run", run.Value), ("seq", input.Sequence), ("schema", input.PayloadSchema),
            ("canonical", bytes), ("sha", SHA256.HashData(bytes)), ("time", boundary));
        await ExecuteAsync(tx, """
            UPDATE revenant.v4_scheduler_run SET watermark_exclusive_ms = @time, clock_ms = @time WHERE run_id = @run
            """, ct, ("time", boundary), ("run", run.Value));
        return input;
    }

    private static async Task<ObservedFinanceInput?> ReadSealAsync(NpgsqlTransaction tx, ScenarioRunId run, long boundary, CancellationToken ct)
    {
        await using var cmd = Sql(tx, """
            SELECT f.fact_seq, f.effective_time_ms, f.canonical, f.canonical_sha256, coalesce(b.lineage_run_id, f.scenario_run_id)
            FROM revenant.finance_fact f LEFT JOIN revenant.v4_branch_execution b
              ON b.execution_run_id = f.scenario_run_id AND b.tenant_id = f.tenant_id
            WHERE f.scenario_run_id = @run AND f.kind = 'ObservationSeal' AND f.effective_time_ms = @boundary
            """, ("run", run.Value), ("boundary", boundary));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? DecodeSealRow(new(reader.GetGuid(4)), reader) : null;
    }

    private static ObservedFinanceInput DecodeSealRow(ScenarioRunId run, NpgsqlDataReader reader)
    {
        var bytes = reader.GetFieldValue<byte[]>(2);
        var input = ObservedFinanceInput.Seal(ObservationSealCodec.Decode(bytes));
        if (input.RunId != run || input.Sequence != reader.GetInt64(0) || input.EffectiveTimeMs != reader.GetInt64(1)
            || !SHA256.HashData(bytes).AsSpan().SequenceEqual(reader.GetFieldValue<byte[]>(3)))
        {
            throw new InvalidOperationException("Stored V4 seal metadata disagrees with its canonical payload.");
        }
        return input;
    }

    internal async Task<IReadOnlyList<V4ScheduledCommand>> ReadJournalAsync(string tenant, ScenarioRunId run, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await BeginAsync(connection, tenant, ct);
        await using var query = Sql(tx, $"SELECT {CommandColumns} FROM revenant.v4_scheduled_command WHERE run_id = @run ORDER BY command_seq",
            ("run", run.Value));
        await using var reader = await query.ExecuteReaderAsync(ct);
        var commands = new List<V4ScheduledCommand>();
        while (await reader.ReadAsync(ct))
        {
            commands.Add(DecodeCommand(run, tenant, reader));
        }
        return commands;
    }

    private static async Task<V4ScheduledCommand?> ReadCommandAsync(NpgsqlTransaction tx, string tenant, ScenarioRunId run,
        CancellationToken ct, string? key = null)
    {
        const string next = "status IN ('Pending', 'RetryableBlocked') ORDER BY due_time_ms, command_seq LIMIT 1";
        await using var query = Sql(tx, $"SELECT {CommandColumns} FROM revenant.v4_scheduled_command WHERE run_id = @run AND "
            + (key is null ? next : "command_key = @key"), ("run", run.Value));
        if (key is not null)
        {
            query.Parameters.AddWithValue("key", key);
        }
        await using var reader = await query.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? DecodeCommand(run, tenant, reader) : null;
    }

    private static V4ScheduledCommand DecodeCommand(ScenarioRunId run, string tenant, NpgsqlDataReader reader) =>
        new(run, tenant, reader.GetInt64(0), new(reader.GetString(1), Enum.Parse<V4CommandClass>(reader.GetString(2)), reader.GetString(3),
            reader.GetString(4), [.. reader.GetFieldValue<byte[]>(5)], reader.GetInt64(7), reader.GetInt64(9)),
            Convert.ToHexStringLower(reader.GetFieldValue<byte[]>(6)), reader.GetInt64(8), Enum.Parse<V4CommandStatus>(reader.GetString(10)),
            reader.IsDBNull(11) ? null : reader.GetInt64(11), reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.IsDBNull(13) ? null : Convert.ToHexStringLower(reader.GetFieldValue<byte[]>(13)));

    private static async Task<SchedulerRow> LockRunAsync(NpgsqlTransaction tx, string tenant, ScenarioRunId run, CancellationToken ct)
    {
        await using (var runLock = Sql(tx, """
            SELECT run_id FROM revenant.v4_scheduler_run WHERE run_id = @run AND tenant_id = @tenant FOR UPDATE
            """, ("run", run.Value), ("tenant", tenant)))
        {
            if (await runLock.ExecuteScalarAsync(ct) is null)
            {
                throw new V4ScheduleException(V4ScheduleError.RunNotFound);
            }
        }
        await using var cmd = Sql(tx, """
            SELECT s.watermark_exclusive_ms, s.clock_ms, s.next_command_seq,
                   CASE WHEN c.closed_fact_seq IS NULL THEN s.status ELSE 'Closed' END, s.command_completion_digest
            FROM revenant.v4_scheduler_run s
            JOIN revenant.run_cursor c ON c.scenario_run_id = s.run_id AND c.tenant_id = s.tenant_id
            WHERE s.run_id = @run AND s.tenant_id = @tenant
            """, ("run", run.Value), ("tenant", tenant));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3), Convert.ToHexStringLower(reader.GetFieldValue<byte[]>(4)))
            : throw new V4ScheduleException(V4ScheduleError.RunNotFound);
    }

    private static void RequireOpen(SchedulerRow row)
    {
        if (row.Status != "Open")
        {
            throw new V4ScheduleException(V4ScheduleError.RunNotOpen);
        }
    }

    private static async Task<NpgsqlTransaction> BeginAsync(NpgsqlConnection connection, string tenant, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        var tx = await connection.BeginTransactionAsync(ct);
        try
        {
            await SetTenantAsync(tx, tenant, ct);
            return tx;
        }
        catch
        {
            await tx.DisposeAsync();
            throw;
        }
    }

    private static Task SetTenantAsync(NpgsqlTransaction tx, string tenant, CancellationToken ct) =>
        ExecuteAsync(tx, "SELECT set_config('revenant.tenant_id', @tenant, true)", ct, ("tenant", tenant));

    private static async Task ExecuteAsync(NpgsqlTransaction tx, string sql, CancellationToken ct, params (string Key, object Value)[] parameters)
    {
        await using var cmd = Sql(tx, sql, parameters);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static NpgsqlCommand Sql(NpgsqlTransaction tx, string sql, params (string Key, object Value)[] parameters)
    {
        var command = new NpgsqlCommand(sql, tx.Connection, tx);
        foreach (var (key, value) in parameters)
        {
            if (value is DBNull)
            {
                command.Parameters.AddWithValue(key, NpgsqlTypes.NpgsqlDbType.Bigint, value);
            }
            else
            {
                command.Parameters.AddWithValue(key, value);
            }
        }
        return command;
    }
}
