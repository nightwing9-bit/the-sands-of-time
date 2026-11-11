using Xunit;
using Xunit.Abstractions;

namespace Revenant.Tests.Platform;

/// <summary>
/// Spec 08 task 8.1 platform preflight. <c>MlFastTree</c> runs on any supported host (native macOS ARM64 or Linux).
/// <c>MlSsaX64</c> needs real MKL/OpenMP on Linux x64 and fails loudly anywhere else: it is excluded from the default
/// fast filters and run by <c>scripts/checks/ml-platform-probe.sh</c> and the <c>ml-platform-x64</c> CI job, which also
/// require that the tests actually executed (no silent skip).
/// </summary>
public sealed class MlPlatformProbeTests(ITestOutputHelper output) : IDisposable
{
    private readonly string workDir = Directory.CreateTempSubdirectory("ml-probe-").FullName;

    [Fact]
    [Trait("Category", "MlFastTree")]
    public void FastTree_fits_saves_loads_and_predicts_on_this_host() => Run(MlPlatformProbe.FastTreeFitSaveLoadPredict);

    [Fact]
    [Trait("Category", "MlSsaX64")]
    public void DetectSpikeBySsa_fits_saves_loads_and_predicts_on_linux_x64() => Run(MlPlatformProbe.SpikeBySsaFitSaveLoadPredict);

    [Fact]
    [Trait("Category", "MlSsaX64")]
    public void ForecastBySsa_fits_saves_loads_and_predicts_on_linux_x64() => Run(MlPlatformProbe.ForecastBySsaFitSaveLoadPredict);

    private void Run(Func<string, MlProbeReport> probe)
    {
        var report = probe(workDir);
        var path = MlPlatformProbe.Write(report);
        output.WriteLine($"{report.Probe}: {report.ProcessArchitecture} | {report.OsDescription} | {report.FrameworkDescription} | RID {report.RuntimeIdentifier}");
        output.WriteLine($"ML.NET {report.MlNetVersion} | FastTree {report.FastTreeVersion} | TimeSeries {report.TimeSeriesVersion}");
        output.WriteLine($"native libraries mapped: {string.Join(", ", report.NativeLibrariesLoaded)}");
        foreach (var o in report.Observations)
        {
            output.WriteLine(o);
        }
        output.WriteLine($"report: {path}");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(workDir, recursive: true);
        }
        catch (IOException)
        {
            // temp probe files only
        }
    }
}
