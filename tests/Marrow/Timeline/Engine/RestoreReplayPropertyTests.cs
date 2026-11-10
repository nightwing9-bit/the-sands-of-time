using FsCheck;
using FsCheck.Fluent;
using Marrow.Tests.TestDomains;
using Marrow.Tests.Timeline.Persistence;
using Marrow.Timeline;
using Xunit;
using static Marrow.Tests.Timeline.Engine.EngineTestKit;

namespace Marrow.Tests.Timeline.Engine;

/// <summary>
/// FsCheck properties over real PostgreSQL: for random tick-shaped and event-shaped histories (random input
/// counts including empty steps, tied ordinals, shuffled supply order, nonuniform business timestamps and
/// random checkpoint spacing), a fresh engine on a new connection pool restores every valid position
/// 0..LastPosition to the recorded state hash and to an independent reference fold; out-of-range positions
/// are InvalidPosition; full replay reproduces the final state hash and record chain.
/// **Validates: Requirements R02.02, R02.03, R02.12, R02.14**
/// </summary>
[Trait("Category", "Integration")]
public sealed class RestoreReplayPropertyTests(TimelinePostgresDatabase db) : IClassFixture<TimelinePostgresDatabase>
{
    private const int Cases = 25;

    public sealed record Scenario<TState, TInput, TContext>(
        TState Initial, TContext Context, int WriterInterval, int ReaderInterval, IReadOnlyList<StepRecord<TInput>> Steps)
    {
        public override string ToString() =>
            $"Initial={Initial}, Context={Context}, Intervals={WriterInterval}/{ReaderInterval}, Steps=[{string.Join(" | ", Steps.Select(s =>
                $"{s.Position}:{string.Join(",", s.Inputs.Select(i => $"{i.InputId}@{i.Ordinal}={i.Payload}"))}"))}]";
    }

    [Fact]
    public void Tick_histories_restore_every_position_to_its_recorded_hash()
    {
        var input =
            from action in Gen.Elements(TickAction.Collect, TickAction.Burst)
            from item in Gen.Elements("a", "b", "c", "d", "e")
            from ordinal in Gen.Choose(0, 2)
            from tag in Gen.Choose(0, 99)
            select (action, item, ordinal, tag);
        var gen =
            from timer in Gen.Choose(0, 30)
            from cooldown in Gen.Choose(0, 4)
            from writerInterval in Gen.Choose(1, 6)
            from readerInterval in Gen.Choose(1, 6)
            from shapes in Gen.Choose(0, 3).ListOf().Select(l => l.Take(24).ToList())
            from raw in input.ArrayOf(shapes.Sum())
            select new Scenario<TickWorld, TickInput, TickContext>(
                TickWorld.Initial(timer), new TickContext(cooldown), writerInterval, readerInterval,
                BuildSteps(shapes, raw, r => new TickInput(r.action, r.item), r => r.ordinal, r => r.tag));

        Prop.ForAll(gen.ToArbitrary(), s => RunAsync(s, () => Tick()).GetAwaiter().GetResult())
            .Check(Config.QuickThrowOnFailure.WithMaxTest(Cases));
    }

    [Fact]
    public void Event_histories_restore_every_position_to_its_recorded_hash()
    {
        var input =
            from businessTime in Gen.OneOf(Gen.Constant(1_000L), Gen.Choose(0, 3).Select(i => i * 997L), Gen.Choose(0, 1_000_000).Select(i => (long)i))
            from amount in Gen.Choose(-9, 9)
            from ordinal in Gen.Choose(0, 1)
            from tag in Gen.Choose(0, 99)
            select (businessTime, amount, ordinal, tag);
        var gen =
            from multiplier in Gen.Elements(-1, 2)
            from writerInterval in Gen.Choose(1, 6)
            from readerInterval in Gen.Choose(1, 6)
            from shapes in Gen.Choose(0, 2).ListOf().Select(l => l.Take(20).ToList())
            from raw in input.ArrayOf(shapes.Sum())
            select new Scenario<CounterState, CounterEvent, CounterContext>(
                CounterState.Initial, new CounterContext(multiplier), writerInterval, readerInterval,
                BuildSteps(shapes, raw, r => new CounterEvent(r.businessTime, r.amount), r => r.ordinal, r => r.tag));

        Prop.ForAll(gen.ToArbitrary(), s => RunAsync(s, Counter).GetAwaiter().GetResult())
            .Check(Config.QuickThrowOnFailure.WithMaxTest(Cases));
    }

    /// <summary>Steps 1..n; InputIds are unique but sort independently of supply order; supply order is reversed when the tag is odd.</summary>
    private static List<StepRecord<TInput>> BuildSteps<TRaw, TInput>(
        IReadOnlyList<int> inputsPerStep, IReadOnlyList<TRaw> raw, Func<TRaw, TInput> payload, Func<TRaw, int> ordinal, Func<TRaw, int> tag)
    {
        var steps = new List<StepRecord<TInput>>();
        var next = 0;
        for (var p = 0; p < inputsPerStep.Count; p++)
        {
            var inputs = new List<OrderedInput<TInput>>();
            for (var k = 0; k < inputsPerStep[p]; k++, next++)
            {
                var r = raw[next];
                inputs.Add(new OrderedInput<TInput>($"x{tag(r):D2}-{p + 1}-{k}", ordinal(r), payload(r)));
            }

            if (inputs.Count > 0 && tag(raw[next - 1]) % 2 == 1)
            {
                inputs.Reverse();
            }

            steps.Add(new StepRecord<TInput>(p + 1, inputs));
        }

        return steps;
    }

    private async Task RunAsync<TState, TInput, TContext>(
        Scenario<TState, TInput, TContext> s, Func<TimelineComponents<TState, TInput, TContext>> components)
    {
        var ct = CancellationToken.None;
        var writer = NewEngine(db.Store, s.WriterInterval, components());
        var id = (await writer.CreateSourceAsync(
            new CreateSourceRequest<TState, TContext>(Owner, components().Versions, s.Initial, s.Context, "rules-p", "seed-p"), Key(), ct)).Value;
        for (var i = 0; i < s.Steps.Count; i++)
        {
            var appended = await writer.AppendAsync(id, i, s.Steps[i], Key(), ct);
            Assert.True(appended.IsSuccess, appended.ToString());
        }

        var expected = ReferenceFold(components(), s.Initial, s.Context, s.Steps);
        var last = s.Steps.Count;

        // What was durably recorded: base state hash plus each step's chain-protected state hash.
        var stored = (await db.Store.GetHistoryAsync(id, ct)).Value;
        var recorded = new List<string> { stored.BaseStateHash };
        recorded.AddRange((await db.Store.ReadStepsAsync(id, 0, last, ct)).Value.Select(step => step.StateHash));
        Assert.Equal(expected.Select(e => e.Hash), recorded);

        // A fresh engine, registry, codec instances and connection pool, with different checkpoint spacing.
        var reader = NewEngine(db.NewColdStore(), s.ReaderInterval, components());
        var codec = components().StateCodec;
        for (var t = 0; t <= last; t++)
        {
            var restored = await reader.RestoreAsync(id, t, ct);
            Assert.True(restored.IsSuccess, $"T={t}: {restored}");
            var v = restored.Value;
            Assert.Equal(t, v.Position);
            Assert.Equal(recorded[t], v.StateHash);
            Assert.Equal(expected[t].Bytes, codec.EncodeCanonical(v.State));
            Assert.Equal(HistoryStatus.Recording, v.HistoryStatus);
            Assert.Equal(t % s.WriterInterval == 0 ? StateHashOrigin.StoredCheckpoint : StateHashOrigin.ReconstructedFromVerifiedChain, v.HashOrigin);
        }

        Assert.Equal(TimelineErrorCode.InvalidPosition, (await reader.RestoreAsync(id, -1, ct)).Error.Code);
        Assert.Equal(TimelineErrorCode.InvalidPosition, (await reader.RestoreAsync(id, last + 1, ct)).Error.Code);

        var replay = (await reader.ReplayAsync(id, ct)).Value;
        Assert.Equal(last, replay.FinalPosition);
        Assert.Equal(recorded[last], replay.FinalStateHash);
        Assert.Equal(stored.Manifest.LastRecordHash, replay.LastRecordHash);
        Assert.Equal(expected[last].Bytes, codec.EncodeCanonical(replay.FinalState));

        var completed = await reader.CompleteAsync(id, last, ct);
        Assert.Equal(HistoryStatus.Verified, completed.Value.Status);
        Assert.Equal(recorded[last], completed.Value.FinalStateHash);
    }
}
