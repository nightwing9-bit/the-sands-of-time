using System.Diagnostics.Metrics;

namespace Revenant.Payments;

internal static class V4FinanceMetrics
{
    internal const string MeterName = "Revenant.V4.Finance";
    internal const string ScoringName = "revenant.v4.model.score.duration";
    internal const string DispatchName = "revenant.v4.finance.dispatch.duration";
    private static readonly Meter Meter = new(MeterName);

    internal static Histogram<double> Scoring { get; } = Meter.CreateHistogram<double>(ScoringName, unit: "ms");
    internal static Histogram<double> Dispatch { get; } = Meter.CreateHistogram<double>(DispatchName, unit: "ms");
}
