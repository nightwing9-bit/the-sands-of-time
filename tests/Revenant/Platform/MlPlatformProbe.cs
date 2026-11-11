using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.Transforms.TimeSeries;

namespace Revenant.Tests.Platform;

/// <summary>What one probe actually observed. Written next to the test output so a run can be quoted, not remembered.</summary>
public sealed record MlProbeReport(
    string Probe,
    string ProcessArchitecture,
    string OsDescription,
    string FrameworkDescription,
    string RuntimeIdentifier,
    string MlNetVersion,
    string FastTreeVersion,
    string TimeSeriesVersion,
    string[] NativeLibrariesLoaded,
    string[] Observations);

public sealed class ProbeRow
{
    public bool Label { get; set; }

    [VectorType(4)]
    public float[] Features { get; set; } = [];
}

public sealed class ProbePrediction
{
    public bool PredictedLabel { get; set; }

    public float Probability { get; set; }

    public float Score { get; set; }
}

public sealed class SeriesPoint
{
    public float Value { get; set; }
}

public sealed class SpikeOutput
{
    /// <summary>[alert (0/1), raw score, p-value].</summary>
    [VectorType(3)]
    public double[] Prediction { get; set; } = [];
}

public sealed class ForecastOutput
{
    public float[] Forecast { get; set; } = [];

    public float[] LowerBound { get; set; } = [];

    public float[] UpperBound { get; set; } = [];
}

/// <summary>
/// Spec 08 task 8.1 platform probe. Tiny, seeded, no PaySim and no database. Each method really fits a pipeline, saves it to a
/// ZIP, loads it back and predicts, then reports the process architecture, versions and the native libraries the process has mapped.
/// SSA uses the pinned starter settings of design.md section 4/5 (spike: confidence 95, training 32, seasonality 8, p-value history 16;
/// forecast: window 8, series 32, train 96, horizon 8, non-adaptive, 95 percent bounds).
/// </summary>
public static class MlPlatformProbe
{
    public const string SsaRequirement = "SSA probes need Linux x64 (glibc): run scripts/checks/ml-platform-probe.sh --docker-amd64 or the ml-platform-x64 CI job.";

    public static bool IsLinuxX64 => OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    /// <summary>Deterministic non-negative series with period 8 and tiny seeded jitter.</summary>
    public static float[] Series(int count, int seed)
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

    public static MlProbeReport FastTreeFitSaveLoadPredict(string workDir)
    {
        var ml = new MLContext(seed: 7);
        var rows = FastTreeRows(400);
        var data = ml.Data.LoadFromEnumerable(rows);
        var trainer = ml.BinaryClassification.Trainers.FastTree(
            labelColumnName: "Label", featureColumnName: "Features",
            numberOfLeaves: 8, numberOfTrees: 30, minimumExampleCountPerLeaf: 5, learningRate: 0.2);
        var model = trainer.Fit(data);
        var path = Path.Combine(workDir, "fasttree-probe.zip");
        ml.Model.Save(model, data.Schema, path);
        var loaded = ml.Model.Load(path, out _);

        var risky = new ProbeRow { Features = [9f, 0f, 10f, 1f] };
        var safe = new ProbeRow { Features = [3f, 11f, 0.01f, 0f] };
        var before = ml.Model.CreatePredictionEngine<ProbeRow, ProbePrediction>(model);
        var after = ml.Model.CreatePredictionEngine<ProbeRow, ProbePrediction>(loaded);
        var (b1, b2) = (before.Predict(risky), before.Predict(safe));
        var (a1, a2) = (after.Predict(risky), after.Predict(safe));
        Require(Finite01(a1.Probability) && Finite01(a2.Probability), "FastTree probability is not finite in [0,1].");
        Require(a1.Probability > a2.Probability, "FastTree did not rank the risky row above the safe row.");
        Require(b1.Probability == a1.Probability && b2.Probability == a2.Probability, "Loaded FastTree model differs from the fitted one.");

        return Report("fasttree-fit-save-load-predict",
        [
            $"zipBytes={new FileInfo(path).Length}",
            $"probabilityRisky={a1.Probability.ToString("R", CultureInfo.InvariantCulture)}",
            $"probabilitySafe={a2.Probability.ToString("R", CultureInfo.InvariantCulture)}",
            "loadedEqualsFitted=true",
        ]);
    }

    public static MlProbeReport SpikeBySsaFitSaveLoadPredict(string workDir)
    {
        RequireX64();
        var ml = new MLContext(seed: 7);
        var training = Series(64, 1).Select(v => new SeriesPoint { Value = v }).ToArray();
        var data = ml.Data.LoadFromEnumerable(training);
        var pipeline = ml.Transforms.DetectSpikeBySsa(
            outputColumnName: nameof(SpikeOutput.Prediction), inputColumnName: nameof(SeriesPoint.Value),
            confidence: 95d, pvalueHistoryLength: 16, trainingWindowSize: 32, seasonalityWindowSize: 8, side: AnomalySide.Positive);
        var model = pipeline.Fit(data);
        var path = Path.Combine(workDir, "ssa-spike-probe.zip");
        ml.Model.Save(model, data.Schema, path);
        var loaded = ml.Model.Load(path, out _);

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

        return Report("detect-spike-by-ssa-fit-save-load-predict",
        [
            $"zipBytes={new FileInfo(path).Length}",
            $"calmAlertsInFirst40={calmAlerts}",
            $"calmPrediction={string.Join(",", last.Prediction.Select(d => d.ToString("R", CultureInfo.InvariantCulture)))} (alert {calmAlert.ToString(CultureInfo.InvariantCulture)})",
            $"spikePrediction={string.Join(",", spike.Prediction.Select(d => d.ToString("R", CultureInfo.InvariantCulture)))}",
        ]);
    }

