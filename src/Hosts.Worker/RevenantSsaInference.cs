using Microsoft.ML;
using Microsoft.ML.Transforms.TimeSeries;

namespace Hosts.Worker;

internal readonly record struct RevenantSpike(bool Alert, double RawScore, double PValue);

internal static class RevenantSsaInference
{
    internal static TimeSeriesPredictionEngine<SeriesPoint, T> Load<T>(MLContext ml, Stream artifact) where T : class, new()
    {
        RequireSupportedPlatform();
        return ml.Model.Load(artifact, out _).CreateTimeSeriesEngine<SeriesPoint, T>(ml);
    }

    internal static RevenantSpike ObserveAccount(TimeSeriesPredictionEngine<SeriesPoint, SpikeOutput> engine, long count)
    {
        if (count is < 0 or > 16_777_216)
            throw new InvalidDataException("Account count cannot be represented exactly as an ML.NET Single.");
        var raw = engine.Predict(new SeriesPoint { Value = count }).Prediction;
        if (raw.Length != 3 || raw.Any(x => !double.IsFinite(x)) || raw[0] is not (0d or 1d) || raw[2] is < 0 or > 1)
            throw new InvalidDataException("Invalid ML.NET anomaly output.");
        return new(raw[0] == 1, raw[1], raw[2]);
    }

    internal static ForecastOutput ObserveBank(TimeSeriesPredictionEngine<SeriesPoint, ForecastOutput> engine, float value)
    {
        if (!float.IsFinite(value)) throw new InvalidDataException("Invalid normalized bank input.");
        return engine.Predict(new SeriesPoint { Value = value });
    }

    internal static void VerifyForecast(ForecastOutput forecast, int horizons)
    {
        if (forecast.Forecast.Length != horizons || forecast.LowerBound.Length != horizons || forecast.UpperBound.Length != horizons)
            throw new InvalidDataException("Invalid ML.NET forecast dimensions.");
        for (var h = 0; h < horizons; h++)
        {
            var (point, lower, upper) = (forecast.Forecast[h], forecast.LowerBound[h], forecast.UpperBound[h]);
            if (!float.IsFinite(point) || !float.IsFinite(lower) || !float.IsFinite(upper) || lower > point || upper < point)
                throw new InvalidDataException("Invalid ML.NET forecast interval.");
        }
    }

    private static void RequireSupportedPlatform()
    {
        if (!OperatingSystem.IsLinux() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
            != System.Runtime.InteropServices.Architecture.X64)
            throw new PlatformNotSupportedException("Revenant SSA inference requires Linux AMD64.");
    }
}
