using Revenant.Contracts;
using Revenant.Intelligence;
using Xunit;

namespace Revenant.Tests.Intelligence;

public sealed class InferenceEvidenceTests
{
    private static readonly ScenarioRunId Run = new(Guid.Parse("11111111-2222-3333-4444-555555555555"));
    private static readonly string Sha = new('a', 64);

    private static InferenceEvidenceV1 Evidence(int score = 500_000, int threshold = 500_000, ScreeningDecisionKind baseDecision = ScreeningDecisionKind.Clear,
        long asOf = 41, long amount = 150_000, long posted = 300_000) =>
        new(Run, "intent-7", asOf, asOf + 1, RiskFeaturesV1.Build("USD", amount, posted).Value.Raw, "risk-fasttree-fixture-1", Sha, new string('b', 64),
            "5.0.1", "v7-base+ml-risk-v1", threshold, score, baseDecision);

    [Theory]
    [InlineData(0f, 0)]
    [InlineData(1f, 1_000_000)]
    [InlineData(0.5f, 500_000)]
    [InlineData(0.0078125f, 7_813)]   // 1/128 * 1e6 = 7812.5 exactly: half away from zero gives 7813 (round-half-even would give 7812)
    [InlineData(0.0234375f, 23_438)]  // 3/128 * 1e6 = 23437.5 exactly
    [InlineData(0.99884903f, 998_849)]
    public void Probability_converts_to_ppm_exactly_with_half_away_from_zero(float p, int expected)
    {
        var r = RiskScoreNormalization.FromProbability(p);
        Assert.True(r.IsSuccess);
        Assert.Equal(expected, r.Value);
    }

    [Theory]
    [InlineData(float.NaN, PpmError.NotFinite)]
    [InlineData(float.PositiveInfinity, PpmError.NotFinite)]
    [InlineData(float.NegativeInfinity, PpmError.NotFinite)]
    [InlineData(-0.0000001f, PpmError.OutOfUnitRange)]
    [InlineData(1.0000001f, PpmError.OutOfUnitRange)]
    [InlineData(2f, PpmError.OutOfUnitRange)]
    public void Non_finite_and_out_of_range_probabilities_are_rejected_never_clamped(float p, PpmError expected)
    {
        var r = RiskScoreNormalization.FromProbability(p);
        Assert.True(r.IsFailure);
        Assert.Equal(expected, r.Error);
    }

    [Fact]
    public void Threshold_tie_holds_and_the_decision_precedence_is_fixed()
    {
        Assert.True(RiskScoreNormalization.AtOrAboveThreshold(500_000, 500_000));
        Assert.False(RiskScoreNormalization.AtOrAboveThreshold(499_999, 500_000));

        Assert.Equal(ScreeningDecisionKind.Held, Evidence(score: 500_000).FinalDecision);
        Assert.Equal(ScreeningDecisionKind.Clear, Evidence(score: 499_999).FinalDecision);
        Assert.Equal(ScreeningDecisionKind.Rejected, Evidence(score: 1_000_000, baseDecision: ScreeningDecisionKind.Rejected).FinalDecision);
        Assert.Equal(ScreeningDecisionKind.Held, Evidence(score: 0, baseDecision: ScreeningDecisionKind.Held).FinalDecision);
        Assert.True(Evidence(score: 0, baseDecision: ScreeningDecisionKind.Held).FinalDecision == ScreeningDecisionKind.Held); // a low score never releases
    }

    [Fact]
    public void Canonical_bytes_round_trip_and_hash_is_stable()
    {
        var e = Evidence();
        var decoded = InferenceEvidenceV1.Decode(e.ToCanonicalBytes());
        Assert.True(decoded.IsSuccess);
        Assert.Equal(e.ToCanonicalBytes(), decoded.Value.ToCanonicalBytes());
        Assert.Equal(e.EvidenceHash, decoded.Value.EvidenceHash);
        Assert.Equal(e.Features.FeatureHash, e.FeatureHash);
        Assert.NotEqual(e.EvidenceHash, Evidence(score: 500_001).EvidenceHash);
        Assert.NotEqual(e.EvidenceHash, Evidence(asOf: 42).EvidenceHash);
    }

