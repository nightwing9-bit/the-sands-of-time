using Marrow.Kernel.Messaging;
using Marrow.Kernel.Results;
using Marrow.Kernel.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Revenant.Contracts;
using Revenant.Intelligence;
using Revenant.Payments;
using Revenant.Projection;
using Revenant.Timeline;
using Marrow.Timeline;

namespace Revenant.Api;

/// <summary>Owns the lifetime of the dedicated ledger connection pool so the container disposes it on shutdown.</summary>
public sealed class RevenantLedgerDataSource(NpgsqlDataSource dataSource) : IAsyncDisposable
{
    public NpgsqlDataSource DataSource { get; } = dataSource;

    public ValueTask DisposeAsync() => DataSource.DisposeAsync();
}

public static class RevenantLedgerRegistration
{
    public const string ConnectionStringName = "RevenantLedger";

    /// <summary>
    /// Registers the Revenant ledger services (LiveOps finance writer) when <c>ConnectionStrings:RevenantLedger</c> is configured.
    /// The connection must log in as a member of <c>revenant_ledger_writer</c> (never the migration owner, never BYPASSRLS);
    /// it is a dedicated pool, separate from the shared identity pool. Without it nothing is registered and the API answers
    /// <c>503 ledger_unavailable</c>: it never falls back to another credential. The host supplies the shared outbox writer as
    /// <paramref name="outboxFactory"/> so this assembly does not reference Marrow.Infrastructure. Login provisioning for
    /// the deployed hosts is closed in task 7.5.
    /// </summary>
    public static IServiceCollection AddRevenantLedger(
        this IServiceCollection services, IConfiguration configuration, Func<NpgsqlTransaction, IOutboxWriter> outboxFactory)
    {
        ArgumentNullException.ThrowIfNull(outboxFactory);
        var connectionString = configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return services;
        }
        NpgsqlDataSource dataSource;
        try
        {
            dataSource = new NpgsqlDataSourceBuilder(connectionString).Build();
        }
        catch (Exception)
        {
            throw new InvalidOperationException($"Configuration 'ConnectionStrings:{ConnectionStringName}' is not a valid connection string.");
        }
        var options = new RevenantLedgerOptions();
        configuration.GetSection(RevenantLedgerOptions.SectionName).Bind(options);
        if (string.IsNullOrWhiteSpace(options.TenantId) || options.TenantId.Length > 128)
        {
            throw new InvalidOperationException($"Configuration '{RevenantLedgerOptions.SectionName}:TenantId' must be 1-128 characters.");
        }
        services.AddSingleton(new RevenantLedgerDataSource(dataSource));
        services.AddSingleton(options);
        // Spec 08 task 8.3: the pinned-artifact loader for model-required runs (loads lazily, verifies before use, never trains).
        var riskModels = new RiskModelOptions();
        configuration.GetSection(RiskModelOptions.SectionName).Bind(riskModels);
        services.AddSingleton(new RiskModelProvider(string.IsNullOrWhiteSpace(riskModels.ArtifactRoot) ? RiskModelOptions.DefaultArtifactRoot : riskModels.ArtifactRoot));
        services.AddSingleton(sp => new PaymentLedgerStore(dataSource, outboxFactory, sp.GetService<TimeProvider>() ?? TimeProvider.System,
            sp.GetRequiredService<RiskModelProvider>()));
        services.AddSingleton(new RevenantRunDirectory(dataSource));
        services.AddSingleton(sp => new RevenantStaffAuthorizer(
            sp.GetRequiredService<RevenantRunDirectory>(), sp.GetRequiredService<RevenantRunDirectory>()));
        services.AddSingleton<RevenantLedgerService>();
        return services;
    }

    /// <summary>
    /// Registers the LiveOps settlement loop (<see cref="SettlementWorker"/>) for the Worker host. It uses the same dedicated
    /// <c>revenant_ledger_writer</c> pool as <see cref="AddRevenantLedger"/>, which must be called first; without a configured
    /// ledger nothing is registered. The tenant is <c>Revenant:Ledger:TenantId</c>; batch and backoff settings are
    /// <c>Revenant:Settlement</c>.
    /// </summary>
    public static IServiceCollection AddRevenantSettlementWorker(this IServiceCollection services, IConfiguration configuration)
    {
        if (!services.Any(d => d.ServiceType == typeof(PaymentLedgerStore)))
        {
            return services;
        }
        var options = new SettlementWorkerOptions();
        configuration.GetSection(SettlementWorkerOptions.SectionName).Bind(options);
        if (options.BatchSize < 1 || options.BatchSize > PaymentLedgerStore.MaxSettlementBatch || options.IdleDelayMs < 10
            || options.MaxBackoffMs < 100)
        {
            throw new InvalidOperationException(
                $"Configuration '{SettlementWorkerOptions.SectionName}' needs BatchSize 1-{PaymentLedgerStore.MaxSettlementBatch}, IdleDelayMs >= 10 and MaxBackoffMs >= 100.");
        }
        services.AddSingleton(options);
        services.AddSingleton<SettlementWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<SettlementWorker>());
        return services;
    }

    /// <summary>
    /// Registers the LiveOps screening loop (<see cref="ScreeningWorker"/>, task 7.5), the production path into Held. Like the
    /// settlement loop it needs <see cref="AddRevenantLedger"/> first; settings are <c>Revenant:Screening</c>.
    /// </summary>
    public static IServiceCollection AddRevenantScreeningWorker(this IServiceCollection services, IConfiguration configuration)
    {
        if (!services.Any(d => d.ServiceType == typeof(PaymentLedgerStore)))
        {
            return services;
        }
        var options = new ScreeningWorkerOptions();
        configuration.GetSection(ScreeningWorkerOptions.SectionName).Bind(options);
        if (options.BatchSize < 1 || options.BatchSize > PaymentLedgerStore.MaxSettlementBatch || options.IdleDelayMs < 10 || options.MaxBackoffMs < 100)
        {
            throw new InvalidOperationException(
                $"Configuration '{ScreeningWorkerOptions.SectionName}' needs BatchSize 1-{PaymentLedgerStore.MaxSettlementBatch}, IdleDelayMs >= 10 and MaxBackoffMs >= 100.");
        }
        services.AddSingleton(options);
        services.AddSingleton<ScreeningWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<ScreeningWorker>());
        return services;
    }

    /// <summary>
    /// Registers the V2 Timeline projection (<see cref="LedgerProjector"/> and its LiveOps catch-up loop, task 7.5). The host must have
    /// registered the shared engine for the V2 shape (<c>AddMarrowTimeline(FinanceLedgerTimelineVersions.All, ...)</c>) over its main
    /// <see cref="NpgsqlDataSource"/>, whose login holds <c>marrow_timeline_runtime</c> and <c>revenant_replay_reader</c> only: the
    /// projector reads live facts and state and writes nothing but Timeline records. Without a configured ledger (no tenant to select)
    /// nothing is registered. <paramref name="runWorker"/> false registers the projector without the background loop.
    /// </summary>
    public static IServiceCollection AddRevenantLedgerProjection(
        this IServiceCollection services, IConfiguration configuration, bool runWorker = true)
    {
        if (!services.Any(d => d.ServiceType == typeof(RevenantLedgerOptions)))
        {
            return services;
        }
        var options = new LedgerProjectionOptions();
        configuration.GetSection(LedgerProjectionOptions.SectionName).Bind(options);
        if (options.IntervalMs < 50)
        {
            throw new InvalidOperationException($"Configuration '{LedgerProjectionOptions.SectionName}:IntervalMs' must be at least 50.");
        }
        services.AddSingleton(options);
        services.AddSingleton(sp => new LedgerProjector(
            sp.GetRequiredService<NpgsqlDataSource>(),
            sp.GetRequiredService<ITimelineEngine<LedgerFinanceState, FinanceFact, LedgerFinanceContext>>()));
        // Spec 08 task 8.3: model-aware (V3) runs are projected too when the host registered the V3 engine shape
        // (AddMarrowTimeline(ModelLedgerTimelineVersions.All, ...)) before this call.
        var modelAware = services.Any(d => d.ServiceType == typeof(ITimelineEngine<ModelLedgerState, FinanceFact, ModelLedgerContext>));
        if (modelAware)
        {
            services.AddSingleton(sp => new ModelLedgerProjector(
                sp.GetRequiredService<NpgsqlDataSource>(),
                sp.GetRequiredService<ITimelineEngine<ModelLedgerState, FinanceFact, ModelLedgerContext>>()));
        }
        if (runWorker)
        {
            services.AddSingleton(sp => new LedgerProjectionWorker(
                sp.GetRequiredService<LedgerProjector>(), sp.GetRequiredService<RevenantLedgerOptions>(), sp.GetRequiredService<LedgerProjectionOptions>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<LedgerProjectionWorker>>(),
                modelAware ? sp.GetRequiredService<ModelLedgerProjector>() : null));
            services.AddHostedService(sp => sp.GetRequiredService<LedgerProjectionWorker>());
        }
        return services;
    }
}

