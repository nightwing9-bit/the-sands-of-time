using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Marrow.Infrastructure.Identity;
using Marrow.Infrastructure.Identity.Policy;
using Marrow.Kernel.Security;
using Marrow.Timeline;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Revenant.Contracts;
using Revenant.Finance;
using Revenant.Scenario;
using Revenant.Timeline;
namespace Sands.E2E.Backend;
// ---- Wire DTOs of the TEST-ONLY Revenant slice surface (Spec 05 task 5.4) ---------------------------------------------------
// Every number a browser shows comes from these records, which are built from verified Marrow Timeline restores of the real
// FinanceReducer projection. Positions are decimal strings; money is integer minor units of the stated currency.
public sealed record SliceLabelling(bool Synthetic, bool ProjectionOnly, string LedgerPosting, string LabelSchema, string CostModel, string Note);
public sealed record SliceBank(string BankId, string Name, long OpeningMinor);
public sealed record SliceEvent(
    string Position, string IntentId, string BusinessTime, string SourceBankId, string SourceAccountId, string DestBankId,
    string DestAccountId, long AmountMinor, string Currency, string SyntheticLabel, bool NewBeneficiary, bool SanctionsHit);
public sealed record SlicePolicy(
    string PolicyId, string ConfigHash, int VelocityHoldThreshold, long VelocityAmountFloorMinor, long? NewBeneficiaryAmountFloorMinor,
    long VelocityWindowMs, bool HoldOnSanctionsHit);
public sealed record SliceScenario(string ScenarioId, string ScenarioVersion, long Seed, string ManifestHash, string FactsHash, string StartBusinessTime);
public sealed record SliceRunDescriptor(
    string TimelineId, string RunId, string SourceId, string? BranchId, BoundsDto Bounds, bool ReadOnly, IReadOnlyList<MarkerDto> Markers,
    string HistoryStatus, string SourcePolicyId, SliceScenario Scenario, SliceLabelling Labelling, IReadOnlyList<SliceBank> Banks,
    IReadOnlyList<SliceEvent> Events, IReadOnlyList<SlicePolicy> Policies, string SourceFinalStateHash);
public sealed record SliceBankState(string BankId, string Name, long PostedMinor, long ReservedMinor, long AvailableMinor);
public sealed record SlicePayment(string Position, string IntentId, string Status, string Reason, string PolicyId, int Velocity15m, IReadOnlyList<string> Trail);
public sealed record SliceCounters(int FraudSettled, int FraudPendingHold, int FraudTerminallyStopped, int LegitimateSettled, int LegitimatePendingHold, int LegitimateTerminallyStopped);
public sealed record SliceState(
    string BusinessTime, string StateHash, string Currency, long TotalPostedMinor, long TotalReservedMinor,
    IReadOnlyList<SliceBankState> Banks, IReadOnlyList<SlicePayment> Payments, SliceCounters Counters, SliceLabelling Labelling);
public sealed record SliceMetrics(
    string CostModel, string LabelSchema, int FraudPreventedCount, long FraudPreventedExposureMinor, int FraudDeferredAtHorizonCount,
    int LegitimateDelayedCount, long FalsePositiveCostMinor, long IncrementalDelayCostMinor, long NetEffectMinor, int DecisionDifferenceCount);
public sealed record SliceAligned(string Position, string BusinessTime, SliceState Original, SliceState Alternate, SliceMetrics Metrics);
public sealed record SliceDecision(string Status, string Reason, string PolicyId);
public sealed record SliceDifference(
    string Position, string IntentId, string SyntheticLabel, long AmountMinor, string Currency, SliceDecision Original, SliceDecision Alternate);
public sealed record SliceSourceCheck(string HashWhenRecorded, string HashNow, bool Unchanged);
public sealed record SliceComparison(
    string BranchId, string SourceId, string ForkPosition, string FinalPosition, string SourcePolicyId, string? BranchPolicyId,
    string BranchStatus, string SourceFinalStateHash, string BranchFinalStateHash, IReadOnlyList<SliceAligned> Aligned,
    IReadOnlyList<SliceDifference> Differences, SliceSourceCheck SourceCheck, SliceLabelling Labelling);
