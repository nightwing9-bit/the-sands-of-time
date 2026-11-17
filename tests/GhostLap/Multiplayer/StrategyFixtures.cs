using GhostLap.Multiplayer;
using GhostLap.Rules;
using GhostLap.Simulation;

namespace GhostLap.Tests.Multiplayer;

/// <summary>
/// Task 10.5 test fixtures: scripted, deterministic stand-ins for a human strategy, used ONLY to drive the real ghost-mp-v1 reducer
/// (<see cref="MpRules"/>) in tests and to measure what the sealed rules reward. They read the authoritative state and emit ordinary inputs.
/// They are not gameplay bots: nothing in <c>src</c> references them, they make no decisions about rules, and their numbers are observations
/// for architectural review, never tuning inputs.
/// </summary>
internal abstract class ScriptedPlayer
{
    public abstract string Role { get; }

    /// <summary>The held direction and Burst press for the next tick, or null for "no input this tick" (the six-tick hold then applies).</summary>
    public abstract (sbyte X, sbyte Y, bool Burst)? Decide(GameState world, MpMatchContext context, int slot);
}

/// <summary>Chases the nearest active candle (Manhattan, id tie-break). Optionally presses Burst when Overlit with a rival inside the Burst radius.</summary>
internal sealed class CandleSeeker(bool burstsNearRivals, int? persona = null, int? legacyOrientation = null) : ScriptedPlayer
{
    private (int X, int Y) _last;
    private (sbyte X, sbyte Y) _held;
    private int _detour;

    public override string Role => burstsNearRivals ? "seeker-burst" : "seeker";

    public override (sbyte X, sbyte Y, bool Burst)? Decide(GameState world, MpMatchContext context, int slot)
    {
        var me = world.Players[slot];
        var tick = world.Tick + 1;
        var stuck = _last == (me.XFixed, me.YFixed) && _held != (0, 0);
        _last = (me.XFixed, me.YFixed);
        if (stuck)
        {
            _detour = 25;
        }
        sbyte mx = 0, my = 0;
        var target = context.Plan.ActiveAt(tick).Where(c => world.ActiveCandleIds.Contains(c.Id))
            .OrderBy(c => Math.Abs((long)c.Site.X - me.XFixed) + Math.Abs((long)c.Site.Y - me.YFixed))
            .ThenBy(c => c.Id, StringComparer.Ordinal).FirstOrDefault();
        if (_detour > 0)
        {
            _detour--;
            (mx, my) = (tick / 50 + (legacyOrientation ?? slot)) % 2 == 0 ? ((sbyte)0, (sbyte)1) : ((sbyte)1, (sbyte)-1);
            if (persona is { } style)
            {
                (mx, my) = GreedyDetour(me, tick, style);
            }
        }
        else if (target is not null)
        {
            var dx = target.Site.X - me.XFixed;
            var dy = target.Site.Y - me.YFixed;
            mx = (sbyte)(Math.Abs(dx) < GameRulesV1.CardinalStepFixed ? 0 : Math.Sign(dx));
            my = (sbyte)(Math.Abs(dy) < GameRulesV1.CardinalStepFixed ? 0 : Math.Sign(dy));
        }
        _held = (mx, my);
        var rival = world.Players.Any(o => o.Slot != slot
            && Math.Abs((long)o.XFixed - me.XFixed) + Math.Abs((long)o.YFixed - me.YFixed) < GameRulesV1.BurstRadiusFixed);
        return (mx, my, burstsNearRivals && me.IsOverlit && !me.BurstLatch && rival);
    }
    private static (sbyte X, sbyte Y) GreedyDetour(PlayerState player, long tick, int persona)
    {
        ReadOnlySpan<(sbyte X, sbyte Y)> turns = [(1, 0), (0, 1), (-1, 0), (0, -1)];
        for (var i = 0; i < turns.Length; i++)
        {
            var direction = turns[(int)((tick / 25 + persona + i) % turns.Length)];
            if (Movement.Move(player.XFixed, player.YFixed, direction.X, direction.Y).DistanceFixed != 0)
            {
                return direction;
            }
        }
        return default;
    }

}

