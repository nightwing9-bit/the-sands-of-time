using Marrow.Kernel.Canonical;
using Revenant.Intelligence;
using Revenant.Observations;
using Revenant.Payments;
using Revenant.Synthetic;
using Revenant.Timeline;
using Xunit;

namespace Revenant.Tests.Observations;

public sealed class V4RiskFeatureTests
{
    [Fact]
    public void Same_timestamp_incoming_is_visible_and_later_facts_do_not_change_vector()
    {
        var fixture = new ObservationFixture();
        var at = fixture.Origin + 123;
        var payment = fixture.Intent("candidate", at, 9_000);
        var incoming = fixture.Intent("funds", at, 9_000, payer: 2, payee: 0);
        fixture.Screen(incoming, at);
        fixture.Settle(incoming, at);
        var live = V4ObservationProjector.RiskAt(fixture.History(), payment.IntentId, at);
        Assert.Equal(70, RiskFeatureVectorV2.Columns.Length);
        Assert.Equal("9b905e1f3f3a89e9627f0f970b46638d1a60aaa9c073b24690659a087b54e87f", RiskFeatureVectorV2.SchemaHash);
        Assert.Equal(0, Get(live, "submitted_count_5m"));
        Assert.Equal(1, Get(live, "settled_in_count_5m"));
        Assert.Equal(0, Get(live, "incoming_age"));
        Assert.Equal(0, Get(live, "incoming_missing"));
        Assert.Equal(1_000_000, Get(live, "forwarded_principal_match_30m"));
        Assert.Equal(0, Get(live, "complete_5m"));
        Assert.Equal(1, Get(live, "outgoing_missing"));

        fixture.Screen(payment, at);
        fixture.Settle(payment, at);
        var later = fixture.Intent("later", at, payer: 3, payee: 0);
        fixture.Screen(later, at);
        fixture.Settle(later, at);
        fixture.Seal();
        var replay = V4ObservationProjector.Project(fixture.History(), captureRisk: true).RiskAsOf
            .Single(r => r.IntentId == payment.IntentId);
        Assert.Equal(live.CutoffPrefixDigest, replay.CutoffPrefixDigest);
        Assert.Equal(live.Vector.Encode(), replay.Vector.Encode());
        Assert.Equal(live.Vector.Values, RiskFeatureVectorV2.Decode(live.Vector.Encode()).Values.AsEnumerable());
        Assert.Throws<InvalidOperationException>(() => V4ObservationProjector.RiskAt(fixture.History(), payment.IntentId, at));
        Assert.Throws<CanonicalFormatException>(() => RiskFeatureVectorV2.Decode(live.Vector.Encode().Append((byte)0).ToArray()));
        Assert.Throws<CanonicalFormatException>(() => RiskFeatureVectorV2.Decode(live.Vector.Encode()[..^1]));

        var ordinary = new V4PaymentTruth("root", payment.IntentId, false, null, null, false);
        var malicious = ordinary with { Malicious = true, Attack = EpisodeAttack.Ring, CampaignKey = "truth-only" };
        Assert.NotEqual(ObservationEncoding.Hash(ordinary.Encode()), ObservationEncoding.Hash(malicious.Encode()));
        Assert.Equal(live.Vector.Digest, V4ObservationProjector.Project(fixture.History(), captureRisk: true).RiskAsOf
            .Single(r => r.IntentId == malicious.IntentId).Vector.Digest);
    }

    [Fact]
    public void Diverging_suffixes_at_the_same_position_produce_their_own_coordination_features()
    {
        var source = new ObservationFixture();
        var child = new ObservationFixture();
        source.Seal();
        child.Seal();
        Assert.Equal(source.State.PrefixDigest, child.State.PrefixDigest);
        var inherited = child.History().Segments.Single();
        var fork = new V4ForkOrigin(source.Run.RunId, default, child.State.LastFact, child.State.LastSealedBoundaryMs,
            new string('0', 64), child.State.PrefixDigest, "test-fork");
        var at = source.State.LastSealedBoundaryMs;
        var incoming = source.Intent("source-only", at, payer: 2, payee: 0);
        source.Screen(incoming, at);
        source.Settle(incoming, at);
        var sourceCandidate = source.Intent("candidate", at);
        var childCandidate = child.Intent("candidate", at);
        var sourceRisk = V4ObservationProjector.RiskAt(source.History(), sourceCandidate.IntentId, at);
        var history = child.History();
        var suffix = history.Segments.Single();
        var forked = history with { Segments = [inherited, suffix with { Facts = suffix.Facts.Skip(1).ToArray(), Opening = fork }] };
        var childRisk = V4ObservationProjector.RiskAt(forked, childCandidate.IntentId, at);
        Assert.Equal(1, Get(sourceRisk, "settled_in_count_5m"));
        Assert.Equal(0, Get(childRisk, "settled_in_count_5m"));
        Assert.Equal(1, Get(childRisk, "incoming_missing"));
        Assert.Equal(1, Get(childRisk, "complete_5m"));
        Assert.Equal(0, Get(childRisk, "complete_30m"));
        Assert.NotEqual(sourceRisk.Vector.Digest, childRisk.Vector.Digest);
    }

    [Fact]
    public void Zero_available_balance_and_numeric_limits_are_explicit()
    {
        var fixture = new ObservationFixture();
        var at = fixture.Origin + 1;
        var principal = fixture.Run.Manifest.Accounts[0].OpeningBalance.MinorUnits;
        var drain = fixture.Intent("drain", at, principal);
        fixture.Screen(drain, at);
        fixture.Settle(drain, at);
        var candidate = fixture.Intent("zero-available", at);
        var risk = V4ObservationProjector.RiskAt(fixture.History(), candidate.IntentId, at);
        Assert.Equal(1, Get(risk, "zero_available"));
        Assert.Equal(0, Get(risk, "amount_to_available"));
        Assert.Equal(0, Get(risk, "available"));
        Assert.Equal(RiskFeatureVectorV2.MaxValue, RiskFeatureVectorV2.Minor(long.MaxValue));
        Assert.Equal(RiskFeatureVectorV2.MaxValue, RiskFeatureVectorV2.Ratio(long.MaxValue, 1));
        Assert.Throws<CanonicalFormatException>(() => new RiskFeatureVectorV2(risk.Vector.Values.SetItem(1, 0)));
        Assert.Throws<CanonicalFormatException>(() => new RiskFeatureVectorV2(risk.Vector.Values.RemoveAt(0)));
        var large = fixture.Intent("largest-v4-intent", at, long.MaxValue, payer: 2, payee: 0);
        var largeRisk = V4ObservationProjector.RiskAt(fixture.History(), large.IntentId, at);
        Assert.Equal(RiskFeatureVectorV2.MaxValue, Get(largeRisk, "amount"));
        Assert.Equal(RiskFeatureVectorV2.MaxValue, Get(largeRisk, "amount_to_available"));
        Assert.Equal(largeRisk.Vector.Values, largeRisk.Vector.ToModelInput().Select(x => checked((int)x)));
    }

    private static int Get(V4RiskAsOf row, string name) =>
        row.Vector.Values[RiskFeatureVectorV2.Columns.Select(c => c.Name).ToList().IndexOf(name)];
}
