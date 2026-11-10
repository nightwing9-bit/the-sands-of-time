using System.Collections.Immutable;
using Marrow.Tests.TestDomains;
using Marrow.Timeline;
using static Marrow.Tests.Timeline.Engine.EngineTestKit;

namespace Marrow.Tests.Timeline.EndToEnd;

/// <summary>
/// One domain shape run through the shared engine: components, position-0 state, a source context
/// (the historical "v7-like" pin), a changed child context (the "v8-like" pin applied only after the fork),
/// a context whose artifact is later removed, source and child steps, and the adapter's own outcome projector.
/// The projector stands in for a domain metric; the engine never computes it.
/// </summary>
internal sealed record ShapeScenario<TState, TInput, TContext>(
    string Name,
    TimelineComponents<TState, TInput, TContext> Components,
    int CheckpointInterval,
    TState Initial,
    TContext SourceContext,
    TContext ChildContext,
    TContext MissingArtifactContext,
    IReadOnlyList<StepRecord<TInput>> SourceSteps,
    long ForkAt,
    IReadOnlyList<StepRecord<TInput>> ChildSteps,
    TInput ForgedInput,
    Func<TState, long> Outcome)
{
    public long SourceLast => SourceSteps.Count;

    public long ChildLast => ForkAt + ChildSteps.Count;

    /// <summary>Same reducer/codecs under a key the stored histories never pinned (a later "current" deployment only).</summary>
    public TimelineComponents<TState, TInput, TContext> UnpinnedComponents =>
        Components with { Versions = Components.Versions with { ReducerVersion = Components.Versions.ReducerVersion + "-next" } };

    /// <summary>Reference hashes 0..SourceLast computed without the engine.</summary>
    public List<(byte[] Bytes, string Hash)> SourceReference() =>
        ReferenceFold(Components, Initial, SourceContext, SourceSteps);

    /// <summary>Prefix 0..ForkAt under the source context, then ForkAt+1..ChildLast under the child context.</summary>
    public List<(byte[] Bytes, string Hash)> ChildReference()
    {
        var prefix = ReferenceFold(Components, Initial, SourceContext, SourceSteps.Take((int)ForkAt));
        var atFork = Components.StateCodec.Decode(prefix[^1].Bytes);
        return [.. prefix, .. ReferenceFold(Components, atFork, ChildContext, ChildSteps).Skip(1)];
    }
}

internal static class ShapeScenarios
{
    /// <summary>
    /// Adapter A, tick clock: every position is one tick; ticks 2, 4, 8, 10 have no inputs but still drain the
    /// timer/cooldown. Cooldown 3 at the source, 1 on the child, so the child's bursts diverge after the fork.
    /// </summary>
    public static ShapeScenario<TickWorld, TickInput, TickContext> Tick() => new(
        Name: "tick",
        Components: Engine.EngineTestKit.Tick(),
        CheckpointInterval: 4,
        Initial: TickWorld.Initial(20),
        SourceContext: new TickContext(3),
        ChildContext: new TickContext(1),
        MissingArtifactContext: new TickContext(9),
        SourceSteps: BurstyTicks(1, 12, "gate-src"),
        ForkAt: 5,
        ChildSteps: BurstyTicks(6, 8, "gate-kid"), // position 6 collects, so it has an input to forge
        ForgedInput: new TickInput(TickAction.Collect, "forged"),
        Outcome: s => s.Bursts);

    /// <summary>
    /// Adapter B, event clock: positions are event batches with nonuniform business timestamps (payload only).
    /// Every third batch has two events at the same business time with ordinals 1 and 0, supplied in that
    /// (reversed) order; the order-sensitive fold proves the engine applies (Ordinal, InputId) order.
    /// </summary>
    public static ShapeScenario<CounterState, CounterEvent, CounterContext> Counter() => new(
        Name: "event",
        Components: Engine.EngineTestKit.Counter(),
        CheckpointInterval: 3,
        Initial: CounterState.Initial,
        SourceContext: new CounterContext(2),
        ChildContext: new CounterContext(3),
        MissingArtifactContext: new CounterContext(5),
        SourceSteps: CounterSteps(1, 10, "gate-src"),
        ForkAt: 4,
        ChildSteps: CounterSteps(5, 5, "gate-kid"),
        ForgedInput: new CounterEvent(1, 999),
        Outcome: s => s.Value);

    public static List<StepRecord<CounterEvent>> CounterSteps(long first, int count, string idPrefix)
    {
        var steps = new List<StepRecord<CounterEvent>>();
        for (var p = first; p < first + count; p++)
        {
            var businessTime = 1_000 + p * p * 37; // nonuniform gaps between batches
            var amount = (int)(p * 7 % 11) - 3;
            var inputs = p % 3 == 0
                ? new List<OrderedInput<CounterEvent>>
                {
                    new($"{idPrefix}-{p}-late", 1, new CounterEvent(businessTime, amount + 5)),
                    new($"{idPrefix}-{p}-early", 0, new CounterEvent(businessTime, amount)),
                }
                : [new($"{idPrefix}-{p}", 0, new CounterEvent(businessTime, amount))];
            steps.Add(new StepRecord<CounterEvent>(p, inputs.ToImmutableArray()));
        }
        return steps;
    }
}
