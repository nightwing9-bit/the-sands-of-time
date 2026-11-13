using Revenant.Synthetic;

namespace Revenant.Tests.Intelligence.R7;

internal static class R7ForecastMetrics
{
    internal sealed record HorizonMetrics(int Horizon, int Samples, int Active, int Stress,
        double? Mae, double? Bias, double? Rmse, double? ActiveMae, double? StressMae, double? PersistenceMae, double? RobustMae,
        double? ActivePersistenceMae, double? ActiveRobustMae, double? StressPersistenceMae, double? StressRobustMae,
        double? NativeCoverage, double? NativeWidth, double? CalibratedCoverage, double? CalibratedWidth,
        double? StressNativeCoverage, double? OrdinaryNativeCoverage, double? StressCalibratedCoverage, double? OrdinaryCalibratedCoverage);

    internal sealed record WarningMetrics(int Warnings, int FalseWarnings, int Crossings, int DevelopingCrossings,
        int SuddenCrossings, int WarnedDeveloping, int WarnedSudden, int[] LeadMinutes, int[] DevelopingLeadMinutes)
    {
        public double? Precision => Warnings == 0 ? null : 1d - (double)FalseWarnings / Warnings;
        public double? Recall => Crossings == 0 ? null : (double)(Warnings - FalseWarnings) / Crossings;
    }

    internal sealed record BankCase(string Root, string Bank, long Reference, long[] Actual,
        R7RollingEvaluation.BankOrigin[] Origins, LongitudinalEvent[] Events, long?[][]? Targets = null)
    {
        internal long? Target(int bucket, int horizon) => Targets is not null ? Targets[bucket][horizon]
            : bucket + horizon + 1 < Actual.Length ? Actual[bucket + horizon + 1] : null;
    }

    private sealed record Error(double Ssa, double Persistence, double Robust, long NativeWidth, long CalibratedWidth,
        bool NativeCovered, bool CalibratedCovered, bool Active, bool Stress);

    internal static HorizonMetrics[] Evaluate(BankCase[] cases, long[] adjustments)
    {
        return Enumerable.Range(0, R7RollingEvaluation.Horizon).Select(h =>
        {
            var rows = cases.SelectMany(bank => bank.Origins.Where(origin => bank.Target(origin.Bucket, h).HasValue)
                .Select(origin => Compare(bank, origin, h, adjustments[h]))).ToArray();
            var active = rows.Where(r => r.Active || r.Stress).ToArray();
            var stress = rows.Where(r => r.Stress).ToArray();
            var ordinary = rows.Where(r => !r.Stress).ToArray();
            return new HorizonMetrics(h + 1, rows.Length, active.Length, stress.Length,
                Mean(rows, r => Math.Abs(r.Ssa)), Mean(rows, r => r.Ssa),
                Mean(rows, r => r.Ssa * r.Ssa) is { } mse ? Math.Sqrt(mse) : null,
                Mean(active, r => Math.Abs(r.Ssa)), Mean(stress, r => Math.Abs(r.Ssa)),
                Mean(rows, r => r.Persistence), Mean(rows, r => r.Robust),
                Mean(active, r => r.Persistence), Mean(active, r => r.Robust),
                Mean(stress, r => r.Persistence), Mean(stress, r => r.Robust),
                Mean(rows, r => r.NativeCovered ? 1 : 0), Mean(rows, r => r.NativeWidth),
                Mean(rows, r => r.CalibratedCovered ? 1 : 0), Mean(rows, r => r.CalibratedWidth),
                Mean(stress, r => r.NativeCovered ? 1 : 0), Mean(ordinary, r => r.NativeCovered ? 1 : 0),
                Mean(stress, r => r.CalibratedCovered ? 1 : 0), Mean(ordinary, r => r.CalibratedCovered ? 1 : 0));
        }).ToArray();
    }