/// <summary>
/// Empty-lane distance farming: runs at full speed along one axis and reverses when blocked, never steering toward a candle. It is the
/// control for "does contesting candles beat running in an empty lane" (ADR-P1-22 D11; requirement R10.19).
/// </summary>
internal sealed class LaneFarmer : ScriptedPlayer
{
    private (int X, int Y) _last;
    private sbyte _dx;
    private bool _started;

    public override string Role => "lane-farmer";

    public override (sbyte X, sbyte Y, bool Burst)? Decide(GameState world, MpMatchContext context, int slot)
    {
        var me = world.Players[slot];
        if (!_started)
        {
            _started = true;
            _dx = me.XFixed < GameRulesV1.WorldWidthFixed / 2 ? (sbyte)1 : (sbyte)-1;
        }
        else if (_last == (me.XFixed, me.YFixed))
        {
            _dx = (sbyte)-_dx; // blocked by a wall or obstacle: turn around
        }
        _last = (me.XFixed, me.YFixed);
        return (_dx, 0, false);
    }
}

/// <summary>What one scripted round measured. Plain data, so it can be written as JSON for the delegated fairness matrix.</summary>
internal sealed record PlayStats(
    long Seed,
    int Players,
    string Scenario,
    string[] Roles,
    int[] Scores,
    int[] MovementPoints,
    int[] PickupPoints,
    int[] PenaltyPoints,
    int[] Pickups,
    int[] Bursts,
    int[] BurstHits,
    int CandlesCollected,
    int ContestedCollections,
    int ContactDrains,
    int[] ContactChargeLost,
    int[] ContestedWins,
    int[] AutoBursts,
    int PreemptedBursts,
    long[] FirstPickupTicks,
    int[] InitialNearestCandlePathTicks,
    string StateHash,
    int[] MoveAttempts,
    int[] BlockedTicks,
    int[] OverlitTicks,
    int[] RequestedBursts,
    int[] ContestedOpportunities,
    int[][] ContactTicks);

internal static class Playtest
{
    /// <summary>Movement points reachable in a round at full speed (design: 1687 at 300 u/s over 90 s, one point per 16 u).</summary>
    public const int MovementPointCeiling = 1687;

