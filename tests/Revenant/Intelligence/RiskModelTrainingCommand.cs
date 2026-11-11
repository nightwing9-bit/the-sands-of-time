using Revenant.Intelligence;
using Xunit;

namespace Revenant.Tests.Intelligence;

/// <summary>
/// The repeatable OFFLINE command for minting the committed fixture artifact: <c>scripts/checks/train-risk-model.sh</c>. It is a
/// test only because the repository has no tools project; it is excluded from normal CI by Category=OfflineTraining and fails loudly
/// (never silently passes) when run without its output directory. It refuses to overwrite an existing artifact: a retrain is a new
/// model id in a new directory, never an edit of a pinned file.
/// </summary>
public sealed class RiskModelTrainingCommand
{
    public const string OutputEnvironmentVariable = "REVENANT_RISK_MODEL_OUT";

    [Fact]
    [Trait("Category", "OfflineTraining")]
    public void Mint_fixture_artifact()
    {
        var output = Environment.GetEnvironmentVariable(OutputEnvironmentVariable);
        Assert.False(string.IsNullOrWhiteSpace(output), $"Set {OutputEnvironmentVariable} (use scripts/checks/train-risk-model.sh).");
        Assert.False(File.Exists(Path.Combine(output!, RiskModelScorer.ManifestFileName)), $"{output} already holds a pinned artifact; mint a new model id instead of overwriting it.");

        var result = RiskModelTrainer.TrainFixture(output!, DateTimeOffset.UtcNow);

        Console.WriteLine($"artifact={result.ArtifactPath} bytes={result.Manifest.ArtifactBytes} sha256={result.Manifest.ArtifactSha256}");
        Console.WriteLine($"manifestSha256={result.Manifest.ManifestSha256} thresholdPpm={result.Manifest.ThresholdPpm}");
    }
}
