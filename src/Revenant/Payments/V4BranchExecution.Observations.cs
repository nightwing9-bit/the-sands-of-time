using Revenant.Contracts;
using Revenant.Observations;

namespace Revenant.Payments;

internal sealed partial class V4BranchExecution
{
    internal async Task<V4ProjectionHistory> ReadProjectionHistoryAsync(string tenant, ScenarioRunId execution, CancellationToken ct)
    {
        var binding = await store.ReadBindingAsync(tenant, execution, ct);
        var lineage = binding.LineageRunId;
        var segments = new Stack<V4ProjectionSegment>();
        var seen = new HashSet<ScenarioRunId>();
        V4ForkOrigin? child = null;

        while (true)
        {
            if (!seen.Add(binding.ExecutionRunId) || binding.LineageRunId != lineage
                || (child is not null && child.SourceTimelineId != binding.TimelineId))
            {
                throw new InvalidOperationException("Broken V4 projection lineage.");
            }

            var context = await store.ReadPinnedContextAsync(tenant, binding.ExecutionRunId, ct);
            var history = await store.ReadFinanceHistoryAsync(tenant, binding.ExecutionRunId, lineage, ct);
            if (binding.ContextHash != ContextHash(context))
            {
                throw new InvalidOperationException("Projection context disagrees with the execution binding.");
            }

            var last = child?.FactSequence ?? history.LastFact;
            if (child is not null)
            {
                var prefix = Require(await engine.RestoreAsync(binding.TimelineId, last, ct));
                if (!history.IsClosed || last > history.LastFact || prefix.StateHash != child.StateHash
                    || prefix.State.PrefixDigest != child.PrefixDigest || prefix.State.LastSealedBoundaryMs != child.BoundaryMs)
                {
                    throw new InvalidOperationException("Parent Timeline does not certify the inherited projection prefix.");
                }
            }
            segments.Push(new(context, history.Facts.TakeWhile(f => f.Input.Sequence <= last).ToArray(), last, binding.Fork));
            if (binding.Fork is null)
            {
                var genesis = Require(await engine.RestoreAsync(binding.TimelineId, 0, ct)).State;
                return new(execution, genesis, segments.ToArray());
            }

            child = binding.Fork;
            binding = await store.ReadBindingAsync(tenant, child.SourceExecutionRunId, ct);
        }
    }
}
