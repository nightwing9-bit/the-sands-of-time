using Marrow.Kernel.Canonical;
using Npgsql;
using Revenant.Contracts;
using Revenant.Timeline;

namespace Revenant.Payments;

internal sealed record V4FinanceDispatch(V4ScheduledCommand Command, ObservedFinanceContext Context, ObservedFinanceState Prior)
{
    internal ScenarioRunId Execution => Command.RunId;
    internal long EffectiveMs => Command.Request.DueTimeMs;
}

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
            SELECT s.canonical_context, o.canonical_state FROM revenant.v4_scheduler_run s
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
        }
        foreach (var fact in await ReadFactsAsync(tx, tenant, run, binding.LineageRunId, ct))
        {
            state = fact.Apply(state, context);
        }
        if (context.RiskMode != ObservedRiskMode.NotRequired)
        {
            return new(V4CommandStatus.RetryableBlocked, "model-required", [1]);
        }
        return await ledger!.ExecuteV4Async(tx.Connection!, tx, tenant, new(command, context, state), ct);
    }
}
