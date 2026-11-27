using System.Globalization;
using System.Security.Cryptography;
using Marrow.Kernel.Canonical;
using Marrow.Kernel.Security;
using Marrow.Timeline;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Revenant.Cases;
using Revenant.Contracts;
using Revenant.Counterfactual;
using Revenant.Finance;
using Revenant.Intelligence;
using Revenant.Observations;
using Revenant.Payments;
using Revenant.Scenario;
using Revenant.Timeline;
using static Revenant.Counterfactual.V4ReplayWorker;

namespace Revenant.Api;

public static class RevenantR8Endpoints
{
    public static IEndpointRouteBuilder MapRevenantR8(this IEndpointRouteBuilder app, string policy)
    {
        var g = app.MapGroup("/revenant/r8").RequireAuthorization(policy);
        g.MapPost("/runs", (R8Create req, HttpContext http, IActorContext actor, CancellationToken ct) =>
            Handle(http, actor, null, true, async service => Results.Ok(await service.Create(req, Key(http), ct)), ct)).Produces<R8Run>();
        g.MapGet("/runs", (HttpContext http, IActorContext actor, CancellationToken ct) =>
            Handle(http, actor, null, false, async service => Results.Ok(await service.List(ct)), ct)).Produces<R8Run[]>();
        g.MapGet("/runs/{id:guid}", (Guid id, HttpContext http, IActorContext actor, CancellationToken ct) =>
            Handle(http, actor, id, false, async service => Results.Ok(await service.Snapshot(new(id), ct)), ct)).Produces<R8Snapshot>();
        g.MapPost("/runs/{id:guid}/close", (Guid id, HttpContext http, IActorContext actor, CancellationToken ct) =>
            Handle(http, actor, id, true, async service => Results.Ok(await service.Close(new(id), Key(http), ct)), ct)).Produces<R8Snapshot>();
        g.MapGet("/runs/{id:guid}/timeline/{position}", (Guid id, string position, HttpContext http, IActorContext actor, CancellationToken ct) =>
            Handle(http, actor, id, false, async service => Results.Ok(await service.Preview(new(id), position, ct)), ct)).Produces<R8Preview>();
        g.MapGet("/runs/{id:guid}/cases/{intent}", (Guid id, string intent, HttpContext http, IActorContext actor, CancellationToken ct) =>
            Handle(http, actor, id, false, async service => Results.Ok(await service.Case(new(id), intent, ct)), ct)).Produces<R8Case>();
        g.MapPost("/runs/{id:guid}/cases/{intent}", (Guid id, string intent, R8Review req, HttpContext http, IActorContext actor, CancellationToken ct) =>
            Handle(http, actor, id, true, async service => Results.Ok(await service.Review(new(id), intent, req, Key(http), ct)), ct)).Produces<R8Case>();
        g.MapGet("/runs/{id:guid}/liquidity", (Guid id, HttpContext http, IActorContext actor, CancellationToken ct) =>
            Handle(http, actor, id, false, async service => Results.Ok(await service.Liquidity(new(id), ct)), ct)).Produces<R8Liquidity>();
        g.MapPost("/runs/{id:guid}/counterfactual", (Guid id, R8Replay req, HttpContext http, IActorContext actor, CancellationToken ct) =>
            Handle(http, actor, id, true, async service => Results.Ok(await service.Replay(new(id), req, Key(http), ct)), ct)).Produces<R8Job>();
        g.MapGet("/runs/{id:guid}/counterfactual/{job:guid}", (Guid id, Guid job, HttpContext http, IActorContext actor, CancellationToken ct) =>
            Handle(http, actor, id, false, async service => Results.Ok(await service.Job(new(id), job, ct)), ct)).Produces<R8Job>();
        g.MapPost("/runs/{id:guid}/counterfactual/{job:guid}/cancel", (Guid id, Guid job, HttpContext http, IActorContext actor, CancellationToken ct) =>
            Handle(http, actor, id, true, async service => Results.Ok(await service.Cancel(new(id), job, Key(http), ct)), ct)).Produces<R8Job>();
        g.MapGet("/runs/{id:guid}/audit", (Guid id, HttpContext http, IActorContext actor, CancellationToken ct) =>
            Handle(http, actor, id, false, async service => Results.Ok(await service.Audit(new(id), ct)), ct)).Produces<R8Audit>();
        return app;
    }

    private static string Key(HttpContext http) => http.Request.Headers["Idempotency-Key"].ToString() is { Length: > 0 and <= 80 } key
        && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.')
        ? key : throw new R8Failure(400, "idempotency_key_required");

