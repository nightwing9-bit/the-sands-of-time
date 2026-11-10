using FsCheck;
using FsCheck.Fluent;
using Marrow.Tests.TestDomains;
using Marrow.Tests.Timeline.Persistence;
using Marrow.Timeline;
using Xunit;
using static Marrow.Tests.Timeline.Engine.EngineTestKit;
using static Marrow.Tests.Timeline.Engine.RowDigest;

namespace Marrow.Tests.Timeline.Engine;

/// <summary>
/// FsCheck properties over real PostgreSQL for tick-shaped and event-shaped reducers through the same engine:
/// for a random Verified source, a random fork position T in 0..Last and two sibling branches with their own
/// contexts and random suffixes (after a cancelled append attempt), a fresh engine on a new connection pool
/// restores every child position 0..T to the source's recorded hash and every later position to a reference
/// fold that switches to the child context at T+1; aligned compare of the siblings returns those same hashes;
/// both children complete as Verified; the source's rows and manifest are byte-for-byte unchanged.
/// **Validates: Requirements R02.04, R02.05, R02.07, R02.09, R02.10, R02.13, R02.15**
/// </summary>
[Trait("Category", "Integration")]
public sealed class ForkPropertyTests(TimelinePostgresDatabase db) : IClassFixture<TimelinePostgresDatabase>
{
    private const int Cases = 15;

    public sealed record Scenario<TState, TInput, TContext>(
        TState Initial, TContext SourceContext, TContext ContextA, TContext ContextB, int Interval, long ForkAt,
        IReadOnlyList<StepRecord<TInput>> Source, IReadOnlyList<StepRecord<TInput>> SuffixA, IReadOnlyList<StepRecord<TInput>> SuffixB)
    {
        public override string ToString() =>
            $"Initial={Initial}, Contexts={SourceContext}/{ContextA}/{ContextB}, Interval={Interval}, T={ForkAt}, " +
            $"Source=[{Describe(Source)}], A=[{Describe(SuffixA)}], B=[{Describe(SuffixB)}]";

        private static string Describe(IReadOnlyList<StepRecord<TInput>> steps) =>
            string.Join(" | ", steps.Select(s => $"{s.Position}:{string.Join(",", s.Inputs.Select(i => $"{i.InputId}@{i.Ordinal}={i.Payload}"))}"));
    }

    [Fact]
    public void Tick_branches_share_the_exact_prefix_and_keep_independent_suffixes()
    {
        var input =
            from action in Gen.Elements(TickAction.Collect, TickAction.Burst)
            from item in Gen.Elements("a", "b", "c", "d")
            from ordinal in Gen.Choose(0, 2)
            select (Payload: new TickInput(action, item), Ordinal: ordinal);
        var gen = Scenarios(
            input,
            Gen.Choose(0, 30).Select(TickWorld.Initial),
            Gen.Choose(0, 5).Select(c => new TickContext(c)),
            maxInputsPerStep: 3);

        Prop.ForAll(gen.ToArbitrary(), s => RunAsync(s, () => Tick()).GetAwaiter().GetResult())
            .Check(Config.QuickThrowOnFailure.WithMaxTest(Cases));
    }

    [Fact]
    public void Event_branches_share_the_exact_prefix_and_keep_independent_suffixes()
    {
        var input =
            from businessTime in Gen.OneOf(Gen.Constant(1_000L), Gen.Choose(0, 1_000_000).Select(i => (long)i))
            from amount in Gen.Choose(-9, 9)
            from ordinal in Gen.Choose(0, 1)
            select (Payload: new CounterEvent(businessTime, amount), Ordinal: ordinal);
        var gen = Scenarios(
            input,
            Gen.Constant(CounterState.Initial),
            Gen.Elements(-1, 1, 2).Select(m => new CounterContext(m)), // |value| stays far below long range for <= 40 events
            maxInputsPerStep: 2);

        Prop.ForAll(gen.ToArbitrary(), s => RunAsync(s, Counter).GetAwaiter().GetResult())
            .Check(Config.QuickThrowOnFailure.WithMaxTest(Cases));
    }

    private static Gen<Scenario<TState, TInput, TContext>> Scenarios<TState, TInput, TContext>(
        Gen<(TInput Payload, int Ordinal)> input, Gen<TState> initial, Gen<TContext> context, int maxInputsPerStep)
    {
        Gen<List<List<(TInput, int)>>> Steps(int maxSteps) =>
            input.ListOf().Select(l => l.Take(maxInputsPerStep).ToList()).ListOf().Select(l => l.Take(maxSteps).ToList());

        return
            from start in initial
            from sourceContext in context
            from contextA in context
            from contextB in context
            from interval in Gen.Choose(1, 5)
            from source in Steps(12).Where(l => l.Count > 0)
            from forkAt in Gen.Choose(0, source.Count)
            from suffixA in Steps(8)
            from suffixB in Steps(8)
            select new Scenario<TState, TInput, TContext>(
                start, sourceContext, contextA, contextB, interval, forkAt,
                Build(source, 1, "s"), Build(suffixA, forkAt + 1, "a"), Build(suffixB, forkAt + 1, "b"));
    }