public static class RevenantLedgerEndpoints
{
    private const int MaxIdLength = 128;

    /// <summary>
    /// Maps the production Revenant ledger endpoints under <c>/revenant/ledger</c>. <paramref name="staffPolicy"/> is the host's
    /// authenticated-staff authorization policy (the identity policy name is owned by the shared identity layer). The handlers
    /// carry no finance rules: they validate input shape, call <see cref="RevenantLedgerService"/> with the verified actor and
    /// translate typed results.
    /// </summary>
    public static IEndpointRouteBuilder MapRevenantLedgerApi(this IEndpointRouteBuilder app, string staffPolicy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(staffPolicy);
        var group = app.MapGroup("/revenant/ledger").RequireAuthorization(staffPolicy);
        group.MapPost("/runs", OpenRun).WithName("OpenRevenantRun").Produces<RunResponse>().WithProblems();
        group.MapGet("/runs/{runId:guid}", GetRun).WithName("GetRevenantRun").Produces<RunResponse>().WithProblems();
        group.MapPost("/runs/{runId:guid}/payments", SubmitPayment).WithName("SubmitRevenantPayment").Produces<PaymentResponse>().WithProblems();
        group.MapGet("/runs/{runId:guid}/payments/{intentId}", GetPayment).WithName("GetRevenantPayment").Produces<PaymentResponse>().WithProblems();
        group.MapPost("/runs/{runId:guid}/payments/{intentId}/return", ReturnPayment).WithName("ReturnRevenantPayment").Produces<PaymentResponse>().WithProblems();
        group.MapPost("/runs/{runId:guid}/staff", GrantStaff).WithName("GrantRevenantRunStaff").Produces(StatusCodes.Status204NoContent).WithProblems();
        group.MapPost("/runs/{runId:guid}/staff/{subjectId}/revoke", RevokeStaff).WithName("RevokeRevenantRunStaff").Produces(StatusCodes.Status204NoContent).WithProblems();
        group.MapGet("/runs/{runId:guid}/cases/{intentId}", GetCase).WithName("GetRevenantCase").Produces<CaseResponse>().WithProblems();
        group.MapPost("/runs/{runId:guid}/cases/{intentId}/assign", (Guid runId, string intentId, CaseActionRequest? body, HttpContext http, IActorContext actor, CancellationToken ct) =>
            CaseAction(runId, intentId, Cases.CaseAction.Assign, body, http, actor, ct)).WithName("AssignRevenantCase").Produces<CaseResponse>().WithProblems();
        group.MapPost("/runs/{runId:guid}/cases/{intentId}/approve", (Guid runId, string intentId, CaseActionRequest? body, HttpContext http, IActorContext actor, CancellationToken ct) =>
            CaseAction(runId, intentId, Cases.CaseAction.Approve, body, http, actor, ct)).WithName("ApproveRevenantCase").Produces<CaseResponse>().WithProblems();
        group.MapPost("/runs/{runId:guid}/cases/{intentId}/reject", (Guid runId, string intentId, CaseActionRequest? body, HttpContext http, IActorContext actor, CancellationToken ct) =>
            CaseAction(runId, intentId, Cases.CaseAction.Reject, body, http, actor, ct)).WithName("RejectRevenantCase").Produces<CaseResponse>().WithProblems();
        group.MapPost("/runs/{runId:guid}/cases/{intentId}/comments", CommentCase).WithName("CommentRevenantCase").Produces<CaseResponse>().WithProblems();
        group.MapGet("/runs/{runId:guid}/audit/verification", VerifyAudit).WithName("VerifyRevenantAudit").Produces<AuditVerificationResponse>().WithProblems();
        return app;
    }

