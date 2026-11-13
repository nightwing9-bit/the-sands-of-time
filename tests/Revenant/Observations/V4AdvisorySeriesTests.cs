using Revenant.Finance;
using Revenant.Intelligence;
using Revenant.Observations;
using Revenant.Scenario;
using Revenant.Tests.Scenario;
using Revenant.Timeline;
using Xunit;

namespace Revenant.Tests.Observations;

public sealed class V4AdvisorySeriesTests
{
    private const long Bucket = ObservedFinanceVersion.BucketMs;

    [Fact]
    public void Committed_intake_counts_match_offline_series_and_live_seal_prefixes()
    {
        var fixture = new ObservationFixture();
        _ = fixture.Intent("first", fixture.Origin);
        _ = fixture.Intent("second", fixture.Origin + Bucket - 1, payer: 2, payee: 3);
        fixture.Seal();
        var firstLive = V4ObservationProjector.Project(fixture.History());
        _ = fixture.Intent("next", fixture.Origin + Bucket, payer: 2, payee: 3);
        fixture.Seal();
        var secondLive = V4ObservationProjector.Project(fixture.History());
        _ = fixture.Intent("unsealed", fixture.Origin + 2 * Bucket);

        var history = fixture.History();
        var accepted = history.Segments.SelectMany(s => s.Facts)
            .Where(f => f.Input.Kind == ObservedFactKind.PaymentIntent)
            .Select(f => PaymentIntentCodec.Instance.Decode(f.Input.Payload.AsSpan()))
            .ToLookup(p => (p.SourceAccountId, (p.EventTimeUtcMs - fixture.Origin) / Bucket));
        var offline = V4ObservationProjector.Project(history);
        foreach (var (live, bucketIndex) in new[] { firstLive, secondLive }.Select((p, i) => (p, i)))
        {
            var boundary = fixture.Origin + (bucketIndex + 1) * Bucket;
            foreach (var account in fixture.Run.Manifest.Accounts)
            {
                var accountId = account.AccountId;
                var liveSeries = live.AccountOutgoingSeries(accountId, boundary);
                Assert.Equal(accepted[(accountId, bucketIndex)].LongCount(), liveSeries.Points[^1].Value);
                Assert.Equal(liveSeries.Encode(), offline.AccountOutgoingSeries(accountId, boundary).Encode());
            }
        }
    }

    [Fact]
    public void Account_series_counts_committed_intakes_and_distinguishes_covered_zero_from_missing()
    {
        var fixture = new ObservationFixture();
        var origin = fixture.Origin;
        var first = origin + Bucket;
        var payer = fixture.Run.Manifest.Accounts[0];
        var pending = fixture.Intent("pending", origin);
        fixture.Seal();

        var firstPrefix = V4ObservationProjector.Project(fixture.History()).AccountOutgoingSeries(payer.AccountId, first);
        Assert.Equal(AdvisorySeriesCoverage.Complete, firstPrefix.Coverage);
        Assert.Equal(1, Assert.Single(firstPrefix.Points).Value);
        fixture.Screen(pending, first);
        fixture.Settle(pending, first);
        var rejected = fixture.Intent("accepted-but-rejected", first, 100_000_000, sanctioned: true);
        fixture.Screen(rejected, first);
        Assert.Equal(PaymentStatus.Rejected, fixture.State.Ledger.Payments[rejected.IntentId].Status);
        fixture.Seal();
        fixture.Seal();

        var projector = V4ObservationProjector.Project(fixture.History());
        var account = projector.AccountOutgoingSeries(payer.AccountId, first + 2 * Bucket);
        Assert.Equal([1L, 1L, 0L], account.Points.Select(p => p.Value));
        var bytes = account.Encode();
        var decoded = V4AdvisorySeries.Decode(bytes, account.Digest, account.ExecutionRunId);
        Assert.Equal(bytes, decoded.Encode());
        Assert.Equal(account.Origin, decoded.Origin);
        var trailing = bytes.Append((byte)0).ToArray();
        Assert.Throws<Marrow.Kernel.Canonical.CanonicalFormatException>(() =>
            V4AdvisorySeries.Decode(trailing, ObservationEncoding.Hash(trailing), account.ExecutionRunId));
        Assert.Equal(firstPrefix.Encode(), projector.AccountOutgoingSeries(payer.AccountId, first).Encode());
        Assert.Equal(firstPrefix.Digest, projector.AccountOutgoingSeries(payer.AccountId, first).Digest);
        Assert.Equal(AdvisorySeriesCoverage.UnobservedOrigin, projector.AccountOutgoingSeries(payer.AccountId, origin).Coverage);
        var unsealed = projector.AccountOutgoingSeries(payer.AccountId, first + 3 * Bucket);
        Assert.Equal(AdvisorySeriesCoverage.UnobservedOrigin, unsealed.Coverage);
        Assert.Null(unsealed.Origin);
        Assert.Equal(3, unsealed.Points.Length);
        Assert.Equal(AdvisorySeriesCoverage.UnknownSubject, projector.AccountOutgoingSeries("unknown", first).Coverage);

        // The hidden evaluation sidecar is never an argument to the projector or series builder.
        Assert.Equal(1, projector.AccountAsOf(payer.AccountId, first + Bucket)!.Windows[0].SubmittedCount);
    }

