namespace Revenant.Intelligence;

internal readonly record struct AdvisoryBalanceNormalization
{
    internal const string Schema = "available-balance-opening-centered/1";
    internal long ReferenceMinor { get; }
    internal long ScaleMinor { get; }

    internal AdvisoryBalanceNormalization(long referenceMinor, long scaleMinor)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(referenceMinor);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scaleMinor);
        ReferenceMinor = referenceMinor;
        ScaleMinor = scaleMinor;
    }

    internal float Normalize(long availableMinor)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(availableMinor);
        RequireValidScale();
        var delta = (Int128)availableMinor - ReferenceMinor;
        return (float)((double)delta / ScaleMinor);
    }

    internal long Restore(float value)
    {
        if (!float.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
        RequireValidScale();
        var delta = (double)value * ScaleMinor;
        var roundedDelta = checked((Int128)Math.Round(delta, MidpointRounding.ToEven));
        return checked((long)(ReferenceMinor + roundedDelta));
    }

    private void RequireValidScale()
    {
        if (ScaleMinor <= 0) throw new InvalidOperationException("An advisory normalization requires a positive pinned scale.");
    }
}