    private static string Correlation(HttpContext http) =>
        http.TraceIdentifier is { Length: > 0 and <= 200 } trace ? trace : Guid.NewGuid().ToString("N");

    private static async Task<IResult> GetCase(Guid runId, string intentId, HttpContext http, IActorContext actor, CancellationToken ct) =>
        await Guarded(http, ct, async service =>
        {
            if (!ValidId(intentId))
            {
                return Problem(http, PaymentFailureCode.InvalidRequest);
            }
            var result = await service.GetCaseAsync(actor, new ScenarioRunId(runId), intentId, ct, Correlation(http));
            return result.Match(view => Results.Ok(ToResponse(view, false, null)), failure => Problem(http, failure.Code));
        });

    private static async Task<IResult> CaseAction(
        Guid runId, string intentId, Cases.CaseAction action, CaseActionRequest? body, HttpContext http, IActorContext actor, CancellationToken ct) =>
        await Guarded(http, ct, async service =>
        {
            if (!ValidId(intentId) || body is null || !ValidId(body.CommandId) || body.ExpectedVersion < 1
                || (body.Reason is not null && !ValidId(body.Reason)))
            {
                return Problem(http, PaymentFailureCode.InvalidRequest);
            }
            var result = await service.CaseActionAsync(
                actor, new ScenarioRunId(runId), intentId, action, body.ExpectedVersion, body.CommandId, body.Reason, Correlation(http), ct);
            return result.Match(receipt => Results.Ok(ToResponse(receipt.Case, receipt.IsReplay, receipt.FactSequence)), failure => Problem(http, failure.Code));
        });