    private static async Task<IResult> Handle(HttpContext http, IActorContext actor, Guid? id, bool write,
        Func<R8Service, Task<IResult>> action, CancellationToken ct)
    {
        if (!actor.IsAuthenticated || actor.PrincipalKind != PrincipalKind.Staff
            || !RevenantStaffAuthorizer.HasStaffRole(actor) || actor.TenantId is not { Length: > 0 })
            return Problem(403, "run_forbidden");
        var db = http.RequestServices.GetService<RevenantLedgerDataSource>();
        var store = http.RequestServices.GetService<V4SimulationStore>();
        var branch = http.RequestServices.GetService<V4BranchExecution>();
        var engine = http.RequestServices.GetService<ITimelineEngine<ObservedFinanceState, ObservedFinanceInput, ObservedFinanceContext>>();
        if (db is null || store is null || branch is null || engine is null) return Problem(503, "ledger_unavailable");
        var authorizer = http.RequestServices.GetRequiredService<RevenantStaffAuthorizer>();
        if (id is { } run && !(await authorizer.AuthorizeAsync(actor, RevenantResources.Run(new(run)),
            write ? ResourceOperation.Write : ResourceOperation.Read, ct)).IsAllowed)
            return Problem(403, "run_forbidden");
        try
        {
            return await action(new(db.DataSource, store, branch, engine,
                http.RequestServices.GetRequiredService<PaymentLedgerStore>(), actor));
        }
        catch (R8Failure e) { return Problem(e.Status, e.Code); }
        catch (V4ScheduleException e) { return Problem(409, e.Error.ToString()); }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.InsufficientPrivilege) { return Problem(403, "run_forbidden"); }
        catch (NpgsqlException) { return Problem(503, "database_unavailable"); }
        catch (InvalidOperationException e) { return Problem(409, e.Message == "ConflictingOperationKey" ? e.Message : "operation_not_verified"); }
    }

    private static IResult Problem(int status, string code) => Results.Problem(statusCode: status, title: code,
        extensions: new Dictionary<string, object?> { ["code"] = code });
}

internal sealed class R8Failure(int status, string code) : Exception(code)
{
    internal int Status => status;
    internal string Code => Message;
}
public sealed record R8Create(string Seed);
public sealed record R8Review(string Action, long ExpectedVersion);
public sealed record R8Intervention(string IntentId, string Action, long ExpectedVersion, long AtMs);
public sealed record R8Replay(string ForkPosition, long HorizonMs, string Policy, R8Intervention[]? Interventions);
public sealed record R8Run(Guid RunId, string Seed, string ManifestHash);
public sealed record R8Balance(string BankId, string PostedMinor, string ReservedMinor, string AvailableMinor);
public sealed record R8Risk(int? ScorePpm, int ThresholdPpm, string ModelId, string Policy, string Reason, string EvidenceSha256,
    R8RiskObservation[] Observations);
public sealed record R8RiskObservation(string Name, string Value);
public sealed record R8Anomaly(string Subject, long AtMs, long Observed, double Expected, double Score, double Threshold, string EvidenceSha256);
public sealed record R8AuditEvent(long Sequence, string Event, string Actor, string Subject, long? SubjectVersion, string? EvidenceSha256);
public sealed record R8Payment(string IntentId, string SourceBank, string DestinationBank, string AmountMinor,
    long AcceptedAtMs, string Status, string Reason, R8Risk? Risk);
public sealed record R8Case(Guid CaseId, string IntentId, long Version, string Status, int RequiredApprovals, int ApprovalCount,
    string AmountMinor, R8Risk? Risk);
public sealed record R8Marker(string Position, long AtMs, string Kind, string? IntentId);
public sealed record R8Snapshot(Guid RunId, Guid TimelineId, string LastPosition, long BoundaryMs, string Status,
    R8Balance[] Opening, R8Balance[] Balances, R8Payment[] Payments, R8Case[] Cases, R8Marker[] Markers,
    string AnomalyStatus, R8Anomaly[] Anomalies);
public sealed record R8Preview(string Position, long BoundaryMs, string StateHash, string Status, R8Balance[] Balances);
public sealed record R8BankPoint(long AtMs, R8Balance Balance);
public sealed record R8Projection(string BankId, long AtMs, string AvailableMinor);
public sealed record R8Liquidity(string Method, string Uncertainty, string AnomalyStatus, R8BankPoint[] Actual, R8Projection[] Projection);
public sealed record R8Audit(Guid TimelineId, string Status, string StateHash, string RecordHash, string ManifestHash,
    string Policy, string Model, string LastPosition, string LedgerVerification, string LedgerHeadHash, R8AuditEvent[] RecentEvents);
