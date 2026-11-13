using Revenant.Contracts;
using Revenant.Observations;
using Revenant.Synthetic;
using Revenant.Timeline;
using Xunit;

namespace Revenant.Tests.Synthetic;

/// <summary>
/// Structural invariants of generator 4 over every predeclared root. The test asserts properties only; it never reports
/// labels, so it does not expose FinalTest or Challenge truth.
/// </summary>
public sealed class V4LongitudinalPlannerG4Tests
{
    public static TheoryData<byte> Splits => [.. Enum.GetValues<LongitudinalSplit>().Select(s => (byte)s)];

    [Theory]
    [MemberData(nameof(Splits))]
    public void Every_root_plans_deterministically_with_fundable_payments_and_in_range_truth(byte split)
    {
        foreach (var root in V4LongitudinalPlannerG4.Roots((LongitudinalSplit)split))
        {
            var id = new ScenarioRunId(new Guid(Convert.FromHexString(root.Key[..32]), bigEndian: true));
            var episode = V4LongitudinalPlannerG4.Plan(root, id);
            Assert.Equal(episode.BehaviorSha256, V4LongitudinalPlannerG4.Plan(root, id).BehaviorSha256);
            Assert.Equal(V4LongitudinalPlannerG4.Version, episode.Generator);
            Assert.Equal(V4LongitudinalPlannerG4.TruthSchema, episode.TruthSchema);
            var manifest = episode.Financial.Opening.Manifest;
            Assert.Equal(24, manifest.Accounts.Length);
            Assert.Equal(manifest.Accounts.Select(a => a.AccountId).Order(), episode.Rhythms!.Keys.Order());

            // Planned balances never fall below the 2% reserve, so no ordinary intent is planned to fail for funds.
            var balance = manifest.Accounts.ToDictionary(a => a.AccountId, a => a.OpeningBalance.MinorUnits);
            var reserve = manifest.Accounts.ToDictionary(a => a.AccountId, a => a.OpeningBalance.MinorUnits / 50);
            var origin = manifest.StartUtcMs;
            var buckets = root.Days * V4LongitudinalPlanner.BucketsPerDay;
            long previous = 0;
            foreach (var intent in episode.Financial.Payments.Select(p => p.Intent))
            {
                Assert.True(intent.EventTimeUtcMs >= previous && intent.EventTimeUtcMs < origin + buckets * ObservedFinanceVersion.BucketMs);
                previous = intent.EventTimeUtcMs;
                balance[intent.SourceAccountId] -= intent.Amount.MinorUnits;
                balance[intent.DestAccountId] += intent.Amount.MinorUnits;
                Assert.True(balance[intent.SourceAccountId] >= reserve[intent.SourceAccountId]);
            }

            var accountEvents = episode.Events.Where(e => e.Truth is not null).ToArray();
            Assert.All(episode.Events, e => Assert.InRange(e.Start, 0, e.End));
            Assert.All(episode.Events, e => Assert.InRange(e.End, e.Start, buckets - 1));
            Assert.All(accountEvents, e => Assert.True(e.Kind == LongitudinalEventKind.LegitimateBurst
                ? e.Truth == VelocityTruth.Normal
                : e.TailProbability is > 0 and <= 1 && e.ObservedCount > 0 && e.Intent is not null && e.Mechanism is not null));
            Assert.All(accountEvents.Where(e => e.Mechanism is not VelocityMechanism.TreasuryDrain),
                e => Assert.True(e.Start >= V4LongitudinalPlannerG4.FirstInjectionDay * V4LongitudinalPlanner.BucketsPerDay));
            Assert.Equal(3, episode.Events.Count(e => e.Kind is LongitudinalEventKind.DevelopingPressure or LongitudinalEventKind.SuddenShock));
            Assert.Equal(episode.Events.Length, episode.Events.Select(e => e.Id).Distinct().Count());
        }
    }

    [Fact]
    public void Generator_4_roots_are_disjoint_from_generator_3_roots()
    {
        var g3 = Enum.GetValues<LongitudinalSplit>().SelectMany(s => V4LongitudinalPlanner.Roots(s)).Select(r => r.Key).ToHashSet();
        var g4 = Enum.GetValues<LongitudinalSplit>().SelectMany(s => V4LongitudinalPlannerG4.Roots(s)).Select(r => r.Key).ToArray();
        Assert.Equal(g4.Length, g4.Distinct().Count());
        Assert.DoesNotContain(g4, g3.Contains);
    }
}