    [Fact]
    public void Decode_detects_tampering_truncation_trailing_bytes_and_inconsistent_derived_fields()
    {
        var bytes = Evidence().ToCanonicalBytes();
        Assert.Equal(EvidenceError.Malformed, InferenceEvidenceV1.Decode(bytes[..^1]).Error);
        Assert.Equal(EvidenceError.Malformed, InferenceEvidenceV1.Decode([.. bytes, 0]).Error);
        Assert.Equal(EvidenceError.Malformed, InferenceEvidenceV1.Decode([]).Error);

        var wrongFormat = (byte[])bytes.Clone();
        wrongFormat[3] = 2; // int32 format version 1 -> 2 (big-endian low byte; checked below)
        Assert.True(InferenceEvidenceV1.Decode(wrongFormat).IsFailure);

        // The last three bytes are: learned hold flag, final decision. Flip each independently.
        var flippedHold = (byte[])bytes.Clone();
        flippedHold[^2] ^= 1;
        Assert.Equal(EvidenceError.DecisionInconsistent, InferenceEvidenceV1.Decode(flippedHold).Error);
        var flippedFinal = (byte[])bytes.Clone();
        flippedFinal[^1] = (byte)ScreeningDecisionKind.Clear;
        Assert.Equal(EvidenceError.DecisionInconsistent, InferenceEvidenceV1.Decode(flippedFinal).Error);

        // Change the stored amount (raw integer) without updating the stored feature hash.
        var amountBytes = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(amountBytes, 150_000L);
        var index = IndexOf(bytes, amountBytes);
        Assert.True(index > 0);
        var tampered = (byte[])bytes.Clone();
        tampered[index + 7] ^= 1;
        Assert.Equal(EvidenceError.FeatureHashMismatch, InferenceEvidenceV1.Decode(tampered).Error);
    }

    [Fact]
    public void Construction_rejects_invalid_identity_sequence_threshold_and_score()
    {
        var raw = RiskFeaturesV1.Build("USD", 100, 100).Value.Raw;
        InferenceEvidenceV1 Make(ScenarioRunId run, long asOf, long screening, string artifact, int threshold, int score) =>
            new(run, "i", asOf, screening, raw, "m", artifact, Sha, "5.0.1", "p", threshold, score, ScreeningDecisionKind.Clear);
        Assert.Throws<ArgumentException>(() => Make(default, 1, 2, Sha, 1, 1));
        Assert.Throws<ArgumentException>(() => Make(Run, 1, 3, Sha, 1, 1));            // screening fact must directly follow the boundary
        Assert.Throws<ArgumentException>(() => Make(Run, 1, 2, "ABC", 1, 1));          // not lower-case hex sha256
        Assert.Throws<ArgumentOutOfRangeException>(() => Make(Run, 1, 2, Sha, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Make(Run, 1, 2, Sha, 1_000_001, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Make(Run, 1, 2, Sha, 1, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Make(Run, 1, 2, Sha, 1, 1_000_001));
    }

    [Fact]
    public void Evidence_binds_only_to_the_exact_intent_it_describes()
    {
        PaymentIntent Intent(ScenarioRunId run, string id, string currency, long minor) => new(
            run, id, 1, 0, "BANK-A", "ACC-1", "BANK-B", "ACC-2", new Money(currency, minor), SyntheticTruth.Legitimate, new PaymentFeatures(false, false));
        var e = Evidence();
        Assert.True(e.CheckBinds(Intent(Run, "intent-7", "USD", 150_000)).IsSuccess);
        Assert.Equal(EvidenceError.IdentityMismatch, e.CheckBinds(Intent(new ScenarioRunId(Guid.NewGuid()), "intent-7", "USD", 150_000)).Error);
        Assert.Equal(EvidenceError.IdentityMismatch, e.CheckBinds(Intent(Run, "intent-8", "USD", 150_000)).Error);
        Assert.Equal(EvidenceError.IdentityMismatch, e.CheckBinds(Intent(Run, "intent-7", "USD", 150_001)).Error);
        Assert.Equal(EvidenceError.IdentityMismatch, e.CheckBinds(Intent(Run, "intent-7", "EUR", 150_000)).Error);
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
            {
                return i;
            }
        }
        return -1;
    }
}
