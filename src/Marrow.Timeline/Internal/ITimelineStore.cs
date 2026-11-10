using Marrow.Kernel.Results;

namespace Marrow.Timeline.Internal;

/// <summary>
/// Request to durably create a history: a source at position 0, or a fork child based at
/// <see cref="ForkOrigin.ForkPosition"/>. All payloads are already canonical bytes from the pinned codecs.
/// </summary>
internal sealed record NewHistory(
    string OwnerId,
    TimelineVersionKey Versions,
    byte[] CanonicalContext,
    string RulesHash,
    string SeedHash,
    byte[] CanonicalBaseState,
    string OperationKey,
    ForkOrigin? Fork)
{
    public string OwnerId { get; init; } = Require(OwnerId, nameof(OwnerId));
    public TimelineVersionKey Versions { get; init; } = Versions ?? throw new ArgumentNullException(nameof(Versions));
    public byte[] CanonicalContext { get; init; } = CanonicalContext ?? throw new ArgumentNullException(nameof(CanonicalContext));
    public string RulesHash { get; init; } = Require(RulesHash, nameof(RulesHash));
    public string SeedHash { get; init; } = Require(SeedHash, nameof(SeedHash));
    public byte[] CanonicalBaseState { get; init; } = CanonicalBaseState ?? throw new ArgumentNullException(nameof(CanonicalBaseState));
    public string OperationKey { get; init; } = Require(OperationKey, nameof(OperationKey));

    private static string Require(string value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException($"{name} must be non-blank.", name) : value;
}

/// <summary>Parent history and the position T whose complete state becomes the child's base state.</summary>
internal sealed record ForkOrigin(TimelineId ParentId, long ForkPosition);

/// <summary>
/// One step to append. <see cref="StateHashAfter"/> is the state hash the engine computed by applying the
/// step; <see cref="CheckpointState"/> optionally stores the complete canonical state at this position.
/// </summary>
internal sealed record NewStep(
    TimelineId Timeline,
    long ExpectedLastPosition,
    StepRecord<byte[]> Step,
    string StateHashAfter,
    byte[]? CheckpointState,
    string OperationKey)
{
    public StepRecord<byte[]> Step { get; init; } = Step ?? throw new ArgumentNullException(nameof(Step));
    public string OperationKey { get; init; } = string.IsNullOrWhiteSpace(OperationKey)
        ? throw new ArgumentException("OperationKey must be non-blank.", nameof(OperationKey))
        : OperationKey;
}

/// <summary>
/// Stored manifest plus the immutable base record facts (position, record hash and complete state hash)
/// that anchor this history's own suffix.
/// </summary>
internal sealed record StoredHistory(
    HistoryManifest Manifest,
    long BasePosition,
    string BaseRecordHash,
    string BaseStateHash,
    string? FailureReason);

/// <summary>One persisted step of a history's own suffix; inputs are in canonical order with canonical payload bytes.</summary>
internal sealed record StoredStep(
    long Position,
    IReadOnlyList<OrderedInput<byte[]>> Inputs,
    string StateHash,
    string PreviousRecordHash,
    string RecordHash);

/// <summary>A complete stored state at <see cref="Position"/>, already verified against its stored hash.</summary>
internal sealed record StoredCheckpoint(long Position, string StateSchemaVersion, byte[] CanonicalState, string StateHash);

/// <summary>A pinned context artifact, already verified against its content address.</summary>
internal sealed record StoredContext(string Reference, string ContextSchemaVersion, string ContextHash, byte[] CanonicalContext);

/// <summary>
/// Timeline-owned persistence port (manifest, immutable ordered steps, checkpoints, pinned context
/// artifacts and fork origins). Internal by design: <see cref="ITimelineEngine{TState,TInput,TContext}"/>
/// is the only domain-facing lifecycle authority, and domain assemblies cannot reach this write path.
/// Historical rows are never updated or deleted; only manifest progress/status advances by controlled transitions.
/// </summary>
internal interface ITimelineStore
{
    /// <summary>
    /// Atomically commits pinned context artifact, manifest and complete base checkpoint. Idempotent by
    /// (owner, parent, fork position, operation key): the same request returns the existing history, a
    /// changed request is a Conflict. A fork requires a Verified parent and a base state whose hash equals
    /// the parent's recorded state hash at T.
    /// </summary>
    Task<Result<StoredHistory, TimelineError>> CreateHistoryAsync(NewHistory request, CancellationToken ct);

    /// <summary>
    /// Atomically appends step <c>ExpectedLastPosition + 1</c>, extends the record chain and advances the
    /// manifest. Idempotent by (history, operation key).
    /// </summary>
    Task<Result<AppendReceipt, TimelineError>> AppendStepAsync(NewStep request, CancellationToken ct);

    Task<Result<StoredHistory, TimelineError>> GetHistoryAsync(TimelineId id, CancellationToken ct);

    /// <summary>
    /// The history's own steps in <c>(afterPosition, throughPosition]</c>, read in a fixed number of
    /// queries. Requires <c>BasePosition &lt;= afterPosition &lt;= throughPosition &lt;= LastPosition</c>.
    /// </summary>
    Task<Result<IReadOnlyList<StoredStep>, TimelineError>> ReadStepsAsync(
        TimelineId id, long afterPosition, long throughPosition, CancellationToken ct);

    /// <summary>Nearest own complete checkpoint at or before the position (at least the base checkpoint).</summary>
    Task<Result<StoredCheckpoint, TimelineError>> ReadNearestCheckpointAsync(TimelineId id, long atOrBefore, CancellationToken ct);

    /// <summary>Missing artifact: IncompatibleVersion. Bytes not matching the content address: CorruptHistory.</summary>
    Task<Result<StoredContext, TimelineError>> ReadContextAsync(string contextArtifactRef, CancellationToken ct);

    /// <summary>
    /// Controlled Recording -> Verified transition after the caller's deterministic replay produced
    /// <paramref name="replayedFinalStateHash"/>. The store re-verifies its own stored record chain and
    /// terminal hashes in the same transaction. Retrying an already Verified completion returns the same receipt.
    /// </summary>
    Task<Result<CompletionReceipt, TimelineError>> MarkVerifiedAsync(
        TimelineId id, long expectedLastPosition, string replayedFinalStateHash, CancellationToken ct);

    /// <summary>Controlled transition to the terminal, auditable Failed status. History rows are retained untouched.</summary>
    Task<Result<StoredHistory, TimelineError>> MarkFailedAsync(TimelineId id, string reason, CancellationToken ct);
}
