using System.Collections.Immutable;
using GhostLap.Rules;
using GhostLap.Simulation;
using Marrow.Kernel.Canonical;
using Marrow.Timeline;

namespace GhostLap.Timeline;

/// <summary>
/// Canonical codec of <see cref="GameState"/>, schema <see cref="GhostLapTimelineVersions.StateSchemaVersion"/>.
/// Big-endian fixed-width integers, no floating point. Decode rejects anything that is not the canonical form
/// the encoder would produce: out-of-range fields, unordered candle ids and trailing bytes.
/// This is the single Ghost Lap state codec: it carries the whole world, including Soulfire charge and Overlit deadline,
/// score components, the cumulative fixed-point distance (so the sub-point remainder survives a checkpoint), the
/// effective-input state (last sequence, held vector and its expiry, Burst latch), collected candles, pair cooldowns and
/// the tick's events. Schema stays <c>ghost-state/1</c> because no history was persisted before this layout (task 6.2);
/// any later layout change needs a new schema version. Spec 11 consumes this codec and never replaces it.
/// </summary>
public sealed class GameStateCodec : IStateCodec<GameState>
{
    public static GameStateCodec Instance { get; } = new();

    public byte[] EncodeCanonical(GameState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var w = new CanonicalWriter().WriteInt64(state.Tick).WriteCount(state.Players.Length);
        foreach (var p in state.Players)
        {
            if (p.HeldMoveX is < -1 or > 1 || p.HeldMoveY is < -1 or > 1)
            {
                throw new ArgumentOutOfRangeException(nameof(state), "Held move components must be -1, 0 or 1.");
            }
            w.WriteByte(p.Slot).WriteByte(p.MascotId).WriteInt32(p.XFixed).WriteInt32(p.YFixed).WriteInt64(p.DistanceFixed)
                .WriteByte(checked((byte)p.Charge)).WriteInt64(p.OverlitDeadlineTick)
                .WriteInt32(p.Pickups).WriteInt32(p.PenaltyPoints).WriteInt32(p.Bursts).WriteInt32(p.BurstHits)
                .WriteInt32(p.LastSequence).WriteByte((byte)(p.HeldMoveX + 1)).WriteByte((byte)(p.HeldMoveY + 1))
                .WriteInt64(p.HoldUntilTick).WriteBoolean(p.BurstLatch);
        }
        w.WriteCount(state.ActiveCandleIds.Length);
        foreach (var id in state.ActiveCandleIds)
        {
            w.WriteString(id);
        }
        w.WriteCount(state.Collected.Length);
        foreach (var c in state.Collected)
        {
            w.WriteString(c.Id).WriteByte(c.Slot).WriteInt64(c.Tick);
        }
        w.WriteCount(state.PairContactReadyTicks.Length);
        foreach (var ready in state.PairContactReadyTicks)
        {
            w.WriteInt64(ready);
        }
        w.WriteCount(state.Events.Length);
        foreach (var e in state.Events)
        {
            w.WriteByte((byte)e.Kind).WriteByte(e.Slot).WriteByte(e.OtherSlot).WriteInt32(e.Value).WriteString(e.CandleId);
        }
        return w.ToArray();
    }

