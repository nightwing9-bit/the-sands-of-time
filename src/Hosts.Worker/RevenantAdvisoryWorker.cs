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

/// <summary>Scores a verified causal prefix and persists advisory evidence separately from financial dispatch.</summary>
internal sealed class RevenantAdvisoryWorker(V4SimulationStore store, V4BranchExecution branch,
    RevenantLedgerOptions options)
{
    internal static readonly ObservedModelPin SupportedAnomaly = new(
        "revenant-ssa-anomaly/672a05a26995653585e5ba9505807df7a2f707914886634c39b133c7dbc72980",
        "03e895da4fa4348b69b1251c6ef9c1c9e6c2deee893e83bf2a983c698281b35e");
    private static readonly string[] Banks = ["BANK-A", "BANK-B", "BANK-C", "BANK-D", "BANK-E", "BANK-F"];

    internal static readonly JsonSerializerOptions ManifestJson = new()
    {
        Converters = { new JsonStringEnumConverter() }, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    internal enum AdvisoryKind { LearnedAnomaly, StatisticalLiquidityProjection }
    internal sealed record Origin(long BoundaryMs, long FactSequence, string PrefixSha256);
    internal sealed record Receipt(Guid Execution, Guid Lineage, long BoundaryMs, string Subject, string Unit,
        [property: JsonConverter(typeof(JsonStringEnumConverter<AdvisoryStatus>))] AdvisoryStatus Status,
        [property: JsonConverter(typeof(JsonStringEnumConverter<AdvisoryKind>))] AdvisoryKind Kind,
        string Schema, string ProducerId, string PolicyId, string? ModelId, string? ArtifactSha256,
        Origin? CausalOrigin, string? SeriesSha256, string? EvidenceSha256, AdvisoryAnomalyV2? Anomaly,
        AdvisoryLiquidityProjection? Liquidity);

    internal async Task<Receipt[]> ScoreAsync(ScenarioRunId execution, long boundary, string? account, CancellationToken ct)
    {
        var history = await branch.ReadProjectionHistoryAsync(options.TenantId, execution, ct);
        var projection = V4ObservationProjector.Project(history);
        var lineage = history.Genesis.RunId;
        var series = account is null
            ? Banks.Select(bank => projection.BankAvailableSeries(bank, boundary))
            : [projection.AccountOutgoingSeries(account, boundary)];
        var results = new List<Receipt>();
        foreach (var input in series)
        {
            // Context model pins are historical lineage data, not a selector for the supported advisory workload.
            var evidence = ScoreSupported(input, lineage, ArtifactRoot);
            var digest = evidence.Status == AdvisoryStatus.Available
                ? await store.PersistAdvisoryAsync(options.TenantId, evidence, ct) : null;
            results.Add(Describe(evidence, digest));
        }
        return results.ToArray();
    }

    internal static Receipt Describe(AdvisoryEvidence evidence, string? digest)
    {
        var input = evidence.Series;
        var seal = input.Origin;
        var liquidity = input.Schema == V4AdvisorySeries.BankSchema;
        return new(input.ExecutionRunId.Value, evidence.Lineage.Value, input.RequestedBoundaryMs, input.SubjectId, input.Unit,
            evidence.Status, liquidity ? AdvisoryKind.StatisticalLiquidityProjection : AdvisoryKind.LearnedAnomaly, evidence.Schema,
            evidence.ProducerId, evidence.Policy, evidence.Model?.ModelId, evidence.Model?.ArtifactSha256,
            seal is null ? null : new(seal.BoundaryMs, seal.FactSequence, seal.PrefixDigest),
            seal is null ? null : input.Digest, digest, evidence.AnomalyV2, evidence.Liquidity);
    }

    internal static string ArtifactRoot => Path.Combine(AppContext.BaseDirectory, "Intelligence", "Artifacts", "revenant-ssa");

    internal static AdvisoryEvidence ScoreSupported(V4AdvisorySeries series, ScenarioRunId lineage, string artifactRoot)
    {
        var bank = series.Schema == V4AdvisorySeries.BankSchema;
        if (series.Origin is null)
        {
            var status = series.Coverage == AdvisorySeriesCoverage.UnknownSubject
                ? AdvisoryStatus.UnknownSubject : AdvisoryStatus.UnobservedOrigin;
            return bank ? new(series, lineage, null, AdvisoryLiquidityProjection.Method, status)
                : new(series, lineage, SupportedAnomaly, AdvisoryEvidence.GlobalAnomalyPolicy, status);
        }
        return bank ? AdvisoryLiquidityProjection.Project(series, lineage)
            : ScoreAnomaly(series, lineage, SupportedAnomaly, artifactRoot, AdvisoryEvidence.GlobalAnomalyPolicy);
    }

    internal static AdvisoryEvidence ScoreAnomaly(V4AdvisorySeries series, ScenarioRunId lineage, ObservedModelPin pin,
        string artifactRoot, string policy)
    {
        AdvisoryEvidence Unavailable(AdvisoryStatus status) => new(series, lineage, pin, policy, status);
        try
        {
            const string prefix = "revenant-ssa-anomaly/";
            if (series.Schema != V4AdvisorySeries.AccountSchema || !pin.ModelId.StartsWith(prefix, StringComparison.Ordinal)
                || !ObservedFinanceEncoding.IsHash(pin.ModelId[prefix.Length..]))
                return Unavailable(AdvisoryStatus.ArtifactInvalid);
            var manifestHash = pin.ModelId[prefix.Length..];
            var directory = Path.Combine(artifactRoot, "anomaly", manifestHash);
            var manifestBytes = File.ReadAllBytes(Path.Combine(directory, "manifest.json"));
            if (ObservationEncoding.Hash(manifestBytes) != manifestHash)
                return Unavailable(AdvisoryStatus.ArtifactInvalid);
            var manifest = JsonSerializer.Deserialize<SsaAdvisoryManifest>(manifestBytes, ManifestJson);
            if (manifest is null || !manifest.IsValidFor("anomaly") || manifest.ArtifactSha256 != pin.ArtifactSha256)
                return Unavailable(AdvisoryStatus.ArtifactInvalid);
            var artifact = File.ReadAllBytes(Path.Combine(directory, "anomaly.zip"));
            if (ObservationEncoding.Hash(artifact) != pin.ArtifactSha256)
                return Unavailable(AdvisoryStatus.ArtifactInvalid);
            var values = series.Points.Select(p => p.Value).ToArray();
            var spec = manifest.Anomaly!;
            var alert = manifest.Alert!;
            if (!SsaAdvisory.EligibleAccount(spec, values)) return Unavailable(AdvisoryStatus.InsufficientHistory);
            var last = RevenantSsaInference.ScoreAccount(new MLContext(seed: 7), artifact, spec, alert, values)[^1];
            if (last.Bucket != values.Length - 1) throw new InvalidDataException("Anomaly output is not at the requested seal.");
            return new(series, lineage, pin, policy, AdvisoryStatus.Available, AnomalyV2: new(last.Count, last.ExpectedCount,
                last.Residual, alert.Statistic, last.Statistic, alert.Threshold, last.Alert, last.NativeAlert, last.NativePValue,
                spec.SeasonalWindow, alert.Window));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return Unavailable(AdvisoryStatus.ArtifactMissing);
        }
        catch (Exception ex) when (ex.GetBaseException() is PlatformNotSupportedException or DllNotFoundException or BadImageFormatException
            or EntryPointNotFoundException)
        {
            return Unavailable(AdvisoryStatus.RuntimeUnavailable);
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or InvalidOperationException
            or OverflowException or NotSupportedException or UnauthorizedAccessException)
        {
            return Unavailable(AdvisoryStatus.ArtifactInvalid);
        }
    }
}
