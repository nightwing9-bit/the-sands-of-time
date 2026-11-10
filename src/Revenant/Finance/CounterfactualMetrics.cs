using Marrow.Kernel.Results;
using Revenant.Contracts;

namespace Revenant.Finance;

/// <summary>
/// Scenario cost assumptions for synthetic comparison metrics. They are not real banking losses.
/// <c>counterfactual-cost-v1</c>: USD 250.00 per newly held or stopped labelled-legitimate payment, and
/// USD 10.00 per whole additional minute a labelled-legitimate payment settles later (or stays unsettled)
/// than in the original, capped at 120 minutes per payment.
/// </summary>
public sealed record CounterfactualCostModel(
    string Version,
    string Currency,
    long FalsePositiveCostMinor,
    long DelayCostPerMinuteMinor,
    int DelayCapMinutes)
{
    public static CounterfactualCostModel V1 { get; } = new("counterfactual-cost-v1", "USD", 25_000, 1_000, 120);
}

/// <summary>
/// Synthetic comparison of two aligned projections at the same logical position. All counts use synthetic
/// labels from <see cref="LabelSchemaVersion"/>; none is a real-world fraud finding. A fraud payment held
/// and still pending at the horizon is only <see cref="FraudDeferredAtHorizonCount"/>: its principal is not
/// counted as prevented and does not enter <see cref="NetEffectMinor"/>.
/// </summary>
public sealed record CounterfactualMetrics(
    string LabelSchemaVersion,
    string CostModelVersion,
    long HorizonUtcMs,
    long AlignedSequence,
    int FraudPreventedCount,
    long FraudPreventedExposureMinor,
    int FraudDeferredAtHorizonCount,
    int LegitimateDelayedCount,
    long FalsePositiveCostMinor,
    long IncrementalDelayCostMinor,
    long NetEffectMinor,
    int DecisionDifferenceCount)
{
    private const long MinuteMs = 60_000;

    /// <summary>
    /// Compares the <paramref name="original"/> branch with an <paramref name="alternate"/> branch. The horizon
    /// is the shared projection clock; both states must be at the same run, scenario and logical position.
    /// </summary>
    public static Result<CounterfactualMetrics, FinanceError> Compare(
        FinanceProjectionState original,
        FinanceProjectionState alternate,
        CounterfactualCostModel costs)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(alternate);
        ArgumentNullException.ThrowIfNull(costs);
        if (original.RunId != alternate.RunId
            || !string.Equals(original.ManifestContentHash, alternate.ManifestContentHash, StringComparison.Ordinal)
            || original.LastSequence != alternate.LastSequence
            || original.ClockUtcMs != alternate.ClockUtcMs
            || original.Payments.Count != alternate.Payments.Count
            || !original.Payments.Keys.SequenceEqual(alternate.Payments.Keys, StringComparer.Ordinal))
        {
            return FinanceError.Fail<CounterfactualMetrics>(
                FinanceErrorCode.NotAligned, "States are not at the same run, scenario and logical position.");
        }
        if (!string.Equals(costs.Currency, original.Currency, StringComparison.Ordinal))
        {
            return FinanceError.Fail<CounterfactualMetrics>(
                FinanceErrorCode.VersionMismatch, $"Cost model currency {costs.Currency} is not {original.Currency}.");
        }

        var horizon = original.ClockUtcMs;
        int prevented = 0, deferred = 0, delayed = 0, falsePositives = 0, differences = 0;
        long exposure = 0, delayCost = 0;
        foreach (var (id, o) in original.Payments)
        {
            var a = alternate.Payments[id];
            if (o.Status != a.Status || o.Reason != a.Reason)
            {
                differences++;
            }
            if (o.Intent.Truth == SyntheticTruth.FraudMuleRing)
            {
                if (o.Status == PaymentStatus.Settled && IsTerminallyStopped(a))
                {
                    prevented++;
                    exposure = checked(exposure + o.Intent.Amount.MinorUnits);
                }
                else if (o.Status == PaymentStatus.Settled && a.IsPendingHold)
                {
                    deferred++;
                }
                continue;
            }

            if (WasHeldOrStopped(a) && !WasHeldOrStopped(o))
            {
                falsePositives++;
            }
            if (o.SettledAtUtcMs is { } originalSettled
                && (a.SettledAtUtcMs is not { } altSettled || altSettled > originalSettled))
            {
                delayed++;
                var minutes = Math.Min(((a.SettledAtUtcMs ?? horizon) - originalSettled) / MinuteMs, costs.DelayCapMinutes);
                delayCost = checked(delayCost + (minutes * costs.DelayCostPerMinuteMinor));
            }
        }

        var falsePositiveCost = checked(falsePositives * costs.FalsePositiveCostMinor);
        return Result<CounterfactualMetrics, FinanceError>.Success(new CounterfactualMetrics(
            ScenarioSchemas.LabelSchemaVersion,
            costs.Version,
            horizon,
            original.LastSequence,
            prevented,
            exposure,
            deferred,
            delayed,
            falsePositiveCost,
            delayCost,
            checked(exposure - falsePositiveCost - delayCost),
            differences));
    }

    private static bool IsTerminallyStopped(PaymentRecord p) =>
        p.Status is PaymentStatus.Rejected or PaymentStatus.Returned && !p.EverSettled;

    private static bool WasHeldOrStopped(PaymentRecord p) =>
        p.Trail.Contains(PaymentStatus.Held) || IsTerminallyStopped(p);
}
