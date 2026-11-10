using System.Collections.Immutable;
using GhostLap.Rules;
using Marrow.Timeline;

namespace GhostLap.Simulation;

/// <summary>An input or state the pure reducer cannot apply. The caller admits inputs (see <see cref="InputAdmission"/>) before appending a step.</summary>
public sealed class GhostLapTransitionException(string message) : InvalidOperationException(message);

/// <summary>
/// The complete <c>ghost-v1</c> reducer. Per tick, in this fixed order:
/// <list type="number">
/// <item>Validate inputs (tick, slot, vector, strictly newer sequence), sort per slot by sequence, derive the effective
/// input: the highest-sequence vector, held for at most <see cref="GameRulesV1.InputHoldTicks"/> ticks without a newer
/// input, and a Burst request only on a rising edge of <c>requestBurst</c>.</item>
/// <item>Move and collide; add the actual post-collision distance (the only source of movement points).</item>
/// <item>Activate/expire candles from the pinned plan, minus this world's collected ids.</item>
/// <item>Pickups in candle id order; when several players overlap one candle the lowest slot collects it.</item>
/// <item>Contact in sorted slot-pair order using the charges current at that point: when charges differ the weaker loses
/// <see cref="GameRulesV1.ContactChargeLoss"/>, score unchanged, pair cooldown <see cref="GameRulesV1.ContactCooldownTicks"/>.</item>
/// <item>Requested Bursts of Overlit players in slot order, then due auto-Bursts in slot order; at most one per player.
/// A player whose Overlit was cancelled earlier in the tick (for example by a lower slot's Burst) cannot Burst.</item>
/// <item>On the final tick, after all effects, emit <see cref="GameEventKind.RoundFinished"/>; see <see cref="GameState.FinalResult"/>.</item>
/// </list>
/// The reducer reads no clock and no random source, and never accepts a position from a client.
/// </summary>
public sealed class GhostLapTransition : IDeterministicTransition<GameState, GameInput, GameContext>
{
    public static GhostLapTransition Instance { get; } = new();

    private static readonly long BurstRadiusSquared = (long)GameRulesV1.BurstRadiusFixed * GameRulesV1.BurstRadiusFixed;

    public GameState Apply(GameState previous, long position, IReadOnlyList<OrderedInput<GameInput>> inputs, GameContext pinnedContext)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(pinnedContext);
        if (position != previous.Tick + 1)
        {
            throw new GhostLapTransitionException($"Position {position} does not follow tick {previous.Tick}.");
        }
        if (position > GameRulesV1.RoundTicks)
        {
            throw new GhostLapTransitionException($"Tick {position} is past the final tick {GameRulesV1.RoundTicks}.");
        }
        var n = previous.Players.Length;
        if (n != pinnedContext.PlayerCount || previous.PairContactReadyTicks.Length != GameState.PairCount(n))
        {
            throw new GhostLapTransitionException("State does not match the pinned context's player count.");
        }

        var players = previous.Players.ToArray();
        var events = new List<GameEvent>();

