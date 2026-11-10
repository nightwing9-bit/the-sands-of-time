using GhostLap.Rules;
using GhostLap.Simulation;

namespace GhostLap.Tests.Simulation;

/// <summary>
/// Deterministic test player: steers toward the nearest active candle, detours when stuck, skips every fifth tick per slot
/// (a missing input, so the held vector or neutral expiry applies) and presses Burst when Overlit near a rival, or
/// periodically when solo. It reads the authoritative state and emits only inputs; extracted from FullGameTests (task 6.3).
/// </summary>
internal sealed class CandleSeekingDriver(int players)
{
    private readonly int[] _seq = new int[players];
    private readonly int[] _detour = new int[players];
    private readonly (int X, int Y)[] _last = new (int, int)[players];

    public GameInput[] Inputs(GameState s, GameContext ctx)
    {
        var tick = s.Tick + 1;
        var list = new List<GameInput>();
        foreach (var p in s.Players)
        {
            var i = p.Slot;
            if ((tick + i) % 5 == 0)
            {
                continue; // a missing input: the held vector carries this tick
            }
            var stuck = _last[i] == (p.XFixed, p.YFixed) && (p.HeldMoveX != 0 || p.HeldMoveY != 0);
            _last[i] = (p.XFixed, p.YFixed);
            if (stuck)
            {
                _detour[i] = 25;
            }
            sbyte mx = 0, my = 0;
            var target = ctx.Plan.ActiveAt(tick).Where(c => s.ActiveCandleIds.Contains(c.Id))
                .OrderBy(c => Math.Abs((long)c.Site.X - p.XFixed) + Math.Abs((long)c.Site.Y - p.YFixed))
                .ThenBy(c => c.Id, StringComparer.Ordinal).FirstOrDefault();
            if (_detour[i] > 0)
            {
                _detour[i]--;
                (mx, my) = ((tick / 50 + i) % 2 == 0 ? ((sbyte)0, (sbyte)1) : ((sbyte)1, (sbyte)-1));
            }
            else if (target is not null)
            {
                var dx = target.Site.X - p.XFixed;
                var dy = target.Site.Y - p.YFixed;
                mx = (sbyte)(Math.Abs(dx) < GameRulesV1.CardinalStepFixed ? 0 : Math.Sign(dx));
                my = (sbyte)(Math.Abs(dy) < GameRulesV1.CardinalStepFixed ? 0 : Math.Sign(dy));
            }
            var rival = s.Players.Any(o => o.Slot != i
                && Math.Abs((long)o.XFixed - p.XFixed) + Math.Abs((long)o.YFixed - p.YFixed) < GameRulesV1.BurstRadiusFixed);
            var press = p.IsOverlit && !p.BurstLatch && (rival || (players == 1 && tick % 97 == 0));
            list.Add(new GameInput(tick, i, ++_seq[i], mx, my, press));
        }
        return [.. list];
    }
}
