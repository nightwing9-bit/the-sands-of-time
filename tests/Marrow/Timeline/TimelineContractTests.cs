using Marrow.Kernel.Results;
using Marrow.Tests.TestDomains;
using Marrow.Timeline;
using Marrow.Timeline.Internal;
using Xunit;

namespace Marrow.Tests.Timeline;

/// <summary>
/// Task 2.1 contract-surface proof: both reducer shapes compile and run against the one set of
/// generic Timeline interfaces. No engine/store exists yet (tasks 2.2-2.4), so the lifecycle script is
/// a compile-time proof only; these tests do not claim persistence, restore or fork behavior.
/// </summary>
public sealed class TimelineContractTests
{
    private static readonly TimelineVersionKey TickV1 = new("test-tick", "tick-reducer-1", "tick-input-1", "tick-state-1", "tick-context-1");
    private static readonly TimelineVersionKey CounterV1 = new("test-counter", "counter-reducer-1", "counter-input-1", "counter-state-1", "counter-context-1");

    private static TimelineComponents<TickWorld, TickInput, TickContext> TickComponents() =>
        new(TickV1, new TickReducer(), new TickWorldCodec(), new TickInputCodec(), new TickContextCodec());

    private static TimelineComponents<CounterState, CounterEvent, CounterContext> CounterComponents() =>
        new(CounterV1, new CounterReducer(), new CounterStateCodec(), new CounterEventCodec(), new CounterContextCodec());

    /// <summary>
    /// The full v1 lifecycle written once against the generic engine interface. The child id returned by
    /// Fork is used with the same Append/Restore/Replay calls as the source id.
    /// </summary>
    private static async Task<TimelineId?> LifecycleScript<TState, TInput, TContext>(
        ITimelineEngine<TState, TInput, TContext> engine,
        CreateSourceRequest<TState, TContext> request,
        StepRecord<TInput> firstStep,
        TContext branchContext,
        StepRecord<TInput> childStep,
        CancellationToken ct)
    {
        if (!(await engine.CreateSourceAsync(request, "create-1", ct)).TryGetValue(out var source)) return null;
        if ((await engine.AppendAsync(source, 0, firstStep, "append-1", ct)).IsFailure) return null;
        if ((await engine.CompleteAsync(source, 1, ct)).IsFailure) return null;
        if ((await engine.RestoreAsync(source, 0, ct)).IsFailure) return null;
        if (!(await engine.ForkAsync(source, 1, "fork-1", branchContext, ct)).TryGetValue(out var child)) return null;
        if ((await engine.AppendAsync(child, 1, childStep, "child-append-2", ct)).IsFailure) return null;
        if ((await engine.RestoreAsync(child, 2, ct)).IsFailure) return null;
        if ((await engine.ReplayAsync(child, ct)).IsFailure) return null;
        if ((await engine.ReplayAsync(source, ct)).IsFailure) return null;
        if ((await engine.CompareAlignedAsync(source, child, [0, 1], ct)).IsFailure) return null;
        return child;
    }

    private delegate Task<TimelineId?> Lifecycle<TState, TInput, TContext>(
        ITimelineEngine<TState, TInput, TContext> engine, CreateSourceRequest<TState, TContext> request,
        StepRecord<TInput> firstStep, TContext branchContext, StepRecord<TInput> childStep, CancellationToken ct);

    [Fact]
    public void Lifecycle_script_binds_to_tick_and_event_shapes_of_one_engine_interface()
    {
        // Compile-time proof: these assignments only compile if both shapes satisfy the same generic contract.
        Lifecycle<TickWorld, TickInput, TickContext> tick = LifecycleScript;
        Lifecycle<CounterState, CounterEvent, CounterContext> counter = LifecycleScript;

        Assert.NotNull(tick);
        Assert.NotNull(counter);
    }

