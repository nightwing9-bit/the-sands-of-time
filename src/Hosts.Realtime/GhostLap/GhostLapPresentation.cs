using GhostLap.Contracts.Multiplayer;
using GhostLap.Multiplayer;
using GhostLap.Rules;

namespace GhostLapHost;

/// <summary>Read projections only. Never returns a future candle plan or changes authoritative state.</summary>
public static class GhostLapPresentation
{
    public static ArenaPresentationView Arena() => new(
        MpRulesProfile.LayoutId, MpRulesProfile.LayoutFingerprint,
        GameRulesV1.WorldWidthUnits, GameRulesV1.WorldHeightUnits,
        ArenaV1.Obstacles.Select(r => new ArenaObstacleView(r.X0 / 1024, r.Y0 / 1024, r.X1 / 1024, r.Y1 / 1024)).ToArray());

    public static SnapshotFrame Project(SnapshotFrame frame, CandlePlan? plan)
    {
        if (plan is null) return frame;
        var tick = frame.Snapshot.Tick;
        var active = frame.Snapshot.ActiveCandleIds.ToHashSet(StringComparer.Ordinal);
        return frame with
        {
            VisibleCandles = plan.Candles
                .Where(c => active.Contains(c.Id) || (c.TelegraphTick <= tick && tick < c.ActivateTick))
                .Select(c => new VisibleCandleView(c.Id, c.Site.X / 1024, c.Site.Y / 1024,
                    active.Contains(c.Id) ? "active" : "telegraph")).ToArray(),
        };
    }
}
