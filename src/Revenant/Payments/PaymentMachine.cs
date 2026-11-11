using Revenant.Contracts;
using Revenant.Finance;

namespace Revenant.Payments;

/// <summary>Typed failure codes of the durable payment machine and its intake. Wire codes are the snake_case names.</summary>
public enum PaymentFailureCode
{
    InvalidRequest,
    /// <summary>The caller may not use this run. Deliberately identical for "unknown run" and "not your run" (no existence oracle).</summary>
    Forbidden,
    RunNotFound,
    PaymentNotFound,
    /// <summary>Account or bank is not part of this run's namespace.</summary>
    UnknownAccount,
    /// <summary>Currency differs from the account currency. There is no FX path.</summary>
    UnsupportedCurrency,
    /// <summary>Intent sequence or business time does not follow the run's last accepted intent.</summary>
    OutOfOrderInput,
    /// <summary>Same key, different content (intent id reused with other facts, command id reused with another request, run reopened differently).</summary>
    ConflictingDuplicate,
    /// <summary>The requested step is not in the frozen legal matrix. Recorded as a rejected command result.</summary>
    IllegalTransition,
    /// <summary>The step moves money and needs its balanced posting group (settlement path, task 7.3). Recorded as rejected.</summary>
    PostingRequired,
    /// <summary>The caller's expected version is not the payment's current version. Nothing is recorded; re-read and retry.</summary>
    StaleVersion,
    /// <summary>The ledger database is not configured or not reachable. Nothing was acknowledged.</summary>
    Unavailable,
    /// <summary>
    /// The account that the step would lower (the destination of a compensating return) has less available balance than the
    /// amount. Recorded as a rejected command result (task 7.3). Holds and settlement do not fail with this code: they record
    /// the V1 outcome (Held then Rejected, or Cleared then Returned, reason <c>insufficient_available</c>) instead.
    /// </summary>
    InsufficientAvailable,

    /// <summary>Held -> Approved was requested outside the review case (task 7.4).</summary>
    ReviewRequired,

    /// <summary>The payment has no review case (it was never held with a reservation).</summary>
    CaseNotFound,

    /// <summary>A privileged review action needs a fresh, server-verified MFA step-up.</summary>
    MfaRequired,

    /// <summary>The final approval of a high-value case needs a supervisor who did not give the first approval.</summary>
    DistinctSupervisorRequired,

    /// <summary>The run was closed for new financial facts (exit review); PostgreSQL refused the write (RV009). Nothing was recorded.</summary>
    RunClosed,

    /// <summary>The run cannot be closed yet: payments are still in LiveOps-owned states and must produce their ordered facts first.</summary>
    RunNotQuiescent,
}

public sealed record PaymentFailure(PaymentFailureCode Code, string Message);

public enum PaymentDecisionKind { Accepted, Stale, Illegal, PostingRequired }

public readonly record struct PaymentDecision(PaymentDecisionKind Kind, long NextVersion);

/// <summary>
/// The pure decision of the durable payment machine for one transition command. It adds no matrix of its own: legality is
/// <see cref="PaymentLifecycle"/>, which PostgreSQL mirrors in <c>revenant.is_legal_payment_transition</c>. The store applies the
/// decision under a row lock, so one command yields at most one version step.
/// </summary>
public static class PaymentMachine
{
    public static PaymentDecision Decide(PaymentStatus current, long currentVersion, PaymentStatus target, long expectedVersion)
    {
        if (expectedVersion != currentVersion)
        {
            return new PaymentDecision(PaymentDecisionKind.Stale, currentVersion);
        }
        if (!PaymentLifecycle.IsLegal(current, target))
        {
            return new PaymentDecision(PaymentDecisionKind.Illegal, currentVersion);
        }
        return PaymentLifecycle.RequiresPosting(current, target)
            ? new PaymentDecision(PaymentDecisionKind.PostingRequired, currentVersion)
            : new PaymentDecision(PaymentDecisionKind.Accepted, checked(currentVersion + 1));
    }
}

public sealed record PaymentTrailEntry(long Version, PaymentStatus? From, PaymentStatus To, string? Reason, string Actor);

/// <summary>A payment as stored: its current status and version plus the append-only trail (v1 is Initiated).</summary>
public sealed record PaymentView(
    Guid PaymentId,
    ScenarioRunId RunId,
    string IntentId,
    PaymentStatus Status,
    long Version,
    string Currency,
    long AmountMinor,
    IReadOnlyList<PaymentTrailEntry> Trail);

/// <param name="IsReplay">True when this exact intent was already accepted; nothing new was written.</param>
/// <param name="FactSequence">The run-local canonical fact sequence of this intent.</param>
public sealed record IntakeReceipt(PaymentView Payment, bool IsReplay, long FactSequence);

/// <summary>One transition request. <see cref="CommandId"/> is the idempotency key, scoped to the run.</summary>
public sealed record TransitionCommand(
    ScenarioRunId RunId, string IntentId, PaymentStatus Target, long ExpectedVersion, string CommandId, string? Reason);

/// <param name="Payment">The payment as it is now.</param>
/// <param name="AppliedVersion">The version this command produced; on a replay this can be older than <c>Payment.Version</c>.</param>
public sealed record TransitionReceipt(PaymentView Payment, PaymentStatus AppliedStatus, long AppliedVersion, bool IsReplay);

/// <param name="ReservedMinor">Sum of the bank's Active holds. <c>AvailableMinor = PostedMinor - ReservedMinor</c>.</param>
public sealed record BankBalance(string BankId, string Name, long OpeningMinor, long PostedMinor, long ReservedMinor)
{
    public long AvailableMinor => PostedMinor - ReservedMinor;
}

/// <summary>One payment the settlement worker resolved: <see cref="PaymentStatus.Settled"/> with its posting group, or
/// <see cref="PaymentStatus.Returned"/> (reason <c>insufficient_available</c>) with no posting.</summary>
public sealed record SettlementOutcome(
    ScenarioRunId RunId, string IntentId, Guid PaymentId, PaymentStatus Status, long Version, Guid? LedgerGroupId);

/// <param name="Outcomes">In processing order (run, then the run-local fact order). Empty when the queue had nothing claimable.</param>
public sealed record SettlementBatch(IReadOnlyList<SettlementOutcome> Outcomes);

/// <summary>A compensating return of a Settled payment. <see cref="CommandId"/> shares the transition command namespace.</summary>
public sealed record ReturnCommand(ScenarioRunId RunId, string IntentId, long ExpectedVersion, string CommandId, string? Reason);

public sealed record RunView(
    ScenarioRunId RunId, string TenantId, string OwnerSubjectId, long Seed, string ScenarioVersion, string ManifestHash,
    string FinanceVersion, IReadOnlyList<BankBalance> Banks);

public sealed record RunReceipt(RunView Run, bool IsReplay);
