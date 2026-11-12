using System.Globalization;
using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.Transforms.TimeSeries;

namespace Hosts.Worker;

internal sealed class SeriesPoint
{
    public float Value { get; set; }
}

internal sealed class SpikeOutput
{
    /// <summary>[alert (0/1), raw score, p-value].</summary>
    [VectorType(3)]
    public double[] Prediction { get; set; } = [];
}

internal sealed class ForecastOutput
{
    public float[] Forecast { get; set; } = [];

    public float[] LowerBound { get; set; } = [];

    public float[] UpperBound { get; set; } = [];
}

/// <summary>Deterministic synthetic SSA workloads shared by the Worker self-test and Linux x64 platform probe.</summary>
internal static class MlRuntimeSsa
{
    /// <summary>Deterministic non-negative series with period 8 and tiny seeded jitter.</summary>
    private static float[] Series(int count, int seed)
    {
        var state = (ulong)seed * 0x9E3779B97F4A7C15UL + 1;
        var values = new float[count];
        for (var i = 0; i < count; i++)
        {
            state = state * 6364136223846793005UL + 1442695040888963407UL;
            var jitter = ((state >> 40) % 100) / 100f - 0.5f; // [-0.5, 0.5)
            values[i] = 20f + 5f * MathF.Sin(2f * MathF.PI * i / 8f) + jitter;
        }
        return values;
    }

    public static string[] SpikeBySsaFitSaveLoadPredict(string workDir, Action? fitted = null, Action? reloaded = null)
    {
        var ml = new MLContext(seed: 7);
        var training = Series(64, 1).Select(v => new SeriesPoint { Value = v }).ToArray();
        var data = ml.Data.LoadFromEnumerable(training);
        var pipeline = ml.Transforms.DetectSpikeBySsa(
            outputColumnName: nameof(SpikeOutput.Prediction), inputColumnName: nameof(SeriesPoint.Value),
            confidence: 95d, pvalueHistoryLength: 16, trainingWindowSize: 32, seasonalityWindowSize: 8, side: AnomalySide.Positive);
        var model = pipeline.Fit(data);
        fitted?.Invoke();
        var path = Path.Combine(workDir, "ssa-spike-probe.zip");
        ml.Model.Save(model, data.Schema, path);
        var loaded = ml.Model.Load(path, out _);
        reloaded?.Invoke();

        using var engine = loaded.CreateTimeSeriesEngine<SeriesPoint, SpikeOutput>(ml);
        // The loaded detector starts with an empty score history: feed more than trainingWindowSize calm buckets before the spike.
        var continuation = Series(120, 1).Skip(64).ToArray();
        SpikeOutput last = new();
        var calmAlerts = 0;
        foreach (var v in continuation.Take(40))
        {
            last = engine.Predict(new SeriesPoint { Value = v });
            Require(last.Prediction.Length == 3 && last.Prediction.All(double.IsFinite), "SSA spike output is not three finite numbers.");
            calmAlerts += (int)last.Prediction[0];
        }
        var calmAlert = last.Prediction[0];
        var spike = engine.Predict(new SeriesPoint { Value = 200f });
        Require(spike.Prediction.Length == 3 && spike.Prediction.All(double.IsFinite), "SSA spike output for the spike is not finite.");
        Require(spike.Prediction[0] == 1d, $"SSA did not alert on an obvious positive spike (prediction [{string.Join(",", spike.Prediction)}]).");

        return
        [
            $"zipBytes={new FileInfo(path).Length}",
            $"calmAlertsInFirst40={calmAlerts}",
            $"calmPrediction={string.Join(",", last.Prediction.Select(d => d.ToString("R", CultureInfo.InvariantCulture)))} (alert {calmAlert.ToString(CultureInfo.InvariantCulture)})",
            $"spikePrediction={string.Join(",", spike.Prediction.Select(d => d.ToString("R", CultureInfo.InvariantCulture)))}",
        ];
    }

    public static string[] ForecastBySsaFitSaveLoadPredict(string workDir, Action? fitted = null, Action? reloaded = null)
    {
        var ml = new MLContext(seed: 7);
        var training = Series(120, 2).Select(v => new SeriesPoint { Value = v }).ToArray();
        var data = ml.Data.LoadFromEnumerable(training);
        var pipeline = ml.Forecasting.ForecastBySsa(
            outputColumnName: nameof(ForecastOutput.Forecast), inputColumnName: nameof(SeriesPoint.Value),
            windowSize: 8, seriesLength: 32, trainSize: 96, horizon: 8, isAdaptive: false, confidenceLevel: 0.95f,
            confidenceLowerBoundColumn: nameof(ForecastOutput.LowerBound), confidenceUpperBoundColumn: nameof(ForecastOutput.UpperBound));
        var model = pipeline.Fit(data);
        fitted?.Invoke();
        var path = Path.Combine(workDir, "ssa-forecast-probe.zip");
        ml.Model.Save(model, data.Schema, path);
        var loaded = ml.Model.Load(path, out _);
        reloaded?.Invoke();

        using var engine = loaded.CreateTimeSeriesEngine<SeriesPoint, ForecastOutput>(ml);
        var f = engine.Predict();
        Require(f.Forecast.Length == 8 && f.LowerBound.Length == 8 && f.UpperBound.Length == 8, "ForecastBySsa did not return 8 steps with bounds.");
        Require(f.Forecast.Concat(f.LowerBound).Concat(f.UpperBound).All(float.IsFinite), "ForecastBySsa returned a non-finite value.");
        for (var i = 0; i < 8; i++)
        {
            Require(f.LowerBound[i] <= f.Forecast[i] && f.Forecast[i] <= f.UpperBound[i], $"Bounds do not enclose the forecast at step {i}.");
        }
        Require(f.Forecast.Max() - f.Forecast.Min() > 1f, "ForecastBySsa returned an implausibly flat path for a period-8 series.");

        return
        [
            $"zipBytes={new FileInfo(path).Length}",
            $"forecast={string.Join(",", f.Forecast.Select(v => v.ToString("R", CultureInfo.InvariantCulture)))}",
            $"lower={string.Join(",", f.LowerBound.Select(v => v.ToString("R", CultureInfo.InvariantCulture)))}",
            $"upper={string.Join(",", f.UpperBound.Select(v => v.ToString("R", CultureInfo.InvariantCulture)))}",
        ];
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
