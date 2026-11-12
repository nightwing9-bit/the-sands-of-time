using Revenant.Contracts;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    public void Published_v1_manifest_and_every_root_fingerprint_remain_unchanged()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TheSandsOfTime.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var published = JsonSerializer.Deserialize<V4DatasetManifest>(
            File.ReadAllBytes(Path.Combine(dir.FullName, "docs", "recovery", "r4-qualified-dataset-manifest.json")),
            new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } })!;
        Assert.Equal("a40cfb31cab319541a339d5c07a7068b6fda4717718a8012af5a999a83e793df",
            ObservationEncoding.Hash(V4DatasetExporter.EncodeManifest(published)));
        foreach (var (group, root) in V4EpisodeConfig.Qualification.Roots().Zip(published.Roots))
        {
            var plan = V4EpisodePlanner.Plan(V4EpisodeConfig.Qualification, group, new(Guid.NewGuid()));
            Assert.Equal(root.Key, group.Key);
            Assert.Equal(root.ManifestSha256, plan.Financial.Opening.Manifest.ContentHash);
            Assert.Equal(root.BehaviorSha256, plan.HistoryFingerprint);
        }
    }

    [Fact]
    public void V2_uses_pinned_independent_openings_and_overlapping_development_schedules()
    {
        var config = V4EpisodeConfig.RiskQualification;
        Assert.Equal(new[] { 12, 4, 4, 4 }, config.Roots().GroupBy(x => x.Split).Select(x => x.Count()));
        Assert.Empty(config.Roots().Select(x => x.Key).Intersect(V4EpisodeConfig.Qualification.Roots().Select(x => x.Key)));
        Assert.Empty(config.Roots().Select(x => x.Key).Intersect(V4EpisodeConfig.RiskRingChallenge.Roots().Select(x => x.Key)));
        var development = config.Roots().Where(r => r.Split is V4DatasetSplit.Train or V4DatasetSplit.Validation)
            .Select(g => V4EpisodePlanner.Plan(config, g, new(Guid.NewGuid()))).ToArray();
        Assert.Equal(development.Length, development.Select(d => d.Financial.Opening.Manifest.StartUtcMs).Distinct().Count());
        Assert.Equal(development.Length * 5, development.SelectMany(d => d.CampaignFingerprints).Distinct().Count());
        foreach (var design in development)
        {
            var run = design.Financial.Opening;
            var origin = run.Manifest.StartUtcMs;
            Assert.True(BehavioralEpisodeScenario.MatchesOpening(run, origin));
            Assert.False(BehavioralEpisodeScenario.MatchesOpening(run with { Manifest = run.Manifest with { V4EpisodeGenerator = null } }, origin));
            var scored = design.Financial.Payments.Zip(design.Truth.Payments)
                .Where(row => row.First.Intent.EventTimeUtcMs >= origin + V4EpisodeConfig.WarmupMs).ToArray();
            Assert.Equal(design.Truth.Payments.Count(p => p.Malicious), scored.Count(p => p.Second.Malicious));
            foreach (var offset in new[] { 1, 2 })
            {
                var day = scored.Where(row => (row.First.Intent.EventTimeUtcMs - origin) / V4EpisodeConfig.WarmupMs == offset);
                Assert.Contains(day, row => row.Second.Malicious);
                Assert.Contains(day, row => row.Second.HardNegative);
            }
            Assert.All(design.Financial.Payments, p => Assert.Equal(SyntheticTruth.Legitimate, p.Intent.Truth));
            var again = V4EpisodePlanner.Plan(config, design.Group, new(Guid.NewGuid()));
            Assert.Equal(design.HistoryFingerprint, again.HistoryFingerprint);
            Assert.Equal(run.Manifest.ContentHash, again.Financial.Opening.Manifest.ContentHash);
        }
    }

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