    [Fact]
    public void Bank_series_uses_available_balance_and_keeps_future_targets_separate()
    {
        var fixture = new ObservationFixture();
        var first = fixture.Origin + Bucket;
        var payer = fixture.Run.Manifest.Accounts[0];
        var bank = payer.BankId;
        fixture.Seal();
        var before = V4ObservationProjector.Project(fixture.History()).BankAvailableSeries(bank, first);

        var held = fixture.Intent("hold", first, 101_001, sanctioned: true);
        fixture.Screen(held, first);
        fixture.Seal();
        for (var i = 0; i < 7; i++) fixture.Seal();

        var projection = V4ObservationProjector.Project(fixture.History());
        var through = fixture.State.LastSealedBoundaryMs;
        var series = projection.BankAvailableSeries(bank, through);
        var authoritative = projection.Banks.Where(r => r.BankId == bank).ToArray();
        Assert.Equal(authoritative.Select(r => checked(r.Balance.Posted - r.Balance.Reserved)), series.Points.Select(p => p.Value));
        Assert.Equal(payer.OpeningBalance.MinorUnits + fixture.Run.Manifest.Accounts[1].OpeningBalance.MinorUnits, series.ReferenceMinor);
        Assert.Equal("USD", series.Unit);
        Assert.Equal(series.ReferenceMinor - held.Amount.MinorUnits, series.Points[1].Value);
        Assert.Equal(before.Encode(), projection.BankAvailableSeries(bank, first).Encode());
        Assert.Equal(AdvisorySeriesCoverage.UnknownSubject, projection.BankAvailableSeries("unknown", first).Coverage);

        var targets = V4BankTargets.Build(projection.Banks).Where(t => t.BankId == bank).ToArray();
        Assert.Equal(before.Origin, targets[0].Origin);
        Assert.True(targets[0].FullyCovered);
        Assert.Equal(authoritative.Skip(1).Select(r => r.Seal.BoundaryMs), targets[0].Horizons.Select(h => h!.BoundaryMs));
        Assert.Equal(authoritative.Skip(1).Select(r => r.Balance.Available), targets[0].Horizons.Select(h => h!.Balance.Available));
        Assert.Null(targets[1].Horizons[^1]);
        Assert.All(targets[^1].Horizons, h => Assert.Null(h));
    }

    [Fact]
    public void Child_series_has_the_same_inherited_bytes_and_its_own_suffix()
    {
        var source = new ObservationFixture();
        source.Seal();
        var inherited = Assert.Single(source.History().Segments);
        var cutoff = source.State.LastSealedBoundaryMs;
        var fork = new Revenant.Payments.V4ForkOrigin(source.Run.RunId, default, source.State.LastFact, cutoff,
            new string('0', 64), source.State.PrefixDigest, "r7-series-test");
        var childPhysical = new Revenant.Contracts.ScenarioRunId(Guid.NewGuid());

        var suffix = new ObservationFixture(BehavioralEpisodeScenarioTests.Opening(source.Run.RunId));
        suffix.Seal();
        var p = suffix.Intent("child-only", cutoff, sanctioned: true);
        suffix.Screen(p, cutoff);
        suffix.Seal();
        var child = suffix.History() with
        {
            ExecutionRunId = childPhysical,
            Segments = [inherited, new(suffix.Context, suffix.Facts.Skip(1).ToArray(), suffix.State.LastFact, fork)],
        };

        var sourceOnly = source.Intent("source-only", cutoff, payer: 2, payee: 4);
        source.Screen(sourceOnly, cutoff);
        source.Settle(sourceOnly, cutoff);
        source.Seal();
        var a = V4ObservationProjector.Project(source.History());
        var b = V4ObservationProjector.Project(child);
        Assert.Equal(a.AccountOutgoingSeries(p.SourceAccountId, cutoff).Encode(), b.AccountOutgoingSeries(p.SourceAccountId, cutoff).Encode());
        Assert.Equal(a.BankAvailableSeries(p.SourceBankId, cutoff).Encode(), b.BankAvailableSeries(p.SourceBankId, cutoff).Encode());
        Assert.Equal([0L, 0L], a.AccountOutgoingSeries(p.SourceAccountId, cutoff + Bucket).Points.Select(x => x.Value));
        Assert.Equal([0L, 1L], b.AccountOutgoingSeries(p.SourceAccountId, cutoff + Bucket).Points.Select(x => x.Value));
        Assert.Equal([0L, 0L], b.AccountOutgoingSeries(sourceOnly.SourceAccountId, cutoff + Bucket).Points.Select(x => x.Value));
        Assert.Equal(a.BankAvailableSeries(p.SourceBankId, cutoff + Bucket).Points[^1].Value - p.Amount.MinorUnits,
            b.BankAvailableSeries(p.SourceBankId, cutoff + Bucket).Points[^1].Value);
        Assert.All(b.BankAvailableSeries(p.SourceBankId, cutoff + Bucket).Points, x => Assert.Equal(childPhysical, x.Seal.ExecutionRunId));
    }

    [Fact]
    public void Opening_centering_preserves_small_deltas_and_checks_precision_and_overflow()
    {
        var nearLargeOpening = new AdvisoryBalanceNormalization(long.MaxValue - 1_000, 1_024);
        foreach (var delta in new[] { -1_000L, -1, 0, 1, 1_000 })
        {
            var available = nearLargeOpening.ReferenceMinor + delta;
            Assert.Equal(available, nearLargeOpening.Restore(nearLargeOpening.Normalize(available)));
        }
        var ordinary = new AdvisoryBalanceNormalization(5_000_000, 1_024);
        Assert.Equal(0, ordinary.Restore(ordinary.Normalize(0)));
        Assert.Equal(-1, ordinary.Restore(ordinary.Normalize(0) - 1f / 1_024));
        Assert.Throws<OverflowException>(() => nearLargeOpening.Restore(2));
        Assert.Throws<ArgumentOutOfRangeException>(() => ordinary.Restore(float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => ordinary.Normalize(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AdvisoryBalanceNormalization(0, 0));
    }
}