    /// <summary>Generic fold through the version resolver and canonical step rules; shared by both shapes.</summary>
    private static Result<byte[], TimelineError> FoldToCanonicalState<TState, TInput, TContext>(
        ITimelineVersionResolver<TState, TInput, TContext> resolver,
        TimelineVersionKey versions,
        TState initial,
        TContext context,
        IEnumerable<StepRecord<TInput>> steps)
    {
        if (!resolver.Resolve(versions).TryGetValue(out var components))
        {
            return Result<byte[], TimelineError>.Failure(resolver.Resolve(versions).Error);
        }

        var state = initial;
        long last = 0;
        foreach (var step in steps)
        {
            var ordered = TimelineRules.CanonicalizeNextStep(last, step);
            if (!ordered.TryGetValue(out var inputs))
            {
                return Result<byte[], TimelineError>.Failure(ordered.Error);
            }

            state = components.Transition.Apply(state, step.Position, inputs, context);
            last = step.Position;
        }

        return TimelineResult.Ok(components.StateCodec.EncodeCanonical(state));
    }

    [Fact]
    public void Tick_shape_runs_through_shared_fold_including_empty_ticks()
    {
        var resolver = new TimelineVersionRegistry<TickWorld, TickInput, TickContext>([TickComponents()]);
        StepRecord<TickInput>[] steps =
        [
            new(1, [new("i-1", 0, new TickInput(TickAction.Burst, ""))]),
            new(2, []),
            new(3, [new("i-2", 0, new TickInput(TickAction.Collect, "candle-b")), new("i-3", 1, new TickInput(TickAction.Collect, "candle-a"))]),
            new(4, []),
        ];

        var bytes = FoldToCanonicalState(resolver, TickV1, TickWorld.Initial(timer: 10), new TickContext(CooldownTicks: 2), steps);

        Assert.True(bytes.IsSuccess);
        var world = new TickWorldCodec().Decode(bytes.Value);
        Assert.Equal(4, world.Tick);
        Assert.Equal(6, world.Timer);           // empty ticks still advance the timer
        Assert.Equal(0, world.Cooldown);         // cooldown 2 set at tick 1, expired by tick 3
        Assert.Equal(1, world.Bursts);
        Assert.Equal(new[] { "candle-a", "candle-b" }, world.Collected);

        var again = FoldToCanonicalState(resolver, TickV1, TickWorld.Initial(timer: 10), new TickContext(CooldownTicks: 2), steps);
        Assert.Equal(bytes.Value, again.Value);  // deterministic canonical bytes
    }

    [Fact]
    public void Event_shape_orders_by_ordinal_then_input_id_not_business_time()
    {
        var resolver = new TimelineVersionRegistry<CounterState, CounterEvent, CounterContext>([CounterComponents()]);
        // Same business time, different ordinals, supplied out of order; later ordinal has an earlier timestamp.
        StepRecord<CounterEvent>[] steps =
        [
            new(1, [
                new("e-b", 1, new CounterEvent(BusinessTimeTicks: 100, Amount: 3)),
                new("e-a", 0, new CounterEvent(BusinessTimeTicks: 100, Amount: 2)),
            ]),
            new(2, [
                new("e-d", 0, new CounterEvent(BusinessTimeTicks: 9_000, Amount: 5)),
                new("e-c", 0, new CounterEvent(BusinessTimeTicks: 50, Amount: 7)), // equal ordinal: InputId tie-break puts e-c first
            ]),
        ];

        var bytes = FoldToCanonicalState(resolver, CounterV1, CounterState.Initial, new CounterContext(Multiplier: 10), steps);

        Assert.True(bytes.IsSuccess);
        var state = new CounterStateCodec().Decode(bytes.Value);
        // ((0*10+2)*10+3) = 23; then (23*10+7)=237; then 237*10+5 = 2375
        Assert.Equal(2375, state.Value);
        Assert.Equal(4, state.EventsApplied);
        Assert.Equal(9_000, state.LastBusinessTimeTicks);
    }

    [Fact]
    public void Missing_historical_version_fails_closed_with_IncompatibleVersion()
    {
        var resolver = new TimelineVersionRegistry<TickWorld, TickInput, TickContext>([TickComponents()]);
        var unknownReducer = TickV1 with { ReducerVersion = "tick-reducer-2" };

        var result = FoldToCanonicalState(resolver, unknownReducer, TickWorld.Initial(1), new TickContext(1), Array.Empty<StepRecord<TickInput>>());

        Assert.True(result.IsFailure);
        Assert.Equal(TimelineErrorCode.IncompatibleVersion, result.Error.Code);
    }

