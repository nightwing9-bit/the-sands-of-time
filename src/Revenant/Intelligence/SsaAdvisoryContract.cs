using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Revenant.Intelligence;

/// <summary>Count-series transform applied before the outgoing-velocity SSA detector sees an observation.</summary>
internal enum SsaCountTransform : byte
{
    /// <summary>Historical R7/1 input: the raw five-minute intent count.</summary>
    Count = 1,

    /// <summary>
    /// 2·sqrt(c + 3/8) − 2·sqrt(3/8). The Anscombe transform makes Poisson-like count noise approximately unit variance, so a
    /// residual of the same size means a similar degree of surprise for a quiet and a busy account. Zero maps to exactly zero, so a
    /// silent history produces a zero SSA state and a zero expectation without relying on the recurrence preserving a constant.
    /// </summary>
    AnscombeZeroAnchored = 2,
}

/// <summary>Statistic that turns the fitted SSA one-step residual into an alert.</summary>
internal enum SsaAlertStatistic : byte
{
    /// <summary>ML.NET kernel p-value. Diagnostic only in R7/2; see docs/recovery/r7-scientific-recovery.md.</summary>
    NativePValue = 1,

    /// <summary>The native SSA raw score (observed minus SSA-expected input) compared with a calibrated threshold.</summary>
    Residual = 2,

    /// <summary>One-sided CUSUM of the native SSA residual: S_t = max(0, S_{t-1} + r_t − k), started at zero after warm-up.</summary>
    ResidualCusum = 3,

    /// <summary>
    /// −log10 P(Poisson(Σμ) ≥ Σc) over the last <see cref="SsaAlertPolicy.Window"/> buckets, where c is the observed count and
    /// μ = max(SSA-expected count, floor). The SSA expectation is input − native raw score mapped back to counts. Unlike a residual
    /// in transformed units, the tail probability treats two intents where the account's learned expectation is near zero as far
    /// more surprising than five where it expects two.
    /// </summary>
    PoissonSurprise = 4,
}

/// <summary>How the bank available-balance series is presented to ForecastBySsa. The decoded output is always eight levels.</summary>
internal enum SsaForecastRepresentation : byte
{
    /// <summary>Historical R7/1: (A_t − A_opening) / scale.</summary>
    OpeningCenteredLevel = 1,

    /// <summary>(A_t − A_{t−1}) / scale with A_{−1} = A_opening. Levels are A_t + scale·Σ predicted changes.</summary>
    CompletedIntervalChange = 2,
}

internal sealed record SsaAnomalySpec(SsaCountTransform Transform, int SeasonalWindow, int TrainingWindow, int PValueHistory,
    int Warmup, int MinimumNonzero)
{
    /// <summary>
    /// The ML.NET 5.0 SSA state is the last L−1 buffered observations plus a diagonal-averaged state vector that blends windows
    /// ending at the previous L−2 steps, so an observation older than 2L−2 steps no longer affects the prediction. A fresh engine
    /// starts from the training series tail; the warm-up has to consume at least that many of the subject's own observations.
    /// </summary>
    internal static int StateMemory(int window) => 2 * window - 2;

    [JsonIgnore]
    internal bool IsValid => Enum.IsDefined(Transform) && SeasonalWindow >= 2 && TrainingWindow > 2 * SeasonalWindow
        && PValueHistory >= 1 && Warmup >= StateMemory(SeasonalWindow) && MinimumNonzero >= 0 && MinimumNonzero <= Warmup;
}

internal sealed record SsaAlertPolicy(SsaAlertStatistic Statistic, double Threshold, double Allowance, int Window = 1,
    double Floor = 0)
{
    [JsonIgnore]
    internal bool IsValid => Statistic is SsaAlertStatistic.Residual or SsaAlertStatistic.ResidualCusum or SsaAlertStatistic.PoissonSurprise
        && double.IsFinite(Threshold) && Threshold > 0 && double.IsFinite(Allowance) && Allowance >= 0 && Window is >= 1 and <= 288
        && double.IsFinite(Floor) && Floor >= 0 && (Statistic != SsaAlertStatistic.PoissonSurprise || Floor > 0);
}

