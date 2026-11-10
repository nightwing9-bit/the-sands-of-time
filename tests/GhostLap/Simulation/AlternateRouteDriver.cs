using GhostLap.Rules;
using GhostLap.Simulation;

namespace GhostLap.Tests.Simulation;

/// <summary>
/// Deterministic test player for a branch (task 6.4): it plays differently from <see cref="CandleSeekingDriver"/> by
/// steering toward the second-nearest uncollected candle, sends an input every tick, continues the sequence from the
/// authoritative <see cref="PlayerState.LastSequence"/> (so it can take over any restored state) and presses Burst on
/// every 61st tick while Overlit. It reads the authoritative state and emits only inputs.
/// </summary>
internal sealed class AlternateRouteDriver
{
    private int _detour;
    private (int X, int Y) _last;

    public GameInput[] Inputs(GameState s, GameContext ctx)
    {
        var tick = s.Tick + 1;
        var p = s.Players[0];
        var stuck = _last == (p.XFixed, p.YFixed) && (p.HeldMoveX != 0 || p.HeldMoveY != 0);
        _last = (p.XFixed, p.YFixed);
        if (stuck)
        {
            _detour = 20;
        }

        sbyte mx = 0, my = 0;
        var candidates = ctx.Plan.ActiveAt(tick).Where(c => s.ActiveCandleIds.Contains(c.Id))
            .OrderBy(c => Math.Abs((long)c.Site.X - p.XFixed) + Math.Abs((long)c.Site.Y - p.YFixed))
            .ThenBy(c => c.Id, StringComparer.Ordinal).Take(2).ToList();
        var target = candidates.Count == 0 ? null : candidates[^1];
        if (_detour > 0)
        {
            _detour--;
            (mx, my) = (tick / 40) % 2 == 0 ? ((sbyte)-1, (sbyte)1) : ((sbyte)1, (sbyte)1);
        }
        else if (target is not null)
        {
            var dx = target.Site.X - p.XFixed;
            var dy = target.Site.Y - p.YFixed;
            mx = (sbyte)(Math.Abs(dx) < GameRulesV1.CardinalStepFixed ? 0 : Math.Sign(dx));
            my = (sbyte)(Math.Abs(dy) < GameRulesV1.CardinalStepFixed ? 0 : Math.Sign(dy));
        }

        var press = p.IsOverlit && !p.BurstLatch && tick % 61 == 0;
        return [new GameInput(tick, 0, p.LastSequence + 1, mx, my, press)];
    }
}
