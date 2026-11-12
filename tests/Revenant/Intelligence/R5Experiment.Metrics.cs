using Revenant.Finance;
using Revenant.Synthetic;

namespace Revenant.Tests.Intelligence;

internal sealed record R5Ranking(double AveragePrecision, double RocAuc);
internal sealed record R5Review(int Opportunities, int LegalRejections, int MandatoryHolds, int BudgetSlots,
    int ExtraReviews, int Reviewed, int MaliciousReviewed, int AdditionalMalicious, int LegitimateReviewed,
    int HardNegativesReviewed, int HardNegatives, int Malicious, double ReviewRate, double Recall, double Precision,
    bool MandatoryOverload, bool BudgetExceeded, IReadOnlyDictionary<string, R5Stratum> Families,
    IReadOnlyDictionary<string, R5Stratum> Roots, IReadOnlyDictionary<string, R5Stratum> Banks);
internal sealed record R5Stratum(int Opportunities, int Malicious, int Reviewed, int MaliciousReviewed, int LegitimateReviewed,
    int HardNegativeReviewed, int Ring, int RingReviewed);
internal sealed record R5Baseline(int Percent, R5Review V7, R5Review Heuristic, R5Review V7Heuristic);
internal sealed record R5CandidateResult(R5Configuration Config, R5Ranking Ranking, R5Review Intrinsic, R5Review Combined,
    int Rings, int Families, int RootsWithMaliciousReview);
internal sealed record R5Reliability(int Band, int Count, double MeanProbability, double ObservedPrevalence);
internal sealed record R5ThresholdReport(int Ppm, R5Review Review, double F1, double Brier, double LogLoss,
    R5Reliability[] Reliability, IReadOnlyDictionary<string, R5Stratum> EpisodeDays, int PeakFiveMinuteReviews,
    int PeakFiveMinuteAdditionalReviews);

internal static partial class R5Experiment
{
    internal static readonly int[] Budgets = [2, 5, 10];

    private static R5Ranking Ranking(IReadOnlyList<V4RiskResearchRow> rows, Func<V4RiskResearchRow, double> score)
    {
        var positives = rows.Count(IsMalicious);
        var negatives = rows.Count - positives;
        if (positives == 0 || negatives == 0) throw new InvalidOperationException("Ranking requires both classes.");
        double ap = 0, auc = 0;
        var seenPositive = 0;
        var seenNegative = 0;
        foreach (var group in rows.GroupBy(score).OrderByDescending(x => x.Key))
        {
            var tp = group.Count(IsMalicious);
            var fp = group.Count() - tp;
            seenPositive += tp;
            auc += tp * (negatives - seenNegative - fp / 2d);
            seenNegative += fp;
            ap += (double)tp / positives * seenPositive / (seenPositive + seenNegative);
        }
        return new(ap, auc / ((double)positives * negatives));
    }

    private static R5Baseline[] Baselines(IReadOnlyList<V4RiskResearchRow> rows) => Budgets.Select(percent => new R5Baseline(percent,
        Review(rows, V4RiskReviewBudget.Evaluate(rows, percent, true, null), true),
        Review(rows, V4RiskReviewBudget.Evaluate(rows, percent, false, V4RiskReviewBudget.Heuristic), false),
        Review(rows, V4RiskReviewBudget.Evaluate(rows, percent, true, V4RiskReviewBudget.Heuristic), true))).ToArray();

    private static R5Review Review(IReadOnlyList<V4RiskResearchRow> rows, V4ReviewResult r, bool mandatory)
    {
        var selected = r.Selected.ToHashSet();
        var additionalMalicious = mandatory ? r.Selected.Count(x => IsMalicious(x) && IsCleared(x)) : r.Malicious;
        return new(r.Opportunities, r.Rejections, r.MandatoryHolds, r.Slots, r.Additional, r.Reviewed, r.Malicious,
            additionalMalicious, r.Legitimate, r.HardNegatives, rows.Count(x => x.Truth.HardNegative), r.TotalMalicious,
            r.ReviewRate, r.Recall, r.Precision, r.Overloaded, r.Reviewed > r.Slots,
            Strata(rows, selected, x => x.Truth.Attack?.ToString() ?? "Legitimate"), Strata(rows, selected, x => x.Root),
            Strata(rows, selected, x => x.Bank));
    }