    public static MlProbeReport ForecastBySsaFitSaveLoadPredict(string workDir)
    {
        RequireX64();
        var ml = new MLContext(seed: 7);
        var training = Series(120, 2).Select(v => new SeriesPoint { Value = v }).ToArray();
        var data = ml.Data.LoadFromEnumerable(training);
        var pipeline = ml.Forecasting.ForecastBySsa(
            outputColumnName: nameof(ForecastOutput.Forecast), inputColumnName: nameof(SeriesPoint.Value),
            windowSize: 8, seriesLength: 32, trainSize: 96, horizon: 8, isAdaptive: false, confidenceLevel: 0.95f,
            confidenceLowerBoundColumn: nameof(ForecastOutput.LowerBound), confidenceUpperBoundColumn: nameof(ForecastOutput.UpperBound));
        var model = pipeline.Fit(data);
        var path = Path.Combine(workDir, "ssa-forecast-probe.zip");
        ml.Model.Save(model, data.Schema, path);
        var loaded = ml.Model.Load(path, out _);

        using var engine = loaded.CreateTimeSeriesEngine<SeriesPoint, ForecastOutput>(ml);
        var f = engine.Predict();
        Require(f.Forecast.Length == 8 && f.LowerBound.Length == 8 && f.UpperBound.Length == 8, "ForecastBySsa did not return 8 steps with bounds.");
        Require(f.Forecast.Concat(f.LowerBound).Concat(f.UpperBound).All(float.IsFinite), "ForecastBySsa returned a non-finite value.");
        for (var i = 0; i < 8; i++)
        {
            Require(f.LowerBound[i] <= f.Forecast[i] && f.Forecast[i] <= f.UpperBound[i], $"Bounds do not enclose the forecast at step {i}.");
        }
        Require(f.Forecast.Max() - f.Forecast.Min() > 1f, "ForecastBySsa returned an implausibly flat path for a period-8 series.");

        return Report("forecast-by-ssa-fit-save-load-predict",
        [
            $"zipBytes={new FileInfo(path).Length}",
            $"forecast={string.Join(",", f.Forecast.Select(v => v.ToString("R", CultureInfo.InvariantCulture)))}",
            $"lower={string.Join(",", f.LowerBound.Select(v => v.ToString("R", CultureInfo.InvariantCulture)))}",
            $"upper={string.Join(",", f.UpperBound.Select(v => v.ToString("R", CultureInfo.InvariantCulture)))}",
        ]);
    }

    /// <summary>Writes the report as JSON in <c>ML_PROBE_OUT</c> (default: next to the test binaries).</summary>
    public static string Write(MlProbeReport report)
    {
        var dir = Environment.GetEnvironmentVariable("ML_PROBE_OUT");
        dir = string.IsNullOrWhiteSpace(dir) ? Path.Combine(AppContext.BaseDirectory, "ml-platform") : dir;
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{report.Probe}.{report.ProcessArchitecture.ToLowerInvariant()}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    private static void RequireX64()
    {
        if (!IsLinuxX64)
        {
            throw new PlatformNotSupportedException(
                $"{SsaRequirement} Observed {RuntimeInformation.OSDescription} / {RuntimeInformation.ProcessArchitecture}.");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static bool Finite01(float v) => float.IsFinite(v) && v is >= 0f and <= 1f;

    private static List<ProbeRow> FastTreeRows(int count)
    {
        var rows = new List<ProbeRow>(count);
        var state = 12345UL;
        float Next()
        {
            state = state * 6364136223846793005UL + 1442695040888963407UL;
            return ((state >> 40) % 1000) / 1000f;
        }
        for (var i = 0; i < count; i++)
        {
            var risky = i % 4 == 0;
            rows.Add(new ProbeRow
            {
                Label = risky,
                Features = risky
                    ? [7f + 3f * Next(), 4f * Next(), 5f + 5f * Next(), Next() < 0.8f ? 1f : 0f]
                    : [1f + 6f * Next(), 3f + 9f * Next(), 0.5f * Next(), Next() < 0.05f ? 1f : 0f],
            });
        }
        return rows;
    }

    private static MlProbeReport Report(string probe, string[] observations)
    {
        string Version(Type t) => t.Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        return new MlProbeReport(
            probe,
            RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeInformation.OSDescription,
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.RuntimeIdentifier,
            Version(typeof(MLContext)),
            Version(typeof(Microsoft.ML.Trainers.FastTree.FastTreeBinaryTrainer)),
            Version(typeof(SrCnnAnomalyDetector)),
            LoadedNativeLibraries(),
            observations);
    }

    /// <summary>Distinct file names of ML.NET / MKL / OpenMP native objects mapped into this process (Linux only; empty elsewhere).</summary>
    private static string[] LoadedNativeLibraries()
    {
        const string maps = "/proc/self/maps";
        if (!OperatingSystem.IsLinux() || !File.Exists(maps))
        {
            return [];
        }
        return File.ReadLines(maps)
            .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(p => p.Length >= 6)
            .Select(p => Path.GetFileName(p[^1]))
            .Where(n => n.Contains("mkl", StringComparison.OrdinalIgnoreCase)
                || n.Contains("omp", StringComparison.OrdinalIgnoreCase)
                || n.Contains("FastTreeNative", StringComparison.OrdinalIgnoreCase)
                || n.Contains("CpuMath", StringComparison.OrdinalIgnoreCase)
                || n.Contains("MklImports", StringComparison.OrdinalIgnoreCase)
                || n.Contains("Microsoft.ML.", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }
}
