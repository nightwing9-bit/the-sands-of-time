using Marrow.Kernel.Results;

namespace Revenant.Intelligence;

/// <summary>One scored example: the quantised integer score (PPM) and the generator/evaluation label. Labels are never model inputs.</summary>
public readonly record struct ScoredExample(int ScorePpm, bool Positive);

/// <summary>
/// Confusion counts and rates at one threshold on one cohort, plus threshold-free PR-AUC and ROC-AUC. A metric that is mathematically
/// undefined (no predicted positives, no positives, no negatives) is <c>null</c> AND named in <see cref="Undefined"/>; it is never
/// reported as 0 or 1. Everything is computed from integer PPM scores and integer counts only.
/// </summary>
public sealed record RiskMetrics(
    string Cohort,
    int? ThresholdPpm,
    long Rows,
    long Positives,
    long Negatives,
    double? Prevalence,
    long TruePositives,
    long FalsePositives,
    long TrueNegatives,
    long FalseNegatives,
    double? Precision,
    double? Recall,
    double? F1,
    double? FalsePositiveRate,
    double? HoldRate,
    double? PrAuc,
    double? RocAuc,
    string[] Undefined);

public enum ThresholdError
{
    ValidationClassMissing = 1,
    NoCandidateCatchesAPositive = 2,
}

/// <summary>The pinned threshold and how it was chosen, for the manifest. Selected on validation labels only.</summary>
public sealed record RiskThresholdSelection(
    string Rule,
    int ThresholdPpm,
    long CandidateCount,
    long ValidationTruePositives,
    long ValidationFalsePositives,
    long ValidationFalseNegatives,
    double ValidationPrecision,
    double ValidationRecall,
    double ValidationF1);

/// <summary>Pure metric and threshold code. No ML.NET, no floats in any decision: ties are resolved by exact integer cross-multiplication.</summary>
public static class RiskEvaluation
{
    public const string ThresholdRule =
        "maximize validation F1 over distinct quantised validation scores >= 1; ties: higher precision, then higher threshold; predicted hold iff scorePpm >= thresholdPpm";

    /// <summary>
    /// Chooses <c>thresholdPpm</c> from VALIDATION examples only. Candidates are the distinct validation scores in [1, 1,000,000].
    /// F1 = 2TP/(2TP+FP+FN) and precision = TP/(TP+FP) are compared as exact fractions.
    /// </summary>
    public static Result<RiskThresholdSelection, ThresholdError> SelectThreshold(IReadOnlyList<ScoredExample> validation)
    {
        ArgumentNullException.ThrowIfNull(validation);
        var positives = validation.LongCount(e => e.Positive);
        if (positives == 0 || positives == validation.Count)
        {
            return Result<RiskThresholdSelection, ThresholdError>.Failure(ThresholdError.ValidationClassMissing);
        }

        var candidates = validation.Select(e => e.ScorePpm).Where(s => s >= 1 && s <= RiskScoreNormalization.PpmScale).Distinct().OrderDescending().ToArray();
        var ordered = validation.OrderByDescending(e => e.ScorePpm).ToArray();
        long tp = 0, fp = 0, cursor = 0;
        (long Tp, long Fp, long Fn, int T)? best = null;
        foreach (var t in candidates)
        {
            while (cursor < ordered.Length && ordered[cursor].ScorePpm >= t)
            {
                if (ordered[cursor].Positive) { tp++; } else { fp++; }
                cursor++;
            }
            var fn = positives - tp;
            if (best is null || IsBetter(tp, fp, fn, t, best.Value))
            {
                best = (tp, fp, fn, t);
            }
        }

        if (best is not { Tp: > 0 } b)
        {
            return Result<RiskThresholdSelection, ThresholdError>.Failure(ThresholdError.NoCandidateCatchesAPositive);
        }
        return Result<RiskThresholdSelection, ThresholdError>.Success(new RiskThresholdSelection(
            ThresholdRule, b.T, candidates.LongLength, b.Tp, b.Fp, b.Fn,
            (double)b.Tp / (b.Tp + b.Fp), (double)b.Tp / (b.Tp + b.Fn), 2d * b.Tp / (2 * b.Tp + b.Fp + b.Fn)));
    }

    // Candidates arrive from the highest threshold downwards, so on an exact tie of F1 and precision the incumbent has the higher threshold.
    private static bool IsBetter(long tp, long fp, long fn, int threshold, (long Tp, long Fp, long Fn, int T) best)
    {
        // F1 = 2tp / (2tp + fp + fn); compare a/b > c/d as a*d > c*b (all terms are non-negative and well inside long).
        var left = 2 * tp * (2 * best.Tp + best.Fp + best.Fn);
        var right = 2 * best.Tp * (2 * tp + fp + fn);
        if (left != right)
        {
            return left > right;
        }
        // Equal F1: higher precision wins. tp/(tp+fp) vs best.Tp/(best.Tp+best.Fp).
        var pLeft = tp * (best.Tp + best.Fp);
        var pRight = best.Tp * (tp + fp);
        if (pLeft != pRight)
        {
            return pLeft > pRight;
        }
        return threshold > best.T;
    }

