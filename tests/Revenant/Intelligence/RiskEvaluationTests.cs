using Revenant.Intelligence;
using Xunit;

namespace Revenant.Tests.Intelligence;

/// <summary>Pure metric and threshold arithmetic against hand-computed values (Spec 08 task 8.2). No ML.NET.</summary>
public sealed class RiskEvaluationTests
{
    private static ScoredExample E(int score, bool positive) => new(score, positive);

    // Sorted by score: 900 T, 800 F, 700 T, 600 F, 500 T.
    private static readonly ScoredExample[] Worked = [E(900, true), E(800, false), E(700, true), E(600, false), E(500, true)];

    [Fact]
    public void Confusion_precision_recall_f1_and_prevalence_at_a_threshold_match_hand_computation()
    {
        var m = RiskEvaluation.Compute("worked", Worked, 700);

        // hold iff score >= 700: 900 T, 800 F, 700 T are held; 600 F and 500 T are not.
        Assert.Equal((2L, 1L, 1L, 1L), (m.TruePositives, m.FalsePositives, m.TrueNegatives, m.FalseNegatives));
        Assert.Equal(0.6, m.Prevalence);
        Assert.Equal(2d / 3, m.Precision!.Value, 12);
        Assert.Equal(2d / 3, m.Recall!.Value, 12);
        Assert.Equal(2d / 3, m.F1!.Value, 12);
        Assert.Equal(0.5, m.FalsePositiveRate);
        Assert.Equal(0.6, m.HoldRate);
        Assert.Empty(m.Undefined);
    }

    [Fact]
    public void Pr_auc_and_roc_auc_match_hand_computation()
    {
        // Average precision: recall steps of 1/3 at precision 1, 2/3 and 3/5 = (1 + 2/3 + 3/5) / 3 = 34/45.
        Assert.Equal(34d / 45, RiskEvaluation.PrAuc(Worked)!.Value, 12);
        // Pairs (positive > negative): 900 beats 800 and 600, 700 beats 600, 500 beats none = 3 of 6.
        Assert.Equal(0.5, RiskEvaluation.RocAuc(Worked)!.Value, 12);
    }

    [Fact]
    public void Tied_scores_are_grouped_not_ordered_arbitrarily()
    {
        ScoredExample[] tied = [E(5, true), E(5, false)];
        Assert.Equal(0.5, RiskEvaluation.RocAuc(tied)!.Value);
        Assert.Equal(0.5, RiskEvaluation.PrAuc(tied)!.Value);
        ScoredExample[] reversed = [tied[1], tied[0]];
        Assert.Equal(RiskEvaluation.PrAuc(tied), RiskEvaluation.PrAuc(reversed));
    }

    [Fact]
    public void Threshold_edge_is_inclusive()
    {
        var atEdge = RiskEvaluation.Compute("edge", [E(257_796, true), E(257_795, true)], 257_796);
        Assert.Equal((1L, 1L), (atEdge.TruePositives, atEdge.FalseNegatives));
        var all = RiskEvaluation.Compute("all", Worked, 1);
        Assert.Equal(5, all.TruePositives + all.FalsePositives);
        var none = RiskEvaluation.Compute("none", Worked, RiskScoreNormalization.PpmScale);
        Assert.Equal(0, none.TruePositives + none.FalsePositives);
    }

    [Fact]
    public void Undefined_metrics_are_null_and_named_never_zero()
    {
        var noPositives = RiskEvaluation.Compute("no positives", [E(10, false), E(20, false)], 25);
        Assert.Null(noPositives.PrAuc);
        Assert.Null(noPositives.RocAuc);
        Assert.Null(noPositives.Recall);
        Assert.Null(noPositives.Precision);
        Assert.Contains("prAuc", noPositives.Undefined);
        Assert.Contains("rocAuc", noPositives.Undefined);
        Assert.Contains("recall", noPositives.Undefined);

        var noNegatives = RiskEvaluation.Compute("no negatives", [E(10, true), E(20, true)], 15);
        Assert.Null(noNegatives.RocAuc);
        Assert.Null(noNegatives.FalsePositiveRate);
        Assert.NotNull(noNegatives.PrAuc);

        var noneHeld = RiskEvaluation.Compute("none held", Worked, RiskScoreNormalization.PpmScale);
        Assert.Null(noneHeld.Precision);
        Assert.Contains("precision", noneHeld.Undefined);

        var empty = RiskEvaluation.Compute("empty", [], 5);
        Assert.Null(empty.Prevalence);
    }

    [Fact]
    public void Threshold_maximises_validation_f1()
    {
        // F1 by candidate: 900 -> 1/2, 800 -> 2/5, 700 -> 2/3, 600 -> 4/7, 500 -> 3/4.
        var selected = RiskEvaluation.SelectThreshold(Worked).Value;
        Assert.Equal(500, selected.ThresholdPpm);
        Assert.Equal(5, selected.CandidateCount);
        Assert.Equal((3L, 2L, 0L), (selected.ValidationTruePositives, selected.ValidationFalsePositives, selected.ValidationFalseNegatives));
        Assert.Equal(0.75, selected.ValidationF1, 12);
    }

    [Fact]
    public void Equal_f1_is_broken_by_higher_precision()
    {
        // 900 T, 600 F, 550 F, 500 T. Threshold 900: tp1 fp0 fn1 -> F1 2/3, precision 1. Threshold 500: tp2 fp2 fn0 -> F1 4/6 = 2/3, precision 1/2.
        var selected = RiskEvaluation.SelectThreshold([E(900, true), E(600, false), E(550, false), E(500, true)]).Value;
        Assert.Equal(900, selected.ThresholdPpm);
        Assert.Equal(1d, selected.ValidationPrecision);
    }

    [Fact]
    public void Only_distinct_scores_of_at_least_one_are_candidates_and_never_zero()
    {
        // The positive at 0 can never be caught by a legal threshold (>= 1); the other positive is.
        var selected = RiskEvaluation.SelectThreshold([E(0, true), E(0, false), E(40, true), E(10, false)]).Value;
        Assert.True(selected.ThresholdPpm >= 1);
        Assert.Equal(40, selected.ThresholdPpm);
        Assert.Equal(2, selected.CandidateCount);
    }

    [Fact]
    public void Threshold_selection_refuses_one_class_validation_and_hopeless_scores()
    {
        Assert.Equal(ThresholdError.ValidationClassMissing, RiskEvaluation.SelectThreshold([E(10, false), E(20, false)]).Error);
        Assert.Equal(ThresholdError.ValidationClassMissing, RiskEvaluation.SelectThreshold([E(10, true)]).Error);
        Assert.Equal(ThresholdError.NoCandidateCatchesAPositive, RiskEvaluation.SelectThreshold([E(0, true), E(30, false)]).Error);
    }

    [Fact]
    public void Threshold_selection_is_order_independent()
    {
        var a = RiskEvaluation.SelectThreshold(Worked).Value;
        var b = RiskEvaluation.SelectThreshold([.. Worked.Reverse()]).Value;
        Assert.Equal(a, b);
    }
}