    public static PlayStats Run(long seed, ScriptedPlayer[] players, string scenario, int ticks = GameRulesV1.RoundTicks, Func<GameContext>? world = null,
        CompetitivePhases? phases = null, int spawnRotation = 0, bool reverseInputs = false,
        string rulesId = MpRulesProfile.RulesId)
    {
        var n = players.Length;
        var context = MpMatchContext.Create(seed, n, rulesId);
        var state = world is null
            ? MpMatchState.Initial(context)
            : new MpMatchState(GameState.Initial(world()), [.. new long[n]]) { RulesId = rulesId };
        if (spawnRotation != 0)
        {
            var original = state.World.Players;
            state = state with
            {
                World = state.World with
                {
                    Players = [.. original.Select((p, i) => p with
                    {
                        XFixed = original[(i + spawnRotation) % n].XFixed,
                        YFixed = original[(i + spawnRotation) % n].YFixed,
                    })],
                },
            };
        }
        var moveAttempts = new int[n];
        var blockedTicks = new int[n];
        var overlitTicks = new int[n];
        var requestedBursts = new int[n];
        var contestedOpportunities = new int[n];
        var contactTicks = Enumerable.Range(0, n).Select(_ => new int[n]).ToArray();
        var candles = context.Plan.Candles.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var seq = new int[n];
        var firstPickup = new long[n];
        var contactChargeLost = new int[n];
        var contestedWins = new int[n];
        var autoBursts = new int[n];
        var initialCandles = context.Plan.ActiveAt(0).ToArray();
        var initialTravel = state.World.Players
            .Select(p => initialCandles.Min(candle => RoutedSeeker.InitialDistance(new FixedPoint(p.XFixed, p.YFixed), candle))).ToArray();
        int collected = 0, contested = 0, drains = 0, preempted = 0;
        while (state.Tick < ticks)
        {
            var tick = state.Tick + 1;
            var inputs = new List<GameInput>();
            var wanted = new bool[n];
            for (var slot = 0; slot < n; slot++)
            {
                if (players[slot].Decide(state.World, context, slot) is not { } d)
                {
                    continue;
                }
                inputs.Add(new GameInput(tick, (byte)slot, ++seq[slot], d.X, d.Y, d.Burst));
                wanted[slot] = d.Burst && !state.World.Players[slot].BurstLatch && state.World.Players[slot].IsOverlit;
            }
            var before = state.World;
            if (reverseInputs)
            {
                inputs.Reverse();
            }
            state = phases is { } overrides ? MpRules.StepWithPhases(state, inputs, [], context, overrides)
                : MpRules.Step(state, inputs, [], context);
            foreach (var input in inputs)
            {
                var slot = input.Slot;
                if (input.MoveX != 0 || input.MoveY != 0)
                {
                    moveAttempts[slot]++;
                    if (state.World.Players[slot].DistanceFixed == before.Players[slot].DistanceFixed)
                    {
                        blockedTicks[slot]++;
                    }
                }
                if (wanted[slot])
                {
                    requestedBursts[slot]++;
                }
                if (before.Players[slot].IsOverlit)
                {
                    overlitTicks[slot]++;
                }
            }
            for (var i = 0; i < n; i++)
            {
                var a = state.World.Players[i];
                var box = ArenaV1.ColliderAt(a.XFixed, a.YFixed);
                for (var j = i + 1; j < n; j++)
                {
                    var b = state.World.Players[j];
                    if (box.Overlaps(ArenaV1.ColliderAt(b.XFixed, b.YFixed)))
                    {
                        contactTicks[i][j]++;
                        contactTicks[j][i]++;
                    }
                }
            }
            foreach (var e in state.World.Events)
            {
                switch (e.Kind)
                {
                    case GameEventKind.CandleCollected:
                        collected++;
                        if (firstPickup[e.Slot] == 0)
                        {
                            firstPickup[e.Slot] = tick;
                        }
                        var site = candles[e.CandleId].Site;
                        var box = new FixedRect(
                            site.X - GameRulesV1.CandleHalfExtentFixed, site.Y - GameRulesV1.CandleHalfExtentFixed,
                            site.X + GameRulesV1.CandleHalfExtentFixed, site.Y + GameRulesV1.CandleHalfExtentFixed);
                        var overlapping = state.World.Players.Where(p => ArenaV1.ColliderAt(p.XFixed, p.YFixed).Overlaps(box)).Select(p => (int)p.Slot).ToArray();
                        if (overlapping.Length > 1)
                        {
                            contested++;
                            contestedWins[e.Slot]++;
                            foreach (var slot in overlapping)
                            {
                                contestedOpportunities[slot]++;
                            }
                        }
                        // ADR-P1-22 D4: a contested candle goes to the lowest overlapping slot. Checked on every collection of every round.
                        if (!overlapping.Contains(e.Slot) || (!(phases?.HasFlag(CompetitivePhases.Pickups) ?? (rulesId == MpRulesProfile.CurrentRulesId)) && overlapping.Min() != e.Slot))
                        {
                            throw new InvalidOperationException($"Candle {e.CandleId} at tick {tick} went to slot {e.Slot}, not the lowest overlapping slot {overlapping.Min()}.");
                        }
                        break;
                    case GameEventKind.ContactDrain:
                        drains++;
                        contactChargeLost[e.Slot] += e.Value;
                        break;
                    case GameEventKind.BurstAuto:
                        autoBursts[e.Slot]++;
                        break;
                }
            }
            for (var slot = 0; slot < n; slot++)
            {
                if (wanted[slot]
                    && state.World.Events.Any(e => e.Kind == GameEventKind.OverlitCancelled && e.Slot == slot)
                    && !state.World.Events.Any(e => e.Kind == GameEventKind.BurstManual && e.Slot == slot))
                {
                    preempted++; // a lower slot's Burst drained this slot's charge before its own Burst resolved (ADR-P1-22 D4)
                }
            }
        }
        var w = state.World;
        return new PlayStats(
            seed, n, scenario, [.. players.Select(p => p.Role)],
            [.. w.Players.Select(p => p.TotalScore)], [.. w.Players.Select(p => p.MovementPoints)], [.. w.Players.Select(p => p.PickupPoints)],
            [.. w.Players.Select(p => p.PenaltyPoints)], [.. w.Players.Select(p => p.Pickups)], [.. w.Players.Select(p => p.Bursts)],
            [.. w.Players.Select(p => p.BurstHits)], collected, contested, drains, contactChargeLost, contestedWins, autoBursts, preempted, firstPickup, initialTravel, MpStateCodec.Hash(state),
            moveAttempts, blockedTicks, overlitTicks, requestedBursts, contestedOpportunities, contactTicks);
    }
}

