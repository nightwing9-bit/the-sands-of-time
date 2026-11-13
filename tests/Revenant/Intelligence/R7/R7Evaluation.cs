using Revenant.Intelligence;
using Revenant.Observations;
using Revenant.Synthetic;

namespace Revenant.Tests.Intelligence.R7;

/// <summary>
/// R7/2 measurement. Scores come from Hosts.Worker.RevenantSsaInference (the advisory implementation); this class only
/// matches alerts to hidden truth, computes non-ML comparators and calibrates thresholds on a designated partition.
/// </summary>
internal static class R7Evaluation
{
    internal const int Cooldown = 6;
    internal const int LateMatch = 3;
    internal const double FalseAlertBudget = 0.1;

    internal enum Scorer
    {
        /// <summary>The fitted SSA alert statistic, recomputed from scored steps with <see cref="SsaAlertState"/>.</summary>
        Ssa,

        /// <summary>Robust count comparator: −log10 of the count's upper empirical rank in the account's trailing 288 buckets.</summary>
        TrailingRank,

        /// <summary>Diagnostic non-ML comparator: count minus the same-time-of-day (±3) mean on earlier days.</summary>
        SeasonalExcess,
    }

    /// <summary>One account's causal count history and its hidden labels. Digest identifies the series or the plan-derived counts.</summary>
    internal sealed record AccountInput(string Root, string Subject, string Digest, string? Rhythm, long[] Counts, LongitudinalEvent[] Labels);

    internal static AccountInput[] Inputs(IEnumerable<LongitudinalSample> roots) => roots.SelectMany(r => r.Accounts.Select(s =>
        new AccountInput(r.Manifest.Root.Key, s.SubjectId, s.Digest, r.Rhythms?.GetValueOrDefault(s.SubjectId),
            s.Points.Select(p => p.Value).ToArray(), r.Events.Where(e => e.Subject == s.SubjectId).ToArray()))).ToArray();

    internal sealed record AccountCase(string Root, string Subject, string? Rhythm, long[] Counts, SsaAnomalyStep[] Steps,
        LongitudinalEvent[] Labels);

    internal sealed record Tally(int Events, int Detected, int FalseAlerts, double NormalDays)
    {
        public double? Recall => Events == 0 ? null : (double)Detected / Events;
        public double? FalseAlertsPerNormalDay => NormalDays == 0 ? null : FalseAlerts / NormalDays;
    }

    internal sealed record AnomalyMetrics(int Accounts, int QualifiedAccounts, double NormalAccountDays, int Events, int Detected,
        int Excluded, int AlertedExcluded, int FalseAlerts, int AlertEvents, int HardNegatives, int AlertedHardNegatives,
        int[] DetectionDelayMinutes, Dictionary<string, Tally> ByMechanism, Dictionary<string, Tally> ByIntent,
        Dictionary<string, Tally> ByRhythm, Dictionary<string, Tally> ByRoot)
    {
        public double? Recall => Events == 0 ? null : (double)Detected / Events;
        public double? FalseAlertsPerNormalDay => NormalAccountDays == 0 ? null : FalseAlerts / NormalAccountDays;
        public double? Precision => Detected + FalseAlerts == 0 ? null : (double)Detected / (Detected + FalseAlerts);
        public double? MedianDelayMinutes => DetectionDelayMinutes.Length == 0 ? null
            : DetectionDelayMinutes.Order().ElementAt(DetectionDelayMinutes.Length / 2);
    }

    internal static bool IsPositive(LongitudinalEvent e) => e.Truth is { } truth
        ? truth == VelocityTruth.Anomalous && e.Kind != LongitudinalEventKind.LegitimateBurst
        : e.Kind is LongitudinalEventKind.VelocityAnomaly or LongitudinalEventKind.FraudAssociatedVelocityAnomaly;

    /// <summary>Injected windows that the oracle did not call anomalous are excluded: an alert there is neither a hit nor false.</summary>
    internal static bool IsExcluded(LongitudinalEvent e) => e.Truth is { } truth && truth != VelocityTruth.Anomalous
        && e.Kind is LongitudinalEventKind.VelocityAnomaly or LongitudinalEventKind.FraudAssociatedVelocityAnomaly;

