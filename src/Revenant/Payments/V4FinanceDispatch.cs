using System.Diagnostics;
using Npgsql;
using Revenant.Contracts;
using Revenant.Observations;
using Revenant.Timeline;

namespace Revenant.Payments;

internal sealed record V4FinanceDispatch(V4ScheduledCommand Command, ObservedFinanceContext Context, ObservedFinanceState Prior,
    Func<string, CancellationToken, Task<V4ModelInference>>? Infer = null,
    V4ModelScreeningEvidence? Screening = null)
{
    internal ScenarioRunId Execution => Command.RunId;
    internal long EffectiveMs => Command.Request.DueTimeMs;
}

internal sealed record V4ModelInference(V4ModelScreeningEvidence? Evidence, string? BlockedCode = null);

internal sealed partial class V4SimulationStore
{
    private async Task<V4CommandOutcome?> DispatchFinanceAsync(NpgsqlTransaction tx, string tenant, ScenarioRunId run,
        V4ScheduledCommand command, CancellationToken ct)
    {
        var binding = await ReadBindingAsync(tx, tenant, run, ct);
        if (binding is null || binding.TimelineId == default)
        {
            return null;
        }
        ObservedFinanceContext context;
        ObservedFinanceState state;
        await using (var query = Sql(tx, """
            SELECT s.canonical_context, o.canonical_state, s.model_review_capacity_policy FROM revenant.v4_scheduler_run s
            JOIN revenant.v4_finance_opening o ON o.run_id = s.run_id AND o.tenant_id = s.tenant_id
            JOIN revenant.v4_branch_execution b ON b.execution_run_id = s.run_id AND b.tenant_id = s.tenant_id
            WHERE s.run_id = @run AND s.tenant_id = @tenant AND b.readiness_status = 'Ready'
            """, ("run", run.Value), ("tenant", tenant)))
        await using (var reader = await query.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct))
            {
                return null;
            }
            context = ObservedFinanceContextCodec.Instance.Decode(reader.GetFieldValue<byte[]>(0));
            state = ObservedFinanceStateCodec.Instance.Decode(reader.GetFieldValue<byte[]>(1));
            var capacityPolicy = reader.IsDBNull(2) ? null : reader.GetString(2);
            if (capacityPolicy != (context.RiskMode == ObservedRiskMode.ModelRequired ? V4ReviewCounts.Policy : null))
                throw new InvalidOperationException("V4 execution has a mismatched review capacity policy.");
        }
        var opening = state;
        var facts = await ReadFactsAsync(tx, tenant, run, binding.LineageRunId, ct);
        foreach (var fact in facts)
        {
            state = fact.Apply(state, context);
        }
        return await ledger!.ExecuteV4Async(tx.Connection!, tx, tenant, new(command, context, state, Infer), ct);

        async Task<V4ModelInference> Infer(string intent, CancellationToken token)
        {
            var at = command.Request.DueTimeMs;
            if (V4ReviewCounts.InWarmup(context, at))
                return new(V4ModelScreeningEvidence.Create(state, context, intent, at, [], null, null));
            if (model is null)
                return new(null, "model-configuration-not-registered");
            if (!model.Value.TryGetValue(out var scorer))
                return new(null, $"model-configuration-{model.Value.Error.Code}");
            if (scorer.Pin != context.RiskModel)
                return new(null, "model-configuration-pin-mismatch");

            var segments = new Stack<V4ProjectionSegment>();
            segments.Push(new(context, facts, state.LastFact, binding.Fork));
            var current = binding;
            var genesis = opening;
            while (current.Fork is { } fork)
            {
                current = await ReadBindingAsync(tx, tenant, fork.SourceExecutionRunId, token)
                    ?? throw new InvalidOperationException("Missing V4 ancestor binding.");
                if (current.LineageRunId != binding.LineageRunId || current.TimelineId != fork.SourceTimelineId
                    || (current.Fork is { } ancestor && ancestor.FactSequence >= fork.FactSequence))
                    throw new InvalidOperationException("Invalid V4 ancestor binding.");

                ObservedFinanceContext ancestorContext;
                await using (var query = Sql(tx, """
                    SELECT s.canonical_context, o.canonical_state FROM revenant.v4_scheduler_run s
                    JOIN revenant.v4_finance_opening o ON o.run_id = s.run_id AND o.tenant_id = s.tenant_id
                    JOIN revenant.run_cursor c ON c.scenario_run_id = s.run_id AND c.tenant_id = s.tenant_id
                    WHERE s.run_id = @run AND s.tenant_id = @tenant AND c.closed_fact_seq >= @through
                    """, ("run", current.ExecutionRunId.Value), ("tenant", tenant), ("through", fork.FactSequence)))
                await using (var reader = await query.ExecuteReaderAsync(token))
                {
                    if (!await reader.ReadAsync(token))
                        throw new InvalidOperationException("Missing closed V4 ancestor opening.");
                    ancestorContext = ObservedFinanceContextCodec.Instance.Decode(reader.GetFieldValue<byte[]>(0));
                    genesis = ObservedFinanceStateCodec.Instance.Decode(reader.GetFieldValue<byte[]>(1));
                }
                var inherited = await ReadFactsAsync(tx, tenant, current.ExecutionRunId, current.LineageRunId, token, fork.FactSequence);
                segments.Push(new(ancestorContext, inherited, fork.FactSequence, current.Fork));
            }

            var risk = V4ObservationProjector.RiskAt(new(run, genesis, segments.ToArray()), intent, at);
            if (risk.CutoffFactSeq != state.LastFact || risk.CutoffPrefixDigest != state.PrefixDigest)
                throw new InvalidOperationException("V4 model features disagree with the transaction prefix.");
            var bytes = risk.Vector.Encode();
            var scoreStarted = Stopwatch.GetTimestamp();
            var score = scorer.Score(bytes);
            V4FinanceMetrics.Scoring.Record(Stopwatch.GetElapsedTime(scoreStarted).TotalMilliseconds);
            if (!score.TryGetValue(out var result))
                return new(null, $"model-inference-{score.Error.Code}");
            if (result.FeatureDigest != risk.Vector.Digest)
                throw new InvalidOperationException("The R5 score did not bind to the transaction vector.");
            return new(V4ModelScreeningEvidence.Create(state, context, intent, at, [.. bytes],
                BitConverter.SingleToInt32Bits(result.RawScore), result.RiskPpm));
        }
    }
}
