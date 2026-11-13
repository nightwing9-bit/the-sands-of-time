using Revenant.Observations;
using Revenant.Synthetic;

namespace Revenant.Tests.Intelligence.R7;

/// <summary>
/// Model-free measurements of what the R7 inputs can identify. These functions never produce an advisory output; they quantify
/// label/observation overlap and the linear predictability ceiling that any SSA linear recurrence is bounded by.
/// </summary>
internal static class R7Diagnostics
{
    internal sealed record EventProfile(string Id, string Subject, LongitudinalEventKind Kind, string? Truth, int Start, int Duration,
        long[] Counts, long Total, long Max, double PriorNonzeroFraction, double PriorMeanCount, long PriorDailyMax,
        double SeasonalExpected, double SeasonalExcess, double TrailingRankOfMax, bool PriorHistory);

    internal sealed record FeatureAuc(string Feature, int Positives, int Negatives, double? Auc);

    internal static EventProfile[] Events(IEnumerable<LongitudinalSample> roots, int minimumHistory = 288)
    {
        var result = new List<EventProfile>();
        foreach (var root in roots)
            foreach (var e in root.Events.Where(e => e.Kind is LongitudinalEventKind.VelocityAnomaly
                         or LongitudinalEventKind.FraudAssociatedVelocityAnomaly or LongitudinalEventKind.LegitimateBurst))
            {
                var series = root.Accounts.Single(s => s.SubjectId == e.Subject).Points.Select(p => p.Value).ToArray();
                var window = series[e.Start..(e.End + 1)];
                var prior = series[..e.Start];
                var trailing = series[Math.Max(0, e.Start - 288)..e.Start];
                var expected = Enumerable.Range(e.Start, e.End - e.Start + 1).Sum(b => SeasonalMean(series, b, e.Start));
                result.Add(new(e.Id, e.Subject, e.Kind, e.Truth?.ToString(), e.Start, window.Length, window, window.Sum(), window.Max(),
                    prior.Length == 0 ? 0 : prior.Count(c => c > 0) / (double)prior.Length, prior.Length == 0 ? 0 : prior.Average(),
                    prior.Length == 0 ? 0 : prior.Chunk(288).Max(d => d.Max()), expected, window.Sum() - expected,
                    trailing.Length == 0 ? 1 : (trailing.Count(c => c >= window.Max()) + 1d) / (trailing.Length + 1),
                    e.Start >= minimumHistory));
            }
        return result.ToArray();
    }

    /// <summary>Mean count at the same time of day (±3 buckets) on all earlier days, using only buckets before the cutoff.</summary>
    internal static double SeasonalMean(long[] series, int bucket, int cutoff)
    {
        var tod = bucket % V4LongitudinalPlanner.BucketsPerDay;
        double total = 0;
        var n = 0;
        for (var day = 0; day < bucket / V4LongitudinalPlanner.BucketsPerDay; day++)
            for (var o = -3; o <= 3; o++)
            {
                var b = day * V4LongitudinalPlanner.BucketsPerDay + tod + o;
                if (b < 0 || b >= cutoff) continue;
                total += series[b];
                n++;
            }
        return n == 0 ? 0 : total / n;
    }

    internal static FeatureAuc[] Separability(EventProfile[] events, Func<EventProfile, bool> positive, Func<EventProfile, bool> negative)
    {
        var features = new (string Name, Func<EventProfile, double> Value)[]
        {
            ("EventMaxCount", e => e.Max), ("EventTotal", e => e.Total), ("Duration", e => e.Duration),
            ("SeasonalExcess", e => e.SeasonalExcess), ("NegativeTrailingRank", e => -e.TrailingRankOfMax),
            ("NegativePriorMean", e => -e.PriorMeanCount),
        };
        var pos = events.Where(positive).ToArray();
        var neg = events.Where(negative).ToArray();
        return features.Select(f => new FeatureAuc(f.Name, pos.Length, neg.Length, Auc(pos.Select(f.Value), neg.Select(f.Value)))).ToArray();
    }

    internal static double? Auc(IEnumerable<double> positives, IEnumerable<double> negatives)
    {
        var p = positives.ToArray();
        var n = negatives.ToArray();
        if (p.Length == 0 || n.Length == 0) return null;
        double wins = 0;
        foreach (var a in p)
            foreach (var b in n)
                wins += a > b ? 1 : a == b ? .5 : 0;
        return wins / (p.Length * n.Length);
    }

    /// <summary>Correlation of ±3-bucket smoothed daily profiles between consecutive days, averaged per account.</summary>
    internal static double? DailyProfilePersistence(long[] counts)
    {
        var days = counts.Chunk(V4LongitudinalPlanner.BucketsPerDay).Where(d => d.Length == V4LongitudinalPlanner.BucketsPerDay)
            .Select(d => Enumerable.Range(0, d.Length).Select(t => (double)Enumerable.Range(t - 3, 7)
                .Where(i => i >= 0 && i < d.Length).Sum(i => d[i])).ToArray()).ToArray();
        var correlations = days.Zip(days.Skip(1), Correlation).Where(c => c.HasValue).Select(c => c!.Value).ToArray();
        return correlations.Length == 0 ? null : correlations.Average();
    }

