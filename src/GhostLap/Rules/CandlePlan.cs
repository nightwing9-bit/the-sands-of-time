using System.Collections.Immutable;

namespace GhostLap.Rules;

/// <summary>One scheduled candle. Active when <c>ActivateTick &lt;= tick &lt; ExpireTick</c>; the telegraph precedes activation.</summary>
public sealed record CandleSpec(
    string Id, int SiteIndex, FixedPoint Site, int TelegraphTick, int ActivateTick, int ExpireTick);

/// <summary>
/// The candle opportunities of one round: a pure function of (seed, rules <c>ghost-v1</c>, layout <c>arena-v1</c>).
/// It never reads a clock, never consumes shared random state, and never sees player actions, so a branch that
/// collects different candles still faces exactly the same opportunities. Every random choice is a stateless
/// hash of (seed, candle index, purpose), so there is no RNG state to checkpoint.
/// </summary>
public sealed class CandlePlan
{
    private const ulong PurposeJitter = 1;
    private const ulong PurposeSite = 2;

    private CandlePlan(ImmutableArray<CandleSpec> candles) => Candles = candles;

    /// <summary>All candles, ordered by activation tick; ids are <c>c-0000</c>, <c>c-0001</c>, ... in that order.</summary>
    public ImmutableArray<CandleSpec> Candles { get; }

    public static CandlePlan Create(long seed)
    {
        var lead = GameRulesV1.CandleLifetimeTicks / GameRulesV1.CandleSpacingTicks; // candles already running at tick 0
        var sites = ArenaV1.CandleSites;
        var drafts = new List<(int Site, int Activate)>();
        for (var k = -lead; ; k++)
        {
            var activate = k * GameRulesV1.CandleSpacingTicks
                + (int)(Mix(seed, k, PurposeJitter) % GameRulesV1.CandleJitterTicks);
            if (activate >= GameRulesV1.RoundTicks)
            {
                break;
            }
            if (activate + GameRulesV1.CandleLifetimeTicks <= 0)
            {
                continue;
            }
            // Probe forward from the hashed site so no two candles overlap in time on one site.
            var site = (int)(Mix(seed, k, PurposeSite) % (ulong)sites.Length);
            while (drafts.Any(d => d.Site == site && d.Activate + GameRulesV1.CandleLifetimeTicks > activate))
            {
                site = (site + 1) % sites.Length;
            }
            drafts.Add((site, activate));
        }

        var candles = ImmutableArray.CreateBuilder<CandleSpec>(drafts.Count);
        for (var i = 0; i < drafts.Count; i++)
        {
            var (site, activate) = drafts[i];
            candles.Add(new CandleSpec(
                $"c-{i:D4}", site, sites[site], activate - GameRulesV1.CandleTelegraphTicks,
                activate, activate + GameRulesV1.CandleLifetimeTicks));
        }
        return new CandlePlan(candles.MoveToImmutable());
    }

    /// <summary>Candles active at <paramref name="tick"/>, in id order.</summary>
    public IEnumerable<CandleSpec> ActiveAt(long tick) =>
        Candles.Where(c => c.ActivateTick <= tick && tick < c.ExpireTick);

    public ImmutableArray<string> ActiveIdsAt(long tick) => [.. ActiveAt(tick).Select(c => c.Id)];

    // SplitMix64 finalizer over a combined key; stateless, endian-independent, integer only.
    private static ulong Mix(long seed, int index, ulong purpose)
    {
        unchecked
        {
            var z = (ulong)seed + 0x9E3779B97F4A7C15UL * ((ulong)(long)index + 1UL) + 0xD1B54A32D192ED03UL * purpose;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }
}
