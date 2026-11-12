using Marrow.Kernel.Canonical;
using Revenant.Observations;
using Revenant.Synthetic;
using Revenant.Timeline;
using Xunit;

namespace Revenant.Tests.Observations;

public sealed class V4CausalObservationTests
{
    [Fact]
    public void As_of_excludes_candidate_and_same_timestamp_suffix_in_fact_order()
    {
        var fixture = new ObservationFixture();
        var at = fixture.Origin + 123;
        var candidate = fixture.Intent("candidate", at, 10_001, sanctioned: true);
        var earlier = fixture.Intent("earlier-screened", at);
        fixture.Screen(earlier, at);
        fixture.Settle(earlier, at);
        var cutoff = fixture.State;
        fixture.Screen(candidate, at);

        var first = V4ObservationProjector.Project(fixture.History()).FraudAsOf.Single(x => x.IntentId == candidate.IntentId);
        Assert.Equal(cutoff.LastFact, first.CutoffFactSeq);
        Assert.Equal(cutoff.PrefixDigest, first.CutoffPrefixDigest);
        Assert.Equal(cutoff.Ledger.Accounts[candidate.SourceAccountId].PostedMinor, first.Inputs.SourceBalance.Posted);
        Assert.Equal(0, first.Inputs.SourceBalance.Reserved);
        Assert.Equal(0, first.Inputs.SinceOutgoingMs);
        Assert.True(first.Inputs.BeneficiarySeenBefore);
        Assert.Equal(1, first.Inputs.Windows[0].SubmittedCount);
        Assert.Equal(1, first.Inputs.Windows[0].NewBeneficiaryCount);
        Assert.Equal(0, first.Inputs.Windows[0].HeldCount);

        var future = fixture.Intent("future", at, 55_001, payee: 3);
        fixture.Screen(future, at);
        fixture.Settle(future, at);
        fixture.Seal();
        var replayed = V4ObservationProjector.Project(fixture.History()).FraudAsOf.Single(x => x.IntentId == candidate.IntentId);
        Assert.Equal(first.Encode(), replayed.Encode());
        Assert.Equal(first.Encode(), V4FraudAsOfObservation.Decode(first.Encode(), first.Digest).Encode());
        var trailing = first.Encode().Append((byte)0).ToArray();
        Assert.Throws<CanonicalFormatException>(() => V4FraudAsOfObservation.Decode(trailing, ObservationEncoding.Hash(trailing)));
    }

    [Fact]
    public void Account_queries_reconstruct_covered_zero_and_have_distinct_warmup_windows()
    {
        var fixture = new ObservationFixture();
        var p = fixture.Intent("early", fixture.Origin + 123);
        fixture.Screen(p, fixture.Origin + 123);
        fixture.Settle(p, fixture.Origin + 123);
        for (var i = 0; i < 288; i++) fixture.Seal();

        var p2 = fixture.Intent("day-two", fixture.State.LastSealedBoundaryMs, payee: 3);
        fixture.Screen(p2, fixture.State.LastSealedBoundaryMs);
        fixture.Seal();
        var projection = V4ObservationProjector.Project(fixture.History());
        Assert.Equal(3, projection.Accounts.Count);
        var five = projection.AccountAsOf(p.SourceAccountId, fixture.Origin + 300_000)!;
        var thirty = projection.AccountAsOf(p.SourceAccountId, fixture.Origin + 1_800_000)!;
        var day = projection.AccountAsOf(p.SourceAccountId, fixture.Origin + 86_400_000)!;
        Assert.Equal(ObservationCoverage.Complete, five.Windows[0].Coverage);
        Assert.Equal(ObservationCoverage.NotEnoughHistory, five.Windows[1].Coverage);
        Assert.Equal(ObservationCoverage.Complete, thirty.Windows[1].Coverage);
        Assert.Equal(ObservationCoverage.NotEnoughHistory, thirty.Windows[2].Coverage);
        Assert.All(day.Windows, w => Assert.Equal(ObservationCoverage.Complete, w.Coverage));
        Assert.Equal(0, thirty.Windows[0].OutgoingCount);
        Assert.Equal(1, thirty.Windows[1].OutgoingCount);
        Assert.Equal(1, day.Windows[^1].OutgoingCount);
        Assert.Equal(five.Balance, thirty.Balance);
        Assert.Equal(five.LastSuccessfulActivityMs, thirty.LastSuccessfulActivityMs);
        Assert.Null(projection.AccountAsOf(p.SourceAccountId, fixture.Origin));
        Assert.Null(projection.AccountAsOf(p.SourceAccountId, fixture.State.LastSealedBoundaryMs + 300_000));
        Assert.Equal(five.Encode(), V4AccountObservation.Decode(five.Encode(), five.Digest).Encode());
    }

    [Fact]
    public void Post_generation_truth_flip_cannot_change_the_financial_or_model_visible_bytes()
    {
        var fixture = new ObservationFixture();
        var p = fixture.Intent("same-instruction", fixture.Origin + 1);
        fixture.Screen(p, fixture.Origin + 1);
        fixture.Settle(p, fixture.Origin + 1);
        fixture.Seal();
        var input = fixture.History();
        var legitimate = new V4PaymentTruth("root", p.IntentId, false, null, null, false);
        var malicious = legitimate with { Malicious = true, Attack = EpisodeAttack.Dispersal, CampaignKey = "offline-campaign" };
        var before = Snapshot(input, legitimate);
        var after = Snapshot(input, malicious);
        Assert.NotEqual(before.Truth, after.Truth);
        Assert.Equal(before.Commands, after.Commands);
        Assert.Equal(before.Facts, after.Facts);
        Assert.Equal(before.Banks, after.Banks);
        Assert.Equal(before.Accounts, after.Accounts);
        Assert.Equal(before.Features, after.Features);

        static (string Truth, string Commands, string[] Facts, string[] Banks, string[] Accounts, string[] Features)
            Snapshot(V4ProjectionHistory history, V4PaymentTruth annotation)
        {
            var projection = V4ObservationProjector.Project(history);
            var facts = history.Segments.SelectMany(s => s.Facts).Select(f => f.Input).ToArray();
            var command = facts.Single(f => f.Kind == ObservedFactKind.PaymentIntent);
            Assert.Equal(Revenant.Contracts.SyntheticTruth.Legitimate,
                Revenant.Scenario.PaymentIntentCodec.Instance.Decode(command.Payload.AsSpan()).Truth);
            return (ObservationEncoding.Hash(annotation.Encode()), ObservationEncoding.Hash(command.Payload.AsSpan()),
                facts.Select(f => ObservationEncoding.Hash(ObservedFinanceInputCodec.Instance.EncodeCanonical(f))).ToArray(),
                projection.Banks.Select(x => x.Digest).ToArray(), projection.Accounts.Select(x => x.Digest).ToArray(),
                projection.FraudAsOf.Select(x => x.Digest).ToArray());
        }
    }
}
