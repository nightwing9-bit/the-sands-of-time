using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.ML;
using Revenant.Api;
using Revenant.Contracts;
using Revenant.Intelligence;
using Revenant.Observations;
using Revenant.Payments;
using Revenant.Timeline;

namespace Hosts.Worker;

/// <summary>
/// Published R7/2 advisory scoring. The Worker composes verified causal series, a pinned manifest and artifact, and the shared
/// <see cref="RevenantSsaInference"/> implementation used by research and qualification. It returns the output at the requested
/// completed seal and persists it as immutable advisory evidence. Advisory outputs never enter Screening or alter balances.
/// </summary>
internal sealed class RevenantAdvisoryWorker(V4SimulationStore store, V4BranchExecution branch,
    RevenantLedgerOptions options)
{
    internal const string Policy = SsaAdvisory.Policy;
    private const int Horizon = V4BankTargets.HorizonCount;
    private const string AnomalyArtifact = "anomaly.zip";
    private const string ForecastArtifact = "forecast.zip";
    private static readonly string[] Banks = ["BANK-A", "BANK-B", "BANK-C", "BANK-D", "BANK-E", "BANK-F"];

    internal static readonly JsonSerializerOptions ManifestJson = new()
    {
        Converters = { new JsonStringEnumConverter() }, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    internal sealed record Receipt(Guid Execution, long BoundaryMs, string Subject, string Status, string? ModelId,
        string? SeriesSha256, string? EvidenceSha256, AdvisoryAnomalyV2? Anomaly, AdvisoryForecastV2? Forecast);

    internal async Task<Receipt[]> ScoreAsync(ScenarioRunId execution, long boundary, string? account, CancellationToken ct)
    {
        var history = await branch.ReadProjectionHistoryAsync(options.TenantId, execution, ct);
        var projection = V4ObservationProjector.Project(history);
        var context = history.Segments[^1].Context;
        var lineage = history.Genesis.RunId;
        var series = account is null
            ? Banks.Select(bank => projection.BankAvailableSeries(bank, boundary))
            : [projection.AccountOutgoingSeries(account, boundary)];
        var results = new List<Receipt>();
        foreach (var input in series)
        {
            var isAccount = input.Schema == V4AdvisorySeries.AccountSchema;
            var pins = isAccount ? context.AnomalyModels : context.LiquidityModels;
            if (input.Origin is null)
            {
                results.Add(new(execution.Value, boundary, input.SubjectId, input.Coverage.ToString(), null, null, null, null, null));
                continue;
            }
            if (pins.Length != 1)
            {
                results.Add(new(execution.Value, boundary, input.SubjectId,
                    (pins.IsEmpty ? AdvisoryStatus.ArtifactMissing : AdvisoryStatus.ArtifactInvalid).ToString(),
                    null, input.Digest, null, null, null));
                continue;
            }

            var evidence = Score(input, lineage, pins[0], isAccount, ArtifactRoot);
            var digest = evidence.Status == AdvisoryStatus.Available
                ? await store.PersistAdvisoryAsync(options.TenantId, evidence, ct) : null;
            results.Add(new(execution.Value, boundary, input.SubjectId, evidence.Status.ToString(), evidence.Model.ModelId,
                input.Digest, digest, evidence.AnomalyV2, evidence.ForecastV2));
        }
        return results.ToArray();
    }

    private static string ArtifactRoot => Path.Combine(AppContext.BaseDirectory, "Intelligence", "Artifacts", "revenant-ssa");

    /// <summary>
    /// Scores one verified causal series with one pinned artifact. The artifact directory is
    /// <c>{root}/{anomaly|forecast}/{manifest-sha256}/</c>; the manifest hash is the model ID suffix and binds the ZIP hash.
    /// </summary>
    internal static AdvisoryEvidence Score(V4AdvisorySeries series, ScenarioRunId lineage, ObservedModelPin pin, bool account,
        string artifactRoot)
    {
        AdvisoryEvidence Unavailable(AdvisoryStatus status) => new(series, lineage, pin, Policy, status);

        try
        {
            var kind = account ? "anomaly" : "forecast";
            var slash = pin.ModelId.IndexOf('/');
            if (slash <= 0 || pin.ModelId[..slash] != $"revenant-ssa-{kind}"
                || !ObservedFinanceEncoding.IsHash(pin.ModelId[(slash + 1)..]))
                return Unavailable(AdvisoryStatus.ArtifactInvalid);
            var directory = Path.Combine(artifactRoot, kind, pin.ModelId[(slash + 1)..]);
            var manifestBytes = File.ReadAllBytes(Path.Combine(directory, "manifest.json"));
            if (ObservationEncoding.Hash(manifestBytes) != pin.ModelId[(slash + 1)..])
                return Unavailable(AdvisoryStatus.ArtifactInvalid);
            var manifest = JsonSerializer.Deserialize<SsaAdvisoryManifest>(manifestBytes, ManifestJson);
            if (manifest is null || !manifest.IsValidFor(kind) || manifest.ArtifactSha256 != pin.ArtifactSha256)
                return Unavailable(AdvisoryStatus.ArtifactInvalid);
            var artifact = File.ReadAllBytes(Path.Combine(directory, account ? AnomalyArtifact : ForecastArtifact));
            if (ObservationEncoding.Hash(artifact) != pin.ArtifactSha256)
                return Unavailable(AdvisoryStatus.ArtifactInvalid);
            var values = series.Points.Select(p => p.Value).ToArray();
            var ml = new MLContext(seed: 7);
            if (account)
            {
                var spec = manifest.Anomaly!;
                var alert = manifest.Alert!;
                if (!SsaAdvisory.EligibleAccount(spec, values)) return Unavailable(AdvisoryStatus.InsufficientHistory);
                var last = RevenantSsaInference.ScoreAccount(ml, artifact, spec, alert, values)[^1];
                if (last.Bucket != values.Length - 1) throw new InvalidDataException("Anomaly output is not at the requested seal.");
                return new(series, lineage, pin, Policy, AdvisoryStatus.Available, AnomalyV2: new(last.Count, last.ExpectedCount,
                    last.Residual, alert.Statistic, last.Statistic, alert.Threshold, last.Alert, last.NativeAlert, last.NativePValue,
                    spec.SeasonalWindow, alert.Window));
            }

            var forecastSpec = manifest.Forecast!;
            var reference = series.ReferenceMinor!.Value;
            if (values.Length <= forecastSpec.Warmup) return Unavailable(AdvisoryStatus.InsufficientHistory);
            var step = RevenantSsaInference.ForecastBank(ml, artifact, forecastSpec, manifest.Interval, reference, values)[^1];
            if (step.Bucket != values.Length - 1) throw new InvalidDataException("Forecast output is not at the requested seal.");
            var floor = reference / 10;
            var path = Enumerable.Range(0, Horizon).Select(h => new AdvisoryForecastStep(
                checked(series.RequestedBoundaryMs + (h + 1) * ObservedFinanceVersion.BucketMs), step.Central[h], step.NativeLower[h],
                step.NativeUpper[h], step.CalibratedLower[h], step.CalibratedUpper[h])).ToArray();
            var first = Array.FindIndex(path, p => p.CentralMinor < floor);
            int? crossing = values[^1] >= floor && first >= 0 ? first + 1 : null;
            return new(series, lineage, pin, Policy, AdvisoryStatus.Available, ForecastV2: new(forecastSpec.Representation, reference,
                SsaAdvisory.Scale(reference, forecastSpec.ScaleDivisor), step.Sigma, floor, crossing, path));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return Unavailable(AdvisoryStatus.ArtifactMissing);
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or DllNotFoundException or BadImageFormatException)
        {
            return Unavailable(AdvisoryStatus.RuntimeUnavailable);
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or InvalidOperationException
            or OverflowException or NotSupportedException)
        {
            return Unavailable(AdvisoryStatus.ArtifactInvalid);
        }
    }
}
