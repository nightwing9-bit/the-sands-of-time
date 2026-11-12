using Marrow.Kernel.Canonical;
using Revenant.Observations;
using Revenant.Timeline;
using Xunit;

namespace Revenant.Tests.Observations;

public sealed class V4BankObservationTests
{
    [Fact]
    public void Seals_reconcile_reservations_settlements_returns_and_empty_intervals()
    {
        var fixture = new ObservationFixture();
        var time = fixture.Origin + 1;
        var cross = fixture.Intent("cross", time);
        fixture.Screen(cross, time);
        fixture.Settle(cross, time);
        var first = fixture.Origin + ObservedFinanceVersion.BucketMs;
        fixture.Seal();
        fixture.Return(cross, first);
        var hold = fixture.Intent("hold", first, sanctioned: true);
        fixture.Screen(hold, first);
        var same = fixture.Intent("same", first, payee: 1);
        fixture.Screen(same, first);
        fixture.Settle(same, first);
        _ = fixture.Intent("pending", first);
        fixture.Seal();
        fixture.Seal();

        var rows = V4ObservationProjector.Project(fixture.History()).Banks;
        Assert.Equal(18, rows.Count);
        var (a, b, zero) = (rows[0], rows[6], rows[12]);
        Assert.Equal((first, 1L, 0L, 5_000L), (a.Seal.BoundaryMs, a.Intents, a.ReturnedIn, a.SettledOut));
        Assert.Equal((3L, 1L, 1L, 5_000L, 5_000L, 0L, 5_000L, 5_000L),
            (b.Intents, b.ScreeningPending, b.Held, b.SettledIn, b.SettledOut, b.NetSettled, b.ReturnedIn, b.Reserved));
        Assert.Equal(ObservationCoverage.Complete, zero.Seal.Coverage);
        Assert.Equal((0L, 0L, 0L, 1L, 5_000L), (zero.Intents, zero.SettledOut, zero.Reserved, zero.ScreeningPending, zero.Balance.Reserved));
        Assert.Equal(fixture.Facts[3].PrefixDigest, a.Seal.PrefixDigest);
        var bytes = a.Encode();
        Assert.Equal(a, V4BankObservation.Decode(bytes, a.Digest));
        Reject(bytes[..^1]);
        Reject([..bytes, 0]);
        Reject(ObservationEncoding.Begin("unknown/1").ToArray());
        Assert.Throws<CanonicalFormatException>(() => V4BankObservation.Decode(bytes, new string('0', 64)));

        void Reject(byte[] invalid) =>
            Assert.Throws<CanonicalFormatException>(() => V4BankObservation.Decode(invalid, ObservationEncoding.Hash(invalid)));
    }
}
