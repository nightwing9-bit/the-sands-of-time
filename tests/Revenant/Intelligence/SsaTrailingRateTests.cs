using Revenant.Intelligence;
using Revenant.Observations;
using Xunit;

namespace Revenant.Tests.Intelligence;

public sealed class SsaTrailingRateTests
{
    private const int Origin = 15;
    private const long Opening = 12_000;
    private const long Scale = 12;

    [Theory]
    [InlineData("constant")]
    [InlineData("isolated debit")]
    [InlineData("sustained debit")]
    [InlineData("recovery")]
    public void Oracle_rates_invert_to_completed_levels(string scenario)
    {
        var levels = Enumerable.Range(0, 24).Select(i => scenario switch
        {
            "isolated debit" when i >= Origin => Opening - 1_440,
            "sustained debit" => Opening - 120 * (i + 1),
            "recovery" when i >= Origin - 5 && i < Origin => Opening - 1_440,
            _ => Opening,
        }).ToArray();
        var rates = Enumerable.Range(Origin + 1, 8).Select(i => SsaTrailingRate.Input(levels, Opening, Scale, i)).ToArray();
        var prefix = levels[..(Origin + 1)];
        Assert.Equal(levels[(Origin + 1)..], SsaTrailingRate.Decode(SsaTrailingRateDecoder.TrailingRateExactInverse,
            prefix, Scale, Origin, rates));
        var integrated = SsaTrailingRate.Decode(SsaTrailingRateDecoder.TrailingRateIntegratedExtrapolation,
            prefix, Scale, Origin, rates);
        if (scenario is "constant" or "sustained debit") Assert.Equal(levels[(Origin + 1)..], integrated);
        if (scenario == "isolated debit") Assert.Equal(levels[Origin] - 960, integrated[^1]);
        if (scenario == "recovery") Assert.Equal(levels[Origin] + 240, integrated[^1]);
    }

    [Fact]
    public void Completed_seal_prefix_and_zero_innovation_do_not_consume_future_levels()
    {
        var levels = Enumerable.Repeat(Opening, 24).ToArray();
        levels[Origin] = new ObservationBalance(Opening, 1_440).Available;
        levels[Origin + 1] = new ObservationBalance(Opening, 8_000).Available;
        Assert.Equal(-10f, SsaTrailingRate.Input(levels, Opening, Scale, Origin));
        var prefix = levels[..(Origin + 1)];
        var rates = Enumerable.Range(1, 8).Select(h => (float)SsaTrailingRate.ZeroInnovationRate(prefix, Scale, Origin, h)).ToArray();
        Assert.Equal(rates, Enumerable.Range(1, 8).Select(h => (float)SsaTrailingRate.ZeroInnovationRate(levels, Scale, Origin, h)));
        Assert.All(SsaTrailingRate.Decode(SsaTrailingRateDecoder.TrailingRateExactInverse, prefix, Scale, Origin, rates),
            level => Assert.Equal(levels[Origin], level));
        Assert.Throws<ArgumentOutOfRangeException>(() => SsaTrailingRate.Input(levels, Opening, Scale, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => SsaTrailingRate.Input(prefix, Opening, Scale, Origin + 1));
    }

    [Fact]
    public void Minor_units_round_to_even_and_invalid_arithmetic_fails_closed()
    {
        Assert.Equal(1, SsaAdvisory.Scale(999, 1_000));
        Assert.Equal(11L, SsaTrailingRate.AddMovement(11, .5));
        Assert.Equal(13L, SsaTrailingRate.AddMovement(11, 1.5));
        Assert.Throws<OverflowException>(() => SsaTrailingRate.Rate(long.MinValue, long.MaxValue, 1));
        Assert.Throws<OverflowException>(() => SsaTrailingRate.AddMovement(long.MaxValue, 1));
        Assert.Throws<InvalidDataException>(() => SsaTrailingRate.AddMovement(0, double.NaN));
        Assert.Throws<InvalidDataException>(() => SsaTrailingRate.Decode(SsaTrailingRateDecoder.TrailingRateExactInverse,
            new long[12], 1, 11, Enumerable.Repeat(float.PositiveInfinity, 8).ToArray()));
        var levels = Enumerable.Range(0, 24).Select(i => i <= Origin ? 19_100_000_000L - 143 * i : 17_000_000_000L + 127 * i).ToArray();
        var scale = SsaAdvisory.Scale(levels[0], 1_000);
        var rates = Enumerable.Range(Origin + 1, 8).Select(i => SsaTrailingRate.Input(levels, levels[0], scale, i)).ToArray();
        var decoded = SsaTrailingRate.Decode(SsaTrailingRateDecoder.TrailingRateExactInverse, levels, scale, Origin, rates);
        Assert.InRange(decoded.Zip(levels[(Origin + 1)..], (a, b) => Math.Abs(a - b)).Max(), 1, 150);
    }

    [Fact]
    public void L1_shrinkage_uses_absolute_movement_weights_and_lower_median_on_ties()
    {
        Assert.Equal(.2, SsaTrailingRate.FitWeight([(2, 10), (1, 1), (-1, -1), (100, 0)]));
        Assert.Equal(.25, SsaTrailingRate.FitWeight([(1, 4), (3, 4)]));
        Assert.Equal(1, SsaTrailingRate.FitWeight([(-11, -10)]));
        Assert.Equal(0, SsaTrailingRate.FitWeight([(-11, 10)]));
        Assert.Equal(0, SsaTrailingRate.FitWeight([(99, 0)]));
        Assert.Equal(975, SsaTrailingRate.Shrink(1_000, 900, .25));
    }
}