    /// <summary>Computes the metrics of <paramref name="examples"/> at <paramref name="thresholdPpm"/> (hold iff score &gt;= threshold).</summary>
    public static RiskMetrics Compute(string cohort, IReadOnlyList<ScoredExample> examples, int thresholdPpm)
    {
        ArgumentNullException.ThrowIfNull(examples);
        long tp = 0, fp = 0, tn = 0, fn = 0;
        foreach (var e in examples)
        {
            var hold = RiskScoreNormalization.AtOrAboveThreshold(e.ScorePpm, thresholdPpm);
            if (e.Positive) { if (hold) { tp++; } else { fn++; } }
            else { if (hold) { fp++; } else { tn++; } }
        }
        return Build(cohort, thresholdPpm, tp, fp, tn, fn, PrAuc(examples), RocAuc(examples));
    }

    /// <summary>Average precision (step-wise area under the precision-recall curve, tied scores grouped). Null when there is no positive.</summary>
    public static double? PrAuc(IReadOnlyList<ScoredExample> examples)
    {
        var positives = examples.LongCount(e => e.Positive);
        if (positives == 0)
        {
            return null;
        }
        double area = 0;
        long tp = 0, fp = 0;
        double previousRecall = 0;
        foreach (var group in examples.GroupBy(e => e.ScorePpm).OrderByDescending(g => g.Key))
        {
            foreach (var e in group)
            {
                if (e.Positive) { tp++; } else { fp++; }
            }
            var recall = (double)tp / positives;
            area += (recall - previousRecall) * ((double)tp / (tp + fp));
            previousRecall = recall;
        }
        return area;
    }

    /// <summary>ROC-AUC as P(score of a positive &gt; score of a negative) + half of the ties. Null unless both classes are present.</summary>
    public static double? RocAuc(IReadOnlyList<ScoredExample> examples)
    {
        var positives = examples.LongCount(e => e.Positive);
        var negatives = examples.Count - positives;
        if (positives == 0 || negatives == 0)
        {
            return null;
        }
        double wins = 0; // twice the favourable pair count, so ties add exactly 1
        long negativesBelow = 0;
        foreach (var group in examples.GroupBy(e => e.ScorePpm).OrderBy(g => g.Key))
        {
            var p = group.LongCount(e => e.Positive);
            var n = group.LongCount() - p;
            wins += 2d * p * negativesBelow + (double)p * n;
            negativesBelow += n;
        }
        return wins / (2d * positives * negatives);
    }

    /// <summary>Metrics of a fixed, parameter-free classifier given as a 0/1 prediction per example (used for the baselines).</summary>
    public static RiskMetrics ComputeBinary(string cohort, IReadOnlyList<(bool Predicted, bool Positive)> examples)
    {
        ArgumentNullException.ThrowIfNull(examples);
        var scored = examples.Select(e => new ScoredExample(e.Predicted ? RiskScoreNormalization.PpmScale : 0, e.Positive)).ToArray();
        long tp = 0, fp = 0, tn = 0, fn = 0;
        foreach (var (predicted, positive) in examples)
        {
            if (positive) { if (predicted) { tp++; } else { fn++; } }
            else { if (predicted) { fp++; } else { tn++; } }
        }
        return Build(cohort, null, tp, fp, tn, fn, PrAuc(scored), RocAuc(scored));
    }

    private static RiskMetrics Build(string cohort, int? threshold, long tp, long fp, long tn, long fn, double? prAuc, double? rocAuc)
    {
        var rows = tp + fp + tn + fn;
        var positives = tp + fn;
        var negatives = fp + tn;
        var undefined = new List<string>();
        double? Ratio(string name, long numerator, long denominator)
        {
            if (denominator == 0)
            {
                undefined.Add(name);
                return null;
            }
            return (double)numerator / denominator;
        }
        var prevalence = Ratio("prevalence", positives, rows);
        var precision = Ratio("precision", tp, tp + fp);
        var recall = Ratio("recall", tp, positives);
        var f1 = Ratio("f1", 2 * tp, 2 * tp + fp + fn);
        var fpr = Ratio("falsePositiveRate", fp, negatives);
        var holdRate = Ratio("holdRate", tp + fp, rows);
        if (prAuc is null) { undefined.Add("prAuc"); }
        if (rocAuc is null) { undefined.Add("rocAuc"); }
        return new RiskMetrics(cohort, threshold, rows, positives, negatives, prevalence, tp, fp, tn, fn, precision, recall, f1, fpr, holdRate, prAuc, rocAuc, [.. undefined]);
    }
}
