namespace Revenant.Intelligence;

internal enum SsaTrailingRateDecoder
{
    TrailingRateExactInverse,
    TrailingRateIntegratedExtrapolation,
}

/// <summary>Development contract for a twelve-completed-interval rate and two distinct level decoders.</summary>
internal static class SsaTrailingRate
{
    internal const int Window = 12;

    // A[-1] is the opening balance. A[11] is the first seal with twelve observed intervals.
    internal const int FirstRate = Window - 1;

    internal static double Rate(long lagged, long current, long scale, int window = Window)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scale);
        return checked(current - lagged) / (window * (double)scale);
    }

    internal static float Input(IReadOnlyList<long> balances, long opening, long scale, int bucket, int window = Window)
    {
        if (bucket < window - 1 || bucket >= balances.Count)
            throw new ArgumentOutOfRangeException(nameof(bucket));
        var lagged = bucket == window - 1 ? opening : balances[bucket - window];
        return (float)Rate(lagged, balances[bucket], scale, window);
    }

    internal static double ZeroInnovationRate(IReadOnlyList<long> balances, long scale, int origin, int horizon)
    {
        ValidateOrigin(balances, origin);
        if (horizon is < 1 or > SsaAdvisory.Horizon)
            throw new ArgumentOutOfRangeException(nameof(horizon));
        return Rate(balances[origin + horizon - Window], balances[origin], scale);
    }

    internal static long[] Decode(SsaTrailingRateDecoder decoder, IReadOnlyList<long> balances, long scale, int origin,
        ReadOnlySpan<float> rates, int window = Window)
    {
        if (window is not (3 or Window)) throw new ArgumentOutOfRangeException(nameof(window));
        if (origin < window - 1 || origin >= balances.Count) throw new ArgumentOutOfRangeException(nameof(origin));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scale);
        if (rates.Length != SsaAdvisory.Horizon)
            throw new InvalidDataException("Expected eight finite trailing-rate forecasts.");
        var levels = new long[rates.Length];
        double cumulative = 0;
        for (var i = 0; i < rates.Length; i++)
        {
            if (!float.IsFinite(rates[i]))
                throw new InvalidDataException("Nonfinite trailing-rate forecast.");
            cumulative += rates[i];
            var anchor = origin + i + 1 - window;
            levels[i] = decoder switch
            {
                SsaTrailingRateDecoder.TrailingRateExactInverse =>
                    AddMovement(anchor <= origin ? balances[anchor] : levels[i - window], rates[i] * (window * (double)scale)),
                SsaTrailingRateDecoder.TrailingRateIntegratedExtrapolation =>
                    AddMovement(balances[origin], cumulative * scale),
                _ => throw new ArgumentOutOfRangeException(nameof(decoder)),
            };
        }
        return levels;
    }

    /// <summary>Lowest weighted median of actual/predicted, weighted by |predicted|, then clipped to [0, 1].</summary>
    internal static double FitWeight(IEnumerable<(long Actual, long Predicted)> samples)
    {
        var points = samples.Where(s => s.Predicted != 0).Select(s =>
            (Ratio: (double)s.Actual / s.Predicted, Weight: Int128.Abs((Int128)s.Predicted))).OrderBy(s => s.Ratio).ToArray();
        Int128 total = 0;
        foreach (var point in points) total += point.Weight;
        Int128 cumulative = 0;
        foreach (var point in points)
        {
            cumulative += point.Weight;
            if (2 * cumulative >= total) return Math.Clamp(point.Ratio, 0, 1);
        }
        return 0;
    }

    internal static long Shrink(long origin, long proposedLevel, double weight)
    {
        if (!double.IsFinite(weight) || weight is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(weight));
        return AddMovement(origin, checked(proposedLevel - origin) * weight);
    }

    internal static long AddMovement(long level, double movement)
    {
        if (!double.IsFinite(movement)) throw new InvalidDataException("Nonfinite forecast movement.");
        return checked(level + (long)Math.Round(movement, MidpointRounding.ToEven));
    }

    private static void ValidateOrigin(IReadOnlyList<long> balances, int origin)
    {
        if (origin < FirstRate || origin >= balances.Count)
            throw new ArgumentOutOfRangeException(nameof(origin));
    }
}