    internal static double[] Score(AccountCase c, Scorer scorer, SsaAlertPolicy? policy)
    {
        var scores = new double[c.Steps.Length];
        var state = scorer == Scorer.Ssa ? new SsaAlertState(policy ?? throw new ArgumentNullException(nameof(policy))) : null;
        for (var k = 0; k < c.Steps.Length; k++)
        {
            var step = c.Steps[k];
            var i = step.Bucket;
            scores[k] = scorer switch
            {
                Scorer.Ssa => state!.Next(step.Count, step.ExpectedCount, step.Residual),
                Scorer.TrailingRank => -Math.Log10((c.Counts.Skip(Math.Max(0, i - 288)).Take(Math.Min(288, i))
                    .Count(v => v >= step.Count) + 1d) / (Math.Min(288, i) + 1)),
                Scorer.SeasonalExcess => step.Count - R7Diagnostics.SeasonalMean(c.Counts, i, i),
                _ => throw new ArgumentOutOfRangeException(nameof(scorer)),
            };
        }
        return scores;
    }

    internal static AnomalyMetrics Evaluate(AccountCase[] cases, Scorer scorer, SsaAlertPolicy? policy, double threshold) =>
        Evaluate(cases, cases.Select(c => Score(c, scorer, policy)).ToArray(), threshold);

    internal static AnomalyMetrics Evaluate(AccountCase[] cases, double[][] scores, double threshold)
    {
        var normalBuckets = 0;
        int positives = 0, detected = 0, excluded = 0, alertedExcluded = 0, falseAlerts = 0, alerts = 0, hard = 0, alertedHard = 0, qualified = 0;
        var delays = new List<int>();
        var byMechanism = new Dictionary<string, Tally>();
        var byIntent = new Dictionary<string, Tally>();
        var byRhythm = new Dictionary<string, Tally>();
        var byRoot = new Dictionary<string, Tally>();
        for (var n = 0; n < cases.Length; n++)
        {
            var c = cases[n];
            var steps = c.Steps;
            if (steps.Length == 0) continue;
            qualified++;
            var first = steps[0].Bucket;
            var last = steps[^1].Bucket;
            var inScope = c.Labels.Where(e => e.Start >= first && e.End <= last).ToArray();
            var pos = inScope.Where(IsPositive).OrderBy(e => e.Start).ToArray();
            var skip = inScope.Where(IsExcluded).ToArray();
            var negatives = inScope.Where(e => e.Kind == LongitudinalEventKind.LegitimateBurst).ToArray();
            bool Within(LongitudinalEvent e, int b) => b >= e.Start && b <= e.End + LateMatch;
            var normal = steps.Count(s => !pos.Any(e => Within(e, s.Bucket)) && !skip.Any(e => Within(e, s.Bucket)));
            normalBuckets += normal;
            var active = steps.Where((s, k) => s.Count > 0 && scores[n][k] >= threshold).Select(s => s.Bucket);
            var events = R7ForecastMetrics.Coalesce(active, Cooldown);
            alerts += events.Length;
            var claimed = new HashSet<string>(StringComparer.Ordinal);
            var accountFalse = 0;
            foreach (var a in events)
            {
                var hit = pos.FirstOrDefault(e => Within(e, a) && !claimed.Contains(e.Id));
                if (hit is not null)
                {
                    claimed.Add(hit.Id);
                    delays.Add((a - hit.Start) * 5);
                    continue;
                }
                if (pos.Any(e => Within(e, a))) continue;
                if (skip.Any(e => Within(e, a)))
                {
                    alertedExcluded++;
                    continue;
                }
                accountFalse++;
            }
            falseAlerts += accountFalse;
            positives += pos.Length;
            detected += claimed.Count;
            excluded += skip.Length;
            hard += negatives.Length;
            alertedHard += negatives.Count(e => events.Any(a => Within(e, a)));
            foreach (var e in pos)
            {
                Add(byMechanism, e.Mechanism?.ToString() ?? e.Kind.ToString(), 1, claimed.Contains(e.Id) ? 1 : 0, 0, 0);
                Add(byIntent, e.Intent?.ToString() ?? e.Kind.ToString(), 1, claimed.Contains(e.Id) ? 1 : 0, 0, 0);
            }
            var rhythm = c.Rhythm ?? "Unknown";
            Add(byRhythm, rhythm, pos.Length, claimed.Count, accountFalse, normal / 288d);
            Add(byRoot, c.Root, pos.Length, claimed.Count, accountFalse, normal / 288d);
        }
        return new(cases.Length, qualified, normalBuckets / 288d, positives, detected, excluded, alertedExcluded, falseAlerts, alerts,
            hard, alertedHard, delays.ToArray(), byMechanism, byIntent, byRhythm, byRoot);

        static void Add(Dictionary<string, Tally> map, string key, int events, int hits, int falseAlerts, double days)
        {
            var t = map.GetValueOrDefault(key) ?? new Tally(0, 0, 0, 0);
            map[key] = new(t.Events + events, t.Detected + hits, t.FalseAlerts + falseAlerts, t.NormalDays + days);
        }
    }

