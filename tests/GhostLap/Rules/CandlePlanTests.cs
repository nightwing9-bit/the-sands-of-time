using System.Security.Cryptography;
using System.Text;
using FsCheck;
using FsCheck.Fluent;
using GhostLap.Rules;
using Xunit;

namespace GhostLap.Tests.Rules;

public sealed class CandlePlanTests
{
    // Regression pin of the seed-42 plan. It was produced by this implementation (not an independent source),
    // so it detects drift of the schedule algorithm or layout; it does not prove the schedule is "right".
    private const string Seed42Digest = "9d31d21c1022217500a1e4ee6dd8d2433087c608bb8724911861eff75f0193f7";

    [Fact]
    public void Same_seed_gives_identical_plans_and_different_seeds_differ()
    {
        Assert.Equal(Digest(CandlePlan.Create(42)), Digest(CandlePlan.Create(42)));
        Assert.NotEqual(Digest(CandlePlan.Create(42)), Digest(CandlePlan.Create(43)));
    }

    [Fact]
    public void Seed_42_plan_matches_the_pinned_digest()
    {
        Assert.True(Seed42Digest == Digest(CandlePlan.Create(42)), "ACTUAL=" + Digest(CandlePlan.Create(42)));
    }

    [Fact]
    public void Candles_have_stable_sequential_ids_the_210_tick_lifetime_and_a_telegraph_before_activation()
    {
        var plan = CandlePlan.Create(42);
        for (var i = 0; i < plan.Candles.Length; i++)
        {
            var c = plan.Candles[i];
            Assert.Equal($"c-{i:D4}", c.Id);
            Assert.Equal(GameRulesV1.CandleLifetimeTicks, c.ExpireTick - c.ActivateTick);
            Assert.Equal(GameRulesV1.CandleTelegraphTicks, c.ActivateTick - c.TelegraphTick);
            Assert.True(c.ExpireTick > 0 && c.ActivateTick < GameRulesV1.RoundTicks);
            if (i > 0)
            {
                Assert.True(c.ActivateTick > plan.Candles[i - 1].ActivateTick);
            }
        }
    }

    [Fact]
    public void Activation_is_inclusive_and_expiry_exclusive()
    {
        var c = CandlePlan.Create(42).Candles.First(c => c.ActivateTick > 0);
        var plan = CandlePlan.Create(42);
        Assert.DoesNotContain(c.Id, plan.ActiveIdsAt(c.ActivateTick - 1));
        Assert.Contains(c.Id, plan.ActiveIdsAt(c.ActivateTick));
        Assert.Contains(c.Id, plan.ActiveIdsAt(c.ExpireTick - 1));
        Assert.DoesNotContain(c.Id, plan.ActiveIdsAt(c.ExpireTick));
    }

    /// <summary>Property: for any seed, 8 to 12 candles are active at every tick of the round, including tick 0.</summary>
    [Fact]
    public void Between_eight_and_twelve_candles_are_active_at_every_tick_for_any_seed()
    {
        Prop.ForAll(Gen.Choose(int.MinValue, int.MaxValue).Select(i => (long)i * 104729).ToArbitrary(), seed =>
        {
            var plan = CandlePlan.Create(seed);
            for (var t = 0; t <= GameRulesV1.RoundTicks; t++)
            {
                var n = plan.ActiveAt(t).Count();
                if (n is < 8 or > 12)
                {
                    return false;
                }
            }
            return true;
        }).Check(Config.QuickThrowOnFailure.WithMaxTest(25));
    }

    [Fact]
    public void Two_candles_never_share_a_site_while_both_are_active_and_every_site_is_occupiable()
    {
        Prop.ForAll(Gen.Choose(0, 1_000_000).Select(i => (long)i).ToArbitrary(), seed =>
        {
            var plan = CandlePlan.Create(seed);
            foreach (var a in plan.Candles)
            {
                if (!ArenaV1.CanOccupy(a.Site.X, a.Site.Y) || ArenaV1.CandleSites[a.SiteIndex] != a.Site)
                {
                    return false;
                }
                if (plan.Candles.Any(b => b != a && b.SiteIndex == a.SiteIndex
                    && a.ActivateTick < b.ExpireTick && b.ActivateTick < a.ExpireTick))
                {
                    return false;
                }
            }
            return true;
        }).Check(Config.QuickThrowOnFailure.WithMaxTest(50));
    }

    [Fact]
    public void Plan_source_has_no_clock_or_ambient_random_dependency()
    {
        var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/GhostLap"));
        Assert.True(Directory.Exists(dir), dir);
        string[] forbidden = ["DateTime", "DateTimeOffset", "Stopwatch", "Environment.TickCount", "System.Random", "new Random", "Random.Shared", "Guid.NewGuid", "Math.Sin", "Math.Cos", "Math.Sqrt", "float", "double"];
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains("/obj/") && !f.Contains("/bin/")))
        {
            var text = File.ReadAllText(file);
            foreach (var token in forbidden)
            {
                Assert.False(text.Contains(token, StringComparison.Ordinal) && !IsCommentOnly(text, token), $"{file} uses {token}");
            }
        }
    }

    // A token is acceptable if every occurrence is on a comment line.
    private static bool IsCommentOnly(string text, string token) =>
        text.Split('\n').Where(l => l.Contains(token, StringComparison.Ordinal))
            .All(l => l.TrimStart().StartsWith("//", StringComparison.Ordinal) || l.TrimStart().StartsWith("///", StringComparison.Ordinal)
                      || l.TrimStart().StartsWith('*'));

    internal static string Digest(CandlePlan plan)
    {
        var sb = new StringBuilder();
        foreach (var c in plan.Candles)
        {
            sb.Append(c.Id).Append(':').Append(c.SiteIndex).Append(':').Append(c.TelegraphTick).Append(':')
                .Append(c.ActivateTick).Append(':').Append(c.ExpireTick).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }
}
