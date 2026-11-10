using Marrow.Infrastructure.Timeline;
using Marrow.Kernel.Results;
using Marrow.Tests.TestDomains;
using Marrow.Tests.Timeline.Persistence;
using Marrow.Timeline;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using static Marrow.Tests.Timeline.Engine.EngineTestKit;
using static Marrow.Tests.Timeline.Engine.RowDigest;

namespace Marrow.Tests.Timeline.EndToEnd;

/// <summary>
/// Task 2.5 / Spec 02 exit gate. A tick-clock adapter and an event-clock adapter are composed into the same
/// host container through the public <see cref="TimelineServiceCollectionExtensions.AddMarrowTimeline{TState,TInput,TContext}"/>
/// entry point, backed by a freshly migrated PostgreSQL (Spec-01 <c>migrate.sh</c>) and the runtime login.
/// Each shape goes create(0) → append → complete → restore every T → fork → child append → aligned compare,
/// then the server restarts and a brand-new container replays everything from durable pinned codecs/contexts.
/// Covers R02.01–R02.15 end to end; the engine never computes the outcome metric.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CrossDomainShapeGateTests(TimelinePostgresDatabase db) : IClassFixture<TimelinePostgresDatabase>
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task Tick_and_event_shapes_run_through_one_engine_and_replay_after_a_restart_from_pinned_data()
    {
        var tick = ShapeScenarios.Tick();
        var counter = ShapeScenarios.Counter();
        Assert.Contains(tick.SourceSteps, s => s.Inputs.Count == 0); // empty ticks are real positions
        await using (var count = db.Owner.CreateCommand("SELECT count(*) FROM marrow_timeline.histories"))
        {
            Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!); // freshly migrated, empty schema
        }

        RunIds tickRun;
        RunIds counterRun;
        await using (var host = NewHost(tick.Components, counter.Components))
        {
            var tickEngine = host.GetRequiredService<ITimelineEngine<TickWorld, TickInput, TickContext>>();
            var counterEngine = host.GetRequiredService<ITimelineEngine<CounterState, CounterEvent, CounterContext>>();
            // R02.09: one engine implementation from the Timeline assembly serves both shapes.
            Assert.Equal(tickEngine.GetType().GetGenericTypeDefinition(), counterEngine.GetType().GetGenericTypeDefinition());
            Assert.Same(typeof(ITimelineEngine<,,>).Assembly, tickEngine.GetType().Assembly);

            tickRun = await RecordAsync(tickEngine, tick);
            counterRun = await RecordAsync(counterEngine, counter);
        }

        await db.RestartAsync(); // server process restart: every pooled connection is gone

        await using (var host = NewHost(tick.Components, counter.Components))
        {
            await ReplayColdAsync(host.GetRequiredService<ITimelineEngine<TickWorld, TickInput, TickContext>>(), tick, tickRun);
            await ReplayColdAsync(host.GetRequiredService<ITimelineEngine<CounterState, CounterEvent, CounterContext>>(), counter, counterRun);
        }

        // A deployment that only knows later version keys must fail closed, never fall back to "current".
        await using (var later = NewHost(tick.UnpinnedComponents, counter.UnpinnedComponents))
        {
            await AssertIncompatibleAsync(later.GetRequiredService<ITimelineEngine<TickWorld, TickInput, TickContext>>(), tickRun);
            await AssertIncompatibleAsync(later.GetRequiredService<ITimelineEngine<CounterState, CounterEvent, CounterContext>>(), counterRun);
        }

        await using (var host = NewHost(tick.Components, counter.Components))
        {
            await CorruptAndMissingArtifactAsync(host.GetRequiredService<ITimelineEngine<TickWorld, TickInput, TickContext>>(), tick, tickRun);
            await CorruptAndMissingArtifactAsync(host.GetRequiredService<ITimelineEngine<CounterState, CounterEvent, CounterContext>>(), counter, counterRun);
        }
    }

    [Fact]
    public void Composition_rejects_a_second_registration_for_the_same_shape_and_a_nonpositive_checkpoint_interval()
    {
        var services = new ServiceCollection();
        services.AddMarrowTimeline([Tick()], 4);
        Assert.Throws<InvalidOperationException>(() => services.AddMarrowTimeline([Tick()], 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection().AddMarrowTimeline([Counter()], 0));
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddMarrowTimeline([Counter(), Counter()], 4));
    }

    private ServiceProvider NewHost(
        TimelineComponents<TickWorld, TickInput, TickContext> tick,
        TimelineComponents<CounterState, CounterEvent, CounterContext> counter)
    {
        var connectionString = db.AppConnectionString; // runtime login; read after any restart (port changes)
        var services = new ServiceCollection();
        services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString)); // owned and disposed by this container
        services.AddMarrowTimeline([tick], ShapeScenarios.Tick().CheckpointInterval);
        services.AddMarrowTimeline([counter], ShapeScenarios.Counter().CheckpointInterval);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private sealed record RunIds(
        TimelineId Source, TimelineId Child, TimelineId MissingArtifactChild, string SourceRows, string SourceManifest);

    /// <summary>Host 1: everything up to a Recording child that has been compared against its Verified source.</summary>
    private async Task<RunIds> RecordAsync<TState, TInput, TContext>(
        ITimelineEngine<TState, TInput, TContext> engine, ShapeScenario<TState, TInput, TContext> s)
    {
        var sourceRef = s.SourceReference();
        var childRef = s.ChildReference();

        var request = new CreateSourceRequest<TState, TContext>(Owner, s.Components.Versions, s.Initial, s.SourceContext, $"rules-{s.Name}", "seed-1");
        var source = Ok(await engine.CreateSourceAsync(request, Key(), Ct), s, "create");
        var initial = Ok(await engine.RestoreAsync(source, 0, Ct), s, "restore 0");
        Assert.Equal(sourceRef[0].Hash, initial.StateHash);
        Assert.Equal(StateHashOrigin.StoredCheckpoint, initial.HashOrigin);
        Assert.Equal(HistoryStatus.Recording, initial.HistoryStatus);

        await AppendAllAsync(engine, source, 0, s.SourceSteps, s);
        await AssertRestoresAsync(engine, source, sourceRef, s, "recording source");
        var completed = Ok(await engine.CompleteAsync(source, s.SourceLast, Ct), s, "complete source");
        Assert.Equal(HistoryStatus.Verified, completed.Status);
        Assert.Equal(sourceRef[^1].Hash, completed.FinalStateHash);
        var rowsBefore = await RowsDigestAsync(db.Owner, source);
        var manifestBefore = await ManifestAsync(source);

        var forkKey = Key();
        var child = Ok(await engine.ForkAsync(source, s.ForkAt, forkKey, s.ChildContext, Ct), s, "fork");
        Assert.Equal(child, Ok(await engine.ForkAsync(source, s.ForkAt, forkKey, s.ChildContext, Ct), s, "fork retry"));
        var missing = Ok(await engine.ForkAsync(source, s.ForkAt, Key(), s.MissingArtifactContext, Ct), s, "fork missing");

        // Forks continue at T+1 through the same AppendAsync as a source.
        Assert.Equal(TimelineErrorCode.InvalidPosition,
            (await engine.AppendAsync(child, s.ForkAt, s.ChildSteps[1], Key(), Ct)).Error.Code);
        await AppendAllAsync(engine, child, s.ForkAt, s.ChildSteps, s);
        await AppendAllAsync(engine, missing, s.ForkAt, [s.ChildSteps[0]], s);
        await AssertRestoresAsync(engine, child, childRef, s, "recording child");

        // Aligned compare returns verified states only; the outcome metric is this adapter's projection.
        var positions = Enumerable.Range(0, (int)Math.Min(s.SourceLast, s.ChildLast) + 1).Select(p => (long)p).ToList();
        var aligned = Ok(await engine.CompareAlignedAsync(source, child, positions, Ct), s, "compare");
        Assert.Equal(positions, aligned.Select(a => a.Position));
        var diverged = false;
        foreach (var pair in aligned)
        {
            Assert.Equal(sourceRef[(int)pair.Position].Hash, pair.Source.StateHash);
            Assert.Equal(childRef[(int)pair.Position].Hash, pair.Alternative.StateHash);
            Assert.Equal(HistoryStatus.Verified, pair.Source.HistoryStatus);
            Assert.Equal(HistoryStatus.Recording, pair.Alternative.HistoryStatus);
            var delta = s.Outcome(pair.Alternative.State) - s.Outcome(pair.Source.State);
            if (pair.Position <= s.ForkAt)
            {
                Assert.Equal(pair.Source.StateHash, pair.Alternative.StateHash);
                Assert.Equal(0L, delta);
            }
            diverged |= delta != 0;
        }
        Assert.True(diverged, $"{s.Name}: the changed child context should change the adapter outcome after T.");

        // Cancellation never yields Verified and changes nothing durable.
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        Assert.Equal(TimelineErrorCode.Cancelled, (await engine.CompleteAsync(child, s.ChildLast, cancelled.Token)).Error.Code);
        Assert.Equal(TimelineErrorCode.Cancelled,
            (await engine.AppendAsync(child, s.ChildLast, s.ChildSteps[0] with { Position = s.ChildLast + 1 }, Key(), cancelled.Token)).Error.Code);
        Assert.Equal(TimelineErrorCode.InvalidPosition, (await engine.RestoreAsync(child, s.ChildLast + 1, Ct)).Error.Code);
        Assert.Equal(HistoryStatus.Recording, Ok(await engine.RestoreAsync(child, s.ChildLast, Ct), s, "child status").HistoryStatus);

        Assert.Equal(rowsBefore, await RowsDigestAsync(db.Owner, source));
        Assert.Equal(manifestBefore, await ManifestAsync(source));
        return new RunIds(source, child, missing, rowsBefore, manifestBefore);
    }

    /// <summary>Host 2 after a server restart: nothing in memory, only durable pinned codecs/contexts.</summary>
    private async Task ReplayColdAsync<TState, TInput, TContext>(
        ITimelineEngine<TState, TInput, TContext> engine, ShapeScenario<TState, TInput, TContext> s, RunIds ids)
    {
        var sourceRef = s.SourceReference();
        var childRef = s.ChildReference();

        var sourceReplay = Ok(await engine.ReplayAsync(ids.Source, Ct), s, "cold source replay");
        Assert.Equal((s.SourceLast, sourceRef[^1].Hash, HistoryStatus.Verified),
            (sourceReplay.FinalPosition, sourceReplay.FinalStateHash, sourceReplay.Status));
        await AssertRestoresAsync(engine, ids.Source, sourceRef, s, "cold source");

        // Prefix 0..T under the source's pinned context, suffix under the child's own pinned context.
        await AssertRestoresAsync(engine, ids.Child, childRef, s, "cold child");
        var childCompleted = Ok(await engine.CompleteAsync(ids.Child, s.ChildLast, Ct), s, "cold child complete");
        Assert.Equal((HistoryStatus.Verified, childRef[^1].Hash), (childCompleted.Status, childCompleted.FinalStateHash));
        var childReplay = Ok(await engine.ReplayAsync(ids.Child, Ct), s, "cold child replay");
        Assert.Equal((s.ChildLast, childRef[^1].Hash, HistoryStatus.Verified),
            (childReplay.FinalPosition, childReplay.FinalStateHash, childReplay.Status));

        Assert.Equal(ids.SourceRows, await RowsDigestAsync(db.Owner, ids.Source));
        Assert.Equal(ids.SourceManifest, await ManifestAsync(ids.Source));
    }

    private static async Task AssertIncompatibleAsync<TState, TInput, TContext>(
        ITimelineEngine<TState, TInput, TContext> engine, RunIds ids)
    {
        foreach (var id in new[] { ids.Source, ids.Child })
        {
            Assert.Equal(TimelineErrorCode.IncompatibleVersion, (await engine.RestoreAsync(id, 0, Ct)).Error.Code);
            Assert.Equal(TimelineErrorCode.IncompatibleVersion, (await engine.ReplayAsync(id, Ct)).Error.Code);
        }
    }

    /// <summary>A missing context artifact and a forged child input fail closed without touching the source.</summary>
    private async Task CorruptAndMissingArtifactAsync<TState, TInput, TContext>(
        ITimelineEngine<TState, TInput, TContext> engine, ShapeScenario<TState, TInput, TContext> s, RunIds ids)
    {
        var missingRef = await ScalarAsync<string>(db.Owner,
            "SELECT context_ref FROM marrow_timeline.histories WHERE timeline_id = @p", ids.MissingArtifactChild.Value);
        await TamperAsync(db.Owner, "DELETE FROM marrow_timeline.context_artifacts WHERE context_ref = @ref", ("ref", missingRef));
        Assert.Equal(TimelineErrorCode.IncompatibleVersion, (await engine.RestoreAsync(ids.MissingArtifactChild, s.ForkAt + 1, Ct)).Error.Code);
        Assert.Equal(TimelineErrorCode.IncompatibleVersion, (await engine.ReplayAsync(ids.MissingArtifactChild, Ct)).Error.Code);

        var forkPlusOne = s.ForkAt + 1;
        await TamperAsync(db.Owner,
            "UPDATE marrow_timeline.step_inputs SET payload = @bytes WHERE timeline_id = @id AND position = @pos",
            ("bytes", s.Components.InputCodec.EncodeCanonical(s.ForgedInput)), ("id", ids.Child.Value), ("pos", forkPlusOne));
        Assert.Equal(s.ChildReference()[(int)s.ForkAt].Hash, Ok(await engine.RestoreAsync(ids.Child, s.ForkAt, Ct), s, "child prefix").StateHash);
        Assert.Equal(TimelineErrorCode.CorruptHistory, (await engine.RestoreAsync(ids.Child, forkPlusOne, Ct)).Error.Code);
        Assert.Equal(TimelineErrorCode.CorruptHistory, (await engine.ReplayAsync(ids.Child, Ct)).Error.Code);

        // Branch damage is isolated: the source still replays to its recorded hash with unchanged rows.
        Assert.Equal(s.SourceReference()[^1].Hash, Ok(await engine.ReplayAsync(ids.Source, Ct), s, "source after damage").FinalStateHash);
        Assert.Equal(ids.SourceRows, await RowsDigestAsync(db.Owner, ids.Source));
    }

    private static async Task AssertRestoresAsync<TState, TInput, TContext>(
        ITimelineEngine<TState, TInput, TContext> engine, TimelineId id, List<(byte[] Bytes, string Hash)> expected,
        ShapeScenario<TState, TInput, TContext> s, string phase)
    {
        for (var t = 0; t < expected.Count; t++)
        {
            var restored = Ok(await engine.RestoreAsync(id, t, Ct), s, $"{phase} restore {t}");
            Assert.Equal((id, (long)t, expected[t].Hash), (restored.Timeline, restored.Position, restored.StateHash));
            Assert.Equal(expected[t].Bytes, s.Components.StateCodec.EncodeCanonical(restored.State));
        }
        Assert.Equal(TimelineErrorCode.InvalidPosition, (await engine.RestoreAsync(id, expected.Count, Ct)).Error.Code);
        Assert.Equal(TimelineErrorCode.InvalidPosition, (await engine.RestoreAsync(id, -1, Ct)).Error.Code);
    }

    private static async Task AppendAllAsync<TState, TInput, TContext>(
        ITimelineEngine<TState, TInput, TContext> engine, TimelineId id, long after, IReadOnlyList<StepRecord<TInput>> steps,
        ShapeScenario<TState, TInput, TContext> s)
    {
        for (var i = 0; i < steps.Count; i++)
        {
            var receipt = Ok(await engine.AppendAsync(id, after + i, steps[i], Key(), Ct), s, $"append {after + i + 1}");
            Assert.Equal(after + i + 1, receipt.Position);
        }
    }

    private Task<string> ManifestAsync(TimelineId id) => ScalarAsync<string>(db.Owner,
        "SELECT row_to_json(h)::text FROM marrow_timeline.histories h WHERE timeline_id = @p", id.Value);

    private static T Ok<T, TState, TInput, TContext>(
        Result<T, TimelineError> result, ShapeScenario<TState, TInput, TContext> s, string step)
    {
        Assert.True(result.IsSuccess, $"{s.Name} {step}: {result}");
        return result.Value;
    }
}
