using System.Text.Json;
using Microsoft.ML;
using Revenant.Api;
using Revenant.Contracts;
using Revenant.Intelligence;
using Revenant.Observations;
using Revenant.Payments;
using Revenant.Timeline;

namespace Hosts.Worker;

internal sealed class RevenantAdvisoryWorker(V4SimulationStore store, V4BranchExecution branch,
    RevenantLedgerOptions options)
{
    internal const string Policy = "revenant-ssa-advisory/1";
    private const int Horizon = V4BankTargets.HorizonCount;
    private const string AnomalyArtifact = "anomaly.zip";
    private const string ForecastArtifact = "forecast.zip";
    private static readonly string[] Banks = ["BANK-A", "BANK-B", "BANK-C", "BANK-D", "BANK-E", "BANK-F"];

    private sealed record Manifest(string Policy, string Kind, string ArtifactSha256, int Warmup, int MinimumNonzero,
        long Scale, double AlertThreshold, long[] IntervalAdjustmentMinor);

    internal sealed record Receipt(Guid Execution, long BoundaryMs, string Subject, string Status, string? ModelId,
        string? SeriesSha256, string? EvidenceSha256, AdvisoryAnomaly? Anomaly, AdvisoryForecast? Forecast);

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

            var evidence = Score(input, lineage, pins[0], isAccount);
            var digest = evidence.Status == AdvisoryStatus.Available
                ? await store.PersistAdvisoryAsync(options.TenantId, evidence, ct) : null;
            results.Add(new(execution.Value, boundary, input.SubjectId, evidence.Status.ToString(), evidence.Model.ModelId,
                input.Digest, digest, evidence.Anomaly, evidence.Forecast));
        }
        return results.ToArray();
    }

    private static AdvisoryEvidence Score(V4AdvisorySeries series, ScenarioRunId lineage, ObservedModelPin pin, bool account)
    {
        AdvisoryEvidence Unavailable(AdvisoryStatus status) => new(series, lineage, pin, Policy, status);

        try
        {
            var kind = account ? "anomaly" : "forecast";
            var slash = pin.ModelId.IndexOf('/');
            if (slash <= 0 || pin.ModelId[..slash] != $"revenant-ssa-{kind}"
                || !ObservedFinanceEncoding.IsHash(pin.ModelId[(slash + 1)..]))
                return Unavailable(AdvisoryStatus.ArtifactInvalid);
            var directory = Path.Combine(AppContext.BaseDirectory, "Intelligence", "Artifacts", "revenant-ssa", kind, pin.ModelId[(slash + 1)..]);
            var manifestBytes = File.ReadAllBytes(Path.Combine(directory, "manifest.json"));
            if (ObservationEncoding.Hash(manifestBytes) != pin.ModelId[(slash + 1)..])
                return Unavailable(AdvisoryStatus.ArtifactInvalid);
            var config = JsonSerializer.Deserialize<Manifest>(manifestBytes);
            if (config is null || config.Policy != Policy || config.Kind != kind || config.ArtifactSha256 != pin.ArtifactSha256
                || config.Warmup < 1 || config.MinimumNonzero < 1 || config.Scale <= 0
                || !double.IsFinite(config.AlertThreshold) || config.AlertThreshold is < 0 or > 1
                || config.IntervalAdjustmentMinor is null || config.IntervalAdjustmentMinor.Length != Horizon
                || config.IntervalAdjustmentMinor.Any(n => n < 0))
                return Unavailable(AdvisoryStatus.ArtifactInvalid);
            var artifact = File.ReadAllBytes(Path.Combine(directory, account ? AnomalyArtifact : ForecastArtifact));
            if (ObservationEncoding.Hash(artifact) != pin.ArtifactSha256)
                return Unavailable(AdvisoryStatus.ArtifactInvalid);
            if (series.Points.Length <= config.Warmup || (account
                && series.Points.Take(config.Warmup).Count(p => p.Value > 0) < config.MinimumNonzero))
                return Unavailable(AdvisoryStatus.InsufficientHistory);
            using var stream = new MemoryStream(artifact, writable: false);
            var ml = new MLContext(seed: 7);
            if (account)
            {
                using var engine = RevenantSsaInference.Load<SpikeOutput>(ml, stream);
                RevenantSpike spike = default;
                foreach (var point in series.Points) spike = RevenantSsaInference.ObserveAccount(engine, point.Value);
                var count = series.Points[^1].Value;
                var mean = series.Points.Skip(series.Points.Length - config.Warmup - 1).Take(config.Warmup)
                    .Average(p => p.Value);
                var anomaly = new AdvisoryAnomaly(count, config.Warmup, mean, spike.Alert, spike.RawScore,
                    spike.PValue, config.AlertThreshold > 0 && count > 0 && spike.PValue <= config.AlertThreshold, config.AlertThreshold);
                return new(series, lineage, pin, Policy, AdvisoryStatus.Available, Anomaly: anomaly);
            }

            using var forecastEngine = RevenantSsaInference.Load<ForecastOutput>(ml, stream);
            var reference = series.ReferenceMinor!.Value;
            var normalize = new AdvisoryBalanceNormalization(reference, config.Scale);
            ForecastOutput forecast = new();
            foreach (var point in series.Points)
                forecast = RevenantSsaInference.ObserveBank(forecastEngine, normalize.Normalize(point.Value));
            RevenantSsaInference.VerifyForecast(forecast, Horizon);
            var floor = reference / 10;
            var path = Enumerable.Range(0, Horizon).Select(h =>
            {
                var lower = normalize.Restore(forecast.LowerBound[h]);
                var upper = normalize.Restore(forecast.UpperBound[h]);
                var boundary = checked(series.RequestedBoundaryMs + (h + 1) * ObservedFinanceVersion.BucketMs);
                return new AdvisoryForecastStep(boundary, normalize.Restore(forecast.Forecast[h]), lower, upper,
                    checked(lower - config.IntervalAdjustmentMinor[h]), checked(upper + config.IntervalAdjustmentMinor[h]));
            }).ToArray();
            var first = Array.FindIndex(path, p => p.CentralMinor < floor);
            int? crossing = series.Points[^1].Value >= floor && first >= 0 ? first + 1 : null;
            return new(series, lineage, pin, Policy, AdvisoryStatus.Available,
                Forecast: new(reference, config.Scale, floor, crossing, path));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return Unavailable(AdvisoryStatus.ArtifactMissing);
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or DllNotFoundException or BadImageFormatException)
        {
            return Unavailable(AdvisoryStatus.RuntimeUnavailable);
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or InvalidOperationException or OverflowException)
        {
            return Unavailable(AdvisoryStatus.ArtifactInvalid);
        }
    }
}
