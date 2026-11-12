using Revenant.Contracts;
using Revenant.Observations;
using Revenant.Scenario;
using Revenant.Synthetic;
using Revenant.Tests.Observations;
using Revenant.Timeline;
using Xunit;

namespace Revenant.Tests.Synthetic;

public sealed class V4DatasetTests
{
    [Fact]
    public void Cohort_groups_are_assigned_before_generation_and_disjoint_across_splits()
    {
        var config = V4EpisodeConfig.Qualification;
        var groups = config.Roots().ToArray();
        Assert.Equal(new[] { 12, 4, 4, 4 }, groups.GroupBy(x => x.Split).Select(g => g.Count()));
        var designs = groups.Select(g => V4EpisodePlanner.Plan(config, g, new(Guid.NewGuid()))).ToArray();
        Assert.Equal(24, designs.Select(x => x.Group.Key).Distinct().Count());
        Assert.Equal(24, designs.Select(x => x.HistoryFingerprint).Distinct().Count());
        Assert.Equal(120, designs.SelectMany(x => x.CampaignFingerprints).Distinct().Count());
        var accounts = designs.SelectMany(x => x.Financial.Opening.Manifest.Accounts.Select(a => a.AccountId)).ToArray();
        Assert.Equal(accounts.Length, accounts.Distinct().Count());
        Assert.True(designs.Select(x => x.Financial.Opening.Manifest.Accounts.Length).Distinct().Count() > 1);
        foreach (var split in designs.GroupBy(x => x.Group.Split))
        {
            var labels = split.SelectMany(d => d.Truth.Payments).ToArray();
            Assert.Equal(Enum.GetValues<EpisodeAttack>(), labels.Where(x => x.Malicious).Select(x => x.Attack!.Value).Distinct().Order());
            Assert.Contains(labels, x => x.HardNegative && !x.Malicious);
            Assert.Contains(split.SelectMany(x => x.Truth.Anomalies), x => x.Family == EpisodeAnomaly.OperationalBurst);
        }
        Assert.All(designs.SelectMany(x => x.Financial.Payments), x => Assert.Equal(SyntheticTruth.Legitimate, x.Intent.Truth));
        var reference = designs[0];
        var repeat = V4EpisodePlanner.Plan(config, groups[0], new(Guid.NewGuid()));
        Assert.Equal(reference.HistoryFingerprint, repeat.HistoryFingerprint);
        Assert.Equal(reference.Financial.Opening.Manifest.ContentHash, repeat.Financial.Opening.Manifest.ContentHash);
        Assert.Equal(reference.Truth.Payments, repeat.Truth.Payments.AsEnumerable());
        Assert.Equal(reference.Financial.Payments.Select(p => ObservationEncoding.Hash(PaymentIntentCodec.EncodeFacts(p.Intent))),
            repeat.Financial.Payments.Select(p => ObservationEncoding.Hash(PaymentIntentCodec.EncodeFacts(p.Intent))));
    }

    [Fact]
    public void Eight_horizons_are_later_intervals_and_missing_tail_stays_unobserved()
    {
        var fx = new ObservationFixture();
        fx.Seal();
        var at = fx.State.LastSealedBoundaryMs;
        var payment = fx.Intent("future-transfer", at);
        fx.Screen(payment, at);
        fx.Settle(payment, at);
        fx.Seal();
        fx.Return(payment, fx.State.LastSealedBoundaryMs);
        for (var i = 0; i < 8; i++) fx.Seal();

        var banks = V4ObservationProjector.Project(fx.History()).Banks;
        var targets = V4BankTargets.Build(banks).ToArray();
        Assert.Equal(60, targets.Length);
        Assert.Equal(12, targets.Count(t => t.FullyCovered));
        var first = targets[0];
        Assert.Equal(0, banks[0].SettledOut);
        Assert.Equal(payment.Amount.MinorUnits, first.Horizons[0]!.SettledOut);
        Assert.Equal(payment.Amount.MinorUnits, first.Horizons[1]!.ReturnedIn);
        Assert.Equal(at + 8 * ObservedFinanceVersion.BucketMs, first.Horizons[7]!.BoundaryMs);
        Assert.Equal(first.Encode(), V4BankTargets.Decode(first.Encode(), first.Digest).Encode());
        Assert.All(targets[^1].Horizons, target => Assert.Null(target));
        Assert.False(targets[^1].FullyCovered);
        Assert.Null(targets[12].Horizons[7]);
    }
}
