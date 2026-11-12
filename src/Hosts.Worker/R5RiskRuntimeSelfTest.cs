using System.Runtime.InteropServices;
using System.Text.Json;
using Revenant.Intelligence;

namespace Hosts.Worker;

internal static class R5RiskRuntimeSelfTest
{
    internal static int Run()
    {
        var pin = BehavioralRiskScorerV2.PublishedPin;
        var directory = Path.Combine(AppContext.BaseDirectory, "Intelligence", "Artifacts", BehavioralRiskScorerV2.ArtifactDirectory);
        var loaded = BehavioralRiskScorerV2.Load(directory, BehavioralRiskScorerV2.FeatureSchema, pin);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Result = loaded.IsSuccess ? "PASS" : "FAIL",
            ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Runtime = RuntimeInformation.FrameworkDescription,
            pin.ModelId, pin.ArtifactSha256, pin.ThresholdPpm,
            FeatureSchema = BehavioralRiskScorerV2.FeatureSchema,
            References = loaded.IsSuccess ? loaded.Value.VerifiedReferenceCount : 0,
            Error = loaded.IsFailure ? loaded.Error.ToString() : null,
        }));
        return loaded.IsSuccess ? 0 : 1;
    }
}
