using Marrow.Kernel.Canonical;
using Marrow.Tests.TestDomains;
using Marrow.Timeline;
using Marrow.Timeline.Internal;
using Npgsql;

namespace Marrow.Tests.Timeline.Engine;

/// <summary>Version keys, component sets, an independent reference fold and SQL helpers for engine tests.</summary>
internal static class EngineTestKit
{
    public const string Owner = "owner-engine";

    // v7 is the historical key a source pins; v8 is a later "current" key with a different reducer.
    public static readonly TimelineVersionKey TickV7 = new("test-tick", "tick-reducer-7", "tick-input-1", "tick-state-1", "tick-context-7");
    public static readonly TimelineVersionKey TickV8 = TickV7 with { ReducerVersion = "tick-reducer-8", ContextSchemaVersion = "tick-context-8" };
    public static readonly TimelineVersionKey CounterV1 = new("test-counter", "counter-reducer-1", "counter-input-1", "counter-state-1", "counter-context-1");

    public static TimelineComponents<TickWorld, TickInput, TickContext> Tick(
        TimelineVersionKey? key = null,
        IDeterministicTransition<TickWorld, TickInput, TickContext>? reducer = null,
        IStateCodec<TickWorld>? stateCodec = null,
        IInputCodec<TickInput>? inputCodec = null,
        IContextCodec<TickContext>? contextCodec = null) =>
        new(key ?? TickV7, reducer ?? new TickReducer(), stateCodec ?? new TickWorldCodec(),
            inputCodec ?? new TickInputCodec(), contextCodec ?? new TickContextCodec());

    public static TimelineComponents<TickWorld, TickInput, TickContext> TickV8Components() =>
        Tick(TickV8, new TickReducerV8());

    public static TimelineComponents<CounterState, CounterEvent, CounterContext> Counter() =>
        new(CounterV1, new CounterReducer(), new CounterStateCodec(), new CounterEventCodec(), new CounterContextCodec());

    public static PersistentTimelineEngine<TState, TInput, TContext> NewEngine<TState, TInput, TContext>(
        ITimelineStore store, int checkpointInterval, params TimelineComponents<TState, TInput, TContext>[] components) =>
        new(store, new TimelineVersionRegistry<TState, TInput, TContext>(components),
            new TimelineEngineOptions { CheckpointInterval = checkpointInterval });

    public static string Key() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// Reference fold that does not use the engine: applies the reducer to inputs sorted by (Ordinal, InputId)
    /// and returns (canonical state bytes, state hash) for positions start..start+steps.Count.
    /// </summary>
    public static List<(byte[] Bytes, string Hash)> ReferenceFold<TState, TInput, TContext>(
        TimelineComponents<TState, TInput, TContext> c, TState start, TContext context, IEnumerable<StepRecord<TInput>> steps)
    {
        var state = start;
        var result = new List<(byte[], string)> { Snapshot(c, state) };
        foreach (var step in steps)
        {
            var ordered = step.Inputs.OrderBy(i => i.Ordinal).ThenBy(i => i.InputId, StringComparer.Ordinal).ToList();
            state = c.Transition.Apply(state, step.Position, ordered, context);
            result.Add(Snapshot(c, state));
        }

        return result;
    }

    public static (byte[] Bytes, string Hash) Snapshot<TState, TInput, TContext>(TimelineComponents<TState, TInput, TContext> c, TState state)
    {
        var bytes = c.StateCodec.EncodeCanonical(state);
        return (bytes, TimelineHashing.StateHash(c.Versions.StateSchemaVersion, bytes));
    }

