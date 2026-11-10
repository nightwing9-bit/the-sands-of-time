using System.Collections.Immutable;
using Marrow.Kernel.Canonical;
using Marrow.Timeline;

namespace Marrow.Tests.TestDomains;

// Test adapter A: integer tick world. Every position is one tick; ticks with zero inputs still
// advance the timer and cooldown. Not a game rule engine; it only exercises tick-shaped histories.

public enum TickAction : byte { Collect = 1, Burst = 2 }

public sealed record TickInput(TickAction Action, string ItemId);

public sealed record TickContext(int CooldownTicks);

public sealed record TickWorld(long Tick, int Timer, int Cooldown, int Bursts, ImmutableSortedSet<string> Collected)
{
    public static TickWorld Initial(int timer) =>
        new(0, timer, 0, 0, ImmutableSortedSet.Create<string>(StringComparer.Ordinal));
}

public sealed class TickReducer : IDeterministicTransition<TickWorld, TickInput, TickContext>
{
    public TickWorld Apply(TickWorld previous, long position, IReadOnlyList<OrderedInput<TickInput>> inputs, TickContext pinnedContext)
    {
        var timer = Math.Max(0, previous.Timer - 1);
        var cooldown = Math.Max(0, previous.Cooldown - 1);
        var bursts = previous.Bursts;
        var collected = previous.Collected;
        foreach (var input in inputs)
        {
            switch (input.Payload.Action)
            {
                case TickAction.Collect:
                    collected = collected.Add(input.Payload.ItemId);
                    break;
                case TickAction.Burst when cooldown == 0:
                    bursts++;
                    cooldown = pinnedContext.CooldownTicks;
                    break;
            }
        }

        return new TickWorld(position, timer, cooldown, bursts, collected);
    }
}

public sealed class TickWorldCodec : IStateCodec<TickWorld>
{
    public byte[] EncodeCanonical(TickWorld state)
    {
        var w = new CanonicalWriter()
            .WriteInt64(state.Tick).WriteInt32(state.Timer).WriteInt32(state.Cooldown).WriteInt32(state.Bursts)
            .WriteCount(state.Collected.Count);
        foreach (var id in state.Collected.Order(StringComparer.Ordinal))
        {
            w.WriteString(id);
        }

        return w.ToArray();
    }

    public TickWorld Decode(ReadOnlySpan<byte> bytes)
    {
        var r = new CanonicalReader(bytes);
        var tick = r.ReadInt64();
        var timer = r.ReadInt32();
        var cooldown = r.ReadInt32();
        var bursts = r.ReadInt32();
        var count = r.ReadCount();
        var ids = ImmutableSortedSet.CreateBuilder<string>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            ids.Add(r.ReadString());
        }

        r.EnsureEnd();
        return new TickWorld(tick, timer, cooldown, bursts, ids.ToImmutable());
    }
}

public sealed class TickInputCodec : IInputCodec<TickInput>
{
    public byte[] EncodeCanonical(TickInput input) =>
        new CanonicalWriter().WriteByte((byte)input.Action).WriteString(input.ItemId).ToArray();

    public TickInput Decode(ReadOnlySpan<byte> bytes)
    {
        var r = new CanonicalReader(bytes);
        var action = r.ReadByte();
        if (action is not ((byte)TickAction.Collect or (byte)TickAction.Burst))
        {
            throw new CanonicalFormatException($"Unknown tick action {action}.");
        }

        var input = new TickInput((TickAction)action, r.ReadString());
        r.EnsureEnd();
        return input;
    }
}

public sealed class TickContextCodec : IContextCodec<TickContext>
{
    public byte[] EncodeCanonical(TickContext context) => new CanonicalWriter().WriteInt32(context.CooldownTicks).ToArray();

    public TickContext Decode(ReadOnlySpan<byte> bytes)
    {
        var r = new CanonicalReader(bytes);
        var context = new TickContext(r.ReadInt32());
        r.EnsureEnd();
        return context;
    }
}
