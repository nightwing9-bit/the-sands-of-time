using Marrow.Kernel.Canonical;
using Marrow.Kernel.Results;
using Marrow.Timeline;
using Revenant.Contracts;
using Revenant.Finance;
using Revenant.Timeline;

namespace Revenant.Payments;

/// <summary>Registers the V4 Timeline and its PostgreSQL execution. The identity checkpoint supports seals.</summary>
internal sealed partial class V4BranchExecution(V4SimulationStore store,
    ITimelineEngine<ObservedFinanceState, ObservedFinanceInput, ObservedFinanceContext> engine)
{
    internal static string FamilyRulesHash { get; } = ObservedFinanceEncoding.Hash(new CanonicalWriter()
        .WriteString("revenant-v4-family-rules/1")
        .WriteString(ObservedFinanceVersion.ReducerVersion).WriteString(ObservedFinanceVersion.InputSchemaVersion)
        .WriteString(ObservedFinanceVersion.StateSchemaVersion).WriteString(ObservedFinanceVersion.ContextSchemaVersion)
        .WriteString(ObservedFinanceVersion.SchedulerContract).WriteString(ObservedFinanceVersion.ObservationContract)
        .WriteInt64(ObservedFinanceVersion.BucketMs));

    internal async Task<V4ExecutionBinding> OpenSourceAsync(string tenant, string owner, ScenarioRun run,
        ObservedFinanceContext context, CancellationToken ct)
    {
        RequireSourcePolicy(context);
        var initial = Initial(run, context);
        var timeline = Require(await engine.CreateSourceAsync(new(owner, ObservedFinanceTimelineVersions.V4, initial, context,
            FamilyRulesHash, context.ManifestSha256), $"revenant-v4:{run.RunId.Value:D}", ct));
        var binding = new V4ExecutionBinding(run.RunId, run.RunId, timeline, ContextHash(context));
        await store.OpenRunAsync(tenant, owner, run, context, binding, ct);
        return binding;
    }

    internal async Task<CompletionReceipt> CompleteAsync(string tenant, ScenarioRunId execution, CancellationToken ct)
    {
        var binding = await store.ReadBindingAsync(tenant, execution, ct);
        await store.CloseBoundSealHistoryAsync(tenant, execution, ct);
        var history = await store.ReadFinanceHistoryAsync(tenant, execution, binding.LineageRunId, ct);
        var context = await store.ReadPinnedContextAsync(tenant, execution, ct);
        _ = await ReconcileAsync(binding, context, history, ct);
        return Require(await engine.CompleteAsync(binding.TimelineId, history.LastFact, ct));
    }

    internal async Task<ObservedFinanceState> SynchronizeAsync(string tenant, ScenarioRunId execution, CancellationToken ct)
    {
        var binding = await store.ReadBindingAsync(tenant, execution, ct);
        var history = await store.ReadFinanceHistoryAsync(tenant, execution, binding.LineageRunId, ct);
        var context = await store.ReadPinnedContextAsync(tenant, execution, ct);
        return await ReconcileAsync(binding, context, history, ct);
    }

    internal async Task<V4ExecutionBinding> ForkAsync(string tenant, string owner, ScenarioRun run, ScenarioRunId sourceExecution,
        ScenarioRunId childExecution, long boundary, string operationKey, ObservedFinanceContext childContext, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationKey);
        var source = await store.ReadBindingAsync(tenant, sourceExecution, ct);
        if (source.LineageRunId != run.RunId || childExecution.IsEmpty || childExecution == sourceExecution
            || childExecution == source.LineageRunId)
        {
            throw new InvalidOperationException("Invalid V4 source lineage or child execution identity.");
        }

        var sourceContext = await store.ReadPinnedContextAsync(tenant, sourceExecution, ct);
        ValidateChildContext(sourceContext, childContext);
        var history = await store.ReadFinanceHistoryAsync(tenant, sourceExecution, source.LineageRunId, ct);
        var seal = history.Facts.SingleOrDefault(f => f.Input.Kind == ObservedFactKind.ObservationSeal && f.Input.EffectiveTimeMs == boundary)
            ?? throw new InvalidOperationException("The source has no committed seal at the requested boundary.");
        var final = Require(await engine.ReplayAsync(source.TimelineId, ct));
        if (!history.IsClosed || final.Status != HistoryStatus.Verified || final.FinalPosition != history.LastFact)
        {
            throw new InvalidOperationException("The V4 source must be closed and verified through its SQL cursor before forking.");
        }

        // Every Append here is a receipt verification on an immutable, Verified Timeline.
        var state = await ReconcileAsync(source, sourceContext, history, ct);
        var genesis = Require(await engine.RestoreAsync(source.TimelineId, 0, ct));
        if (!SameBytes(state, final.FinalState) || !SameBytes(genesis.State, Initial(run, childContext)))
        {
            throw new InvalidOperationException("The source Timeline, SQL history or child scenario opening disagree.");
        }

        var position = seal.Input.Sequence;
        var checkpoint = Require(await engine.RestoreAsync(source.TimelineId, position, ct));
        if (checkpoint.State.LastSealedBoundaryMs != boundary || checkpoint.State.PrefixDigest != seal.PrefixDigest)
        {
            throw new InvalidOperationException("The requested V4 fork does not match the verified seal.");
        }

        var fork = new V4ForkOrigin(sourceExecution, source.TimelineId, position, boundary, checkpoint.StateHash,
            checkpoint.State.PrefixDigest, operationKey);
        var request = new V4ExecutionBinding(childExecution, source.LineageRunId, default, ContextHash(childContext), fork);
        await store.ReserveForkAsync(tenant, owner, run, childContext, request, ct);


        var childTimeline = Require(await engine.ForkAsync(source.TimelineId, position, operationKey, childContext, ct));
        var inherited = Require(await engine.RestoreAsync(childTimeline, position, ct));
        if (childTimeline == source.TimelineId || checkpoint.StateHash != inherited.StateHash
            || !SameBytes(checkpoint.State, inherited.State))
        {
            throw new InvalidOperationException("Marrow changed the canonical V4 checkpoint at the fork.");
        }

        var binding = request with { TimelineId = childTimeline };
        await store.OpenRunAsync(tenant, owner, run, childContext, binding, ct, inherited.State);
        return binding;
    }

    internal static void ValidateChildContext(ObservedFinanceContext source, ObservedFinanceContext child)
    {
        RequireSourcePolicy(source);
        var codec = ObservedFinanceContextCodec.Instance;
        var expected = new ObservedFinanceContext(source.ScenarioVersion, source.ManifestSha256, source.OriginMs,
            PolicyCatalog.RefOf(PolicyCatalog.V8), source.ReviewPolicy, source.RiskMode, source.RiskFeatureSchema,
            source.RiskModel, source.AnomalyModels, source.LiquidityModels);
        var bytes = codec.EncodeCanonical(child);
        if (!codec.EncodeCanonical(codec.Decode(bytes)).AsSpan().SequenceEqual(bytes)
            || !codec.EncodeCanonical(expected).AsSpan().SequenceEqual(bytes))
        {
            throw new InvalidOperationException("The V4 child must preserve the source pins with the catalog v8 suffix policy.");
        }
    }

    private async Task<ObservedFinanceState> ReconcileAsync(V4ExecutionBinding binding, ObservedFinanceContext context,
        V4FinanceHistory history, CancellationToken ct)
    {
        var start = Require(await engine.RestoreAsync(binding.TimelineId, binding.Fork?.FactSequence ?? 0, ct));
        var state = start.State;
        if (binding.ContextHash != ContextHash(context) || state.RunId != binding.LineageRunId
            || (binding.Fork is { } fork && (start.StateHash != fork.StateHash || state.PrefixDigest != fork.PrefixDigest)))
        {
            throw new InvalidOperationException("The V4 binding disagrees with the pinned Timeline origin.");
        }
        foreach (var seal in history.Facts)
        {
            state = seal.Apply(state, context);
            var input = seal.OrderedInput;
            _ = Require(await engine.AppendAsync(binding.TimelineId, seal.Input.Sequence - 1,
                new(seal.Input.Sequence, [input]), input.InputId, ct));
        }

        if (state.LastFact != history.LastFact)
        {
            throw new InvalidOperationException("V4 SQL facts disagree with the run cursor.");
        }
        var restored = Require(await engine.RestoreAsync(binding.TimelineId, state.LastFact, ct));
        if (!SameBytes(state, restored.State))
        {
            throw new InvalidOperationException("The V4 SQL fold disagrees with the restored Timeline.");
        }
        return state;
    }

    private static void RequireSourcePolicy(ObservedFinanceContext context)
    {
        if (context.BasePolicy != PolicyCatalog.RefOf(PolicyCatalog.V7)
            || (context.RiskMode == ObservedRiskMode.ModelRequired && !V4ModelScreeningEvidence.Supports(context)))
        {
            throw new InvalidOperationException("The V4 source requires catalog v7 and a supported risk configuration.");
        }
    }

    private static string ContextHash(ObservedFinanceContext context) => ObservedFinanceEncoding.Hash(new CanonicalWriter()
        .WriteString(ObservedFinanceVersion.ContextSchemaVersion).WriteBytes(ObservedFinanceContextCodec.Instance.EncodeCanonical(context)));

    private static bool SameBytes(ObservedFinanceState left, ObservedFinanceState right) =>
        ObservedFinanceStateCodec.Instance.EncodeCanonical(left).AsSpan().SequenceEqual(ObservedFinanceStateCodec.Instance.EncodeCanonical(right));

    private static ObservedFinanceState Initial(ScenarioRun run, ObservedFinanceContext context)
    {
        var initial = ObservedFinanceReducer.Initial(run, context);
        return initial.TryGetValue(out var state) ? state : throw new ObservedFinanceTransitionException(initial.Error);
    }

    private static T Require<T>(Result<T, TimelineError> result) =>
        result.TryGetValue(out var value) ? value : throw new InvalidOperationException(result.Error.ToString());
}