    private static Dictionary<string, R5Stratum> Strata(IReadOnlyList<V4RiskResearchRow> rows,
        HashSet<V4RiskResearchRow> selected, Func<V4RiskResearchRow, string> key) => rows.GroupBy(key).OrderBy(g => g.Key, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g =>
        {
            var reviewed = g.Where(selected.Contains).ToArray();
            return new R5Stratum(g.Count(), g.Count(IsMalicious), reviewed.Length, reviewed.Count(IsMalicious),
                reviewed.Count(x => !IsMalicious(x)), reviewed.Count(x => x.Truth.HardNegative),
                g.Count(x => x.Truth.Attack == EpisodeAttack.Ring), reviewed.Count(x => x.Truth.Attack == EpisodeAttack.Ring));
        }, StringComparer.Ordinal);

    private static bool IsMalicious(V4RiskResearchRow row) => row.Truth.Malicious;
    private static bool IsHeld(V4RiskResearchRow row) => row.Screening == nameof(PaymentStatus.Held);
    private static bool IsCleared(V4RiskResearchRow row) => row.Screening == nameof(PaymentStatus.Cleared);

    private static R5ThresholdReport Threshold(IReadOnlyList<V4RiskResearchRow> rows, Func<V4RiskResearchRow, double> probability,
        int threshold)
    {
        var mandatory = rows.Where(IsHeld).ToArray();
        // A threshold has no knowledge of later opportunities and is not capped with retrospective top-K.
        var extra = rows.Where(r => IsCleared(r) && Ppm(probability(r)) >= threshold).ToArray();
        var selected = mandatory.Concat(extra).ToArray();
        var slots = V4RiskReviewBudget.Slots(rows.Count, 5);
        var malicious = selected.Count(IsMalicious);
        var positives = rows.Count(IsMalicious);
        var result = new V4ReviewResult(rows.Count, rows.Count(x => !IsHeld(x) && !IsCleared(x)), mandatory.Length, slots, extra.Length,
            selected.Length, malicious, selected.Length - malicious, selected.Count(x => x.Truth.HardNegative), positives,
            mandatory.Length > slots, selected);
        var brier = rows.Average(r => Math.Pow(probability(r) - (IsMalicious(r) ? 1 : 0), 2));
        var logloss = rows.Average(r => -Math.Log(Math.Clamp(IsMalicious(r) ? probability(r) : 1 - probability(r), 1e-15, 1)));
        var reliability = rows.GroupBy(r => Math.Min(9, (int)(probability(r) * 10))).OrderBy(g => g.Key)
            .Select(g => new R5Reliability(g.Key, g.Count(), g.Average(probability), (double)g.Count(IsMalicious) / g.Count())).ToArray();
        return new(threshold, Review(rows, result, true), 2d * malicious / (positives + selected.Length), brier, logloss, reliability,
            Strata(rows, selected.ToHashSet(), r => FormattableString.Invariant($"{r.Root}/day-{(r.At - r.OriginMs) / V4EpisodeConfig.WarmupMs}")),
            Peak(selected), Peak(extra));

        static int Peak(IEnumerable<V4RiskResearchRow> reviewed)
        {
            var peak = 0;
            foreach (var root in reviewed.GroupBy(x => x.Root))
            {
                var times = root.Select(r => r.At).Order().ToArray();
                var left = 0;
                for (var right = 0; right < times.Length; right++)
                {
                    while (times[right] - times[left] >= 300_000) left++;
                    peak = Math.Max(peak, right - left + 1);
                }
            }
            return peak;
        }
    }

    private static object Bootstrap(IReadOnlyList<V4RiskResearchRow> rows, Func<V4RiskResearchRow, double> score)
    {
        var roots = rows.GroupBy(x => x.Root).Select(x => x.ToArray()).ToArray();
        if (roots.Length != 4) throw new InvalidOperationException("Expected four independent evaluation roots.");
        var deltas = new List<double>();
        foreach (var a in roots)
        foreach (var b in roots)
        foreach (var c in roots)
        foreach (var d in roots)
        {
            var sample = a.Concat(b).Concat(c).Concat(d).ToArray();
            deltas.Add(V4RiskReviewBudget.Evaluate(sample, 5, true, score).Recall
                - V4RiskReviewBudget.Evaluate(sample, 5, true, V4RiskReviewBudget.Heuristic).Recall);
        }
        deltas.Sort();
        return new { Draws = deltas.Count, Low = deltas[6], Median = (deltas[127] + deltas[128]) / 2, High = deltas[249] };
    }

    private static int Ppm(double probability)
    {
        if (!double.IsFinite(probability) || probability is < 0 or > 1)
            throw new InvalidOperationException("Invalid probability.");
        return (int)Math.Clamp(Math.Round(probability * 1_000_000, MidpointRounding.AwayFromZero), 0, 1_000_000);
    }
}
