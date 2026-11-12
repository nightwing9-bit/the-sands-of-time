using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.ML;
using Microsoft.ML.Trainers.FastTree;
using Microsoft.ML.Transforms.TimeSeries;
using Revenant.Intelligence;

namespace Hosts.Worker;

internal static class MlRuntimeSelfTest
{
    private sealed class Workload
    {
        public bool Executed { get; set; }
        public bool Fit { get; set; }
        public bool SaveLoad { get; set; }
        public bool PredictionValid { get; set; }
        public string[] Observations { get; set; } = [];
        public string? Error { get; set; }
    }

    public static int Run()
    {
        var spike = new Workload();
        var forecast = new Workload();
        var fastTree = "NOT_RUN";
        string? fastTreeError = null;
        string[] nativeLibraries = [];
        string? environmentError = null;
        var mlNet = VersionOf(typeof(MLContext));
        var timeSeries = VersionOf(typeof(SrCnnAnomalyDetector));
        var fastTreeVersion = VersionOf(typeof(FastTreeBinaryTrainer));
        try
        {
            if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            {
                throw new PlatformNotSupportedException("The published SSA runtime requires Linux x64.");
            }
            var work = Directory.CreateTempSubdirectory("worker-ml-");
            try
            {
                Execute(spike, work.FullName, MlRuntimeSsa.SpikeBySsaFitSaveLoadPredict);
                Execute(forecast, work.FullName, MlRuntimeSsa.ForecastBySsaFitSaveLoadPredict);
                nativeLibraries = File.ReadLines("/proc/self/maps")
                    .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    .Where(parts => parts.Length >= 6)
                    .Select(parts => Path.GetFileName(parts[5]))
                    .Where(name => name.StartsWith("libMkl", StringComparison.Ordinal)
                        || name.StartsWith("libomp", StringComparison.Ordinal) || name.StartsWith("libiomp", StringComparison.Ordinal))
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                if (!nativeLibraries.Contains("libMklImports.so", StringComparer.Ordinal)
                    || !nativeLibraries.Any(name => name.StartsWith("libomp", StringComparison.Ordinal) || name.StartsWith("libiomp", StringComparison.Ordinal)))
                {
                    throw new InvalidOperationException("Expected libMklImports.so and an OpenMP runtime (libiomp5.so) in /proc/self/maps.");
                }
            }
            finally
            {
                work.Delete(recursive: true);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            environmentError = ex.ToString();
        }

        try
        {
            VerifyFastTree();
            fastTree = "PASS";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            fastTree = "FAIL";
            fastTreeError = ex.ToString();
        }

        var pinnedVersion = RiskPolicyCatalog.V1.Model.MlNetPackageVersion;
        var passed = environmentError is null && spike.PredictionValid && forecast.PredictionValid && fastTree == "PASS"
            && mlNet == pinnedVersion && timeSeries == pinnedVersion && fastTreeVersion == pinnedVersion;
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Result = passed ? "PASS" : "FAIL",
            RuntimeInformation.OSDescription,
            ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeInformation.RuntimeIdentifier,
            DotNet = RuntimeInformation.FrameworkDescription,
            MlNet = mlNet,
            TimeSeries = timeSeries,
            FastTreeVersion = fastTreeVersion,
            Image = Environment.GetEnvironmentVariable("ML_RUNTIME_IMAGE_ID"),
            Spike = spike,
            Forecast = forecast,
            SaveLoad = spike.SaveLoad && forecast.SaveLoad,
            PredictionValid = spike.PredictionValid && forecast.PredictionValid,
            FastTree = fastTree,
            FastTreeError = fastTreeError,
            NativeLibraries = nativeLibraries,
            EnvironmentError = environmentError,
        }));
        return passed ? 0 : 1;
    }

    private static void Execute(Workload result, string workDir, Func<string, Action?, Action?, string[]> workload)
    {
        try
        {
            result.Executed = true;
            result.Observations = workload(workDir, () => result.Fit = true, () => result.SaveLoad = true);
            result.PredictionValid = true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            result.Error = ex.ToString();
        }
    }

    private static void VerifyFastTree()
    {
        var policy = RiskPolicyCatalog.V1;
        var loaded = new RiskModelProvider(RiskModelOptions.DefaultArtifactRoot).Get(policy);
        if (!loaded.TryGetValue(out var scorer))
        {
            throw new InvalidOperationException($"Pinned FastTree load failed: {loaded.Error.Code}: {loaded.Error.Detail}");
        }
        // Provider verified the exact bytes, manifest seal and full pinned identity.
        var predictions = scorer.Manifest.ReferencePredictions;
        if (predictions.Length == 0)
        {
            throw new InvalidOperationException("The pinned manifest has no reference predictions.");
        }
        foreach (var reference in predictions)
        {
            var snapshot = RiskFeaturesV1.Build(reference.Currency, reference.AmountMinor, reference.SourcePostedBeforeMinor).Value;
            var ppm = scorer.Score(snapshot.Vector).Value;
            var raw = scorer.Probabilities([snapshot.Vector]).Single();
            var rounded = (int)Math.Round((double)raw * RiskScoreNormalization.PpmScale, MidpointRounding.AwayFromZero);
            if (snapshot.FeatureHash != reference.FeatureHash || ppm != reference.ScorePpm || ppm != rounded
                || RiskScoreNormalization.AtOrAboveThreshold(ppm, policy.Model.ThresholdPpm) != reference.HoldAtThreshold)
            {
                throw new InvalidOperationException($"Pinned FastTree reference prediction differed for feature hash {reference.FeatureHash}.");
            }
        }
    }

    private static string VersionOf(Type type) =>
        (type.Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown").Split('+')[0];
}