    public GameState Decode(ReadOnlySpan<byte> bytes)
    {
        var r = new CanonicalReader(bytes);
        var tick = r.ReadInt64();
        if (tick is < 0 or > GameRulesV1.RoundTicks)
        {
            throw new CanonicalFormatException($"Tick {tick} is outside 0..{GameRulesV1.RoundTicks}.");
        }
        var playerCount = r.ReadCount();
        if (playerCount is < 1 or > GameRulesV1.MaxPlayers)
        {
            throw new CanonicalFormatException($"Player count {playerCount} is outside 1..{GameRulesV1.MaxPlayers}.");
        }
        var players = ImmutableArray.CreateBuilder<PlayerState>(playerCount);
        for (var i = 0; i < playerCount; i++)
        {
            players.Add(ReadPlayer(ref r, i, tick));
        }
        var ids = ReadAscendingIds(ref r, "Active candle ids");

        var collectedCount = r.ReadCount();
        if (collectedCount > r.Remaining / 13)
        {
            throw new CanonicalFormatException($"Collected count {collectedCount} exceeds the remaining bytes.");
        }
        var collected = ImmutableArray.CreateBuilder<CollectedCandle>(collectedCount);
        for (var i = 0; i < collectedCount; i++)
        {
            var id = r.ReadString();
            var slot = r.ReadByte();
            var at = r.ReadInt64();
            if ((i > 0 && string.CompareOrdinal(collected[i - 1].Id, id) >= 0) || slot >= playerCount || at < 1 || at > tick
                || ids.Contains(id))
            {
                throw new CanonicalFormatException("Collected candles must be ascending, collected by a slot at a past tick and not active.");
            }
            collected.Add(new CollectedCandle(id, slot, at));
        }
        var pickupsBySlot = new int[playerCount];
        foreach (var c in collected)
        {
            pickupsBySlot[c.Slot]++;
        }
        for (var i = 0; i < playerCount; i++)
        {
            if (players[i].Pickups != pickupsBySlot[i])
            {
                throw new CanonicalFormatException($"Player {i} pickup count does not match the collected candles.");
            }
        }

        var pairCount = r.ReadCount();
        if (pairCount != GameState.PairCount(playerCount))
        {
            throw new CanonicalFormatException($"Pair cooldown count {pairCount} does not match {playerCount} players.");
        }
        var pairs = ImmutableArray.CreateBuilder<long>(pairCount);
        for (var i = 0; i < pairCount; i++)
        {
            var ready = r.ReadInt64();
            if (ready != 0 && (ready <= tick || ready > tick + GameRulesV1.ContactCooldownTicks))
            {
                throw new CanonicalFormatException($"Pair cooldown {ready} is outside ({tick}, {tick + GameRulesV1.ContactCooldownTicks}].");
            }
            pairs.Add(ready);
        }

        var eventCount = r.ReadCount();
        if (eventCount > r.Remaining / 11)
        {
            throw new CanonicalFormatException($"Event count {eventCount} exceeds the remaining bytes.");
        }
        var events = ImmutableArray.CreateBuilder<GameEvent>(eventCount);
        for (var i = 0; i < eventCount; i++)
        {
            var kind = (GameEventKind)r.ReadByte();
            var slot = r.ReadByte();
            var other = r.ReadByte();
            var value = r.ReadInt32();
            var candle = r.ReadString();
            if (!Enum.IsDefined(kind) || slot >= playerCount || other >= playerCount
                || (candle.Length != 0) != (kind == GameEventKind.CandleCollected))
            {
                throw new CanonicalFormatException($"Event {i} has out-of-range fields.");
            }
            events.Add(new GameEvent(kind, slot, other, value, candle));
        }
        r.EnsureEnd();
        return new GameState(tick, players.MoveToImmutable(), ids)
        {
            Collected = collected.MoveToImmutable(),
            PairContactReadyTicks = pairs.MoveToImmutable(),
            Events = events.MoveToImmutable(),
        };
    }

    private static PlayerState ReadPlayer(ref CanonicalReader r, int index, long tick)
    {
        var slot = r.ReadByte();
        var mascot = r.ReadByte();
        var x = r.ReadInt32();
        var y = r.ReadInt32();
        var distance = r.ReadInt64();
        var charge = r.ReadByte();
        var deadline = r.ReadInt64();
        var pickups = r.ReadInt32();
        var penalty = r.ReadInt32();
        var bursts = r.ReadInt32();
        var hits = r.ReadInt32();
        var sequence = r.ReadInt32();
        var hx = r.ReadByte();
        var hy = r.ReadByte();
        var holdUntil = r.ReadInt64();
        var latch = r.ReadBoolean();
        var held = hx != 1 || hy != 1;
        // A tick moves at most one cardinal step (+1 fixed unit of rounding slack), which also bounds the arithmetic below.
        if (slot != index || mascot >= GameRulesV1.MascotCount || distance < 0 || distance > tick * (GameRulesV1.CardinalStepFixed + 1L)
            || x < 0 || x > GameRulesV1.WorldWidthFixed || y < 0 || y > GameRulesV1.WorldHeightFixed
            || charge > GameRulesV1.MaxCharge
            || (charge == GameRulesV1.MaxCharge) != (deadline != 0)
            || (deadline != 0 && (deadline <= tick || deadline > tick + GameRulesV1.OverlitTicks))
            || pickups < 0 || penalty < 0 || bursts < 0 || hits < 0 || sequence < -1
            || hx > 2 || hy > 2
            || held != (holdUntil != 0)
            || (holdUntil != 0 && (holdUntil < tick || holdUntil > tick + GameRulesV1.InputHoldTicks)))
        {
            throw new CanonicalFormatException($"Player {index} has out-of-range fields.");
        }
        if (distance / GameRulesV1.MovementScoreUnitFixed + (long)pickups * GameRulesV1.CandlePickupPoints - penalty < 0)
        {
            throw new CanonicalFormatException($"Player {index} penalty exceeds the earned score.");
        }
        return new PlayerState(slot, mascot, x, y, distance)
        {
            Charge = charge,
            OverlitDeadlineTick = deadline,
            Pickups = pickups,
            PenaltyPoints = penalty,
            Bursts = bursts,
            BurstHits = hits,
            LastSequence = sequence,
            HeldMoveX = (sbyte)(hx - 1),
            HeldMoveY = (sbyte)(hy - 1),
            HoldUntilTick = holdUntil,
            BurstLatch = latch,
        };
    }

