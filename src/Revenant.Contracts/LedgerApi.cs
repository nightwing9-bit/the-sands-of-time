namespace Revenant.Contracts;

// Wire DTOs of the production Revenant ledger API (Spec 07 task 7.2). Additive: nothing in the frozen Spec 05 contracts changes.
// Money is integer minor units of the stated currency; there is no FX. Statuses travel as the member names of the frozen
// payment lifecycle (Initiated, Screened, Held, Cleared, Approved, Rejected, Returned, Settled).

/// <summary>Opens a seeded Friday Ring run in the caller's tenant. A repeated call with the same <see cref="RunId"/> and seed replays.</summary>
/// <param name="Seed">Scenario seed. Equal seeds give equal opening balances in separate per-run account namespaces.</param>
/// <param name="RunId">Optional client-chosen run id (the idempotency key for opening). The server creates one when absent.</param>
public sealed record OpenRunRequest(long Seed, Guid? RunId);

/// <param name="PostedMinor">Opening plus posted ledger movements. Unchanged by a hold until the payment settles.</param>
/// <param name="ReservedMinor">Sum of Active holds (task 7.3, additive field).</param>
/// <param name="AvailableMinor"><c>PostedMinor - ReservedMinor</c>: the liquidity a new payment can use (task 7.3, additive field).</param>
public sealed record BankBalanceDto(
    string BankId, string Name, long OpeningMinor, long PostedMinor, long ReservedMinor, long AvailableMinor);

public sealed record RunResponse(
    Guid RunId,
    long Seed,
    string ScenarioVersion,
    string ManifestHash,
    string FinanceVersion,
    bool Replayed,
    IReadOnlyList<BankBalanceDto> Banks);

/// <summary>One exogenous payment intent. Every account and bank id is scenario-local; the run id is the route.</summary>
public sealed record SubmitPaymentRequest(
    string IntentId,
    long Sequence,
    long EventTimeUtcMs,
    string SourceBankId,
    string SourceAccountId,
    string DestBankId,
    string DestAccountId,
    string Currency,
    long AmountMinor,
    SyntheticTruth Truth,
    bool NewBeneficiary,
    bool SanctionsHit);

public sealed record PaymentTrailDto(long Version, string? FromStatus, string ToStatus, string? Reason, string Actor);

public sealed record PaymentResponse(
    Guid PaymentId,
    Guid RunId,
    string IntentId,
    string Status,
    long Version,
    string Currency,
    long AmountMinor,
    IReadOnlyList<PaymentTrailDto> Trail,
    bool Replayed,
    long? FactSequence);

// ---- Review cases and four-eyes release (Spec 07 task 7.4, additive). The acting staff member, their tenant, role and MFA
// state are never request fields: the server derives them from the verified session.

/// <summary>Adds a staff member to the run's staff scope (run owner only).</summary>
/// <param name="SubjectId">The staff subject, <c>staff:{uuid}</c>.</param>
/// <param name="Role"><c>operator</c> or <c>supervisor</c>: the member's role on this run (the identity role is still checked per action).</param>
public sealed record GrantRunStaffRequest(string SubjectId, string Role);

/// <param name="ExpectedVersion">The case version the caller read (optimistic concurrency).</param>
/// <param name="CommandId">Idempotency key: a retry with the same body replays, a different body under the same key conflicts.</param>
public sealed record CaseActionRequest(long ExpectedVersion, string CommandId, string? Reason);

public sealed record CaseCommentRequest(string Body, string CommandId);

/// <param name="ExpectedVersion">The payment version the caller read.</param>
public sealed record ReturnPaymentRequest(long ExpectedVersion, string CommandId, string? Reason);

/// <summary>Deterministic, non-ML name-screening evidence against an explicit synthetic list. Not a model prediction.</summary>
public sealed record CaseScreeningDto(
    string Version, string ScreenedName, string? BestEntry, int ScorePermille, int ThresholdPermille, bool IsMatch);

public sealed record CaseApprovalDto(int Order, string SubjectId, string Role, DateTimeOffset DecidedAt);

public sealed record CaseCommentDto(long Id, string AuthorSubject, string Body, DateTimeOffset CreatedAt);

/// <param name="Status"><c>Open</c>, <c>AwaitingSecondApproval</c>, <c>Released</c> or <c>Rejected</c>. Separate from the payment status.</param>
/// <param name="RequiredApprovals">2 at or above USD 10,000.00 (the policy named by PolicyVersion), otherwise 1.</param>
/// <param name="FactSequence">The ordered canonical fact committed by this action, when it was an approval or rejection.</param>
public sealed record CaseResponse(
    Guid CaseId,
    Guid RunId,
    string IntentId,
    string Status,
    long Version,
    string PaymentStatus,
    long PaymentVersion,
    string Currency,
    long AmountMinor,
    string PolicyVersion,
    int RequiredApprovals,
    string? AssignedSubject,
    CaseScreeningDto Screening,
    IReadOnlyList<CaseApprovalDto> Approvals,
    IReadOnlyList<CaseCommentDto> Comments,
    bool Replayed,
    long? FactSequence);

/// <summary>Typed failure body, the same shape as the shared <c>ApiProblem</c> (<c>code</c>, <c>status</c>, <c>traceId</c>).</summary>
public sealed class RevenantProblem
{
    public required string Code { get; init; }
    public required int Status { get; init; }
    public string? TraceId { get; init; }
}
/// <summary>
/// Reviewer audit verification of a run's hash chain (task 7.5). <c>Assurance</c> states the limit of the proof: tamper evidence
/// inside the database, not external notarization.
/// </summary>
public sealed record AuditVerificationResponse(
    Guid RunId, bool ChainIntact, long Events, long HeadSequence, string HeadHashHex, long? FirstBrokenSequence, string Assurance);
