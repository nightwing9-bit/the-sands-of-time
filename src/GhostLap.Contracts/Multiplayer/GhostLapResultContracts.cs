namespace GhostLap.Contracts.Multiplayer;

/// <summary>
/// Identity and policy constants of the durable multiplayer result (Spec 10 task 10.4, ADR-P1-26). Every value is part of the result digest,
/// so changing one is a new schema or policy version, never an edit of an existing one.
/// </summary>
public static class MatchResultContract
{
    /// <summary>Schema of <see cref="MatchResultDocument"/> and of its canonical digest encoding.</summary>
    public const string ResultSchema = "ghost-mp-result/1";

    /// <summary>Outbox event type (Outbox v1 <c>eventType</c>) of <see cref="MatchFinalizedV1"/>; exactly one per finalized match.</summary>
    public const string FinalizedEventType = "ghostlap.MatchFinalizedV1";

    public const int FinalizedEventVersion = 1;

    /// <summary>Finishers above forfeits; within a class a higher total ranks first; equal totals share a placement (competition ranking).</summary>
    public const string PlacementPolicy = "mp-placement-v1";

    /// <summary>ADR-P1-22 D12: rated only with at least one finisher and at least two distinct guest principals at start.</summary>
    public const string EligibilityPolicy = "mp-eligibility-v1";

    public const string OutcomeFinisher = "finisher";
    public const string OutcomeForfeit = "forfeit";

    public const string ReasonEligible = "eligible";
    public const string ReasonNoFinisher = "no_finisher";
    public const string ReasonTooFewGuestPrincipals = "fewer_than_two_guest_principals";
}

/// <summary>
/// One slot of a durable result. Scores and stats are the authoritative server values at the final tick (frozen at the forfeit tick for a forfeited
/// slot). <see cref="CompetitivePlacement"/> is 1-based competition ranking: tied players share it and the next placement skips (1, 1, 3).
/// <see cref="Slot"/> is a display and serialization order only, never a tie-break.
/// </summary>
public sealed record MatchPlayerResult(
    int Slot,
    string MemberId,
    string Subject,
    string Outcome,
    long ForfeitTick,
    int CompetitivePlacement,
    int TotalScore,
    int MovementPoints,
    int PickupPoints,
    int PenaltyPoints,
    long DistanceFixed,
    int Pickups,
    int FinalCharge,
    int Bursts,
    int BurstHits,
    int TimesHitByBurst,
    int ContactDrainsSuffered,
    int ChargeLostToContact);

/// <summary>
/// The immutable result of one finalized match. Every field is server-generated; nothing comes from a client. <see cref="Seed"/> is a decimal
/// string. <see cref="RatingAlgorithmId"/> is the opaque id sealed at countdown and stored on the start receipt, carried through unchanged.
/// The digest is computed from a canonical binary encoding with a fixed field order (not from JSON), so any consumer can recompute it from the
/// parsed document.
/// </summary>
public sealed record MatchResultDocument(
    string Schema,
    string MatchId,
    string RoomId,
    int RoundOrdinal,
    string GenerationId,
    string ConfigDigest,
    string RulesId,
    string LayoutId,
    string RulesHash,
    string LayoutFingerprint,
    string Seed,
    int TargetTicks,
    long FinalTick,
    string FinalStateHash,
    string PlacementPolicy,
    string EligibilityPolicy,
    bool RatedEligible,
    string EligibilityReason,
    string RatingAlgorithmId,
    IReadOnlyList<MatchPlayerResult> Players);

/// <summary>Outbox payload of <see cref="MatchResultContract.FinalizedEventType"/>. <see cref="ResultDigest"/> must equal the digest of <see cref="Result"/>.</summary>
public sealed record MatchFinalizedV1(string MatchId, string ResultDigest, MatchResultDocument Result);

/// <summary>A result slot as members see it: no guest principal id (members are identified by their room-scoped member id).</summary>
public sealed record MatchPlayerResultView(
    int Slot,
    string MemberId,
    string Outcome,
    long ForfeitTick,
    int CompetitivePlacement,
    int TotalScore,
    int MovementPoints,
    int PickupPoints,
    int PenaltyPoints,
    int Pickups,
    int Bursts,
    int BurstHits,
    int TimesHitByBurst,
    int ContactDrainsSuffered);

public static class MatchOutcomeDecisions
{
    /// <summary>The receipt is ResultCommitted; <see cref="MatchOutcomeView.Players"/> is the durable result.</summary>
    public const string Finalized = "finalized";

    /// <summary>The receipt is Aborted: unrated, no winner, no placement.</summary>
    public const string Aborted = "aborted";

    /// <summary>The receipt is still Started: no durable decision yet (provisional, unrated). Not an abort.</summary>
    public const string Pending = "pending";
}

/// <summary>
/// The durable outcome of a match as read from its receipt (task 10.4). Placements are present only when <see cref="Decision"/> is
/// <see cref="MatchOutcomeDecisions.Finalized"/>.
/// </summary>
public sealed record MatchOutcomeView(
    string MatchId,
    string Decision,
    string? AbortReason,
    string? ResultDigest,
    bool RatedEligible,
    string? EligibilityReason,
    string? RatingAlgorithmId,
    IReadOnlyList<MatchPlayerResultView> Players);