/// <summary>Causal alert-statistic state for one account, started empty at the end of the warm-up.</summary>
internal sealed class SsaAlertState(SsaAlertPolicy policy)
{
    private readonly Queue<(long Count, double Expected)> window = new();
    private double cusum;

    internal double Next(long count, double expectedCount, double residual)
    {
        switch (policy.Statistic)
        {
            case SsaAlertStatistic.Residual:
                return residual;
            case SsaAlertStatistic.ResidualCusum:
                return cusum = Math.Max(0, cusum + residual - policy.Allowance);
            case SsaAlertStatistic.PoissonSurprise:
                if (window.Count == policy.Window) window.Dequeue();
                window.Enqueue((count, Math.Max(policy.Floor, double.IsFinite(expectedCount) ? expectedCount : 0)));
                long observed = 0;
                double mean = 0;
                foreach (var (c, mu) in window)
                {
                    observed += c;
                    mean += mu;
                }
                return SsaAdvisory.PoissonSurprise(observed, mean);
            default:
                throw new ArgumentOutOfRangeException(nameof(policy));
        }
    }
}

internal sealed record SsaForecastSpec(SsaForecastRepresentation Representation, int Window, int SeriesLength, int TrainSize,
    int? Rank, int Warmup, long ScaleDivisor)
{
    [JsonIgnore]
    internal bool IsValid => Enum.IsDefined(Representation) && Window >= 2 && SeriesLength > Window && TrainSize > 2 * Window
        && (Rank is null || (Rank >= 1 && Rank < Window))
        && Warmup >= SsaAnomalySpec.StateMemory(Window) && ScaleDivisor >= 1;
}

/// <summary>
/// Locally scaled split-conformal level band. σ_t is the mean absolute completed-interval change over the last
/// <see cref="SigmaWindow"/> intervals, floored at the series scale. Horizon h uses central ± ceil(Multipliers[h]·σ_t).
/// Multipliers are empirical quantiles of |level error| / σ_t on an independent calibration partition.
/// </summary>
internal sealed record SsaIntervalPolicy(int SigmaWindow, double Coverage, ImmutableArray<double> Multipliers)
{
    internal const string Method = "locally-scaled-split-conformal/1";

    [JsonIgnore]
    internal bool IsValid => SigmaWindow >= 1 && Coverage is > 0 and < 1 && !Multipliers.IsDefault
        && Multipliers.Length == SsaAdvisory.Horizon && Multipliers.All(m => double.IsFinite(m) && m >= 0);
}

/// <summary>Model manifest stored beside a promoted R7/2 artifact. Its SHA-256 is the model ID suffix.</summary>
internal sealed record SsaAdvisoryManifest(string Policy, string Kind, string ArtifactSha256, string Generator,
    string TrainingSeriesSha256, SsaAnomalySpec? Anomaly, SsaAlertPolicy? Alert, SsaForecastSpec? Forecast,
    SsaIntervalPolicy? Interval)
{
    internal bool IsValidFor(string kind) => Policy == SsaAdvisory.Policy && Kind == kind
        && !string.IsNullOrWhiteSpace(Generator) && IsHash(ArtifactSha256) && IsHash(TrainingSeriesSha256) && kind switch
        {
            "anomaly" => Anomaly is { IsValid: true } && Alert is { IsValid: true } && Forecast is null && Interval is null,
            "forecast" => Forecast is { IsValid: true } && Interval is { IsValid: true } && Anomaly is null && Alert is null,
            _ => false,
        };

    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigitLower);
}

/// <summary>One scored account bucket. <see cref="Residual"/> is the native SSA raw score in transformed units.</summary>
internal readonly record struct SsaAnomalyStep(int Bucket, long Count, double Residual, double ExpectedCount,
    double? NativePValue, bool NativeAlert, double Statistic, bool Alert);

/// <summary>One forecast origin. Arrays hold horizons +5 … +40 minutes in USD minor units.</summary>
internal sealed record SsaForecastStep(int Bucket, long Available, long Sigma, long[] Central, long[] NativeLower,
    long[] NativeUpper, long[] CalibratedLower, long[] CalibratedUpper);

