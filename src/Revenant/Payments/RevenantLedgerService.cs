using Marrow.Kernel.Results;
using Marrow.Kernel.Security;
using Revenant.Cases;
using Revenant.Contracts;

namespace Revenant.Payments;

/// <summary>Server-side settings of the ledger.</summary>
public sealed class RevenantLedgerOptions
{
    public const string SectionName = "Revenant:Ledger";

    /// <summary>
    /// Tenant the LiveOps background jobs (settlement worker) explicitly select. Never taken from a request. API callers do not
    /// use it: their tenant is the verified actor's server-side tenant membership (task 7.4).
    /// </summary>
    public string TenantId { get; set; } = "demo-bank";
}

/// <summary>
/// The application layer of the Revenant ledger API: it derives the actor from the verified <see cref="IActorContext"/>,
/// authorizes through <see cref="RevenantStaffAuthorizer"/> (which composes the ownership and membership ports and requires the
/// actor's server-side tenant), resolves the tenant from the persisted run, and only then calls <see cref="PaymentLedgerStore"/>.
/// Nothing here trusts a request-supplied principal, tenant or role. A caller that fails authorization always gets
/// <see cref="PaymentFailureCode.Forbidden"/>.
/// </summary>
public sealed class RevenantLedgerService(
    PaymentLedgerStore store,
    RevenantRunDirectory directory,
    RevenantStaffAuthorizer authorizer)
{
    public const string MfaClaim = "mfa";

    /// <summary>Opens a run in the actor's own tenant; a staff member without an active tenant membership cannot open one.</summary>
    public async Task<Result<RunReceipt, PaymentFailure>> OpenRunAsync(
        IActorContext actor, long seed, Guid? requestedRunId, CancellationToken ct)
    {
        if (!actor.IsAuthenticated || actor.PrincipalKind != PrincipalKind.Staff || !RevenantStaffAuthorizer.HasStaffRole(actor)
            || actor.TenantId is not { Length: > 0 } tenant)
        {
            return Forbidden<RunReceipt>();
        }
        var runId = new ScenarioRunId(requestedRunId is { } given && given != Guid.Empty ? given : Guid.NewGuid());
        return await store.OpenRunAsync(tenant, actor.SubjectId, IsSupervisor(actor) ? "supervisor" : "operator", seed, runId, ct);
    }

    public async Task<Result<RunView, PaymentFailure>> GetRunAsync(IActorContext actor, ScenarioRunId runId, CancellationToken ct)
    {
        var tenant = await AuthorizeAsync(actor, runId, ResourceOperation.Read, ct);
        if (tenant is null)
        {
            return Forbidden<RunView>();
        }
        var run = await store.GetRunAsync(tenant, runId, ct);
        return run is null
            ? Result<RunView, PaymentFailure>.Failure(new PaymentFailure(PaymentFailureCode.RunNotFound, "The run does not exist."))
            : Result<RunView, PaymentFailure>.Success(run);
    }

    public async Task<Result<IntakeReceipt, PaymentFailure>> SubmitPaymentAsync(
        IActorContext actor, ScenarioRunId runId, SubmitPaymentRequest request, string correlationId, CancellationToken ct)
    {
        var tenant = await AuthorizeAsync(actor, runId, ResourceOperation.Write, ct);
        if (tenant is null)
        {
            return Forbidden<IntakeReceipt>();
        }
        PaymentIntent intent;
        try
        {
            intent = new PaymentIntent(
                runId, request.IntentId, request.Sequence, request.EventTimeUtcMs, request.SourceBankId, request.SourceAccountId,
                request.DestBankId, request.DestAccountId, new Money(request.Currency, request.AmountMinor), request.Truth,
                new PaymentFeatures(request.NewBeneficiary, request.SanctionsHit));
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentNullException)
        {
            return Result<IntakeReceipt, PaymentFailure>.Failure(
                new PaymentFailure(PaymentFailureCode.InvalidRequest, "The payment intent is malformed."));
        }
        return await store.SubmitIntentAsync(tenant, actor.SubjectId, intent, correlationId, ct);
    }

    public async Task<Result<PaymentView, PaymentFailure>> GetPaymentAsync(
        IActorContext actor, ScenarioRunId runId, string intentId, CancellationToken ct)
    {
        var tenant = await AuthorizeAsync(actor, runId, ResourceOperation.Read, ct);
        if (tenant is null)
        {
            return Forbidden<PaymentView>();
        }
        var payment = string.IsNullOrWhiteSpace(intentId) ? null : await store.GetPaymentAsync(tenant, runId, intentId, ct);
        return payment is null
            ? Result<PaymentView, PaymentFailure>.Failure(new PaymentFailure(PaymentFailureCode.PaymentNotFound, "The payment does not exist."))
            : Result<PaymentView, PaymentFailure>.Success(payment);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Review cases (task 7.4)
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>Reads a case for an authorized staff member; the read itself is recorded in the run's hash-chained audit (task 7.5).</summary>
    public async Task<Result<CaseView, PaymentFailure>> GetCaseAsync(
        IActorContext actor, ScenarioRunId runId, string intentId, CancellationToken ct, string? correlationId = null)
    {
        var tenant = await AuthorizeAsync(actor, runId, ResourceOperation.Read, ct);
        if (tenant is null)
        {
            return Forbidden<CaseView>();
        }
        var view = await store.ReadCaseAuditedAsync(tenant, actor.SubjectId, correlationId ?? Guid.NewGuid().ToString("N"), runId, intentId, ct);
        return view is null
            ? Result<CaseView, PaymentFailure>.Failure(new PaymentFailure(PaymentFailureCode.CaseNotFound, "The payment has no review case."))
            : Result<CaseView, PaymentFailure>.Success(view);
    }

    /// <summary>
    /// Assign, approve or reject. The case actor (subject, supervisor role, fresh MFA) is built from the verified actor only;
    /// four-eyes and MFA rules are then decided by <see cref="CaseMachine"/> and re-checked by PostgreSQL.
    /// </summary>
    public async Task<Result<CaseReceipt, PaymentFailure>> CaseActionAsync(
        IActorContext actor, ScenarioRunId runId, string intentId, CaseAction action, long expectedVersion, string commandId,
        string? reason, string correlationId, CancellationToken ct)
    {
        var tenant = await AuthorizeAsync(actor, runId, ResourceOperation.Write, ct);
        if (tenant is null)
        {
            return Forbidden<CaseReceipt>();
        }
        var caseActor = new CaseActor(actor.SubjectId, IsStaff: true, IsSupervisor(actor), HasFreshMfa(actor));
        return await store.CaseActionAsync(
            tenant, caseActor, new CaseCommand(runId, intentId, action, expectedVersion, commandId, reason), correlationId, ct);
    }

    /// <summary>Any current staff member of the run may comment; the author is the verified actor.</summary>
    public async Task<Result<CaseReceipt, PaymentFailure>> CommentAsync(
        IActorContext actor, ScenarioRunId runId, string intentId, string body, string commandId, string correlationId, CancellationToken ct)
    {
        var tenant = await AuthorizeAsync(actor, runId, ResourceOperation.Write, ct);
        return tenant is null
            ? Forbidden<CaseReceipt>()
            : await store.CommentAsync(tenant, actor.SubjectId, runId, intentId, body, commandId, correlationId, ct);
    }

    /// <summary>A compensating return of a settled payment is privileged: a run supervisor with a fresh MFA step-up.</summary>
    public async Task<Result<TransitionReceipt, PaymentFailure>> ReturnSettledAsync(
        IActorContext actor, ScenarioRunId runId, string intentId, long expectedVersion, string commandId, string? reason,
        string correlationId, CancellationToken ct)
    {
        var tenant = await AuthorizeAsync(actor, runId, ResourceOperation.Write, ct);
        if (tenant is null || !IsSupervisor(actor))
        {
            return Forbidden<TransitionReceipt>();
        }
        if (!HasFreshMfa(actor))
        {
            return Result<TransitionReceipt, PaymentFailure>.Failure(
                new PaymentFailure(PaymentFailureCode.MfaRequired, "A return needs a fresh MFA step-up."));
        }
        return await store.ReturnSettledAsync(
            tenant, actor.SubjectId, new ReturnCommand(runId, intentId, expectedVersion, commandId, reason), correlationId, ct);
    }

    /// <summary>
    /// Reviewer audit verification (task 7.5): a run supervisor recomputes the run's hash chain. The result states that this is
    /// tamper evidence inside the database, not external notarization.
    /// </summary>
    public async Task<Result<AuditVerification, PaymentFailure>> VerifyAuditAsync(IActorContext actor, ScenarioRunId runId, CancellationToken ct)
    {
        var tenant = await AuthorizeAsync(actor, runId, ResourceOperation.Read, ct);
        return tenant is null || !IsSupervisor(actor)
            ? Forbidden<AuditVerification>()
            : Result<AuditVerification, PaymentFailure>.Success(await store.VerifyAuditChainAsync(tenant, runId, ct));
    }

    // ---------------------------------------------------------------------------------------------------------
    // Run staff (task 7.4): only the run owner manages the staff scope.
    // ---------------------------------------------------------------------------------------------------------

    public async Task<Result<bool, PaymentFailure>> GrantStaffAsync(
        IActorContext actor, ScenarioRunId runId, string subjectId, string role, CancellationToken ct)
    {
        if (!await IsOwnerAsync(actor, runId, ct))
        {
            return Forbidden<bool>();
        }
        if (!IsStaffSubject(subjectId) || role is not ("operator" or "supervisor"))
        {
            return Result<bool, PaymentFailure>.Failure(new PaymentFailure(PaymentFailureCode.InvalidRequest, "Staff subject or role is invalid."));
        }
        return await store.GrantRunStaffAsync(runId, subjectId, role, ct)
            ? Result<bool, PaymentFailure>.Success(true)
            : Result<bool, PaymentFailure>.Failure(new PaymentFailure(PaymentFailureCode.ConflictingDuplicate, "This staff member was revoked from the run."));
    }

    public async Task<Result<bool, PaymentFailure>> RevokeStaffAsync(
        IActorContext actor, ScenarioRunId runId, string subjectId, CancellationToken ct)
    {
        if (!await IsOwnerAsync(actor, runId, ct))
        {
            return Forbidden<bool>();
        }
        if (!IsStaffSubject(subjectId) || string.Equals(subjectId, actor.SubjectId, StringComparison.Ordinal))
        {
            return Result<bool, PaymentFailure>.Failure(new PaymentFailure(PaymentFailureCode.InvalidRequest, "The owner cannot be revoked."));
        }
        await store.RevokeRunStaffAsync(runId, subjectId, actor.SubjectId, ct);
        return Result<bool, PaymentFailure>.Success(true);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Internals
    // ---------------------------------------------------------------------------------------------------------

    internal static bool IsSupervisor(IActorContext actor) => actor.Roles.Contains("supervisor", StringComparer.Ordinal);

    internal static bool HasFreshMfa(IActorContext actor) => actor.Claims.Contains(MfaClaim, StringComparer.Ordinal);

    private static bool IsStaffSubject(string? subjectId) =>
        subjectId is { Length: 42 } && subjectId.StartsWith("staff:", StringComparison.Ordinal) && Guid.TryParse(subjectId.AsSpan(6), out _);

    private async Task<bool> IsOwnerAsync(IActorContext actor, ScenarioRunId runId, CancellationToken ct)
    {
        if (await AuthorizeAsync(actor, runId, ResourceOperation.Write, ct) is null)
        {
            return false;
        }
        var owner = await directory.FindOwnershipAsync(RevenantResources.Run(runId), ct);
        return owner is not null && string.Equals(owner.OwnerSubjectId, actor.SubjectId, StringComparison.Ordinal);
    }

    /// <summary>Returns the run's persisted tenant when the actor may perform <paramref name="operation"/>, otherwise null.</summary>
    private async Task<string?> AuthorizeAsync(IActorContext actor, ScenarioRunId runId, ResourceOperation operation, CancellationToken ct)
    {
        if (runId.IsEmpty)
        {
            return null;
        }
        var resource = RevenantResources.Run(runId);
        var decision = await authorizer.AuthorizeAsync(actor, resource, operation, ct);
        if (!decision.IsAllowed)
        {
            return null;
        }
        // The tenant is read from the persisted run, never from the request. A run that vanished between the two reads fails closed.
        var owner = await directory.FindOwnershipAsync(resource, ct);
        return owner?.TenantId;
    }

    private static Result<T, PaymentFailure> Forbidden<T>() =>
        Result<T, PaymentFailure>.Failure(new PaymentFailure(PaymentFailureCode.Forbidden, "This run is not available to the caller."));
}