        // Phase 1: validate, sort by (slot, sequence), derive the effective input.
        var bySlot = new List<GameInput>[n];
        for (var i = 0; i < n; i++)
        {
            bySlot[i] = [];
        }
        foreach (var input in inputs)
        {
            var payload = input.Payload;
            if (payload.Tick != position)
            {
                throw new GhostLapTransitionException($"Input {input.InputId} is for tick {payload.Tick}, not {position}.");
            }
            if (payload.Slot >= n)
            {
                throw new GhostLapTransitionException($"Input {input.InputId} targets missing slot {payload.Slot}.");
            }
            if (payload.MoveX is < -1 or > 1 || payload.MoveY is < -1 or > 1)
            {
                throw new GhostLapTransitionException($"Input {input.InputId} has an out-of-range move vector.");
            }
            bySlot[payload.Slot].Add(payload);
        }
        var burstEdge = new bool[n];
        for (var slot = 0; slot < n; slot++)
        {
            var list = bySlot[slot];
            var p = players[slot];
            if (list.Count == 0)
            {
                if (p.HoldUntilTick != 0 && position > p.HoldUntilTick)
                {
                    players[slot] = p with { HeldMoveX = 0, HeldMoveY = 0, HoldUntilTick = 0 };
                }
                continue;
            }
            list.Sort((a, b) => a.Sequence.CompareTo(b.Sequence));
            var latch = p.BurstLatch;
            var last = p.LastSequence;
            foreach (var input in list)
            {
                if (input.Sequence <= last)
                {
                    throw new GhostLapTransitionException(
                        $"Slot {slot} sequence {input.Sequence} is not newer than {last} at tick {position}.");
                }
                last = input.Sequence;
                burstEdge[slot] |= input.RequestBurst && !latch;
                latch = input.RequestBurst;
            }
            var final = list[^1];
            var neutral = final.MoveX == 0 && final.MoveY == 0;
            players[slot] = p with
            {
                LastSequence = last,
                BurstLatch = latch,
                HeldMoveX = final.MoveX,
                HeldMoveY = final.MoveY,
                HoldUntilTick = neutral ? 0 : position + GameRulesV1.InputHoldTicks,
            };
        }

        // Phase 2: movement in slot order; distance is the displacement that actually happened.
        for (var slot = 0; slot < n; slot++)
        {
            var p = players[slot];
            var r = Movement.Move(p.XFixed, p.YFixed, p.HeldMoveX, p.HeldMoveY);
            players[slot] = p with { XFixed = r.XFixed, YFixed = r.YFixed, DistanceFixed = checked(p.DistanceFixed + r.DistanceFixed) };
        }

        // Phase 3: candles from the pinned plan; this world's collected ids never come back.
        var collected = previous.Collected.ToBuilder();
        var collectedIds = previous.Collected.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var available = pinnedContext.Plan.ActiveAt(position).Where(c => !collectedIds.Contains(c.Id)).ToList();