/// <summary>
/// The pure half of the R7/2 SSA contract: transforms, eligibility, decoding and interval arithmetic. The ML.NET half lives in
/// Hosts.Worker/RevenantSsaInference and calls these functions; research, qualification and the Worker all use that one path.
/// </summary>
internal static class SsaAdvisory
{
    internal const string Policy = "revenant-ssa-advisory/2";
    internal const int Horizon = 8;
    private const double AnscombeOffset = 0.375;
    private static readonly double ZeroAnchor = 2 * Math.Sqrt(AnscombeOffset);

    internal static float AnomalyInput(SsaCountTransform transform, long count)
    {
        if (count is < 0 or > 16_777_216) throw new ArgumentOutOfRangeException(nameof(count));
        return transform switch
        {
            SsaCountTransform.Count => count,
            SsaCountTransform.AnscombeZeroAnchored => (float)(2 * Math.Sqrt(count + AnscombeOffset) - ZeroAnchor),
            _ => throw new ArgumentOutOfRangeException(nameof(transform)),
        };
    }

    /// <summary>Count implied by an SSA-expected transformed input, for the observed-versus-expected explanation.</summary>
    internal static double ExpectedCount(SsaCountTransform transform, double expectedInput)
    {
        if (!double.IsFinite(expectedInput)) return double.NaN;
        return transform switch
        {
            SsaCountTransform.Count => Math.Max(0, expectedInput),
            SsaCountTransform.AnscombeZeroAnchored => Math.Max(0, Math.Pow(Math.Max(0, expectedInput + ZeroAnchor) / 2, 2) - AnscombeOffset),
            _ => throw new ArgumentOutOfRangeException(nameof(transform)),
        };
    }

    /// <summary>A subject is scored only after the warm-up has replaced the training state and shows established activity.</summary>
    internal static bool EligibleAccount(SsaAnomalySpec spec, IReadOnlyList<long> counts)
    {
        if (counts.Count <= spec.Warmup) return false;
        var nonzero = 0;
        for (var i = 0; i < spec.Warmup; i++) if (counts[i] > 0) nonzero++;
        return nonzero >= spec.MinimumNonzero;
    }

    /// <summary>−log10 P(X ≥ observed) for X ~ Poisson(mean); zero when nothing was observed.</summary>
    internal static double PoissonSurprise(long observed, double mean)
    {
        if (observed <= 0) return 0;
        if (!(mean > 0) || !double.IsFinite(mean)) throw new ArgumentOutOfRangeException(nameof(mean));
        // P(X ≥ n) = P(n, μ), the regularized lower incomplete gamma function. Computed in log space.
        return -LogRegularizedLowerGamma(observed, mean) / Math.Log(10);
    }

    private static double LogRegularizedLowerGamma(double a, double x)
    {
        var logPrefix = a * Math.Log(x) - x - LogGamma(a);
        if (x < a + 1)
        {
            double sum = 1 / a, term = 1 / a;
            for (var n = 1; n < 1_000; n++)
            {
                term *= x / (a + n);
                sum += term;
                if (term < sum * 1e-15) break;
            }
            return logPrefix + Math.Log(sum);
        }
        // Lentz continued fraction for Q(a, x); P = 1 − Q.
        double b = x + 1 - a, c = 1 / 1e-300, d = 1 / b, h = d;
        for (var i = 1; i < 1_000; i++)
        {
            var an = -i * (i - a);
            b += 2;
            d = an * d + b;
            if (Math.Abs(d) < 1e-300) d = 1e-300;
            c = b + an / c;
            if (Math.Abs(c) < 1e-300) c = 1e-300;
            d = 1 / d;
            var delta = d * c;
            h *= delta;
            if (Math.Abs(delta - 1) < 1e-15) break;
        }
        var q = Math.Exp(logPrefix) * h;
        return Math.Log(Math.Max(1e-300, 1 - q));
    }

    private static double LogGamma(double z)
    {
        // Lanczos approximation (g = 7, n = 9), accurate to ~1e-15 for z > 0.
        double[] c = [0.99999999999980993, 676.5203681218851, -1259.1392167224028, 771.32342877765313, -176.61502916214059,
            12.507343278686905, -0.13857109526572012, 9.9843695780195716e-6, 1.5056327351493116e-7];
        z -= 1;
        var x = c[0];
        for (var i = 1; i < 9; i++) x += c[i] / (z + i);
        var t = z + 7.5;
        return 0.5 * Math.Log(2 * Math.PI) + (z + 0.5) * Math.Log(t) - t + Math.Log(x);
    }