    /// <summary>Steps from <paramref name="first"/>; InputIds are unique per branch and sort independently of supply order.</summary>
    private static List<StepRecord<TInput>> Build<TInput>(List<List<(TInput Payload, int Ordinal)>> raw, long first, string prefix)
    {
        var steps = new List<StepRecord<TInput>>();
        for (var i = 0; i < raw.Count; i++)
        {
            var position = first + i;
            var inputs = raw[i].Select((r, k) => new OrderedInput<TInput>($"{prefix}{(k * 7 + 3) % 10}-{position}-{k}", r.Ordinal, r.Payload)).ToList();
            if (position % 2 == 0)
            {
                inputs.Reverse();
            }

            steps.Add(new StepRecord<TInput>(position, inputs));
        }

        return steps;
    }

    private async Task RunAsync<TState, TInput, TContext>(
        Scenario<TState, TInput, TContext> s, Func<TimelineComponents<TState, TInput, TContext>> components)
    {
        var ct = CancellationToken.None;
        var engine = NewEngine(db.Store, s.Interval, components());
        var source = (await engine.CreateSourceAsync(
            new CreateSourceRequest<TState, TContext>(Owner, components().Versions, s.Initial, s.SourceContext, "rules-f", "seed-f"), Key(), ct)).Value;
        await AppendAllAsync(engine, source, 0, s.Source);
        Assert.True((await engine.CompleteAsync(source, s.Source.Count, ct)).IsSuccess);
        var sourceRows = await RowsDigestAsync(db.Owner, source);
        var sourceManifest = (await db.Store.GetHistoryAsync(source, ct)).Value;

        var a = (await engine.ForkAsync(source, s.ForkAt, Key(), s.ContextA, ct)).Value;
        var b = (await engine.ForkAsync(source, s.ForkAt, Key(), s.ContextB, ct)).Value;

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        if (s.SuffixA.Count > 0)
        {
            Assert.Equal(TimelineErrorCode.Cancelled, (await engine.AppendAsync(a, s.ForkAt, s.SuffixA[0], Key(), cancelled.Token)).Error.Code);
        }

        await AppendAllAsync(engine, a, s.ForkAt, s.SuffixA);
        await AppendAllAsync(engine, b, s.ForkAt, s.SuffixB);

        var c = components();
        var prefix = ReferenceFold(c, s.Initial, s.SourceContext, s.Source.Take((int)s.ForkAt));
        var atFork = c.StateCodec.Decode(prefix[^1].Bytes);
        var expectedA = prefix.Concat(ReferenceFold(c, atFork, s.ContextA, s.SuffixA).Skip(1)).Select(x => x.Hash).ToList();
        var expectedB = prefix.Concat(ReferenceFold(c, atFork, s.ContextB, s.SuffixB).Skip(1)).Select(x => x.Hash).ToList();
        var recordedSource = ReferenceFold(c, s.Initial, s.SourceContext, s.Source).Select(x => x.Hash).ToList();

        var cold = NewEngine(db.NewColdStore(), s.Interval + 1, components());
        for (var t = 0; t < expectedA.Count; t++)
        {
            var restored = await cold.RestoreAsync(a, t, ct);
            Assert.True(restored.IsSuccess, $"A T={t}: {restored}");
            Assert.Equal(expectedA[t], restored.Value.StateHash);
            if (t <= s.ForkAt)
            {
                Assert.Equal(recordedSource[t], restored.Value.StateHash);
            }
        }

        for (var t = 0; t < expectedB.Count; t++)
        {
            Assert.Equal(expectedB[t], (await cold.RestoreAsync(b, t, ct)).Value.StateHash);
        }

        var common = Enumerable.Range(0, Math.Min(expectedA.Count, expectedB.Count)).Select(i => (long)i).ToList();
        var aligned = await cold.CompareAlignedAsync(a, b, common, ct);
        Assert.True(aligned.IsSuccess, aligned.ToString());
        Assert.Equal(common.Select(p => expectedA[(int)p]), aligned.Value.Select(x => x.Source.StateHash));
        Assert.Equal(common.Select(p => expectedB[(int)p]), aligned.Value.Select(x => x.Alternative.StateHash));

        Assert.Equal(HistoryStatus.Verified, (await cold.CompleteAsync(a, expectedA.Count - 1, ct)).Value.Status);
        Assert.Equal(HistoryStatus.Verified, (await cold.CompleteAsync(b, expectedB.Count - 1, ct)).Value.Status);
        Assert.Equal(sourceRows, await RowsDigestAsync(db.Owner, source));
        Assert.Equal(sourceManifest, (await db.Store.GetHistoryAsync(source, ct)).Value);
    }

    private static async Task AppendAllAsync<TState, TInput, TContext>(
        ITimelineEngine<TState, TInput, TContext> engine, TimelineId id, long after, IReadOnlyList<StepRecord<TInput>> steps)
    {
        for (var i = 0; i < steps.Count; i++)
        {
            var appended = await engine.AppendAsync(id, after + i, steps[i], Key(), CancellationToken.None);
            Assert.True(appended.IsSuccess, appended.ToString());
        }
    }
}