    /// <summary>
    /// Highest-recall threshold whose false-alert rate on these cases is within the target, ties broken toward fewer false alerts and
    /// the higher threshold. Candidates are score quantiles at nonzero-count buckets. Used only on Validation or Calibration roots.
    /// </summary>
    internal static (double Threshold, AnomalyMetrics Metrics) Calibrate(AccountCase[] cases, Scorer scorer, SsaAlertPolicy? policy,
        double target)
    {
        var scores = cases.Select(c => Score(c, scorer, policy)).ToArray();
        var values = cases.SelectMany((c, n) => c.Steps.Select((s, k) => (s.Count, Score: scores[n][k])))
            .Where(x => x.Count > 0 && double.IsFinite(x.Score)).Select(x => x.Score).Order().ToArray();
        if (values.Length == 0) throw new InvalidDataException("No scored nonzero buckets to calibrate.");
        var candidates = Enumerable.Range(0, 400).Select(q => values[(int)((values.Length - 1) * (1 - Math.Pow(q / 400d, 3)))])
            .Append(values[^1] + 1e-9).Distinct().ToArray();
        return candidates.Select(t => (Threshold: t, Metrics: Evaluate(cases, scores, t)))
            .Where(x => x.Metrics.FalseAlertsPerNormalDay <= target)
            .OrderByDescending(x => x.Metrics.Detected).ThenBy(x => x.Metrics.FalseAlerts).ThenByDescending(x => x.Threshold).First();
    }

    internal static R7ForecastMetrics.BankCase BankCase(string root, V4AdvisorySeries bank, SsaForecastStep[] steps,
        IEnumerable<LongitudinalEvent> events, long?[][]? targets)
    {
        var levels = bank.Points.Select(p => p.Value).ToArray();
        var origins = steps.Select(s => new R7ForecastMetrics.BankOrigin(s.Bucket, s.Available,
            Enumerable.Range(0, SsaAdvisory.Horizon).Select(h => new R7ForecastMetrics.ForecastPoint(s.Central[h], s.NativeLower[h],
                s.NativeUpper[h], s.CalibratedLower[h], s.CalibratedUpper[h])).ToArray(), TrailingMedian(levels, s.Bucket))).ToArray();
        return new(root, bank.SubjectId, bank.ReferenceMinor!.Value, levels, origins,
            events.Where(e => e.Subject == bank.SubjectId).ToArray(), targets);
    }

    /// <summary>Robust level comparator: the median of the eight most recent completed levels.</summary>
    internal static long TrailingMedian(long[] levels, int origin)
    {
        var trailing = levels.Skip(Math.Max(0, origin - SsaAdvisory.Horizon + 1)).Take(Math.Min(SsaAdvisory.Horizon, origin + 1)).Order().ToArray();
        return trailing[(trailing.Length - 1) / 2];
    }

    /// <summary>Horizon-wise split-conformal multipliers from |actual − central| / σ on calibration banks.</summary>
    internal static double[] ConformalMultipliers(IEnumerable<(SsaForecastStep[] Steps, Func<int, int, long?> Target)> calibration,
        double coverage) => Enumerable.Range(0, SsaAdvisory.Horizon).Select(h =>
    {
        var errors = calibration.SelectMany(c => c.Steps.Where(s => c.Target(s.Bucket, h).HasValue)
            .Select(s => Math.Abs((double)c.Target(s.Bucket, h)!.Value - s.Central[h]) / s.Sigma)).ToArray();
        return SsaAdvisory.ConformalQuantile(errors, coverage);
    }).ToArray();
}
