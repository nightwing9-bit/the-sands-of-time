using Revenant.Intelligence;
using Xunit;

namespace Revenant.Tests.Intelligence;

public sealed class RiskFeaturesTests
{
    [Fact]
    public void Vector_follows_the_frozen_formulas_and_order()
    {
        var s = RiskFeaturesV1.Build("USD", 150_000, 300_000).Value;
        Assert.Equal((float)Math.Log(1501d), s.Vector.LogAmount);
        Assert.Equal((float)Math.Log(3001d), s.Vector.LogSourcePostedBefore);
        Assert.Equal(0.5f, s.Vector.TransferFraction);
        Assert.Equal(0f, s.Vector.WouldDrainPosted);
        Assert.Equal(4, s.Vector.ToArray().Length);
        Assert.Equal(["logAmount", "logSourcePostedBefore", "transferFraction", "wouldDrainPosted"], RiskFeaturesV1.FeatureNames);
    }

    [Fact]
    public void Zero_balance_cap_and_exact_drain_edges()
    {
        var empty = RiskFeaturesV1.Build("USD", 5_000, 0).Value.Vector; // fraction 5000 / max(0,100) = 50 -> capped at 10
        Assert.Equal(10f, empty.TransferFraction);
        Assert.Equal(0f, empty.WouldDrainPosted);
        Assert.Equal(0f, empty.LogSourcePostedBefore);
        Assert.Equal(1f, RiskFeaturesV1.Build("USD", 7_000, 7_000).Value.Vector.WouldDrainPosted);
        Assert.Equal(0f, RiskFeaturesV1.Build("USD", 6_999, 7_000).Value.Vector.WouldDrainPosted);
        Assert.Equal(1f, RiskFeaturesV1.Build("USD", 50, 40).Value.Vector.WouldDrainPosted);
        Assert.Equal(0.5f, RiskFeaturesV1.Build("USD", 50, 40).Value.Vector.TransferFraction); // 50 / max(40, 100): denominator floored at one major unit
    }

    [Fact]
    public void Canonical_raw_snapshot_is_integer_only_and_hash_is_pinned()
    {
        var a = RiskFeaturesV1.Build("USD", 150_000, 300_000).Value;
        var b = RiskFeaturesV1.Build("USD", 150_000, 300_000).Value;
        Assert.Equal(a.Raw.ToCanonicalBytes(), b.Raw.ToCanonicalBytes());
        Assert.Equal(a.FeatureHash, b.FeatureHash);
        Assert.NotEqual(a.FeatureHash, RiskFeaturesV1.Build("USD", 150_001, 300_000).Value.FeatureHash);
        // Pin recomputed independently with Python (sha256 of int32-length-prefixed UTF-8 strings and big-endian int64s).
        Assert.Equal(PinnedHash, a.FeatureHash);
    }

    private const string PinnedHash = "62e449c88dc85ae075cff53549eab2661d12ae49f1d55cd28ee71e38333790df";

    [Theory]
    [InlineData("EUR", 100L, 100L, RiskFeatureError.UnsupportedCurrency)]
    [InlineData("usd", 100L, 100L, RiskFeatureError.UnsupportedCurrency)]
    [InlineData("USD", 0L, 100L, RiskFeatureError.AmountNotPositive)]
    [InlineData("USD", -5L, 100L, RiskFeatureError.AmountNotPositive)]
    [InlineData("USD", 9_007_199_254_740_993L, 100L, RiskFeatureError.AmountOutOfRange)]
    [InlineData("USD", 100L, -1L, RiskFeatureError.SourcePostedBalanceInvalid)]
    [InlineData("USD", 100L, 9_007_199_254_740_993L, RiskFeatureError.SourcePostedBalanceInvalid)]
    public void Invalid_inputs_are_typed_errors_not_clamped(string currency, long amount, long posted, RiskFeatureError expected)
    {
        var r = RiskFeaturesV1.Build(currency, amount, posted);
        Assert.True(r.IsFailure);
        Assert.Equal(expected, r.Error);
    }

    [Fact]
    public void Ledger_entry_rejects_post_state_balances_and_balances_after_the_decision_boundary()
    {
        var boundary = new ScreeningBoundary(41);
        Assert.Equal(RiskFeatureError.BalanceNotPreScreening,
            RiskFeaturesV1.BuildFromLedger("USD", 100, new SourceBalanceObservation(900, BalanceMoment.AfterPayment, 40), boundary).Error);
        Assert.Equal(RiskFeatureError.BalanceAfterDecisionBoundary,
            RiskFeaturesV1.BuildFromLedger("USD", 100, new SourceBalanceObservation(900, BalanceMoment.BeforeScreening, 42), boundary).Error);
        Assert.True(RiskFeaturesV1.BuildFromLedger("USD", 100, new SourceBalanceObservation(900, BalanceMoment.BeforeScreening, 41), boundary).IsSuccess);
    }

    [Theory]
    [InlineData("1234.565", 123_457L)]
    [InlineData("1234.564", 123_456L)]
    [InlineData("0.005", 1L)]
    [InlineData("-0.005", -1L)]
    [InlineData("  10 ", 1_000L)]
    [InlineData("0", 0L)]
    public void PaySim_amounts_convert_at_100_minor_units_rounding_half_away_from_zero(string text, long expected)
    {
        Assert.True(RiskFeaturesV1.TryParseMinorUnits(text, out var minor));
        Assert.Equal(expected, minor);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1e3")]
    [InlineData("1,234.00")]
    [InlineData("1.2.3")]
    [InlineData("99999999999999999999")]
    public void Unparseable_amounts_are_refused(string text) => Assert.False(RiskFeaturesV1.TryParseMinorUnits(text, out _));

    [Fact]
    public void Model_input_allow_list_is_exactly_the_four_features_in_order()
    {
        Assert.Empty(RiskFeaturesV1.ValidateModelInputColumns([.. RiskFeaturesV1.FeatureNames]));
        Assert.NotEmpty(RiskFeaturesV1.ValidateModelInputColumns([.. RiskFeaturesV1.FeatureNames, "isFraud"]));
        Assert.Contains("forbidden predictor 'newbalanceOrig'", RiskFeaturesV1.ValidateModelInputColumns([.. RiskFeaturesV1.FeatureNames, "newbalanceOrig"]));
        Assert.NotEmpty(RiskFeaturesV1.ValidateModelInputColumns(["wouldDrainPosted", "logAmount", "logSourcePostedBefore", "transferFraction"]));
        Assert.NotEmpty(RiskFeaturesV1.ValidateModelInputColumns(["logAmount"]));
        Assert.NotEmpty(RiskFeaturesV1.ValidateModelInputColumns([.. RiskFeaturesV1.FeatureNames, "step"]));
    }
}
