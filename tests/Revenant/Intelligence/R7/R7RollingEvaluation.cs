using Hosts.Worker;
using Microsoft.ML;
using Microsoft.ML.Transforms.TimeSeries;
using Revenant.Intelligence;
using Revenant.Observations;
using Revenant.Synthetic;

namespace Revenant.Tests.Intelligence.R7;

internal static class R7RollingEvaluation
{
    internal const int Warmup = 288;
    internal const int HistoryNonzero = 8;
    internal const int Horizon = V4BankTargets.HorizonCount;
    internal const long Scale = 1_024;
    internal const int Cooldown = 6;
    internal const int LateMatch = 3;
    internal const long ActiveChangeMinor = 10_000;

    internal sealed record AnomalyConfig(int Window, int History, int Training);
    internal sealed record ForecastConfig(int Window, int History, int Training);
    internal sealed record BucketScore(int Bucket, long Count, bool NativeAlert, double Raw, double PValue, double BaselineRank);
    internal sealed record ForecastPoint(long Central, long Lower, long Upper);
    internal sealed record BankOrigin(int Bucket, long Available, ForecastPoint[] Path, long RobustLevel);

    internal static bool Eligible(V4AdvisorySeries series) =>
        series.Coverage == AdvisorySeriesCoverage.Complete && series.Points.Length > Warmup
        && series.Points.Take(Warmup).Count(p => p.Value > 0) >= HistoryNonzero;

    internal static void FitAnomaly(MLContext ml, V4AdvisorySeries series, AnomalyConfig config, string path)
    {
        if (series.Schema != V4AdvisorySeries.AccountSchema || !Eligible(series))
            throw new InvalidDataException("Anomaly training requires one qualified contiguous account.");
        var observations = ml.Data.LoadFromEnumerable(series.Points.Take(config.Training).Select(p => new SeriesPoint { Value = p.Value }));
        var trainer = ml.Transforms.DetectSpikeBySsa(nameof(SpikeOutput.Prediction), nameof(SeriesPoint.Value),
            confidence: 95d, pvalueHistoryLength: config.History, trainingWindowSize: config.Training,
            seasonalityWindowSize: config.Window, side: AnomalySide.Positive);
        ml.Model.Save(trainer.Fit(observations), observations.Schema, path);
    }

    internal static void FitForecast(MLContext ml, V4AdvisorySeries series, ForecastConfig config, string path)
    {
        if (series.Schema != V4AdvisorySeries.BankSchema || series.Coverage != AdvisorySeriesCoverage.Complete
            || series.Points.Length < config.Training)
            throw new InvalidDataException("Forecast training requires one contiguous bank history.");
        var normalize = new AdvisoryBalanceNormalization(series.ReferenceMinor!.Value, Scale);
        var observations = ml.Data.LoadFromEnumerable(series.Points.Take(config.Training)
            .Select(p => new SeriesPoint { Value = normalize.Normalize(p.Value) }));
        var trainer = ml.Forecasting.ForecastBySsa(nameof(ForecastOutput.Forecast), nameof(SeriesPoint.Value),
            windowSize: config.Window, seriesLength: config.History, trainSize: config.Training, horizon: Horizon,
            isAdaptive: false, confidenceLevel: .95f,
            confidenceLowerBoundColumn: nameof(ForecastOutput.LowerBound), confidenceUpperBoundColumn: nameof(ForecastOutput.UpperBound));
        ml.Model.Save(trainer.Fit(observations), observations.Schema, path);
    }

    internal static BucketScore[] ScoreAccount(MLContext ml, V4AdvisorySeries series, string path)
    {
        if (!Eligible(series)) return [];
        using var artifact = File.OpenRead(path);
        using var engine = RevenantSsaInference.Load<SpikeOutput>(ml, artifact);
        var scores = new List<BucketScore>(series.Points.Length - Warmup);
        for (var i = 0; i < series.Points.Length; i++)
        {
            var count = series.Points[i].Value;
            var raw = RevenantSsaInference.ObserveAccount(engine, count);
            if (i < Warmup) continue;
            var exceed = series.Points.Skip(i - Warmup).Take(Warmup).Count(p => p.Value >= count);
            scores.Add(new(i, count, raw.Alert, raw.RawScore, raw.PValue, (exceed + 1d) / (Warmup + 1)));
        }
        return scores.ToArray();
    }