    private static async Task<IResult> CommentCase(
        Guid runId, string intentId, CaseCommentRequest? body, HttpContext http, IActorContext actor, CancellationToken ct) =>
        await Guarded(http, ct, async service =>
        {
            if (!ValidId(intentId) || body is null || !ValidId(body.CommandId) || string.IsNullOrWhiteSpace(body.Body))
            {
                return Problem(http, PaymentFailureCode.InvalidRequest);
            }
            var result = await service.CommentAsync(actor, new ScenarioRunId(runId), intentId, body.Body, body.CommandId, Correlation(http), ct);
            return result.Match(
                receipt => Results.Json(ToResponse(receipt.Case, receipt.IsReplay, null),
                    statusCode: receipt.IsReplay ? StatusCodes.Status200OK : StatusCodes.Status201Created),
                failure => Problem(http, failure.Code));
        });

    private static async Task<IResult> ReturnPayment(
        Guid runId, string intentId, ReturnPaymentRequest? body, HttpContext http, IActorContext actor, CancellationToken ct) =>
        await Guarded(http, ct, async service =>
        {
            if (!ValidId(intentId) || body is null || !ValidId(body.CommandId) || body.ExpectedVersion < 1
                || (body.Reason is not null && !ValidId(body.Reason)))
            {
                return Problem(http, PaymentFailureCode.InvalidRequest);
            }
            var result = await service.ReturnSettledAsync(
                actor, new ScenarioRunId(runId), intentId, body.ExpectedVersion, body.CommandId, body.Reason, Correlation(http), ct);
            return result.Match(receipt => Results.Ok(ToResponse(receipt.Payment, receipt.IsReplay, null)), failure => Problem(http, failure.Code));
        });

    private static async Task<IResult> VerifyAudit(Guid runId, HttpContext http, IActorContext actor, CancellationToken ct) =>
        await Guarded(http, ct, async service =>
        {
            var result = await service.VerifyAuditAsync(actor, new ScenarioRunId(runId), ct);
            return result.Match(
                v => Results.Ok(new AuditVerificationResponse(v.RunId.Value, v.ChainIntact, v.Events, v.HeadSequence, v.HeadHashHex,
                    v.FirstBrokenSequence, v.Assurance)),
                failure => Problem(http, failure.Code));
        });
    private static async Task<IResult> GrantStaff(
        Guid runId, GrantRunStaffRequest? body, HttpContext http, IActorContext actor, CancellationToken ct) =>
        await Guarded(http, ct, async service =>
        {
            if (body is null || !ValidId(body.SubjectId) || !ValidId(body.Role))
            {
                return Problem(http, PaymentFailureCode.InvalidRequest);
            }
            var result = await service.GrantStaffAsync(actor, new ScenarioRunId(runId), body.SubjectId, body.Role, ct);
            return result.Match(_ => Results.NoContent(), failure => Problem(http, failure.Code));
        });

