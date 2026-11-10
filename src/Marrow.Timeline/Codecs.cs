namespace Marrow.Timeline;

/// <summary>
/// Pure deterministic reducer. Must not read clocks, randomness, network, database or ambient settings;
/// everything it needs is in the previous state, the canonically ordered inputs and the pinned context.
/// </summary>
public interface IDeterministicTransition<TState, TInput, TContext>
{
    TState Apply(TState previous, long position, IReadOnlyList<OrderedInput<TInput>> inputs, TContext pinnedContext);
}

/// <summary>Canonical complete-state codec. Equal states encode to identical bytes. Decode throws on malformed bytes.</summary>
public interface IStateCodec<TState>
{
    byte[] EncodeCanonical(TState state);

    TState Decode(ReadOnlySpan<byte> bytes);
}

/// <summary>Canonical input-payload codec, pinned separately from the state codec.</summary>
public interface IInputCodec<TInput>
{
    byte[] EncodeCanonical(TInput input);

    TInput Decode(ReadOnlySpan<byte> bytes);
}

/// <summary>Canonical pinned-context codec. Context bytes are stored durably so cold restore never uses current defaults.</summary>
public interface IContextCodec<TContext>
{
    byte[] EncodeCanonical(TContext context);

    TContext Decode(ReadOnlySpan<byte> bytes);
}