    /// <summary>Storage-level tampering as the superuser with triggers and FK enforcement disabled for the session.</summary>
    public static async Task TamperAsync(NpgsqlDataSource owner, string sql, params (string Name, object Value)[] parameters)
    {
        await using var cmd = owner.CreateCommand($"SET session_replication_role = replica; {sql}; SET session_replication_role = origin;");
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.Add(new NpgsqlParameter(name, value));
        }

        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task<T> ScalarAsync<T>(NpgsqlDataSource owner, string sql, Guid id)
    {
        await using var cmd = owner.CreateCommand(sql);
        cmd.Parameters.Add(new NpgsqlParameter("p", id));
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    public static StepRecord<TickInput> TickStep(long position, params (string Id, int Ordinal, TickAction Action, string Item)[] inputs) =>
        new(position, inputs.Select(i => new OrderedInput<TickInput>(i.Id, i.Ordinal, new TickInput(i.Action, i.Item))).ToList());

    /// <summary>Ticks 1..count; every odd tick bursts and every third tick collects an item.</summary>
    public static List<StepRecord<TickInput>> BurstyTicks(long firstPosition, int count, string idPrefix)
    {
        var steps = new List<StepRecord<TickInput>>();
        for (var p = firstPosition; p < firstPosition + count; p++)
        {
            var inputs = new List<(string, int, TickAction, string)>();
            if (p % 2 == 1)
            {
                inputs.Add(($"{idPrefix}-b{p}", 0, TickAction.Burst, ""));
            }

            if (p % 3 == 0)
            {
                inputs.Add(($"{idPrefix}-c{p}", 1, TickAction.Collect, $"item-{p % 4}"));
            }

            steps.Add(TickStep(p, [.. inputs]));
        }

        return steps;
    }
}

/// <summary>A later reducer version: the timer drains two per tick. Used to prove historical reducers are selected by pin.</summary>
internal sealed class TickReducerV8 : IDeterministicTransition<TickWorld, TickInput, TickContext>
{
    private readonly TickReducer _inner = new();

    public TickWorld Apply(TickWorld previous, long position, IReadOnlyList<OrderedInput<TickInput>> inputs, TickContext pinnedContext)
    {
        var next = _inner.Apply(previous, position, inputs, pinnedContext);
        return next with { Timer = Math.Max(0, next.Timer - 1) };
    }
}

/// <summary>Same behavior as <see cref="TickReducer"/> but cancels a token when it reaches a given position.</summary>
internal sealed class CancellingTickReducer(CancellationTokenSource cts, long cancelAtPosition)
    : IDeterministicTransition<TickWorld, TickInput, TickContext>
{
    private readonly TickReducer _inner = new();

    public int Calls { get; private set; }

    public TickWorld Apply(TickWorld previous, long position, IReadOnlyList<OrderedInput<TickInput>> inputs, TickContext pinnedContext)
    {
        Calls++;
        if (position == cancelAtPosition)
        {
            cts.Cancel();
        }

        return _inner.Apply(previous, position, inputs, pinnedContext);
    }
}

/// <summary>Deployed under the old key but changed: expects a version tag byte that v1 bytes never start with.</summary>
internal sealed class TaggedTickInputCodec : IInputCodec<TickInput>
{
    private readonly TickInputCodec _inner = new();

    public byte[] EncodeCanonical(TickInput input) => [0x7F, .. _inner.EncodeCanonical(input)];

    public TickInput Decode(ReadOnlySpan<byte> bytes) =>
        bytes.Length > 0 && bytes[0] == 0x7F ? _inner.Decode(bytes[1..]) : throw new CanonicalFormatException("Missing v2 tag.");
}

/// <summary>Deployed under the old key but changed to a 64-bit cooldown; cannot read 32-bit historical context bytes.</summary>
internal sealed class WideTickContextCodec : IContextCodec<TickContext>
{
    public byte[] EncodeCanonical(TickContext context) => new CanonicalWriter().WriteInt64(context.CooldownTicks).ToArray();

    public TickContext Decode(ReadOnlySpan<byte> bytes)
    {
        var r = new CanonicalReader(bytes);
        var context = new TickContext(checked((int)r.ReadInt64()));
        r.EnsureEnd();
        return context;
    }
}

/// <summary>Deployed under the old key but lossy: drops the collected set, so stored states do not round-trip.</summary>
internal sealed class LossyTickWorldCodec : IStateCodec<TickWorld>
{
    private readonly TickWorldCodec _inner = new();

    public byte[] EncodeCanonical(TickWorld state) => _inner.EncodeCanonical(state with { Collected = state.Collected.Clear() });

    public TickWorld Decode(ReadOnlySpan<byte> bytes) => _inner.Decode(bytes);
}