        // Phase 4: pickups by candle id, lowest overlapping slot wins.
        var stillActive = ImmutableArray.CreateBuilder<string>(available.Count);
        foreach (var candle in available)
        {
            var box = new FixedRect(
                candle.Site.X - GameRulesV1.CandleHalfExtentFixed, candle.Site.Y - GameRulesV1.CandleHalfExtentFixed,
                candle.Site.X + GameRulesV1.CandleHalfExtentFixed, candle.Site.Y + GameRulesV1.CandleHalfExtentFixed);
            var collector = -1;
            for (var slot = 0; slot < n && collector < 0; slot++)
            {
                if (ArenaV1.ColliderAt(players[slot].XFixed, players[slot].YFixed).Overlaps(box))
                {
                    collector = slot;
                }
            }
            if (collector < 0)
            {
                stillActive.Add(candle.Id);
                continue;
            }
            collected.Add(new CollectedCandle(candle.Id, (byte)collector, position));
            var p = players[collector];
            events.Add(new GameEvent(GameEventKind.CandleCollected, p.Slot, Value: GameRulesV1.CandlePickupPoints, CandleId: candle.Id));
            if (p.Charge < GameRulesV1.MaxCharge)
            {
                var charge = p.Charge + 1;
                var deadline = charge == GameRulesV1.MaxCharge ? position + GameRulesV1.OverlitTicks : 0;
                p = p with { Charge = charge, OverlitDeadlineTick = deadline };
                if (deadline != 0)
                {
                    events.Add(new GameEvent(GameEventKind.OverlitStarted, p.Slot, Value: checked((int)deadline)));
                }
            }
            players[collector] = p with { Pickups = p.Pickups + 1 }; // while full: score only, deadline unchanged
        }
        collected.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));

        // Phase 5: contact in sorted pair order.
        var pairs = previous.PairContactReadyTicks.ToArray();
        for (var a = 0; a < n; a++)
        {
            for (var b = a + 1; b < n; b++)
            {
                var k = GameState.PairIndex(a, b, n);
                if (pairs[k] > position || players[a].Charge == players[b].Charge
                    || !ArenaV1.ColliderAt(players[a].XFixed, players[a].YFixed)
                        .Overlaps(ArenaV1.ColliderAt(players[b].XFixed, players[b].YFixed)))
                {
                    continue;
                }
                var (weak, strong) = players[a].Charge < players[b].Charge ? (a, b) : (b, a);
                var before = players[weak].Charge;
                // The weaker charge is below the stronger, so contact can never drain an Overlit (full) player.
                players[weak] = Drain(players[weak], GameRulesV1.ContactChargeLoss, events);
                events.Add(new GameEvent(GameEventKind.ContactDrain, (byte)weak, (byte)strong, before - players[weak].Charge));
                pairs[k] = position + GameRulesV1.ContactCooldownTicks;
            }
        }

        // Phase 6: requested Bursts, then due auto-Bursts; at most one per player per tick.
        var burst = new bool[n];
        for (var slot = 0; slot < n; slot++)
        {
            if (burstEdge[slot] && players[slot].IsOverlit)
            {
                Fire(players, slot, GameEventKind.BurstManual, events);
                burst[slot] = true;
            }
        }
        for (var slot = 0; slot < n; slot++)
        {
            if (!burst[slot] && players[slot].IsOverlit && players[slot].OverlitDeadlineTick <= position)
            {
                Fire(players, slot, GameEventKind.BurstAuto, events);
                burst[slot] = true;
            }
        }

        // Expired cooldowns are normalized to 0 so equal worlds encode identically.
        for (var k = 0; k < pairs.Length; k++)
        {
            if (pairs[k] <= position)
            {
                pairs[k] = 0;
            }
        }

        // Phase 7: finalize after every effect of the last tick.
        if (position == GameRulesV1.RoundTicks)
        {
            events.Add(new GameEvent(GameEventKind.RoundFinished, 0, Value: players.Max(p => p.TotalScore)));
        }

        return new GameState(position, [.. players], stillActive.ToImmutable())
        {
            Collected = collected.ToImmutable(),
            PairContactReadyTicks = [.. pairs],
            Events = [.. events],
        };
    }

    private static void Fire(PlayerState[] players, int owner, GameEventKind kind, List<GameEvent> events)
    {
        var o = players[owner];
        var fired = events.Count;
        events.Add(new GameEvent(kind, o.Slot)); // hit count filled in below
        var hits = 0;
        for (var slot = 0; slot < players.Length; slot++)
        {
            if (slot == owner)
            {
                continue;
            }
            var t = players[slot];
            long dx = t.XFixed - o.XFixed;
            long dy = t.YFixed - o.YFixed;
            if (dx * dx + dy * dy > BurstRadiusSquared)
            {
                continue;
            }
            hits++;
            var penalty = Math.Min(GameRulesV1.BurstPenaltyPoints, t.TotalScore); // clamp: a total never goes below zero
            events.Add(new GameEvent(GameEventKind.BurstHit, t.Slot, o.Slot, penalty));
            t = Drain(t, GameRulesV1.BurstChargeLoss, events);
            players[slot] = t with { PenaltyPoints = t.PenaltyPoints + penalty };
        }
        events[fired] = events[fired] with { Value = hits };
        players[owner] = o with { Charge = 0, OverlitDeadlineTick = 0, Bursts = o.Bursts + 1, BurstHits = o.BurstHits + hits };
    }

    /// <summary>Removes charge (clamped at zero); dropping from full charge cancels Overlit, and a later return to full re-arms it.</summary>
    private static PlayerState Drain(PlayerState p, int loss, List<GameEvent> events)
    {
        var charge = Math.Max(0, p.Charge - loss);
        if (p.IsOverlit && charge < GameRulesV1.MaxCharge)
        {
            events.Add(new GameEvent(GameEventKind.OverlitCancelled, p.Slot));
        }
        return p with { Charge = charge, OverlitDeadlineTick = charge == GameRulesV1.MaxCharge ? p.OverlitDeadlineTick : 0 };
    }
}
