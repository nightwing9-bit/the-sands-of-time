using FsCheck;
using FsCheck.Fluent;
using Marrow.Kernel.Canonical;
using Revenant.Contracts;
using Revenant.Scenario;
using Xunit;

namespace Revenant.Tests.Scenario;

/// <summary>Task 5.1. **Validates: Requirements R05.01, R05.08, R05.09, R05.10**</summary>
public sealed class PaymentIntentCodecTests
{
    private static readonly PaymentIntentCodec Codec = PaymentIntentCodec.Instance;

    private static PaymentIntent Sample() => FridayRingScenarioTests.Create(42, new ScenarioRunId(Guid.Parse("00000000-0000-0000-0000-0000000000aa"))).Intents[10];

    [Fact]
    public void Round_trip_preserves_every_field_and_is_byte_stable()
    {
        var intent = Sample();
        var bytes = Codec.EncodeCanonical(intent);

        Assert.Equal(intent, Codec.Decode(bytes));
        Assert.Equal(bytes, Codec.EncodeCanonical(Codec.Decode(bytes)));
    }

    [Fact]
    public void Decode_fails_closed_on_unknown_version_truncation_trailing_bytes_and_bad_fields()
    {
        var bytes = Codec.EncodeCanonical(Sample());

        var wrongVersion = (byte[])bytes.Clone();
        wrongVersion[3] = 2;
        Assert.Throws<CanonicalFormatException>(() => Codec.Decode(wrongVersion));
        Assert.Throws<CanonicalFormatException>(() => Codec.Decode(bytes.AsSpan(0, bytes.Length - 1)));
        Assert.Throws<CanonicalFormatException>(() => Codec.Decode([.. bytes, 0]));

        var badTruth = (byte[])bytes.Clone();
        badTruth[^3] = 9;
        Assert.Throws<CanonicalFormatException>(() => Codec.Decode(badTruth));

        var zeroAmount = new PaymentIntentBytes(bytes).WithLastInt64Before(3, 0);
        Assert.Throws<CanonicalFormatException>(() => Codec.Decode(zeroAmount));
    }

    [Fact]
    public void Facts_encoding_excludes_run_identity_but_canonical_encoding_includes_it()
    {
        var a = FridayRingScenarioTests.Create(42, new ScenarioRunId(Guid.Parse("00000000-0000-0000-0000-0000000000a1"))).Intents[3];
        var b = FridayRingScenarioTests.Create(42, new ScenarioRunId(Guid.Parse("00000000-0000-0000-0000-0000000000b2"))).Intents[3];

        Assert.Equal(PaymentIntentCodec.EncodeFacts(a), PaymentIntentCodec.EncodeFacts(b));
        Assert.NotEqual(Codec.EncodeCanonical(a), Codec.EncodeCanonical(b));
    }

    [Fact]
    public void Intent_rejects_invalid_invariants()
    {
        var run = new ScenarioRunId(Guid.NewGuid());
        var features = new PaymentFeatures(false, false);
        PaymentIntent Make(Money amount, string dest = "B/1", long seq = 1, ScenarioRunId? r = null) =>
            new(r ?? run, "x", seq, 0, "A", "A/1", "B", dest, amount, SyntheticTruth.Legitimate, features);

        Assert.NotNull(Make(Money.Usd(1)));
        Assert.Throws<ArgumentException>(() => Make(Money.Usd(0)));
        Assert.Throws<ArgumentException>(() => Make(default));
        Assert.Throws<ArgumentException>(() => Make(Money.Usd(1), dest: "A/1"));
        Assert.Throws<ArgumentOutOfRangeException>(() => Make(Money.Usd(1), seq: 0));
        Assert.Throws<ArgumentException>(() => Make(Money.Usd(1), r: default(ScenarioRunId)));
        Assert.Throws<ArgumentException>(() => new Money("usd", 1));
    }

    [Fact]
    public void Property_any_seed_and_two_distinct_runs_agree_on_facts_and_round_trip()
    {
        var gen =
            from seed in Gen.Choose(int.MinValue, int.MaxValue)
            from a in Gen.Choose(1, 1_000_000)
            from delta in Gen.Choose(1, 1_000_000)
            select (Seed: (long)seed, A: a, B: a + delta);

        Prop.ForAll(gen.ToArbitrary(), x =>
        {
            var runA = new ScenarioRunId(new Guid(x.A, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1));
            var runB = new ScenarioRunId(new Guid(x.B, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2));
            var a = FridayRingScenarioTests.Create(x.Seed, runA);
            var b = FridayRingScenarioTests.Create(x.Seed, runB);

            var sameFacts = a.ExogenousFactsHash == b.ExogenousFactsHash && a.Manifest.ContentHash == b.Manifest.ContentHash;
            var disjoint = !a.Intents.Select(i => i.RunScopedKey).Intersect(b.Intents.Select(i => i.RunScopedKey)).Any();
            var roundTrip = a.Intents.All(i => Codec.Decode(Codec.EncodeCanonical(i)) == i);
            var ring = a.Intents.Where(i => i.Truth == SyntheticTruth.FraudMuleRing).ToList();
            var diverges = ring.All(i => FridayRingScenarioTests.Velocity15m(a.Intents, i) < 8)
                && ring.Count(i => FridayRingScenarioTests.Velocity15m(a.Intents, i) >= 5) == 10
                && a.Intents.Where(i => i.Truth == SyntheticTruth.Legitimate)
                    .All(i => FridayRingScenarioTests.Velocity15m(a.Intents, i) < 5);
            return sameFacts && disjoint && roundTrip && diverges;
        }).Check(Config.QuickThrowOnFailure.WithMaxTest(100));
    }

    /// <summary>Test helper that patches a big-endian Int64 located a fixed number of trailing bytes from the end.</summary>
    private readonly struct PaymentIntentBytes(byte[] bytes)
    {
        // Tail layout: ... amount(int64, 8) truth(1) newBeneficiary(1) sanctionsHit(1).
        public byte[] WithLastInt64Before(int tailBytes, long value)
        {
            var copy = (byte[])bytes.Clone();
            System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(copy.AsSpan(copy.Length - tailBytes - 8, 8), value);
            return copy;
        }
    }
}