    private static async Task<IResult> RevokeStaff(
        Guid runId, string subjectId, HttpContext http, IActorContext actor, CancellationToken ct) =>
        await Guarded(http, ct, async service =>
        {
            if (!ValidId(subjectId))
            {
                return Problem(http, PaymentFailureCode.InvalidRequest);
            }
            var result = await service.RevokeStaffAsync(actor, new ScenarioRunId(runId), subjectId, ct);
            return result.Match(_ => Results.NoContent(), failure => Problem(http, failure.Code));
        });

    private static RouteHandlerBuilder WithProblems(this RouteHandlerBuilder builder) => builder
        .Produces<RevenantProblem>(StatusCodes.Status400BadRequest)
        .Produces<RevenantProblem>(StatusCodes.Status401Unauthorized)
        .Produces<RevenantProblem>(StatusCodes.Status403Forbidden)
        .Produces<RevenantProblem>(StatusCodes.Status404NotFound)
        .Produces<RevenantProblem>(StatusCodes.Status409Conflict)
        .Produces<RevenantProblem>(StatusCodes.Status422UnprocessableEntity)
        .Produces<RevenantProblem>(StatusCodes.Status503ServiceUnavailable);

    private static async Task<IResult> OpenRun(
        OpenRunRequest? body, HttpContext http, IActorContext actor, CancellationToken ct) =>
        await Guarded(http, ct, async service =>
        {
            if (body is null || (body.RunId is { } id && id == Guid.Empty))
            {
                return Problem(http, PaymentFailureCode.InvalidRequest);
            }
            var result = await service.OpenRunAsync(actor, body.Seed, body.RunId, ct);
            return result.Match(
                receipt => Results.Json(ToResponse(receipt.Run, receipt.IsReplay), statusCode: receipt.IsReplay ? StatusCodes.Status200OK : StatusCodes.Status201Created),
                failure => Problem(http, failure.Code));
        });

    private static async Task<IResult> GetRun(Guid runId, HttpContext http, IActorContext actor, CancellationToken ct) =>
        await Guarded(http, ct, async service =>
        {
            var result = await service.GetRunAsync(actor, new ScenarioRunId(runId), ct);
            return result.Match(run => Results.Ok(ToResponse(run, false)), failure => Problem(http, failure.Code));
        });

    private static async Task<IResult> SubmitPayment(
        Guid runId, SubmitPaymentRequest? body, HttpContext http, IActorContext actor, CancellationToken ct) =>
        await Guarded(http, ct, async service =>
        {
            if (body is null || !ValidId(body.IntentId) || !ValidId(body.SourceBankId) || !ValidId(body.SourceAccountId)
                || !ValidId(body.DestBankId) || !ValidId(body.DestAccountId) || body.Currency is not { Length: 3 }
                || !Enum.IsDefined(body.Truth))
            {
                return Problem(http, PaymentFailureCode.InvalidRequest);
            }
            var correlation = http.TraceIdentifier is { Length: > 0 and <= 200 } trace ? trace : Guid.NewGuid().ToString("N");
            var result = await service.SubmitPaymentAsync(actor, new ScenarioRunId(runId), body, correlation, ct);
            return result.Match(
                receipt => Results.Json(ToResponse(receipt.Payment, receipt.IsReplay, receipt.FactSequence),
                    statusCode: receipt.IsReplay ? StatusCodes.Status200OK : StatusCodes.Status201Created),
                failure => Problem(http, failure.Code));
        });

    private static async Task<IResult> GetPayment(
        Guid runId, string intentId, HttpContext http, IActorContext actor, CancellationToken ct) =>
        await Guarded(http, ct, async service =>
        {
            if (!ValidId(intentId))
            {
                return Problem(http, PaymentFailureCode.InvalidRequest);
            }
            var result = await service.GetPaymentAsync(actor, new ScenarioRunId(runId), intentId, ct);
            return result.Match(payment => Results.Ok(ToResponse(payment, false, null)), failure => Problem(http, failure.Code));
        });