public sealed record SliceStartBody(long? Seed);
public sealed record SliceBranchOptions(string? PolicyId);
public sealed record SliceBranchBody(string? Position, string? OperationKey, SliceBranchOptions? Options);
/// <summary>One recorded run the host remembers (in memory, for the life of the host). The Timeline rows themselves are durable.</summary>
public sealed record SliceRunEntry(ScenarioRun Run, RecordedSource Source, string OwnerId);
public sealed class SliceRunRegistry
{
    private readonly ConcurrentDictionary<Guid, SliceRunEntry> _runs = new();
    public void Add(Guid timelineId, SliceRunEntry entry) => _runs[timelineId] = entry;
    public SliceRunEntry? Find(Guid timelineId) => _runs.GetValueOrDefault(timelineId);
}
/// <summary>
/// TEST-ONLY Revenant slice HTTP surface for task 5.4. Like the fixture Timeline routes it is mapped only inside this assembly;
/// <c>Hosts.Api</c> serves none of it and the OpenAPI document lists none of it. It is the thinnest honest bridge between the browser
/// and the real domain: it records a run with <see cref="FinanceTimelineAdapter"/> (real Marrow engine, PostgreSQL), restores states
/// through the engine, forks and continues through <see cref="FinanceTimelineAdapter.RunCounterfactualAsync"/>, and compares through
/// the engine's aligned comparison. It adds no finance rule, performs no ledger posting, and the owner of every history is the
/// verified guest subject. The production Revenant API, staff authorization and DTO contract arrive with the later specs.
/// </summary>
public static class RevenantSliceEndpoints
{
    public const string PathPrefix = "/revenant";
    private const long DefaultSeed = 42;
    private const long MaxSeed = 1_000_000;
    private const int MaxTrajectoryPoints = 12;
    private static readonly SliceLabelling Labelling = new(
        Synthetic: true,
        ProjectionOnly: true,
        LedgerPosting: "none",
        LabelSchema: ScenarioSchemas.LabelSchemaVersion,
        CostModel: CounterfactualCostModel.V1.Version,
        Note: "Synthetic scenario. Projection only: no database ledger posting, no real bank transaction. Fraud labels are synthetic scenario labels, not real-world findings.");
    public static bool IsSlicePath(PathString path) => path.StartsWithSegments(PathPrefix, StringComparison.Ordinal);
    public static void Map(IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup(PathPrefix).RequireAuthorization(IdentityPolicies.Guest);
        group.MapPost("/runs", StartRun);
        group.MapGet("/runs/{id:guid}", DescribeRun);
        group.MapGet("/runs/{id:guid}/restore", Restore);
        group.MapPost("/runs/{id:guid}/branches", CreateBranch);
        group.MapGet("/branches/{id:guid}/comparison", Compare);
    }
    // ---- Routes -----------------------------------------------------------------------------------------------------------
    private static async Task<IResult> StartRun(
        HttpContext http, IActorContext actor, FinanceTimelineAdapter adapter, SliceRunRegistry registry,
        Marrow.Timeline.ITimelineEngine<FinanceProjectionState, PaymentIntent, FinanceContext> engine)
    {
        var ct = http.RequestAborted;
        SliceStartBody? body;
        try
        {
            body = http.Request.ContentLength is > 1024 ? null : await http.Request.ReadFromJsonAsync<SliceStartBody>(ct);
        }
        catch (JsonException)
        {
            body = null;
        }
        var key = http.Request.Headers["Idempotency-Key"].ToString();
        var seed = body?.Seed ?? DefaultSeed;
        if (body is null || !ValidKey(key) || seed is < 0 or > MaxSeed)
        {
            return Invalid();
        }
        // The run id is derived from the verified owner and the idempotency key, so a retry names the same run and a different
        // guest can never collide with it. The scenario generator is handed the id; it never invents one.
        var runId = new ScenarioRunId(DeriveGuid($"revenant-run|{actor.SubjectId}|{key}"));
        var created = FridayRingScenario.Create(seed, ScenarioSchemas.FridayRingVersion, runId);
        if (!created.TryGetValue(out var run))
        {
            return Invalid();
        }
        var recorded = await adapter.RecordSourceAsync(actor.SubjectId, run, PolicyCatalog.RefOf(PolicyCatalog.V7), $"revenant-slice/{key}", ct);
        if (!recorded.TryGetValue(out var source))
        {
            return Failure(http, recorded.Error);
        }
        var entry = new SliceRunEntry(run, source, actor.SubjectId);
        registry.Add(source.Id.Value, entry);
        return await DescribeCoreAsync(http, engine, entry);
    }
    private static async Task<IResult> DescribeRun(
        Guid id, HttpContext http, IActorContext actor, IResourceAuthorizer authorizer, SliceRunRegistry registry,
        Marrow.Timeline.ITimelineEngine<FinanceProjectionState, PaymentIntent, FinanceContext> engine)
    {
        if (await E2eTimelineEndpoints.DenyAsync(http, actor, authorizer, id, ResourceOperation.Read) is { } denied)
        {
            return denied;
        }
        return registry.Find(id) is { } entry ? await DescribeCoreAsync(http, engine, entry) : RunUnknown();
    }
    private static async Task<IResult> Restore(
        Guid id, string? position, HttpContext http, IActorContext actor, IResourceAuthorizer authorizer, SliceRunRegistry registry,
        FixtureCatalog catalog, Marrow.Timeline.ITimelineEngine<FinanceProjectionState, PaymentIntent, FinanceContext> engine)
    {
        var ct = http.RequestAborted;
        if (await E2eTimelineEndpoints.DenyAsync(http, actor, authorizer, id, ResourceOperation.Read) is { } denied)
        {
            return denied;
        }
        if (!TimelinePosition.TryParseWire(position, out var parsed))
        {
            return TimelineHttpErrors.ToResult(http, TimelineErrorCode.InvalidPosition);
        }
        var row = await catalog.FindAsync(id, ct);
        var entry = row is null ? null : registry.Find(row.RootId);
        if (row is null || entry is null)
        {
            return RunUnknown();
        }
        var restored = await engine.RestoreAsync(new TimelineId(id), parsed.Value, ct);
        if (restored.TryGetError(out var error))
        {
            return TimelineHttpErrors.ToResult(http, error.Code, parsed.ToWireString());
        }
        var v = restored.Value;
        return Results.Json(new
        {
            position = v.Position.ToString(CultureInfo.InvariantCulture),
            state = ToState(v.State, v.StateHash, entry.Run.Manifest),
            hashOrigin = v.HashOrigin.ToString(),
            historyStatus = v.HistoryStatus.ToString(),
        });
    }
    private static async Task<IResult> CreateBranch(
        Guid id, HttpContext http, IActorContext actor, IResourceAuthorizer authorizer, SliceRunRegistry registry,
        FixtureCatalog catalog, FinanceTimelineAdapter adapter)
    {
        var ct = http.RequestAborted;
        if (await E2eTimelineEndpoints.DenyAsync(http, actor, authorizer, id, ResourceOperation.Write) is { } denied)
        {
            return denied;
        }
        SliceBranchBody? body;
        try
        {
            body = http.Request.ContentLength is > 4096 ? null : await http.Request.ReadFromJsonAsync<SliceBranchBody>(ct);
        }
        catch (JsonException)
        {
            body = null;
        }
        var header = http.Request.Headers["Idempotency-Key"].ToString();
        if (body is null || !ValidKey(body.OperationKey) || header != body.OperationKey)
        {
            return Invalid();
        }
        var policy = body.Options?.PolicyId switch
        {
            PolicyCatalog.V7Id => PolicyCatalog.V7,
            PolicyCatalog.V8Id => PolicyCatalog.V8,
            _ => null,
        };
        if (policy is null)
        {
            return Invalid();
        }
        if (!TimelinePosition.TryParseWire(body.Position, out var parsed))
        {
            return TimelineHttpErrors.ToResult(http, TimelineErrorCode.InvalidPosition);
        }
        var row = await catalog.FindAsync(id, ct);
        var entry = row is null ? null : registry.Find(row.RootId);
        if (row is null || entry is null || row.ParentId is not null)
        {
            return RunUnknown();
        }
        // Fork + continue + compare through the one shared engine. The pins are explicit: manifest, policy and reducer version.
        var request = new BranchRequest(new TimelineId(id), parsed.Value, entry.Run.Manifest, PolicyCatalog.RefOf(policy), FinanceReducer.Version, body.OperationKey!);
        var counterfactual = await adapter.RunCounterfactualAsync(request, ct);
        if (!counterfactual.TryGetValue(out var result))
        {
            return Failure(http, counterfactual.Error);
        }
        return Results.Json(new
        {
            branchId = result.Branch.Handle.Branch.Value.ToString("D"),
            sourceId = id.ToString("D"),
            forkPosition = parsed.ToWireString(),
            policyId = policy.PolicyId,
            status = result.Branch.Status.ToString(),
        });
    }
    private static async Task<IResult> Compare(
        Guid id, HttpContext http, IActorContext actor, IResourceAuthorizer authorizer, SliceRunRegistry registry, FixtureCatalog catalog,
        FinanceTimelineAdapter adapter, Marrow.Timeline.ITimelineEngine<FinanceProjectionState, PaymentIntent, FinanceContext> engine)
    {
        var ct = http.RequestAborted;
        if (await E2eTimelineEndpoints.DenyAsync(http, actor, authorizer, id, ResourceOperation.Read) is { } denied)
        {
            return denied;
        }
        var row = await catalog.FindAsync(id, ct);
        var entry = row is null ? null : registry.Find(row.RootId);
        if (row is null || entry is null || row.ParentId is not { } parent || row.ForkPosition is not { } fork)
        {
            return RunUnknown();
        }
        var sourceId = new TimelineId(parent);
        var branchId = new TimelineId(id);
        var sourceReplay = await engine.ReplayAsync(sourceId, ct);
        if (sourceReplay.TryGetError(out var sourceError))
        {
            return TimelineHttpErrors.ToResult(http, sourceError.Code);
        }
        var branchReplay = await engine.ReplayAsync(branchId, ct);
        if (branchReplay.TryGetError(out var branchError))
        {
            return TimelineHttpErrors.ToResult(http, branchError.Code);
        }
        var final = sourceReplay.Value.FinalPosition;
        var compared = await adapter.CompareAsync(sourceId, branchId, TrajectoryPositions(fork, final), ct);
        if (!compared.TryGetValue(out var aligned))
        {
            return Failure(http, compared.Error);
        }
        var manifest = entry.Run.Manifest;
        var last = aligned[^1];
        var differences = last.Original.State.Payments
            .OrderBy(entry => entry.Value.Intent.Sequence)
            .Select(entry => (Original: entry.Value, Alternate: last.Alternate.State.Payments[entry.Key]))
            .Where(pair => pair.Original.Status != pair.Alternate.Status || pair.Original.Reason != pair.Alternate.Reason)
            .Select(pair => new SliceDifference(
                pair.Original.Intent.Sequence.ToString(CultureInfo.InvariantCulture),
                pair.Original.Intent.IntentId,
                LabelOf(pair.Original.Intent.Truth),
                pair.Original.Intent.Amount.MinorUnits,
                pair.Original.Intent.Amount.Currency,
                new SliceDecision(pair.Original.Status.ToString(), pair.Original.Reason.ToString(), pair.Original.PolicyId),
                new SliceDecision(pair.Alternate.Status.ToString(), pair.Alternate.Reason.ToString(), pair.Alternate.PolicyId)))
            .ToList();
        var branchPolicy = last.Alternate.State.Payments.Values
            .Where(p => p.Intent.Sequence > fork)
            .OrderBy(p => p.Intent.Sequence)
            .Select(p => p.PolicyId)
            .FirstOrDefault();
        return Results.Json(new SliceComparison(
            BranchId: id.ToString("D"),
            SourceId: parent.ToString("D"),
            ForkPosition: fork.ToString(CultureInfo.InvariantCulture),
            FinalPosition: final.ToString(CultureInfo.InvariantCulture),
            SourcePolicyId: entry.Source.Policy.PolicyId,
            BranchPolicyId: branchPolicy,
            BranchStatus: branchReplay.Value.Status.ToString(),
            SourceFinalStateHash: sourceReplay.Value.FinalStateHash,
            BranchFinalStateHash: branchReplay.Value.FinalStateHash,
            Aligned: [.. aligned.Select(a => new SliceAligned(
                a.Position.ToString(CultureInfo.InvariantCulture),
                BusinessTime(a.Original.State.ClockUtcMs),
                ToState(a.Original.State, a.Original.StateHash, manifest),
                ToState(a.Alternate.State, a.Alternate.StateHash, manifest),
                ToMetrics(a.Metrics)))],
            Differences: differences,
            SourceCheck: new SliceSourceCheck(
                entry.Source.FinalStateHash,
                sourceReplay.Value.FinalStateHash,
                string.Equals(entry.Source.FinalStateHash, sourceReplay.Value.FinalStateHash, StringComparison.Ordinal)),
            Labelling: Labelling));
    }
    // ---- Projection into DTOs ---------------------------------------------------------------------------------------------
    private static async Task<IResult> DescribeCoreAsync(
        HttpContext http, Marrow.Timeline.ITimelineEngine<FinanceProjectionState, PaymentIntent, FinanceContext> engine, SliceRunEntry entry)
    {
        var replay = await engine.ReplayAsync(entry.Source.Id, http.RequestAborted);
        if (replay.TryGetError(out var error))
        {
            return TimelineHttpErrors.ToResult(http, error.Code);
        }
        var final = replay.Value;
        var manifest = entry.Run.Manifest;
        if (!string.Equals(final.FinalState.ManifestContentHash, manifest.ContentHash, StringComparison.Ordinal))
        {
            return TimelineHttpErrors.ToResult(http, TimelineErrorCode.CorruptHistory);
        }
        var events = final.FinalState.Payments.Values
            .OrderBy(p => p.Intent.Sequence)
            .Select(p => ToEvent(p.Intent))
            .ToList();
        // Markers are positions the server computed from the recorded facts: the 09:00 business time and the first labelled ring transfer.
        var nineAm = FinanceTimelineAdapter.PositionAtOrBefore(entry.Run, manifest.StartUtcMs + (60 * 60 * 1000L));
        var firstRing = final.FinalState.Payments.Values.OrderBy(p => p.Intent.Sequence).FirstOrDefault(p => p.Intent.Truth == SyntheticTruth.FraudMuleRing);
        var markers = new List<MarkerDto>();
        if (nineAm > 0)
        {
            markers.Add(new MarkerDto(nineAm.ToString(CultureInfo.InvariantCulture), "09:00 business time"));
        }
        if (firstRing is not null)
        {
            markers.Add(new MarkerDto(firstRing.Intent.Sequence.ToString(CultureInfo.InvariantCulture), "First synthetic ring transfer"));
        }
        var id = entry.Source.Id.Value.ToString("D");
        return Results.Json(new SliceRunDescriptor(
            TimelineId: id,
            RunId: entry.Run.RunId.ToString(),
            SourceId: id,
            BranchId: null,
            Bounds: new BoundsDto("0", final.FinalPosition.ToString(CultureInfo.InvariantCulture)),
            ReadOnly: final.Status == HistoryStatus.Failed,
            Markers: [.. markers.OrderBy(m => long.Parse(m.Position, CultureInfo.InvariantCulture))],
            HistoryStatus: final.Status.ToString(),
            SourcePolicyId: entry.Source.Policy.PolicyId,
            Scenario: new SliceScenario(
                manifest.ScenarioId, manifest.ScenarioVersion, manifest.Seed, manifest.ContentHash, entry.Run.ExogenousFactsHash, BusinessTime(manifest.StartUtcMs)),
            Labelling: Labelling,
            Banks: [.. manifest.Banks.Select(b => new SliceBank(b.BankId, b.Name, b.OpeningBalance.MinorUnits))],
            Events: events,
            Policies: [ToPolicy(PolicyCatalog.V7), ToPolicy(PolicyCatalog.V8)],
            SourceFinalStateHash: final.FinalStateHash));
    }
    private static SliceEvent ToEvent(PaymentIntent intent) => new(
        intent.Sequence.ToString(CultureInfo.InvariantCulture), intent.IntentId, BusinessTime(intent.EventTimeUtcMs),
        intent.SourceBankId, intent.SourceAccountId, intent.DestBankId, intent.DestAccountId,
        intent.Amount.MinorUnits, intent.Amount.Currency, LabelOf(intent.Truth), intent.Features.NewBeneficiary, intent.Features.SanctionsHit);
    private static SlicePolicy ToPolicy(PolicyConfig config) => new(
        config.PolicyId, config.ContentHash, config.VelocityHoldThreshold, config.VelocityAmountFloorMinor,
        config.NewBeneficiaryAmountFloorMinor, config.VelocityWindowMs, config.HoldOnSanctionsHit);
    private static SliceState ToState(FinanceProjectionState state, string hash, ScenarioManifest manifest)
    {
        var names = manifest.Banks.ToDictionary(b => b.BankId, b => b.Name, StringComparer.Ordinal);
        var counters = state.Counters;
        return new SliceState(
            BusinessTime(state.ClockUtcMs),
            hash,
            state.Currency,
            state.TotalPostedMinor,
            state.TotalReservedMinor,
            [.. state.Banks.Select(b => new SliceBankState(b.BankId, names.GetValueOrDefault(b.BankId, b.BankId), b.PostedMinor, b.ReservedMinor, b.AvailableMinor))],
            [.. state.Payments.Values.OrderBy(p => p.Intent.Sequence).Select(p => new SlicePayment(
                p.Intent.Sequence.ToString(CultureInfo.InvariantCulture), p.Intent.IntentId, p.Status.ToString(), p.Reason.ToString(),
                p.PolicyId, p.Velocity15m, [.. p.Trail.Select(t => t.ToString())]))],
            new SliceCounters(
                counters.FraudSettled, counters.FraudPendingHold, counters.FraudTerminallyStopped,
                counters.LegitimateSettled, counters.LegitimatePendingHold, counters.LegitimateTerminallyStopped),
            Labelling);
    }
    private static SliceMetrics ToMetrics(CounterfactualMetrics m) => new(
        m.CostModelVersion, m.LabelSchemaVersion, m.FraudPreventedCount, m.FraudPreventedExposureMinor, m.FraudDeferredAtHorizonCount,
        m.LegitimateDelayedCount, m.FalsePositiveCostMinor, m.IncrementalDelayCostMinor, m.NetEffectMinor, m.DecisionDifferenceCount);
    /// <summary>The fork position, the final position and up to <see cref="MaxTrajectoryPoints"/> evenly spaced positions between them.</summary>
    private static long[] TrajectoryPositions(long fork, long final)
    {
        if (final <= fork)
        {
            return [fork];
        }
        var points = new SortedSet<long> { fork, final };
        var span = final - fork;
        var steps = Math.Min(MaxTrajectoryPoints, span);
        for (var i = 1L; i < steps; i++)
        {
            points.Add(fork + (span * i / steps));
        }
        return [.. points];
    }
    private static string LabelOf(SyntheticTruth truth) => truth == SyntheticTruth.FraudMuleRing ? "synthetic-fraud-mule-ring" : "synthetic-legitimate";
    private static string BusinessTime(long utcMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(utcMs).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    // ---- Failures -----------------------------------------------------------------------------------------------------------
    private static IResult Failure(HttpContext http, FinanceTimelineError error)
    {
        if (error.TimelineCode is { } timelineCode)
        {
            return TimelineHttpErrors.ToResult(http, timelineCode);
        }
        // A finance pin/reducer refusal (version mismatch, run mismatch, ...) is a typed 422; the message is diagnostic and holds no secret.
        var code = error.FinanceCode?.ToString() ?? "Unknown";
        return Results.Json(
            new E2eTimelineProblem { Code = "revenant_" + ToSnake(code), Status = 422, TraceId = Activity.Current?.TraceId.ToString() ?? http.TraceIdentifier },
            statusCode: 422);
    }
    private static IResult Invalid() =>
        Results.Json(new E2eTimelineProblem { Code = "invalid_request", Status = 400, TraceId = Activity.Current?.TraceId.ToString() }, statusCode: 400);
    private static IResult RunUnknown() =>
        Results.Json(new E2eTimelineProblem { Code = "revenant_run_unknown", Status = 404 }, statusCode: 404);
    private static bool ValidKey(string? key) => !string.IsNullOrWhiteSpace(key) && key.Length <= 128;
    private static Guid DeriveGuid(string text) => new(SHA256.HashData(Encoding.UTF8.GetBytes(text)).AsSpan(0, 16));
    private static string ToSnake(string pascal)
    {
        var builder = new StringBuilder();
        foreach (var c in pascal)
        {
            if (char.IsUpper(c) && builder.Length > 0)
            {
                builder.Append('_');
            }
            builder.Append(char.ToLowerInvariant(c));
        }
        return builder.ToString();
    }
}
