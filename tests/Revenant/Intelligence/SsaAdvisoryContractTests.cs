using Revenant.Intelligence;
using Xunit;

namespace Revenant.Tests.Intelligence;

/// <summary>Managed checks of the pure R7/2 SSA contract arithmetic shared by research, qualification and the Worker.</summary>
public sealed class SsaAdvisoryContractTests
{
    [Theory]
    [InlineData(1, .05)]
    [InlineData(2, .05)]
    [InlineData(5, 1.8)]
    [InlineData(9, 5.4)]
    [InlineData(40, 30)]
    [InlineData(3, 12)]
    public void Poisson_surprise_matches_the_exact_upper_tail(long observed, double mean)
    {
        double below = 0, term = Math.Exp(-mean);
        for (var k = 0; k < observed; k++)
        {
            below += term;
            term *= mean / (k + 1);
        }
        var expected = -Math.Log10(1 - below);
        Assert.Equal(expected, SsaAdvisory.PoissonSurprise(observed, mean), 1e-9 * Math.Max(1, expected));
        Assert.Equal(0, SsaAdvisory.PoissonSurprise(0, mean));
    }

    [Fact]
    public void Poisson_surprise_ranks_a_burst_where_nothing_is_expected_above_a_routine_batch()
    {
        // Two intents where the learned expectation is 0.05 versus five where it is 1.8 (the measured generator-4 failure mode
        // of residuals in transformed units, which gave these nearly equal scores).
        Assert.True(SsaAdvisory.PoissonSurprise(2, .05) > SsaAdvisory.PoissonSurprise(5, 1.8) + 1);
        var anscombeQuiet = SsaAdvisory.AnomalyInput(SsaCountTransform.AnscombeZeroAnchored, 2) - 2 * Math.Sqrt(.05 + .375) + 2 * Math.Sqrt(.375);
        var anscombeBusy = SsaAdvisory.AnomalyInput(SsaCountTransform.AnscombeZeroAnchored, 5) - 2 * Math.Sqrt(1.8 + .375) + 2 * Math.Sqrt(.375);
        Assert.InRange(anscombeQuiet - anscombeBusy, -.2, .2);
    }

    [Fact]
    public void Anscombe_input_is_zero_anchored_and_its_expected_count_inverts_it()
    {
        Assert.Equal(0f, SsaAdvisory.AnomalyInput(SsaCountTransform.AnscombeZeroAnchored, 0));
        foreach (var c in new long[] { 1, 2, 7, 40 })
            Assert.Equal(c, SsaAdvisory.ExpectedCount(SsaCountTransform.AnscombeZeroAnchored,
                SsaAdvisory.AnomalyInput(SsaCountTransform.AnscombeZeroAnchored, c)), 1e-4);
    }

    [Fact]
    public void Change_representation_reconstructs_levels_from_the_current_completed_balance()
    {
        const long opening = 5_000_000;
        var scale = SsaAdvisory.Scale(opening, 1_000);
        Assert.Equal(5_000, scale);
        Assert.Equal(-2f, SsaAdvisory.ForecastInput(SsaForecastRepresentation.CompletedIntervalChange, opening, scale, 4_100_000, 4_090_000));
        float[] changes = [-2, -2, 0, 1.5f, 0, 0, 0, -0.25f];
        var (central, lower, upper) = SsaAdvisory.DecodeLevels(SsaForecastRepresentation.CompletedIntervalChange, opening, scale, 4_090_000,
            changes, changes.Select(c => c - 1).ToArray(), changes.Select(c => c + 1).ToArray());
        Assert.Equal([4_080_000, 4_070_000, 4_070_000, 4_077_500, 4_077_500, 4_077_500, 4_077_500, 4_076_250], central);
        Assert.Equal(central.Select((c, h) => c - (h + 1) * scale), lower);
        Assert.Equal(central.Select((c, h) => c + (h + 1) * scale), upper);
        var level = SsaAdvisory.DecodeLevels(SsaForecastRepresentation.OpeningCenteredLevel, opening, scale, 4_090_000,
            Enumerable.Repeat(-182f, 8).ToArray(), Enumerable.Repeat(-183f, 8).ToArray(), Enumerable.Repeat(-181f, 8).ToArray());
        Assert.All(level.Central, v => Assert.Equal(4_090_000, v));
    }

    [Fact]
    public void Local_scale_is_causal_and_floored_and_conformal_rank_is_finite_sample()
    {
        long[] levels = [1_000, 1_000, 900, 1_300, 1_300];
        Assert.Equal(33, SsaAdvisory.Sigma(levels, 1_000, 10, 2, 3));
        Assert.Equal(167, SsaAdvisory.Sigma(levels, 1_000, 10, 3, 3));
        Assert.Equal(10, SsaAdvisory.Sigma(levels, 1_000, 10, 1, 2));
        var errors = Enumerable.Range(1, 19).Select(i => (double)i).ToArray();
        Assert.Equal(19, SsaAdvisory.ConformalQuantile(errors, .95));
        Assert.Equal(18, SsaAdvisory.ConformalQuantile(errors, .9));
        Assert.Equal((4_000L - 250, 4_000L + 250), SsaAdvisory.Band(4_000, 100, 2.5));
    }

    [Fact]
    public void Specifications_enforce_the_ML_NET_training_rule_and_state_warm_up()
    {
        Assert.True(new SsaAnomalySpec(SsaCountTransform.Count, 288, 577, 32, 574, 8).IsValid);
        Assert.False(new SsaAnomalySpec(SsaCountTransform.Count, 288, 576, 32, 576, 8).IsValid);
        Assert.False(new SsaAnomalySpec(SsaCountTransform.Count, 288, 864, 32, 573, 8).IsValid);
        Assert.True(new SsaForecastSpec(SsaForecastRepresentation.CompletedIntervalChange, 24, 96, 2016, null, 46, 1_000).IsValid);
        Assert.False(new SsaForecastSpec(SsaForecastRepresentation.CompletedIntervalChange, 24, 96, 2016, 24, 288, 1_000).IsValid);
        Assert.False(new SsaAlertPolicy(SsaAlertStatistic.PoissonSurprise, 3, 0, 3, 0).IsValid);
        Assert.True(new SsaAlertPolicy(SsaAlertStatistic.PoissonSurprise, 3, 0, 3, .02).IsValid);
        Assert.False(new SsaAlertPolicy(SsaAlertStatistic.NativePValue, .01, 0).IsValid);
    }

    [Fact]
    public void Alert_state_sums_the_window_and_floors_the_expectation()
    {
        var state = new SsaAlertState(new(SsaAlertStatistic.PoissonSurprise, 3, 0, 3, .02));
        Assert.Equal(0, state.Next(0, 0, 0));
        Assert.Equal(SsaAdvisory.PoissonSurprise(1, .04 + .02), state.Next(1, .04, 0), 1e-12);
        Assert.Equal(SsaAdvisory.PoissonSurprise(3, .06 + .5), state.Next(2, .5, 0), 1e-12);
        Assert.Equal(SsaAdvisory.PoissonSurprise(3, .04 + .5 + .02), state.Next(0, double.NaN, 0), 1e-12);
    }
}
