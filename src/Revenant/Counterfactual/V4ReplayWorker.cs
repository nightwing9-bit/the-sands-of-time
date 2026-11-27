using System.Data;
using System.Text.Json;
using Marrow.Kernel.Canonical;
using Marrow.Timeline;
using Npgsql;
using Revenant.Cases;
using Revenant.Contracts;
using Revenant.Finance;
using Revenant.Intelligence;
using Revenant.Payments;
using Revenant.Timeline;

namespace Revenant.Counterfactual;

internal sealed record V4ReplayRequest(long ForkPosition, long HorizonMs, string AlternativePolicy, V4ComparisonMode Mode,
    V4ReplayAction[] Interventions);
internal sealed record V4ReplayJob(Guid Id, string Tenant, string Actor, ScenarioRunId Source, TimelineId Timeline,
    string Key, V4ReplayRequest Request, string Status, string? Result, string? Failure);
internal sealed record V4ReplayReceipt(Guid ChildTimelineId, string ChildStateHash, string ChildRecordHash, string SourceStateHash,
    string SourceRecordHash, string ForkStateHash, string ForkPrefixDigest, string SourcePolicy, string SourcePolicyConfigHash,
    string AlternativePolicy, string AlternativePolicyConfigHash, string ContinuationPolicy, ObservedRiskPin Model,
    V4ComparisonMode Mode, IReadOnlyList<V4ActionEvidence> Actions, V4EconomicComparison Economics);

internal sealed class V4ReplayWorker(NpgsqlDataSource data, ITimelineEngine<ObservedFinanceState, ObservedFinanceInput, ObservedFinanceContext> engine)
{
    private readonly V4SimulationStore reader = new(data);
    private static readonly Lazy<BehavioralRiskScorerV2> Model = new(() => BehavioralRiskScorerV2.Load(
        Path.Combine(AppContext.BaseDirectory, "Intelligence", "Artifacts", BehavioralRiskScorerV2.ArtifactDirectory),
        BehavioralRiskScorerV2.FeatureSchema, BehavioralRiskScorerV2.PublishedPin).Value);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    internal async Task<Guid[]> PendingAsync(string tenant, CancellationToken ct)
    {
        await using var c = await data.OpenConnectionAsync(ct);
        await using var tx = await BeginAsync(c, tenant, ct);
        await using var q = Command(tx, "SELECT id FROM revenant.counterfactual_run WHERE tenant_id=@tenant AND status IN ('Pending','Running') LIMIT 20",
            ("tenant", tenant));
        var result = new List<Guid>();
        await using var rows = await q.ExecuteReaderAsync(ct);
        while (await rows.ReadAsync(ct)) result.Add(rows.GetGuid(0));
        return result.ToArray();
    }