    /// <summary>Resolves the ledger service (503 when the ledger is not configured) and maps database outages to a typed 503.</summary>
    private static async Task<IResult> Guarded(HttpContext http, CancellationToken ct, Func<RevenantLedgerService, Task<IResult>> action)
    {
        var service = http.RequestServices.GetService<RevenantLedgerService>();
        if (service is null)
        {
            return Problem(http, PaymentFailureCode.Unavailable);
        }
        try
        {
            return await action(service);
        }
        catch (PostgresException ex) when (PaymentLedgerStore.IsRunClosed(ex))
        {
            // The database refused a financial write into a run closed for new facts; the whole action rolled back.
            return Problem(http, PaymentFailureCode.RunClosed);
        }
        catch (NpgsqlException ex) when (ex is not PostgresException || ex.IsTransient)
        {
            return ct.IsCancellationRequested ? Results.StatusCode(StatusCodes.Status499ClientClosedRequest) : Problem(http, PaymentFailureCode.Unavailable);
        }
    }

    private static bool ValidId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= MaxIdLength && value.All(c => !char.IsControl(c));

    internal static IResult Problem(HttpContext http, PaymentFailureCode code)
    {
        var status = code switch
        {
            PaymentFailureCode.InvalidRequest => StatusCodes.Status400BadRequest,
            PaymentFailureCode.Forbidden or PaymentFailureCode.MfaRequired or PaymentFailureCode.DistinctSupervisorRequired =>
                StatusCodes.Status403Forbidden,
            PaymentFailureCode.RunNotFound or PaymentFailureCode.PaymentNotFound or PaymentFailureCode.CaseNotFound => StatusCodes.Status404NotFound,
            PaymentFailureCode.ConflictingDuplicate or PaymentFailureCode.StaleVersion or PaymentFailureCode.IllegalTransition
                or PaymentFailureCode.PostingRequired or PaymentFailureCode.ReviewRequired or PaymentFailureCode.RunClosed
                or PaymentFailureCode.RunNotQuiescent => StatusCodes.Status409Conflict,
            PaymentFailureCode.UnknownAccount or PaymentFailureCode.UnsupportedCurrency or PaymentFailureCode.OutOfOrderInput
                or PaymentFailureCode.InsufficientAvailable =>
                StatusCodes.Status422UnprocessableEntity,
            _ => StatusCodes.Status503ServiceUnavailable,
        };
        var wire = code == PaymentFailureCode.Unavailable ? "ledger_unavailable" : ToSnake(code.ToString());
        return Results.Json(new RevenantProblem { Code = wire, Status = status, TraceId = http.TraceIdentifier }, statusCode: status);
    }

    private static string ToSnake(string name) =>
        string.Concat(name.Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));

    private static RunResponse ToResponse(RunView run, bool replayed) => new(
        run.RunId.Value, run.Seed, run.ScenarioVersion, run.ManifestHash, run.FinanceVersion, replayed,
        [.. run.Banks.Select(b => new BankBalanceDto(b.BankId, b.Name, b.OpeningMinor, b.PostedMinor, b.ReservedMinor, b.AvailableMinor))]);

    private static CaseResponse ToResponse(CaseView view, bool replayed, long? factSequence) => new(
        view.CaseId, view.RunId.Value, view.IntentId, view.Status.ToString(), view.Version, view.PaymentStatus.ToString(), view.PaymentVersion,
        view.Currency, view.AmountMinor, view.PolicyVersion, view.RequiredApprovals, view.AssignedSubject,
        new CaseScreeningDto(view.Screening.Version, view.Screening.ScreenedName, view.Screening.BestEntry, view.Screening.ScorePermille,
            view.Screening.ThresholdPermille, view.Screening.IsMatch),
        [.. view.Approvals.Select(a => new CaseApprovalDto(a.Order, a.SubjectId, a.Role, a.DecidedAt))],
        [.. view.Comments.Select(c => new CaseCommentDto(c.Id, c.AuthorSubject, c.Body, c.CreatedAt))],
        replayed, factSequence);

    private static PaymentResponse ToResponse(PaymentView payment, bool replayed, long? factSequence) => new(
        payment.PaymentId, payment.RunId.Value, payment.IntentId, payment.Status.ToString(), payment.Version, payment.Currency,
        payment.AmountMinor,
        [.. payment.Trail.Select(t => new PaymentTrailDto(t.Version, t.From?.ToString(), t.To.ToString(), t.Reason, t.Actor))],
        replayed, factSequence);
}