public sealed record R8Headline(string Label, string Unit, string Source, string Alternative, string Delta);
public sealed record R8ComparedPayment(string IntentId, string PrincipalMinor, string SourceStatus, string AlternativeStatus,
    string SourceCause, string AlternativeCause, long? SourceSettlementMs, long? AlternativeSettlementMs,
    string? SourceEvidence, string? AlternativeEvidence, string DelayMinutes, string DelayCostMinor);
public sealed record R8ComparedBalance(long AtMs, string BankId, R8Balance Source, R8Balance Alternative);
public sealed record R8Verified(Guid ChildTimelineId, string SourceStateHash, string SourceRecordHash, string ChildStateHash,
    string ChildRecordHash, string EvidenceDigest, string CostPolicy, long HorizonMs, string Mode, string SourcePolicy,
    string AlternativePolicy, string Model, string ContinuationPolicy, R8Headline[] Headlines, R8Headline[] Breakdown,
    R8ComparedPayment[] Payments, R8ComparedBalance[] Balances, R8Action[] Actions);
public sealed record R8Action(string IntentId, string Actor, long AtMs, string Action, string Status, string Code, string? Fact);
public sealed record R8Job(Guid JobId, string Status, string? Failure, R8Verified? Verified);

internal sealed class R8Service(NpgsqlDataSource db, V4SimulationStore store, V4BranchExecution branch,
    ITimelineEngine<ObservedFinanceState, ObservedFinanceInput, ObservedFinanceContext> engine, PaymentLedgerStore ledger, IActorContext actor)
{
    private const long Origin = 1_789_203_300_000; // Simulated, aligned UTC origin.
    private string Tenant => actor.TenantId!;
    private string Actor => actor.SubjectId;
    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);

    internal async Task<R8Run> Create(R8Create request, string key, CancellationToken ct)
    {
        if (!long.TryParse(request.Seed, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var seed) || N(seed) != request.Seed)
            throw new R8Failure(400, "invalid_seed");
        var hash = SHA256.HashData(new CanonicalWriter().WriteString("r8-scenario/1").WriteString(Tenant).WriteString(Actor).WriteString(key).ToArray());
        ScenarioRunId id = new(new Guid(hash.AsSpan(0, 16)));
        var cutoff = Origin + V4ReviewCounts.WarmupMs;
        var run = BehavioralEpisodeScenario.Create(id, seed, Origin, V4ReviewCounts.WarmupMs + 900_000, [2, 2, 2, 2, 2, 2]);
        var context = new ObservedFinanceContext(run.Manifest.ScenarioVersion, run.Manifest.ContentHash, Origin,
            PolicyCatalog.RefOf(PolicyCatalog.V7), ReviewPolicy.Version, ObservedRiskMode.ModelRequired,
            BehavioralRiskScorerV2.FeatureSchema, BehavioralRiskScorerV2.PublishedPin, [], []);
        await using var lease = await db.OpenConnectionAsync(ct);
        await using var mutex = new NpgsqlCommand("SELECT pg_advisory_lock(hashtextextended(@id, 18))", lease);
        mutex.Parameters.AddWithValue("id", id.ToString());
        await mutex.ExecuteNonQueryAsync(ct);
        try
        {
            await branch.OpenSourceAsync(Tenant, Actor, run, context, ct);

            var a = run.Manifest.Accounts;
            var intents = new[]
            {
                Intent("P-001", 1, 0, 2, 150_000, true, false),
                Intent("P-002", 2, 1, 4, 150_000, true, false),
                Intent("P-003", 3, 3, 6, 150_000, true, false),
                Intent("P-004", 4, 5, 8, 150_000, false, true)
            };
            await using (var tx = await BeginAsync(lease, Tenant, ct))
            {
                foreach (var p in intents)
                {
                    await using var label = Command(tx, """
                        INSERT INTO revenant.counterfactual_truth(tenant_id,source_run_id,intent_id,malicious)
                        VALUES(@tenant,@run,@intent,@label) ON CONFLICT DO NOTHING
                        """, ("tenant", Tenant), ("run", id.Value), ("intent", p.IntentId), ("label", p.Sequence is 1 or 3));
                    await label.ExecuteNonQueryAsync(ct);
                }
                await tx.CommitAsync(ct);
            }

            for (var b = Origin + ObservedFinanceVersion.BucketMs; b <= cutoff; b += ObservedFinanceVersion.BucketMs)
                if ((await store.SealAsync(Tenant, id, b, ct)).Kind != V4StepKind.Sealed) throw new R8Failure(409, "scenario_seal_blocked");
            foreach (var p in intents)
            {
                await Dispatch(id, new($"seed:intake:{p.IntentId}", V4CommandClass.Exogenous, "PaymentIntent", ScenarioSchemas.InputSchemaVersion,
                    [.. PaymentIntentCodec.Instance.EncodeCanonical(p)], cutoff, cutoff), ct);
                var result = await Dispatch(id, Opportunity("Screening", p.IntentId, cutoff, $"seed:screen:{p.IntentId}"), ct);
                if (result.OutcomeCode == nameof(PaymentStatus.Cleared))
                    await Dispatch(id, Opportunity("Settlement", p.IntentId, cutoff, $"seed:settle:{p.IntentId}"), ct);
            }
            _ = await branch.SynchronizeAsync(Tenant, id, ct);
            return new(id.Value, N(seed), run.Manifest.ContentHash);

            PaymentIntent Intent(string name, int seq, int from, int to, long amount, bool beneficiary, bool sanctions) =>
                new(id, name, seq, cutoff, a[from].BankId, a[from].AccountId, a[to].BankId, a[to].AccountId,
                    Money.Usd(amount), SyntheticTruth.Legitimate, new(beneficiary, sanctions));
        }
        finally
        {
            await using var unlock = new NpgsqlCommand("SELECT pg_advisory_unlock(hashtextextended(@id, 18))", lease);
            unlock.Parameters.AddWithValue("id", id.ToString());
            await unlock.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    internal async Task<R8Run[]> List(CancellationToken ct)
    {
        await using var q = db.CreateCommand("""
            SELECT r.id,r.seed,r.manifest_hash FROM revenant.scenario_run r
            JOIN revenant.scenario_run_staff s ON s.scenario_run_id=r.id
            WHERE r.tenant_id=@tenant AND s.subject_id=@actor AND r.finance_version='revenant-finance-reducer/4'
              AND NOT EXISTS (SELECT 1 FROM revenant.scenario_run_staff_revocation x WHERE x.scenario_run_id=r.id AND x.subject_id=@actor)
            ORDER BY r.created_at DESC LIMIT 100
            """);
        q.Parameters.AddWithValue("tenant", Tenant);
        q.Parameters.AddWithValue("actor", Actor);
        var runs = new List<R8Run>();
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) runs.Add(new(r.GetGuid(0), N(r.GetInt64(1)), r.GetString(2)));
        return runs.ToArray();
    }

    internal async Task<R8Snapshot> Snapshot(ScenarioRunId id, CancellationToken ct)
    {
        var history = await branch.ReadProjectionHistoryAsync(Tenant, id, ct);
        var b = await store.ReadBindingAsync(Tenant, id, ct);
        var segment = history.Segments.Single();
        var final = history.Genesis;
        foreach (var row in segment.Facts) final = row.Apply(final, segment.Context);
        var risk = Risks(segment);
        var cases = new List<R8Case>();
        foreach (var p in final.Ledger.Payments.Values.Where(x => x.Case.HasValue))
            cases.Add(await Case(id, p.Intent.IntentId, ct, risk));
        var verification = (await engine.ReplayAsync(b.TimelineId, ct)).Value;
        var advisory = await Anomalies(id, final.LastSealedBoundaryMs, segment.Context.OriginMs, ct);
        return new(id.Value, b.TimelineId.Value, N(verification.FinalPosition), final.LastSealedBoundaryMs, verification.Status.ToString(),
            Balances(history.Genesis), Balances(final), final.Ledger.Payments.Values.Select(x => new R8Payment(x.Intent.IntentId,
                x.Intent.SourceBankId, x.Intent.DestBankId, N(x.Intent.Amount.MinorUnits), x.Intent.EventTimeUtcMs,
                x.Status.ToString(), x.Reason.ToString(), risk.GetValueOrDefault(x.Intent.IntentId))).ToArray(),
            cases.ToArray(), segment.Facts.Where(x => x.Input.Sequence <= verification.FinalPosition).Select(x =>
                new R8Marker(N(x.Input.Sequence), x.Input.EffectiveTimeMs, x.Input.Kind.ToString(),
                    x.Input.Kind == ObservedFactKind.Screening ? V4ModelScreeningEvidence.Decode(x.Input).IntentId : null)).ToArray(), advisory.Status, advisory.Alerts);
    }

    internal async Task<R8Case> Case(ScenarioRunId id, string intent, CancellationToken ct, Dictionary<string, R8Risk>? risk = null)
    {
        var c = await ledger.GetCaseAsync(Tenant, id, intent, ct) ?? throw new R8Failure(404, "case_not_found");
        risk ??= Risks((await branch.ReadProjectionHistoryAsync(Tenant, id, ct)).Segments.Single());
        return new(c.CaseId, intent, c.Version, c.Status.ToString(), c.RequiredApprovals, c.Approvals.Count, N(c.AmountMinor),
            risk.GetValueOrDefault(intent));
    }

    internal async Task<R8Case> Review(ScenarioRunId id, string intent, R8Review req, string key, CancellationToken ct)
    {
        RequireMfa();
        if (req.ExpectedVersion < 1) throw new R8Failure(400, "invalid_case_version");
        var action = Parse(req.Action);
        var command = $"r8:review:{key}";
        var prior = (await store.ReadJournalAsync(Tenant, id, ct)).SingleOrDefault(x => x.Request.Key == command);
        var b = await store.ReadBindingAsync(Tenant, id, ct);
        if (b.Fork is not null) throw new R8Failure(403, "sandbox_live_write_denied");
        var history = (await branch.ReadProjectionHistoryAsync(Tenant, id, ct)).Segments.Single();
        var at = prior?.Request.DueTimeMs ?? history.Facts.LastOrDefault()?.Input.EffectiveTimeMs ?? Origin;
        var done = await Dispatch(id, new(command, V4CommandClass.ConditionalHumanRequest, "ReviewDecision", "v4-human-request/1",
            [.. new CanonicalWriter().WriteInt32(1).WriteString(intent).WriteString(Actor).WriteInt64(req.ExpectedVersion)
                .WriteBoolean(true).WriteByte((byte)action).ToArray()], at, at), ct);
        if (done.OutcomeCode != "reviewed") throw new R8Failure(409, done.OutcomeCode ?? "case_not_committed");
        var result = await Case(id, intent, ct);
        if (result.Status == nameof(CaseStatus.Released))
            await Dispatch(id, Opportunity("Settlement", intent, at, $"r8:release:{key}"), ct);
        _ = await branch.SynchronizeAsync(Tenant, id, ct);
        return result;
    }

    internal async Task<R8Snapshot> Close(ScenarioRunId id, string key, CancellationToken ct)
    {
        _ = key;
        var history = await branch.ReadProjectionHistoryAsync(Tenant, id, ct);
        var horizon = Origin + V4ReviewCounts.WarmupMs + 900_000;
        var lastSeal = history.Segments.Single().Facts.LastOrDefault(x => x.Input.Kind == ObservedFactKind.ObservationSeal)?.Input.EffectiveTimeMs ?? Origin;
        for (var b = lastSeal + ObservedFinanceVersion.BucketMs; b <= horizon; b += ObservedFinanceVersion.BucketMs)
            if ((await store.SealAsync(Tenant, id, b, ct)).Kind != V4StepKind.Sealed) throw new R8Failure(409, "scenario_seal_blocked");
        _ = await branch.CompleteAsync(Tenant, id, ct);
        return await Snapshot(id, ct);
    }

    internal async Task<R8Preview> Preview(ScenarioRunId id, string position, CancellationToken ct)
    {
        var b = await store.ReadBindingAsync(Tenant, id, ct);
        if (!TimelinePosition.TryParseWire(position, out var parsed)) throw new R8Failure(400, "invalid_timeline_position");
        var restored = await engine.RestoreAsync(b.TimelineId, parsed.Value, ct);
        if (restored.IsFailure) throw new R8Failure(409, restored.Error.Code.ToString());
        var value = restored.Value;
        return new(N(value.Position), value.State.LastSealedBoundaryMs, value.StateHash, value.HistoryStatus.ToString(), Balances(value.State));
    }

    internal async Task<R8Liquidity> Liquidity(ScenarioRunId id, CancellationToken ct)
    {
        var history = await branch.ReadProjectionHistoryAsync(Tenant, id, ct);
        var projector = V4ObservationProjector.Project(history);
        var boundary = projector.Banks.LastOrDefault()?.Seal.BoundaryMs;
        if (boundary is null) return new(AdvisoryLiquidityProjection.Method, "Uncalibrated", "UnobservedOrigin", [], []);
        var forecast = new List<R8Projection>();
        foreach (var b in Balances(history.Genesis))
        {
            var series = projector.BankAvailableSeries(b.BankId, boundary.Value);
            var e = AdvisoryLiquidityProjection.Project(series, history.Genesis.RunId);
            if (e.Liquidity is { } projection)
                foreach (var p in projection.Horizons) forecast.Add(new(b.BankId, p.BoundaryMs, N(p.ProjectedAvailableMinor)));
        }
        var anomalyStatus = (await Anomalies(id, boundary.Value, history.Segments[0].Context.OriginMs, ct)).Status;
        return new(AdvisoryLiquidityProjection.Method, "Uncalibrated", anomalyStatus,
            projector.Banks.Select(x => new R8BankPoint(x.Seal.BoundaryMs, Balance(x.BankId, x.Balance))).ToArray(), forecast.ToArray());
    }

    internal async Task<R8Job> Replay(ScenarioRunId id, R8Replay req, string key, CancellationToken ct)
    {
        var b = await store.ReadBindingAsync(Tenant, id, ct);
        if (b.Fork is not null || req.Policy != PolicyCatalog.V8Id || !TimelinePosition.TryParseWire(req.ForkPosition, out var fork) || fork.Value == 0 || !ObservedFinanceVersion.IsBoundary(req.HorizonMs))
            throw new R8Failure(400, "invalid_replay_request");
        var requested = req.Interventions ?? [];
        if (requested.Length > 20) throw new R8Failure(400, "too_many_interventions");
        if (requested.Any(x => x is null || string.IsNullOrWhiteSpace(x.IntentId) || x.IntentId.Length > 128
            || x.ExpectedVersion < 1 || x.AtMs < 0 || x.AtMs >= req.HorizonMs))
            throw new R8Failure(400, "invalid_intervention");
        if (requested.Length > 0) RequireMfa();
        var actions = requested.Select((x, i) => new V4ReplayAction($"sandbox:{i:D2}", x.IntentId, x.AtMs, x.ExpectedVersion,
            new(Actor, true, RevenantLedgerService.IsSupervisor(actor), true), Parse(x.Action), null)).ToArray();
        var mode = actions.Length == 0 ? V4ComparisonMode.PolicyOnly : V4ComparisonMode.PolicyAndSandboxIntervention;
        var job = await SubmitAsync(db, Tenant, Actor, id, b.TimelineId, key, new(fork.Value, req.HorizonMs, req.Policy, mode, actions), ct);
        return Describe(job);
    }

    internal async Task<R8Job> Job(ScenarioRunId id, Guid job, CancellationToken ct) => Describe(await Find(id, job, ct));
    internal async Task<R8Job> Cancel(ScenarioRunId id, Guid job, string key, CancellationToken ct)
    {
        _ = key;
        var value = await Find(id, job, ct);
        if (value.Actor != Actor) throw new R8Failure(403, "cancel_forbidden");
        _ = await CancelAsync(db, Tenant, job, Actor, ct);
        return Describe(await Find(id, job, ct));
    }

    private async Task<V4ReplayJob> Find(ScenarioRunId id, Guid job, CancellationToken ct) =>
        await ReadAsync(db, Tenant, job, ct) is { } value && value.Source == id ? value : throw new R8Failure(404, "job_not_found");

    internal async Task<R8Audit> Audit(ScenarioRunId id, CancellationToken ct)
    {
        var b = await store.ReadBindingAsync(Tenant, id, ct);
        var context = await store.ReadPinnedContextAsync(Tenant, id, ct);
        var proof = (await engine.ReplayAsync(b.TimelineId, ct)).Value;
        var sql = await ledger.VerifyAuditChainAsync(Tenant, id, ct);
        return new(b.TimelineId.Value, proof.Status.ToString(), proof.FinalStateHash, proof.LastRecordHash, context.ManifestSha256,
            context.BasePolicy.PolicyId, context.RiskModel!.ModelId, N(proof.FinalPosition), sql.ChainIntact ? "Verified" : "Unverified",
            sql.HeadHashHex, (await ledger.ReadAuditAsync(Tenant, id, ct)).TakeLast(30).Select(e => new R8AuditEvent(e.Sequence,
                e.EventType, e.Actor, e.SubjectRef, e.SubjectVersion, e.RefSha256 is { } hash ? Convert.ToHexStringLower(hash) : null)).ToArray());
    }

    private async Task<V4ScheduledCommand> Dispatch(ScenarioRunId id, V4CommandRequest request, CancellationToken ct)
    {
        var admitted = await store.AdmitAsync(Tenant, id, request, ct);
        if (V4SimulationScheduler.IsTerminal(admitted.Status)) return admitted;
        for (var i = 0; i < 8; i++)
        {
            var step = await store.AdvanceOneStepAsync(Tenant, id, ct);
            if (step.Kind == V4StepKind.Blocked) throw new R8Failure(503, step.Command?.OutcomeCode ?? "finance_blocked");
            if (step.Command is { } done && done.Sequence == admitted.Sequence && V4SimulationScheduler.IsTerminal(done.Status)) return done;
        }
        throw new R8Failure(409, "finance_pending");
    }

    private void RequireMfa()
    {
        if (!RevenantLedgerService.HasFreshMfa(actor)) throw new R8Failure(403, "mfa_required");
    }
    private static CaseAction Parse(string action) => action switch
    { "Approve" => CaseAction.Approve, "Reject" => CaseAction.Reject, _ => throw new R8Failure(400, "invalid_review_action") };
    private static V4CommandRequest Opportunity(string kind, string id, long at, string key) =>
        new(key, V4CommandClass.DerivedOpportunity, kind, "v4-payment-opportunity/1",
            [.. new CanonicalWriter().WriteInt32(1).WriteString(id).ToArray()], at, at);
    private static Dictionary<string, R8Risk> Risks(V4ProjectionSegment segment) => segment.Facts
        .Where(x => x.Input.Kind == ObservedFactKind.Screening).Select(x => (x.Input, Evidence: V4ModelScreeningEvidence.Decode(x.Input)))
        .ToDictionary(x => x.Evidence.IntentId, x => new R8Risk(x.Evidence.Ppm, x.Evidence.Model.ThresholdPpm,
            x.Evidence.Model.ModelId, x.Evidence.BasePolicy.PolicyId, x.Evidence.Decision.ToString(), x.Input.EvidenceSha256!, Observations(x.Evidence)));

    private static R8RiskObservation[] Observations(V4ModelScreeningEvidence e)
    {
        if (e.Ppm is null) return [];
        var vector = RiskFeatureVectorV2.Decode(e.Vector.AsSpan());
        return [new("Base rule", e.BaseReason.ToString()), new("Review slots available", N(e.Available)),
            new("Prior causal fact", N(e.PriorSequence)), new("Feature vector digest", vector.Digest)];
    }

    private async Task<(string Status, R8Anomaly[] Alerts)> Anomalies(ScenarioRunId id, long boundary, long origin,
        CancellationToken ct)
    {
        if ((boundary - origin) / ObservedFinanceVersion.BucketMs < 1152)
            return (AdvisoryStatus.InsufficientHistory.ToString(), []);
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var tx = await BeginAsync(connection, Tenant, ct);
        await using var q = Command(tx, """
            SELECT canonical, evidence_digest FROM revenant.v4_advisory_evidence
             WHERE tenant_id=@tenant AND execution_run_id=@run AND boundary_ms=@boundary
               AND schema_id='anomaly-evidence/2' AND policy_id='R7-AnomalyGlobalPolicy/1'
               AND model_id='revenant-ssa-anomaly/672a05a26995653585e5ba9505807df7a2f707914886634c39b133c7dbc72980'
             ORDER BY subject_id LIMIT 100
            """, ("tenant", Tenant), ("run", id.Value), ("boundary", boundary));
        await using var r = await q.ExecuteReaderAsync(ct);
        var alerts = new List<R8Anomaly>();
        var statuses = new HashSet<AdvisoryStatus>();
        while (await r.ReadAsync(ct))
        {
            var bytes = r.GetFieldValue<byte[]>(0);
            var digest = Convert.ToHexStringLower(r.GetFieldValue<byte[]>(1));
            var (status, alert) = DecodeAlert(bytes, digest, id, boundary);
            statuses.Add(status);
            if (alert is not null) alerts.Add(alert);
        }
        return (statuses.Count switch
        {
            0 => "AdvisoryReceiptNotRequested",
            1 => statuses.Single().ToString(),
            _ => statuses.Contains(AdvisoryStatus.Available) ? "PartiallyAvailable" : "Unavailable",
        }, alerts.ToArray());
    }

    internal static (AdvisoryStatus Status, R8Anomaly? Alert) DecodeAlert(byte[] bytes, string digest,
        ScenarioRunId execution, long boundary)
    {
        ObservationEncoding.RequireDigest(bytes, digest);
        var reader = new CanonicalReader(bytes);
        ObservationEncoding.Expect(ref reader, "anomaly-evidence/2");
        var seal = V4SealIdentity.Read(ref reader);
        var lineage = reader.ReadGuid();
        var subject = reader.ReadString();
        _ = reader.ReadString(); // unit
        _ = reader.ReadString(); // series schema
        _ = reader.ReadBytes();  // series hash
        var model = reader.ReadString();
        var zip = Convert.ToHexStringLower(reader.ReadBytes());
        var policy = reader.ReadString();
        var status = (AdvisoryStatus)reader.ReadByte();
        if (seal.ExecutionRunId != execution || lineage != execution.Value || seal.BoundaryMs != boundary
            || model != "revenant-ssa-anomaly/672a05a26995653585e5ba9505807df7a2f707914886634c39b133c7dbc72980"
            || zip != "03e895da4fa4348b69b1251c6ef9c1c9e6c2deee893e83bf2a983c698281b35e"
            || policy != AdvisoryEvidence.GlobalAnomalyPolicy || !Enum.IsDefined(status))
            throw new InvalidOperationException("Anomaly receipt does not match the pinned source.");
        if (status != AdvisoryStatus.Available)
        {
            reader.EnsureEnd();
            return (status, null);
        }
        var count = reader.ReadInt64();
        var expected = BitConverter.Int64BitsToDouble(reader.ReadInt64());
        _ = reader.ReadInt64(); // residual
        _ = reader.ReadByte();  // statistic identity
        var score = BitConverter.Int64BitsToDouble(reader.ReadInt64());
        var threshold = BitConverter.Int64BitsToDouble(reader.ReadInt64());
        var alert = reader.ReadBoolean();
        return (status, alert ? new(subject, boundary, count, expected, score, threshold, digest) : null);
    }
    private static R8Balance[] Balances(ObservedFinanceState state) => state.Ledger.Accounts.Values.GroupBy(x => x.BankId)
        .OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => Balance(x.Key, new(x.Sum(y => y.PostedMinor), x.Sum(y => y.ReservedMinor)))).ToArray();
    private static R8Balance Balance(string bank, ObservationBalance b) => new(bank, N(b.Posted), N(b.Reserved), N(b.Available));

    private static R8Job Describe(V4ReplayJob job)
    {
        if (job.Status != "Verified" || job.Result is null) return new(job.Id, job.Status, job.Failure, null);
        var r = DecodeResult(job.Result);
        var e = r.Economics;
        var a = e.Source;
        var b = e.Alternative;
        R8Headline[] cards =
        [
            Metric("Fraud prevented", a.InterceptedMinor, b.InterceptedMinor),
            Metric("Legitimate payments delayed", a.DelayedCount, b.DelayedCount, "payments"),
            Metric("False-positive cost", a.InterventionCostMinor, b.InterventionCostMinor),
            Metric("Net financial effect", a.NetFinancialPositionMinor, b.NetFinancialPositionMinor),
        ];
        R8Headline[] breakdown =
        [
            Metric("Deferred malicious principal", a.DeferredMinor, b.DeferredMinor),
            Metric("Settled malicious principal", a.MaliciousSettledMinor, b.MaliciousSettledMinor),
            Metric("Recovered principal", a.RecoveredMinor, b.RecoveredMinor),
            Metric("Legitimate delay cost", a.DelayCostMinor, b.DelayCostMinor),
            Metric("Incremental delay", a.DelayMinutes, b.DelayMinutes, "minutes"),
            Metric("Avoided malicious exposure", -a.EligibleMaliciousExposureMinor, -b.EligibleMaliciousExposureMinor),
        ];
        return new(job.Id, job.Status, null, new(r.ChildTimelineId, r.SourceStateHash, r.SourceRecordHash, r.ChildStateHash,
            r.ChildRecordHash, e.EvidenceDigest, e.CostPolicy.Version, e.HorizonMs, r.Mode.ToString(), r.SourcePolicy, r.AlternativePolicy,
            r.Model.ModelId, r.ContinuationPolicy, cards, breakdown, e.Payments.Select(p => new R8ComparedPayment(p.IntentId,
                N(p.PrincipalMinor), p.Source.Status.ToString(), p.Alternative.Status.ToString(), p.Source.Cause.ToString(),
                p.Alternative.Cause.ToString(), p.Source.SettledAtMs, p.Alternative.SettledAtMs, p.Source.ScreeningEvidenceSha256,
                p.Alternative.ScreeningEvidenceSha256, N(p.AlternativeDelayMinutes), N(p.DelayCostDeltaMinor))).ToArray(),
            e.Balances.Select(p => new R8ComparedBalance(p.BoundaryMs, p.BankId, Balance(p.BankId, p.Source), Balance(p.BankId, p.Alternative))).ToArray(),
            r.Actions.Select(p => new R8Action(p.Action.IntentId, p.Action.Actor.SubjectId, p.Action.At, p.Action.Review?.ToString() ?? "Return",
                p.Disposition.ToString(), p.Code, p.BranchFact is { } fact ? N(fact) : null)).ToArray()));
    }
    private static R8Headline Metric(string name, long a, long b, string unit = "USD minor units") =>
        new(name, unit, N(a), N(b), N(checked(b - a)));
}