    internal async Task<bool> ProcessAsync(string tenant, Guid id, CancellationToken ct)
    {
        // A connection-owned lock survives the short status transaction and is released on process death.
        await using var lease = await data.OpenConnectionAsync(ct);
        await using var mutex = new NpgsqlCommand("SELECT pg_try_advisory_lock(hashtextextended(@id, 8))", lease);
        mutex.Parameters.AddWithValue("id", id.ToString("D"));
        if (await mutex.ExecuteScalarAsync(ct) is not true) return false;
        try
        {
            var job = await ReadAsync(data, tenant, id, ct);
            if (job is null || job.Status is not ("Pending" or "Running")) return false;
            if (job.Status == "Pending" && !await ChangeAsync(tenant, id, "Pending", "Running", null, null, ct)) return false;
            try
            {
                var result = await ExecuteAsync(job, ct);
                return await ChangeAsync(tenant, id, "Running", "Verified", JsonSerializer.Serialize(result, Json), null, ct);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or CanonicalFormatException or OverflowException)
            {
                var code = ex.Message.StartsWith("UnsupportedManualAction:", StringComparison.Ordinal)
                    ? "UnsupportedManualAction" : ex is OverflowException ? "EconomicOverflow" : "ReplayVerificationFailed";
                await ChangeAsync(tenant, id, "Running", "Failed", null, code, ct);
                return false;
            }
        }
        finally
        {
            await using var unlock = new NpgsqlCommand("SELECT pg_advisory_unlock(hashtextextended(@id, 8))", lease);
            unlock.Parameters.AddWithValue("id", id.ToString("D"));
            await unlock.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    private async Task<V4ReplayReceipt> ExecuteAsync(V4ReplayJob job, CancellationToken ct)
    {
        var binding = await reader.ReadBindingAsync(job.Tenant, job.Source, ct);
        if (binding.Fork is not null || binding.TimelineId != job.Timeline || binding.LineageRunId != job.Source)
            throw new InvalidOperationException("Replay must read the authorized root.");
        await using (var connection = await data.OpenConnectionAsync(ct))
        await using (var membership = new NpgsqlCommand("""
            SELECT EXISTS (SELECT 1 FROM revenant.scenario_run_staff s
            WHERE s.scenario_run_id=@run AND s.subject_id=@actor AND NOT EXISTS (
              SELECT 1 FROM revenant.scenario_run_staff_revocation r WHERE r.scenario_run_id=s.scenario_run_id
                AND r.subject_id=s.subject_id))
            """, connection))
        {
            membership.Parameters.AddWithValue("run", job.Source.Value);
            membership.Parameters.AddWithValue("actor", job.Actor);
            if (await membership.ExecuteScalarAsync(ct) is not true)
                throw new InvalidOperationException("Replay actor is no longer authorized for the source.");
        }
        var context = await reader.ReadPinnedContextAsync(job.Tenant, job.Source, ct);
        var sql = await reader.ReadFinanceHistoryAsync(job.Tenant, job.Source, job.Source, ct);
        if (!sql.IsClosed || binding.ContextHash != V4Counterfactual.ContextDigest(context))
            throw new InvalidOperationException("Source cursor or context was not verified.");
        var manifest = await ReadManifestAsync(job.Tenant, job.Timeline, ct);
        var before = (await engine.ReplayAsync(job.Timeline, ct)).Value;
        var genesis = (await engine.RestoreAsync(job.Timeline, 0, ct)).Value.State;
        var actions = await ReadActionsAsync(job, sql, ct);
        var alternatePolicy = job.Request.AlternativePolicy switch
        {
            PolicyCatalog.V8Id => PolicyCatalog.V8,
            _ => throw new InvalidOperationException("Unknown V4 alternative policy.")
        };
        var childContext = new ObservedFinanceContext(context.ScenarioVersion, context.ManifestSha256, context.OriginMs,
            PolicyCatalog.RefOf(alternatePolicy), context.ReviewPolicy, context.RiskMode, context.RiskFeatureSchema,
            context.RiskModel, context.AnomalyModels, context.LiquidityModels);
        var plan = V4Counterfactual.Plan(new(manifest, before, genesis, context, sql.Facts, actions), job.Request.ForkPosition,
            job.Request.HorizonMs, new(job.Id), childContext, job.Request.Mode, job.Request.Interventions, Model.Value, ct);
        var sourceFork = (await engine.RestoreAsync(job.Timeline, plan.Fork.LastFact, ct)).Value;
        if (!V4Counterfactual.SameState(sourceFork.State, plan.Fork)) throw new InvalidOperationException("Source fork differs.");
        var child = (await engine.ForkAsync(job.Timeline, plan.Fork.LastFact, $"r8:{job.Id:N}", childContext, ct)).Value;
        var childFork = (await engine.RestoreAsync(child, plan.Fork.LastFact, ct)).Value;
        if (childFork.StateHash != sourceFork.StateHash || !V4Counterfactual.SameState(childFork.State, sourceFork.State))
            throw new InvalidOperationException("Child fork differs.");
        foreach (var row in plan.Suffix)
        {
            var input = row.OrderedInput;
            _ = (await engine.AppendAsync(child, row.Input.Sequence - 1, new(row.Input.Sequence, [input]), input.InputId, ct)).Value;
        }
        var receipt = (await engine.CompleteAsync(child, plan.AlternativeFinal.LastFact, ct)).Value;
        var verifiedChild = (await engine.ReplayAsync(child, ct)).Value;
        var after = (await engine.ReplayAsync(job.Timeline, ct)).Value;
        var finalSql = await reader.ReadFinanceHistoryAsync(job.Tenant, job.Source, job.Source, ct);
        if (!finalSql.IsClosed || finalSql.LastFact != sql.LastFact
            || !finalSql.Facts.Select(f => f.PrefixDigest).SequenceEqual(sql.Facts.Select(f => f.PrefixDigest)))
            throw new InvalidOperationException("Source SQL finance history changed.");
        if (receipt.Status != HistoryStatus.Verified || verifiedChild.Status != HistoryStatus.Verified
            || !V4Counterfactual.SameState(verifiedChild.FinalState, plan.AlternativeFinal)
            || receipt.LastRecordHash != verifiedChild.LastRecordHash
            || after.Status != HistoryStatus.Verified || before.LastRecordHash != after.LastRecordHash
            || before.FinalStateHash != after.FinalStateHash || manifest != await ReadManifestAsync(job.Tenant, job.Timeline, ct))
            throw new InvalidOperationException("Source or child verification failed.");
        var economics = V4CounterfactualEconomics.Compare(plan, await ReadTruthAsync(job, ct));
        return new(child.Value, receipt.FinalStateHash, receipt.LastRecordHash, before.FinalStateHash, before.LastRecordHash,
            sourceFork.StateHash, plan.Fork.PrefixDigest, context.BasePolicy.PolicyId, context.BasePolicy.ConfigHash,
            childContext.BasePolicy.PolicyId, childContext.BasePolicy.ConfigHash, V4Counterfactual.ContinuationPolicy,
            context.RiskModel!, job.Request.Mode, plan.Actions, economics);
    }

    private async Task<HistoryManifest> ReadManifestAsync(string tenant, TimelineId id, CancellationToken ct)
    {
        await using var c = await data.OpenConnectionAsync(ct);
        await using var tx = await BeginAsync(c, tenant, ct);
        await using var q = Command(tx, """
            SELECT domain_id, owner_id, input_schema_version, state_schema_version, context_schema_version, context_hash,
                   context_ref, reducer_version, rules_hash, seed_hash, last_position, latest_state_hash, last_record_hash,
                   parent_timeline_id, fork_position, status
              FROM marrow_timeline.histories WHERE timeline_id=@id
            """, ("id", id.Value));
        await using var r = await q.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) throw new InvalidOperationException("Replay source manifest not found.");
        return new(id, r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
            r.GetString(6), r.GetString(7), r.GetString(8), r.GetString(9), r.GetInt64(10), r.GetString(11), r.GetString(12),
            r.IsDBNull(13) ? null : new(r.GetGuid(13)), r.IsDBNull(14) ? null : r.GetInt64(14), Enum.Parse<HistoryStatus>(r.GetString(15)));
    }

    private async Task<IReadOnlyList<V4ReplayAction>> ReadActionsAsync(V4ReplayJob job, V4FinanceHistory history, CancellationToken ct)
    {
        var journal = await reader.ReadJournalAsync(job.Tenant, job.Source, ct);
        foreach (var seal in history.Facts.Where(x => x.Input.Kind == ObservedFactKind.ObservationSeal))
        {
            var payload = ObservationSealCodec.Decode(seal.Input.Payload.AsSpan());
            if (V4SimulationScheduler.RecomputeCommandDigest(job.Source, payload.BoundaryMs, journal) != payload.ResolvedCommandDigestSha256)
                throw new InvalidOperationException("Source command digest differs from the seal.");
        }
        await using var c = await data.OpenConnectionAsync(ct);
        await using var tx = await BeginAsync(c, job.Tenant, ct);
        await using var q = Command(tx, """
            SELECT f.fact_seq, f.effective_time_ms, f.kind, f.canonical, j.command_key, j.canonical_payload,
                   j.payload_schema, j.command_class, j.status
              FROM revenant.finance_fact f
              JOIN revenant.v4_scheduled_command j ON j.run_id=f.scenario_run_id AND j.command_seq=f.v4_command_seq
              WHERE f.tenant_id=@tenant AND f.scenario_run_id=@run AND f.kind IN ('ReviewDecision','ReturnInstruction') ORDER BY f.fact_seq
            """, ("tenant", job.Tenant), ("run", job.Source.Value));
        var rows = new List<(long Fact, long At, string Kind, byte[] Canonical, string Key, byte[] Payload)>();
        await using (var r = await q.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                if (r.GetString(6) != "v4-human-request/1" || r.GetString(7) != "ConditionalHumanRequest" || r.GetString(8) != "Completed")
                    throw new InvalidOperationException("UnsupportedManualAction: unsupported command receipt.");
                rows.Add((r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetFieldValue<byte[]>(3), r.GetString(4), r.GetFieldValue<byte[]>(5)));
            }
        var actions = new List<V4ReplayAction>(rows.Count);
        foreach (var row in rows)
        {
            var (intent, subject, version, mfa, action) = DecodeHuman(row.Payload);
            await using var role = Command(tx, """
                SELECT role FROM revenant.scenario_run_staff WHERE scenario_run_id=@run AND subject_id=@subject
                """, ("run", job.Source.Value), ("subject", subject));
            var staff = (string?)await role.ExecuteScalarAsync(ct);
            var actor = new CaseActor(subject, staff is "supervisor" or "operator", staff == "supervisor", mfa);
            var review = row.Kind == "ReviewDecision";
            actions.Add(new(row.Key, intent, row.At, version, actor, review ? action : null, row.Fact,
                review ? null : ReturnInstructionCodec.Decode(row.Canonical).Reason));
        }
        return actions;
    }

    private static (string Intent, string Subject, long Version, bool Mfa, CaseAction Action) DecodeHuman(byte[] bytes)
    {
        var r = new CanonicalReader(bytes);
        if (r.ReadInt32() != 1) throw new InvalidOperationException("UnsupportedManualAction: invalid format.");
        var row = (r.ReadString(), r.ReadString(), r.ReadInt64(), r.ReadBoolean(), (CaseAction)r.ReadByte());
        r.EnsureEnd();
        return row;
    }

    private async Task<IReadOnlyDictionary<string, V4EvaluationTruth>> ReadTruthAsync(V4ReplayJob job, CancellationToken ct)
    {
        await using var c = await data.OpenConnectionAsync(ct);
        await using var tx = await BeginAsync(c, job.Tenant, ct);
        await using var q = Command(tx, """
            SELECT intent_id, malicious FROM revenant.counterfactual_truth WHERE tenant_id=@tenant AND source_run_id=@run
            """, ("tenant", job.Tenant), ("run", job.Source.Value));
        await using var r = await q.ExecuteReaderAsync(ct);
        var labels = new Dictionary<string, V4EvaluationTruth>(StringComparer.Ordinal);
        while (await r.ReadAsync(ct)) labels.Add(r.GetString(0), r.GetBoolean(1) ? V4EvaluationTruth.Malicious : V4EvaluationTruth.Legitimate);
        return labels;
    }

    internal static async Task<V4ReplayJob> SubmitAsync(NpgsqlDataSource data, string tenant, string actor, ScenarioRunId source,
        TimelineId timeline, string key, V4ReplayRequest request, CancellationToken ct)
    {
        await using var c = await data.OpenConnectionAsync(ct);
        await using var tx = await BeginAsync(c, tenant, ct);
        await using (var lockKey = Command(tx, "SELECT pg_advisory_xact_lock(hashtextextended(@key, 8))", ("key", tenant + ":" + key)))
            await lockKey.ExecuteNonQueryAsync(ct);
        var json = JsonSerializer.Serialize(request, Json);
        await using var q = Command(tx, """
            INSERT INTO revenant.counterfactual_run (id,tenant_id,actor_id,source_run_id,source_timeline_id,operation_key,request)
            SELECT @id,@tenant,@actor,@run,@timeline,@key,@request::jsonb
            WHERE NOT EXISTS (SELECT 1 FROM revenant.counterfactual_run WHERE tenant_id=@tenant AND operation_key=@key)
            """, ("id", Guid.NewGuid()), ("tenant", tenant), ("actor", actor), ("run", source.Value), ("timeline", timeline.Value),
            ("key", key), ("request", json));
        await q.ExecuteNonQueryAsync(ct);
        await using var find = Command(tx, """
            SELECT id, status, result::text, failure_code FROM revenant.counterfactual_run
             WHERE tenant_id=@tenant AND operation_key=@key AND actor_id=@actor AND source_run_id=@run
               AND source_timeline_id=@timeline AND request=@request::jsonb
            """, ("tenant", tenant), ("key", key), ("actor", actor), ("run", source.Value), ("timeline", timeline.Value), ("request", json));
        V4ReplayJob job;
        await using (var r = await find.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) throw new InvalidOperationException("ConflictingOperationKey");
            job = new(r.GetGuid(0), tenant, actor, source, timeline, key, request, r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3));
        }
        await tx.CommitAsync(ct);
        return job;
    }

    internal static async Task<V4ReplayJob?> ReadAsync(NpgsqlDataSource data, string tenant, Guid id, CancellationToken ct)
    {
        await using var c = await data.OpenConnectionAsync(ct);
        await using var tx = await BeginAsync(c, tenant, ct);
        await using var q = Command(tx, """
            SELECT actor_id,source_run_id,source_timeline_id,operation_key,request::text,status,result::text,failure_code
              FROM revenant.counterfactual_run WHERE id=@id AND tenant_id=@tenant
            """, ("id", id), ("tenant", tenant));
        await using var r = await q.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new(id, tenant, r.GetString(0), new(r.GetGuid(1)), new(r.GetGuid(2)), r.GetString(3),
            JsonSerializer.Deserialize<V4ReplayRequest>(r.GetString(4), Json)!, r.GetString(5),
            r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7));
    }

    internal static async Task<bool> CancelAsync(NpgsqlDataSource data, string tenant, Guid id, string actor, CancellationToken ct)
    {
        await using var c = await data.OpenConnectionAsync(ct);
        await using var tx = await BeginAsync(c, tenant, ct);
        await using var q = Command(tx, """
            UPDATE revenant.counterfactual_run SET status='Cancelled'
            WHERE id=@id AND tenant_id=@tenant AND actor_id=@actor AND status IN ('Pending','Running')
            """, ("id", id), ("tenant", tenant), ("actor", actor));
        var count = await q.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return count == 1;
    }

    internal static V4ReplayReceipt DecodeResult(string value) => JsonSerializer.Deserialize<V4ReplayReceipt>(value, Json)
        ?? throw new InvalidOperationException("Missing verified result.");

    private async Task<bool> ChangeAsync(string tenant, Guid id, string from, string to, string? result, string? failure, CancellationToken ct)
    {
        await using var c = await data.OpenConnectionAsync(ct);
        await using var tx = await BeginAsync(c, tenant, ct);
        await using var q = Command(tx, """
            UPDATE revenant.counterfactual_run SET status=@to,result=@result::jsonb,failure_code=@failure
             WHERE id=@id AND tenant_id=@tenant AND status=@from
            """, ("id", id), ("tenant", tenant), ("from", from), ("to", to));
        q.Parameters.Add("result", NpgsqlTypes.NpgsqlDbType.Text).Value = (object?)result ?? DBNull.Value;
        q.Parameters.Add("failure", NpgsqlTypes.NpgsqlDbType.Text).Value = (object?)failure ?? DBNull.Value;
        var changed = await q.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return changed == 1;
    }

    internal static async Task<NpgsqlTransaction> BeginAsync(NpgsqlConnection c, string tenant, CancellationToken ct)
    {
        var tx = await c.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            await using var set = Command(tx, "SELECT set_config('revenant.tenant_id', @tenant, true)", ("tenant", tenant));
            await set.ExecuteNonQueryAsync(ct);
            return tx;
        }
        catch { await tx.DisposeAsync(); throw; }
    }

    internal static NpgsqlCommand Command(NpgsqlTransaction tx, string sql, params (string Key, object Value)[] parameters)
    {
        var q = new NpgsqlCommand(sql, tx.Connection, tx);
        foreach (var (key, value) in parameters) q.Parameters.AddWithValue(key, value);
        return q;
    }
}