    internal static double? Correlation(double[] a, double[] b)
    {
        var ma = a.Average();
        var mb = b.Average();
        double sab = 0, saa = 0, sbb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            sab += (a[i] - ma) * (b[i] - mb);
            saa += (a[i] - ma) * (a[i] - ma);
            sbb += (b[i] - mb) * (b[i] - mb);
        }
        return saa == 0 || sbb == 0 ? null : sab / Math.Sqrt(saa * sbb);
    }

    internal static double[] Changes(long opening, long[] levels, long scale) =>
        levels.Select((v, i) => (v - (i == 0 ? opening : levels[i - 1])) / (double)scale).ToArray();

    internal static double?[] Autocorrelation(double[] x, int[] lags)
    {
        var mean = x.Average();
        var variance = x.Sum(v => (v - mean) * (v - mean));
        return lags.Select(lag => variance == 0 || lag >= x.Length ? (double?)null
            : Enumerable.Range(lag, x.Length - lag).Sum(i => (x[i] - mean) * (x[i - lag] - mean)) / variance).ToArray();
    }

    /// <summary>Ridge least-squares AR(p) on pooled change series, no intercept (the SSA recurrence has none either).</summary>
    internal static double[] FitAutoregression(IEnumerable<double[]> series, int order, int start)
    {
        var xtx = new double[order, order];
        var xty = new double[order];
        foreach (var x in series)
            for (var t = Math.Max(order, start); t < x.Length; t++)
                for (var i = 0; i < order; i++)
                {
                    xty[i] += x[t - 1 - i] * x[t];
                    for (var j = 0; j < order; j++) xtx[i, j] += x[t - 1 - i] * x[t - 1 - j];
                }
        var trace = Enumerable.Range(0, order).Sum(i => xtx[i, i]);
        for (var i = 0; i < order; i++) xtx[i, i] += 1e-4 * trace / order + 1e-9;
        return Solve(xtx, xty);
    }

    internal static double[] PredictChanges(double[] coefficients, double[] history, int origin, int horizon)
    {
        var p = coefficients.Length;
        var buffer = new List<double>(p + horizon);
        for (var i = origin - p + 1; i <= origin; i++) buffer.Add(i >= 0 ? history[i] : 0);
        var output = new double[horizon];
        for (var h = 0; h < horizon; h++)
        {
            double next = 0;
            for (var i = 0; i < p; i++) next += coefficients[i] * buffer[^(i + 1)];
            buffer.Add(next);
            output[h] = next;
        }
        return output;
    }

    private static double[] Solve(double[,] a, double[] b)
    {
        var n = b.Length;
        var m = (double[,])a.Clone();
        var x = (double[])b.Clone();
        for (var col = 0; col < n; col++)
        {
            var pivot = Enumerable.Range(col, n - col).MaxBy(r => Math.Abs(m[r, col]));
            for (var k = 0; k < n; k++) (m[col, k], m[pivot, k]) = (m[pivot, k], m[col, k]);
            (x[col], x[pivot]) = (x[pivot], x[col]);
            for (var r = col + 1; r < n; r++)
            {
                var f = m[r, col] / m[col, col];
                for (var k = col; k < n; k++) m[r, k] -= f * m[col, k];
                x[r] -= f * x[col];
            }
        }
        for (var r = n - 1; r >= 0; r--)
        {
            for (var k = r + 1; k < n; k++) x[r] -= m[r, k] * x[k];
            x[r] /= m[r, r];
        }
        return x;
    }

    /// <summary>Exact managed copy of ML.NET 5.0 ComputeKernelPValue's accumulator, used only to explain nonfinite native p-values.</summary>
    internal sealed class KernelPValueReplica(int history)
    {
        private readonly Queue<float> buffer = new();
        internal double SumSquaredDistance { get; private set; }

        internal double Next(double rawScore)
        {
            var values = buffer.ToArray();
            var n = values.Length;
            double p;
            if (n == 0) p = .5;
            else
            {
                var bandwidth = Math.Max(Math.Sqrt(2) * (n == 1 ? 1 : Math.Sqrt(SumSquaredDistance) / n), 1e-6);
                p = 0;
                for (var i = 0; i < n; i++)
                {
                    var diff = rawScore - values[i];
                    p -= Erf(diff / bandwidth);
                    SumSquaredDistance += diff * diff;
                }
                p = .5 + p / (2 * n);
                if (n == history)
                {
                    for (var i = 1; i < n; i++)
                    {
                        var diff = values[0] - (double)values[i];
                        SumSquaredDistance -= diff * diff;
                    }
                    var last = values[0] - rawScore;
                    SumSquaredDistance -= last * last;
                }
            }
            if (buffer.Count == history) buffer.Dequeue();
            buffer.Enqueue((float)rawScore);
            return p;
        }

        // Abramowitz-Stegun 7.1.26 is not ML.NET's Erf, but NaN propagation, which is all this replica is used for, is identical.
        private static double Erf(double x)
        {
            if (double.IsNaN(x)) return double.NaN;
            var sign = Math.Sign(x);
            x = Math.Abs(x);
            var t = 1 / (1 + .3275911 * x);
            var y = 1 - ((((1.061405429 * t - 1.453152027) * t + 1.421413741) * t - .284496736) * t + .254829592) * t * Math.Exp(-x * x);
            return sign * y;
        }
    }
}