/// <summary>Uses exact cardinal lattice paths to candle centres, including routes around arena obstacles.</summary>
internal sealed class RoutedSeeker(bool engage, int? persona = null, int? orientation = null, int preference = 0) : ScriptedPlayer
{
    private const int Step = GameRulesV1.CardinalStepFixed;
    private const int Width = GameRulesV1.WorldWidthFixed / Step + 1;
    private const int Height = GameRulesV1.WorldHeightFixed / Step + 1;
    private static readonly (sbyte X, sbyte Y)[] Directions = [(1, 0), (0, 1), (-1, 0), (0, -1)];
    private static readonly bool[] Open = Enumerable.Range(0, Width * Height)
        .Select(i => ArenaV1.CanOccupy((i % Width) * Step, (i / Width) * Step)).ToArray();
    private static readonly int[][] Routes = ArenaV1.CandleSites.Select(site => Distances(site.X, site.Y)).ToArray();

    private readonly LaneFarmer _fallback = new();

    public override string Role => engage ? "routed-engagement" : "routed-evasion";

    private static int Index(int x, int y) => ((y + Step / 2) / Step) * Width + ((x + Step / 2) / Step);

    private static int[] Distances(int x, int y)
    {
        var distances = new int[Width * Height];
        Array.Fill(distances, int.MaxValue);
        var queue = new Queue<int>();
        var start = Index(x, y);
        distances[start] = 0;
        queue.Enqueue(start);
        while (queue.TryDequeue(out var current))
        {
            foreach (var (dx, dy) in Directions)
            {
                var nx = current % Width + dx;
                var ny = current / Width + dy;
                var next = ny * Width + nx;
                if (nx >= 0 && nx < Width && ny >= 0 && ny < Height && Open[next] && distances[next] == int.MaxValue)
                {
                    distances[next] = distances[current] + 1;
                    queue.Enqueue(next);
                }
            }
        }
        return distances;
    }

    public static int InitialDistance(FixedPoint spawn, CandleSpec candle) => Routes[candle.SiteIndex][Index(spawn.X, spawn.Y)];

    private static long Distance(PlayerState me, PlayerState rival) =>
        Math.Abs((long)me.XFixed - rival.XFixed) + Math.Abs((long)me.YFixed - rival.YFixed);