    internal static long[] Calibrate(BankCase[] calibration) => Enumerable.Range(0, R7RollingEvaluation.Horizon).Select(h =>
    {
        var misses = calibration.SelectMany(bank => bank.Origins
            .Where(origin => bank.Target(origin.Bucket, h).HasValue)
            .Select(origin =>
            {
                var actual = bank.Target(origin.Bucket, h)!.Value;
                var point = origin.Path[h];
                return Math.Max(0, Math.Max(point.Lower - actual, actual - point.Upper));
            })).Order().ToArray();
        if (misses.Length == 0) throw new InvalidDataException("Calibration needs observed forecast targets.");
        return misses[Math.Clamp((int)Math.Ceiling((misses.Length + 1) * .95) - 1, 0, misses.Length - 1)];
    }).ToArray();

    internal static WarningMetrics Warnings(BankCase[] cases)
    {
        var warnings = 0;
        var falseWarnings = 0;
        var crossings = 0;
        var developing = 0;
        var sudden = 0;
        var warnedDeveloping = 0;
        var warnedSudden = 0;
        var lead = new List<int>();
        var developingLead = new List<int>();
        foreach (var bank in cases)
        {
            var floor = bank.Reference / 10;
            var actual = Enumerable.Range(R7RollingEvaluation.Warmup + 1, bank.Actual.Length - R7RollingEvaluation.Warmup - 1)
                .Where(i => bank.Actual[i] < floor && bank.Actual[i - 1] >= floor).ToArray();
            crossings += actual.Length;
            var kind = actual.ToDictionary(i => i, i => bank.Events.FirstOrDefault(e => i >= e.Start && i <= e.End)?.Kind);
            developing += kind.Values.Count(k => k == LongitudinalEventKind.DevelopingPressure);
            sudden += kind.Values.Count(k => k == LongitudinalEventKind.SuddenShock);
            var predicted = bank.Origins.Where(o => o.Available >= floor && o.Bucket + R7RollingEvaluation.Horizon < bank.Actual.Length
                && o.Path.Any(p => p.Central < floor)).Select(o => o.Bucket);
            var matched = new HashSet<int>();
            foreach (var alert in R7RollingEvaluation.Coalesce(predicted, R7RollingEvaluation.Horizon))
            {
                warnings++;
                var hit = actual.FirstOrDefault(i => i > alert && i <= alert + R7RollingEvaluation.Horizon && !matched.Contains(i), -1);
                if (hit < 0)
                {
                    falseWarnings++;
                    continue;
                }
                matched.Add(hit);
                var minutes = (hit - alert) * 5;
                lead.Add(minutes);
                if (kind[hit] == LongitudinalEventKind.DevelopingPressure)
                {
                    warnedDeveloping++;
                    developingLead.Add(minutes);
                }
                if (kind[hit] == LongitudinalEventKind.SuddenShock) warnedSudden++;
            }
        }
        return new(warnings, falseWarnings, crossings, developing, sudden, warnedDeveloping, warnedSudden,
            lead.ToArray(), developingLead.ToArray());
    }

    private static Error Compare(BankCase bank, R7RollingEvaluation.BankOrigin origin, int horizon, long adjustment)
    {
        var target = origin.Bucket + horizon + 1;
        var actual = bank.Target(origin.Bucket, horizon)!.Value;
        var step = origin.Path[horizon];
        var lower = checked(step.Lower - adjustment);
        var upper = checked(step.Upper + adjustment);
        return new((double)step.Central - actual, Math.Abs((double)origin.Available - actual),
            Math.Abs((double)origin.RobustLevel - actual), checked(step.Upper - step.Lower), checked(upper - lower),
            actual >= step.Lower && actual <= step.Upper, actual >= lower && actual <= upper,
            Math.Abs(actual - origin.Available) >= R7RollingEvaluation.ActiveChangeMinor,
            bank.Events.Any(e => target >= e.Start && origin.Bucket <= e.End));
    }

    private static double? Mean(Error[] rows, Func<Error, double> selector) => rows.Length == 0 ? null : rows.Average(selector);
}