    private static ImmutableArray<string> ReadAscendingIds(ref CanonicalReader r, string what)
    {
        var count = r.ReadCount();
        if (count > r.Remaining / 4)
        {
            throw new CanonicalFormatException($"{what} count {count} exceeds the remaining bytes.");
        }
        var ids = ImmutableArray.CreateBuilder<string>(count);
        for (var i = 0; i < count; i++)
        {
            var id = r.ReadString();
            if (i > 0 && string.CompareOrdinal(ids[i - 1], id) >= 0)
            {
                throw new CanonicalFormatException($"{what} must be strictly ascending.");
            }
            ids.Add(id);
        }
        return ids.MoveToImmutable();
    }
}

/// <summary>Canonical codec of <see cref="GameInput"/>, schema <see cref="GhostLapTimelineVersions.InputSchemaVersion"/>.</summary>
public sealed class GameInputCodec : IInputCodec<GameInput>
{
    public static GameInputCodec Instance { get; } = new();

    public byte[] EncodeCanonical(GameInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.MoveX is < -1 or > 1 || input.MoveY is < -1 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(input), "Move components must be -1, 0 or 1.");
        }
        // Move components are stored offset by one (0..2) so the wire form is unsigned bytes.
        return new CanonicalWriter()
            .WriteInt64(input.Tick).WriteByte(input.Slot).WriteInt32(input.Sequence)
            .WriteByte((byte)(input.MoveX + 1)).WriteByte((byte)(input.MoveY + 1)).WriteBoolean(input.RequestBurst)
            .ToArray();
    }

    public GameInput Decode(ReadOnlySpan<byte> bytes)
    {
        var r = new CanonicalReader(bytes);
        var tick = r.ReadInt64();
        var slot = r.ReadByte();
        var sequence = r.ReadInt32();
        var mx = r.ReadByte();
        var my = r.ReadByte();
        var burst = r.ReadBoolean();
        r.EnsureEnd();
        if (tick < 1 || tick > GameRulesV1.RoundTicks || slot >= GameRulesV1.MaxPlayers || sequence < 0 || mx > 2 || my > 2)
        {
            throw new CanonicalFormatException("Game input has out-of-range fields.");
        }
        return new GameInput(tick, slot, sequence, (sbyte)(mx - 1), (sbyte)(my - 1), burst);
    }
}

/// <summary>
/// Canonical codec of <see cref="GameContext"/>, schema <see cref="GhostLapTimelineVersions.ContextSchemaVersion"/>.
/// The rules and layout versions are stored with the seed so a stored context names what it was created under;
/// a version this build does not implement fails to decode and the engine reports an incompatible version.
/// </summary>
public sealed class GameContextCodec : IContextCodec<GameContext>
{
    public static GameContextCodec Instance { get; } = new();

    public byte[] EncodeCanonical(GameContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var w = new CanonicalWriter()
            .WriteString(context.RulesVersion).WriteString(context.LayoutVersion)
            .WriteInt64(context.Seed).WriteCount(context.PlayerCount);
        foreach (var mascot in context.Mascots)
        {
            w.WriteByte(mascot);
        }
        return w.ToArray();
    }

    public GameContext Decode(ReadOnlySpan<byte> bytes)
    {
        var r = new CanonicalReader(bytes);
        var rules = r.ReadString();
        var layout = r.ReadString();
        if (rules != GameRulesV1.RulesVersion || layout != GameRulesV1.LayoutVersion)
        {
            throw new CanonicalFormatException($"Unsupported rules/layout '{rules}'/'{layout}'.");
        }
        var seed = r.ReadInt64();
        var count = r.ReadCount();
        if (count is < 1 or > GameRulesV1.MaxPlayers)
        {
            throw new CanonicalFormatException($"Player count {count} is outside 1..{GameRulesV1.MaxPlayers}.");
        }
        var mascots = new byte[count];
        for (var i = 0; i < count; i++)
        {
            mascots[i] = r.ReadByte();
        }
        r.EnsureEnd();
        try
        {
            return GameContext.Create(seed, count, mascots);
        }
        catch (ArgumentException ex)
        {
            throw new CanonicalFormatException("Game context has invalid mascots.", ex);
        }
    }
}