    public override (sbyte X, sbyte Y, bool Burst)? Decide(GameState world, MpMatchContext context, int slot)
    {
        var me = world.Players[slot];
        var index = Index(me.XFixed, me.YFixed);
        var target = -1;
        var shortest = int.MaxValue;
        var cheapest = int.MaxValue;
        foreach (var id in world.ActiveCandleIds)
        {
            // The plan assigns IDs in sequential order.
            var n = int.Parse(id.AsSpan(2), System.Globalization.CultureInfo.InvariantCulture);
            var candle = context.Plan.Candles[n];
            var distance = Routes[candle.SiteIndex][index];
            if (distance == int.MaxValue || world.Tick + distance >= candle.ExpireTick)
            {
                continue;
            }
            var cost = distance + (persona is { } style
                ? (int)(SeededContest.Priority(context.Seed, n, candle.SiteIndex, style) % (uint)(preference + 1))
                : 0);
            if (cost < cheapest)
            {
                cheapest = cost;
                shortest = distance;
                target = candle.SiteIndex;
            }
        }

        var burst = false;
        PlayerState? threat = null;
        foreach (var rival in world.Players)
        {
            if (slot == rival.Slot || Distance(me, rival) >= GameRulesV1.BurstRadiusFixed)
            {
                continue;
            }
            burst |= engage && me.IsOverlit && !me.BurstLatch;
            if (!engage && rival.IsOverlit)
            {
                threat = rival;
            }
        }
        if (threat is { } other)
        {
            var awayX = (sbyte)Math.Sign(me.XFixed - other.XFixed);
            var awayY = (sbyte)Math.Sign(me.YFixed - other.YFixed);
            foreach (var (dx, dy) in Directions)
            {
                if ((dx == awayX && dx != 0 || dy == awayY && dy != 0)
                    && ArenaV1.CanOccupy(me.XFixed + dx * Step, me.YFixed + dy * Step))
                {
                    return (dx, dy, false);
                }
            }
        }

        if (target >= 0)
        {
            for (var i = 0; i < Directions.Length; i++)
            {
                var (dx, dy) = Directions[(i + (persona ?? 0)) % Directions.Length];
                if (persona is null)
                {
                    var style = orientation ?? slot;
                    var mirrorX = context.PlayerCount == 4 ? style % 2 == 1 : style == context.PlayerCount - 1;
                    var mirrorY = context.PlayerCount % 2 == 0 && style >= context.PlayerCount / 2;
                    dx = mirrorX ? (sbyte)-dx : dx;
                    dy = mirrorY ? (sbyte)-dy : dy;
                }
                var x = me.XFixed + dx * Step;
                var y = me.YFixed + dy * Step;
                if (ArenaV1.CanOccupy(x, y) && Routes[target][Index(x, y)] < shortest)
                {
                    return (dx, dy, burst);
                }
            }
        }
        var fallback = _fallback.Decide(world, context, slot)!.Value;
        return (fallback.X, fallback.Y, burst);
    }
}

/// <summary>Moves around a 60-by-60 loop at an outer corner. Before reaching it, follows the outer edge around obstacles.</summary>
internal sealed class CornerFarmer : ScriptedPlayer
{
    private const int Step = GameRulesV1.CardinalStepFixed;
    private int _leg;
    private (int X, int Y)[]? _waypoints;

    public override string Role => "corner-farmer";

    public override (sbyte X, sbyte Y, bool Burst)? Decide(GameState world, MpMatchContext context, int slot)
    {
        var me = world.Players[slot];
        if (_waypoints is null)
        {
            var left = me.XFixed <= GameRulesV1.WorldWidthFixed / 2;
            var top = me.YFixed < GameRulesV1.WorldHeightFixed / 2;
            var x = GameRulesV1.ToFixed(left ? 60 : 1540);
            var y = GameRulesV1.ToFixed(top ? 60 : 840);
            var ix = x + (left ? 6 : -6) * Step;
            var iy = y + (top ? 6 : -6) * Step;
            // Arena sockets have an unobstructed vertical route to the near outer lane.
            _waypoints = [(me.XFixed, y), (x, y), (ix, y), (ix, iy), (x, iy)];
        }
        var target = _waypoints[_leg];
        if ((me.XFixed, me.YFixed) == target)
        {
            _leg = _leg == _waypoints.Length - 1 ? 1 : _leg + 1;
            target = _waypoints[_leg];
        }
        return ((sbyte)Math.Sign(target.X - me.XFixed), (sbyte)Math.Sign(target.Y - me.YFixed), false);
    }
}