    internal static BankOrigin[] ForecastBank(MLContext ml, V4AdvisorySeries series, string path)
    {
        var normalize = new AdvisoryBalanceNormalization(series.ReferenceMinor!.Value, Scale);
        using var artifact = File.OpenRead(path);
        using var engine = RevenantSsaInference.Load<ForecastOutput>(ml, artifact);
        var predictions = new List<BankOrigin>();
        for (var i = 0; i < series.Points.Length; i++)
        {
            var available = series.Points[i].Value;
            var result = RevenantSsaInference.ObserveBank(engine, normalize.Normalize(available));
            if (i < Warmup) continue;
            RevenantSsaInference.VerifyForecast(result, Horizon);
            var steps = Enumerable.Range(0, Horizon).Select(h => new ForecastPoint(normalize.Restore(result.Forecast[h]),
                normalize.Restore(result.LowerBound[h]), normalize.Restore(result.UpperBound[h]))).ToArray();
            if (steps.Any(p => p.Lower > p.Central || p.Upper < p.Central))
                throw new InvalidDataException("Invalid ML.NET forecast interval.");
            var trailing = series.Points.Skip(i - Horizon + 1).Take(Horizon).Select(p => p.Value).Order().ToArray();
            predictions.Add(new(i, available, steps, trailing[(Horizon - 1) / 2]));
        }
        return predictions.ToArray();
    }

    internal sealed record AnomalyMetrics(int Accounts, int QualifiedAccounts, double NormalAccountDays, int Events,
        int Detected, int FalseAlerts, int AlertEvents, int LegitimateBursts, int AlertedLegitimateBursts,
        int[] DetectionDelayMinutes, Dictionary<LongitudinalEventKind, (int Events, int Detected)> Families)
    {
        public double? Recall => Events == 0 ? null : (double)Detected / Events;
        public double? FalseAlertsPerNormalDay => NormalAccountDays == 0 ? null : FalseAlerts / NormalAccountDays;
        public double? Precision => AlertEvents == 0 ? null : (double)Detected / AlertEvents;
    }

    internal static AnomalyMetrics EvaluateAnomalies(
        IEnumerable<(V4AdvisorySeries Series, BucketScore[] Scores, LongitudinalEvent[] Labels)> input, double threshold, bool baseline)
    {
        var accounts = 0;
        var qualified = 0;
        var normalBuckets = 0;
        var anomalies = 0;
        var falseAlerts = 0;
        var allAlerts = 0;
        var legitimate = 0;
        var legitimateAlerts = 0;
        var delays = new List<int>();
        var families = new Dictionary<LongitudinalEventKind, (int Events, int Detected)>();
        foreach (var (_, scores, labels) in input)
        {
            accounts++;
            if (scores.Length == 0) continue;
            qualified++;
            var positives = labels.Where(e => e.Kind is LongitudinalEventKind.VelocityAnomaly or LongitudinalEventKind.FraudAssociatedVelocityAnomaly)
                .Where(e => e.Start >= Warmup).OrderBy(e => e.Start).ToArray();
            var hard = labels.Where(e => e.Kind == LongitudinalEventKind.LegitimateBurst && e.Start >= Warmup).ToArray();
            normalBuckets += scores.Count(s => !positives.Any(e => s.Bucket >= e.Start && s.Bucket <= e.End + LateMatch));
            var active = scores.Where(s => threshold > 0 && s.Count > 0 && (baseline ? s.BaselineRank : s.PValue) <= threshold)
                .Select(s => s.Bucket).ToArray();
            var alerts = Coalesce(active, Cooldown);
            allAlerts += alerts.Length;
            var claimed = new HashSet<int>();
            foreach (var e in positives)
            {
                anomalies++;
                var hit = alerts.FirstOrDefault(a => a >= e.Start && a <= e.End + LateMatch && !claimed.Contains(a), -1);
                var previous = families.GetValueOrDefault(e.Kind);
                families[e.Kind] = (previous.Events + 1, previous.Detected + (hit >= 0 ? 1 : 0));
                if (hit < 0) continue;
                claimed.Add(hit);
                delays.Add((hit - e.Start) * 5);
            }
            falseAlerts += alerts.Length - claimed.Count;
            legitimate += hard.Length;
            legitimateAlerts += hard.Count(e => alerts.Any(a => a >= e.Start && a <= e.End + LateMatch));
        }
        return new(accounts, qualified, (double)normalBuckets / 288, anomalies, delays.Count, falseAlerts, allAlerts, legitimate,
            legitimateAlerts, delays.ToArray(), families);
    }

    internal static int[] Coalesce(IEnumerable<int> buckets, int cooldown)
    {
        var previous = -cooldown - 1;
        var result = new List<int>();
        foreach (var bucket in buckets)
        {
            if (bucket - previous > cooldown) result.Add(bucket);
            previous = bucket;
        }
        return result.ToArray();
    }
}
