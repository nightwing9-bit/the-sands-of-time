using Revenant.Finance;
using Revenant.Intelligence;

namespace Revenant.Synthetic;

internal sealed record V4ReviewResult(int Opportunities, int Rejections, int MandatoryHolds, int Slots, int Additional,
    int Reviewed, int Malicious, int Legitimate, int HardNegatives, int TotalMalicious, bool Overloaded,
    IReadOnlyList<V4RiskResearchRow> Selected)
{
    internal double ReviewRate => Opportunities == 0 ? 0 : (double)Reviewed / Opportunities;
    internal double Recall => TotalMalicious == 0 ? 0 : (double)Malicious / TotalMalicious;
    internal double Precision => Reviewed == 0 ? 0 : (double)Malicious / Reviewed;
}

internal static class V4RiskReviewBudget
{
    internal const int PrimaryPercent = 5;
    internal static int Slots(int count, int percent)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (percent is not (2 or 5 or 10)) throw new ArgumentOutOfRangeException(nameof(percent));
        return (int)((long)count * percent / 100);
    }

    // Fixed before training. Equal maximum contributions for amount and balance ratio; half for reported novelty.
    internal static double Heuristic(V4RiskResearchRow row) =>
        RiskFeatureVectorV2.Ratio(Math.Min(200_000, row.AsOf.AmountMinor), 200_000)
        + Math.Min(1_000_000, row.AsOf.AmountToAvailablePpm ?? 1_000_000)
        + (row.AsOf.ReportedNewBeneficiary ? 500_000 : 0);

    internal static V4ReviewResult Evaluate(IReadOnlyList<V4RiskResearchRow> rows, int percent,
        bool preserveHolds, Func<V4RiskResearchRow, double>? score)
    {
        var slots = Slots(rows.Count, percent);
        var holds = rows.Where(x => x.Screening == nameof(PaymentStatus.Held)).ToArray();
        var mandatory = preserveHolds ? holds : [];
        var remaining = Math.Max(0, slots - mandatory.Length);
        var selected = score is null || remaining == 0 ? [] : rows
            .Where(x => x.Screening == nameof(PaymentStatus.Cleared) || (!preserveHolds && x.Screening == nameof(PaymentStatus.Held)))
            .Select(x => (Row: x, Score: score(x)))
            .OrderByDescending(x => double.IsFinite(x.Score) ? x.Score : throw new InvalidOperationException("Nonfinite review score."))
            .ThenBy(x => x.Row.At).ThenBy(x => x.Row.Root, StringComparer.Ordinal)
            .ThenBy(x => x.Row.Cutoff).ThenBy(x => x.Row.IntentId, StringComparer.Ordinal)
            .Take(remaining).Select(x => x.Row).ToArray();
        var reviewed = mandatory.Concat(selected).ToArray();
        return new(rows.Count, rows.Count(x => x.Screening == nameof(PaymentStatus.Rejected)), holds.Length, slots,
            selected.Length, reviewed.Length, reviewed.Count(x => x.Truth.Malicious),
            reviewed.Count(x => !x.Truth.Malicious), reviewed.Count(x => x.Truth.HardNegative),
            rows.Count(x => x.Truth.Malicious), mandatory.Length > slots, reviewed);
    }
}