    internal static long Scale(long opening, long divisor)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(opening);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(divisor);
        return Math.Max(1, opening / divisor);
    }

    internal static float ForecastInput(SsaForecastRepresentation representation, long opening, long scale, long previous, long current)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scale);
        var delta = representation switch
        {
            SsaForecastRepresentation.OpeningCenteredLevel => (Int128)current - opening,
            SsaForecastRepresentation.CompletedIntervalChange => (Int128)current - previous,
            _ => throw new ArgumentOutOfRangeException(nameof(representation)),
        };
        var value = (float)((double)delta / scale);
        if (!float.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(current));
        return value;
    }

    /// <summary>Decodes native SSA outputs into eight level paths: central and the native band in that representation.</summary>
    internal static (long[] Central, long[] Lower, long[] Upper) DecodeLevels(SsaForecastRepresentation representation, long opening,
        long scale, long available, ReadOnlySpan<float> forecast, ReadOnlySpan<float> lower, ReadOnlySpan<float> upper)
    {
        if (forecast.Length != Horizon || lower.Length != Horizon || upper.Length != Horizon)
            throw new InvalidDataException("Invalid ML.NET forecast dimensions.");
        var central = new long[Horizon];
        var low = new long[Horizon];
        var high = new long[Horizon];
        double sum = 0, sumLow = 0, sumHigh = 0;
        for (var h = 0; h < Horizon; h++)
        {
            if (!float.IsFinite(forecast[h]) || !float.IsFinite(lower[h]) || !float.IsFinite(upper[h])
                || lower[h] > forecast[h] || upper[h] < forecast[h])
                throw new InvalidDataException("Invalid ML.NET forecast interval.");
            if (representation == SsaForecastRepresentation.OpeningCenteredLevel)
            {
                central[h] = Offset(opening, (double)forecast[h] * scale);
                low[h] = Offset(opening, (double)lower[h] * scale);
                high[h] = Offset(opening, (double)upper[h] * scale);
                continue;
            }
            // Point changes accumulate exactly as the recurrence predicts. The native per-change bounds are summed only as a
            // labelled diagnostic: correlated multi-step errors make that sum neither a calibrated nor a nominal 95% level band.
            sum += forecast[h];
            sumLow += lower[h];
            sumHigh += upper[h];
            central[h] = Offset(available, sum * scale);
            low[h] = Offset(available, sumLow * scale);
            high[h] = Offset(available, sumHigh * scale);
        }
        return (central, low, high);
    }

    /// <summary>Causal local scale: mean absolute completed-interval change over the window ending at <paramref name="origin"/>.</summary>
    internal static long Sigma(IReadOnlyList<long> available, long opening, long scale, int origin, int window)
    {
        if (origin < 0 || origin >= available.Count || window < 1) throw new ArgumentOutOfRangeException(nameof(origin));
        Int128 total = 0;
        var count = 0;
        for (var j = Math.Max(0, origin - window + 1); j <= origin; j++, count++)
            total += Int128.Abs((Int128)available[j] - (j == 0 ? opening : available[j - 1]));
        var mean = (long)((total + count / 2) / count);
        return Math.Max(scale, mean);
    }

    internal static (long Lower, long Upper) Band(long central, long sigma, double multiplier)
    {
        var half = checked((long)Math.Ceiling(multiplier * sigma));
        return (checked(central - half), checked(central + half));
    }

    /// <summary>Split-conformal multiplier: the ⌈(n+1)·coverage⌉-th smallest normalized absolute error.</summary>
    internal static double ConformalQuantile(IReadOnlyCollection<double> normalizedErrors, double coverage)
    {
        if (normalizedErrors.Count == 0) throw new InvalidDataException("Interval calibration needs observed errors.");
        var sorted = normalizedErrors.Order().ToArray();
        var rank = (int)Math.Ceiling((sorted.Length + 1) * coverage) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
    }

    private static long Offset(long origin, double scaledDelta)
    {
        if (!double.IsFinite(scaledDelta)) throw new InvalidDataException("Nonfinite SSA level.");
        return checked(origin + (long)Math.Round(scaledDelta, MidpointRounding.ToEven));
    }
}
