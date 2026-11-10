using Marrow.Kernel.Canonical;
using Marrow.Timeline;

namespace Marrow.Tests.TestDomains;

// Test adapter B: event-driven synthetic counter. Positions are event batches; each event carries a
// nonuniform business timestamp that is payload only. The fold is order-sensitive
// (value * multiplier + amount), so it detects any deviation from (Ordinal, InputId) ordering.
// Not a finance rule engine.

public sealed record CounterEvent(long BusinessTimeTicks, int Amount);

public sealed record CounterContext(int Multiplier);

public sealed record CounterState(long Value, long EventsApplied, long LastBusinessTimeTicks)
{
    public static CounterState Initial => new(0, 0, 0);
}

public sealed class CounterReducer : IDeterministicTransition<CounterState, CounterEvent, CounterContext>
{
    public CounterState Apply(CounterState previous, long position, IReadOnlyList<OrderedInput<CounterEvent>> inputs, CounterContext pinnedContext)
    {
        var state = previous;
        foreach (var input in inputs)
        {
            state = new CounterState(
                checked(state.Value * pinnedContext.Multiplier + input.Payload.Amount),
                state.EventsApplied + 1,
                input.Payload.BusinessTimeTicks);
        }

        return state;
    }
}

public sealed class CounterStateCodec : IStateCodec<CounterState>
{
    public byte[] EncodeCanonical(CounterState state) =>
        new CanonicalWriter().WriteInt64(state.Value).WriteInt64(state.EventsApplied).WriteInt64(state.LastBusinessTimeTicks).ToArray();

    public CounterState Decode(ReadOnlySpan<byte> bytes)
    {
        var r = new CanonicalReader(bytes);
        var state = new CounterState(r.ReadInt64(), r.ReadInt64(), r.ReadInt64());
        r.EnsureEnd();
        return state;
    }
}

public sealed class CounterEventCodec : IInputCodec<CounterEvent>
{
    public byte[] EncodeCanonical(CounterEvent input) =>
        new CanonicalWriter().WriteInt64(input.BusinessTimeTicks).WriteInt32(input.Amount).ToArray();

    public CounterEvent Decode(ReadOnlySpan<byte> bytes)
    {
        var r = new CanonicalReader(bytes);
        var input = new CounterEvent(r.ReadInt64(), r.ReadInt32());
        r.EnsureEnd();
        return input;
    }
}

public sealed class CounterContextCodec : IContextCodec<CounterContext>
{
    public byte[] EncodeCanonical(CounterContext context) => new CanonicalWriter().WriteInt32(context.Multiplier).ToArray();

    public CounterContext Decode(ReadOnlySpan<byte> bytes)
    {
        var r = new CanonicalReader(bytes);
        var context = new CounterContext(r.ReadInt32());
        r.EnsureEnd();
        return context;
    }
}