    [Fact]
    public void Version_keys_match_exactly_and_duplicates_are_rejected()
    {
        var resolver = new TimelineVersionRegistry<CounterState, CounterEvent, CounterContext>([CounterComponents()]);
        Assert.True(resolver.Resolve(CounterV1 with { }).IsSuccess);
        Assert.Equal(TimelineErrorCode.IncompatibleVersion,
            resolver.Resolve(CounterV1 with { StateSchemaVersion = "COUNTER-STATE-1" }).Error.Code);

        Assert.Throws<ArgumentException>(() =>
            new TimelineVersionRegistry<CounterState, CounterEvent, CounterContext>([CounterComponents(), CounterComponents()]));
        Assert.Throws<ArgumentException>(() => new TimelineVersionKey("d", " ", "i", "s", "c"));
    }

    [Fact]
    public void Manifest_exposes_its_pinned_version_key_and_source_and_child_share_TimelineId()
    {
        var sourceId = new TimelineId(Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var childId = new TimelineId(Guid.Parse("00000000-0000-0000-0000-000000000002"));
        var source = Manifest(sourceId, parent: null, forkPosition: null, HistoryStatus.Verified);
        var child = Manifest(childId, parent: sourceId, forkPosition: 1, HistoryStatus.Recording);

        Assert.Equal(TickV1, source.Versions);
        Assert.Equal(sourceId, child.ParentId);
        Assert.IsType<TimelineId>(child.Id);
        Assert.IsType<TimelineId>(source.Id);
    }

    [Fact]
    public void Result_carries_exactly_one_of_value_or_typed_error()
    {
        var ok = TimelineResult.Ok(new TimelineId(Guid.Empty));
        var fail = TimelineResult.Fail<TimelineId>(TimelineErrorCode.NotFound, "no such history");

        Assert.True(ok.TryGetValue(out _));
        Assert.False(ok.TryGetError(out _));
        Assert.Throws<InvalidOperationException>(() => ok.Error);

        Assert.True(fail.TryGetError(out var error));
        Assert.Equal(TimelineErrorCode.NotFound, error.Code);
        Assert.Throws<InvalidOperationException>(() => fail.Value);
        Assert.Equal("NotFound", fail.Match(_ => "ok", e => e.Code.ToString()));
        Assert.Throws<ArgumentNullException>(() => Result<int, TimelineError>.Failure(null!));
    }

    [Fact]
    public void Request_dtos_reject_structurally_invalid_values()
    {
        Assert.Throws<ArgumentException>(() => new OrderedInput<int>(" ", 0, 1));
        Assert.Throws<ArgumentNullException>(() => new StepRecord<int>(1, null!));
        Assert.Throws<ArgumentException>(() =>
            new CreateSourceRequest<TickWorld, TickContext>("", TickV1, TickWorld.Initial(1), new TickContext(1), "r", "s"));
    }

    [Theory]
    [InlineData("0", 0L)]
    [InlineData("2700", 2700L)]
    [InlineData("9223372036854775807", long.MaxValue)]
    public void Position_wire_form_round_trips_canonical_decimals(string wire, long expected)
    {
        Assert.True(TimelinePosition.TryParseWire(wire, out var position));
        Assert.Equal(expected, position.Value);
        Assert.Equal(wire, position.ToWireString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData("01")]
    [InlineData("1.5")]
    [InlineData(" 1")]
    [InlineData("9223372036854775808")]
    public void Position_wire_form_rejects_noncanonical_text(string? wire)
    {
        Assert.False(TimelinePosition.TryParseWire(wire, out _));
    }

    [Fact]
    public void Position_rejects_negative_values()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TimelinePosition(-1));
        Assert.Equal(0, TimelinePosition.Initial.Value);
    }

    internal static HistoryManifest Manifest(TimelineId id, TimelineId? parent, long? forkPosition, HistoryStatus status, long lastPosition = 3) =>
        new(id, TickV1.DomainId, "owner-1", TickV1.InputSchemaVersion, TickV1.StateSchemaVersion, TickV1.ContextSchemaVersion,
            "ctx-hash", "ctx-ref", TickV1.ReducerVersion, "rules-hash", "seed-hash",
            lastPosition, "state-hash", "record-hash", parent, forkPosition, status);
}
