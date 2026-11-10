using Marrow.Kernel.Results;

namespace Marrow.Timeline;

/// <summary>
/// Request to create a source history. <paramref name="OwnerId"/> must be the verified server-side
/// actor's subject id (never a request-supplied principal). <paramref name="InitialState"/> is the
/// complete position-0 state; <paramref name="PinnedContext"/> is stored durably with the manifest.
/// </summary>
public sealed record CreateSourceRequest<TState, TContext>(
    string OwnerId,
    TimelineVersionKey Versions,
    TState InitialState,
    TContext PinnedContext,
    string RulesHash,
    string SeedHash)
{
    public string OwnerId { get; init; } = string.IsNullOrWhiteSpace(OwnerId)
        ? throw new ArgumentException("OwnerId must come from a verified actor.", nameof(OwnerId))
        : OwnerId;

    public TimelineVersionKey Versions { get; init; } = Versions ?? throw new ArgumentNullException(nameof(Versions));
}

/// <summary>Committed append. A retry with the same operation key and payload returns the same receipt.</summary>
public sealed record AppendReceipt(TimelineId Timeline, long Position, string StateHash, string RecordHash);

/// <summary>Result of CompleteAsync after contiguous-chain and terminal-hash verification.</summary>
public sealed record CompletionReceipt(
    TimelineId Timeline,
    long FinalPosition,
    string FinalStateHash,
    string LastRecordHash,
    HistoryStatus Status);

/// <summary>Where a returned state hash comes from, so a reconstructed digest is never presented as a stored per-position hash.</summary>
public enum StateHashOrigin
{
    /// <summary>Matches a hash persisted for this exact position (initial or checkpoint or latest).</summary>
    StoredCheckpoint,

    /// <summary>
    /// Computed from a deterministic fold over a verified record chain; no complete state was stored for this
    /// position. The digest matched the state hash carried in this position's chain record, which is not an
    /// independently stored snapshot.
    /// </summary>
    ReconstructedFromVerifiedChain,
}

/// <summary>
/// The complete state after applying exactly steps 1..<paramref name="Position"/>.
/// <paramref name="HistoryStatus"/> is the whole history's status; a verified prefix of a Recording history stays Recording.
/// </summary>
public sealed record VerifiedState<TState>(
    TimelineId Timeline,
    long Position,
    TState State,
    string StateHash,
    StateHashOrigin HashOrigin,
    HistoryStatus HistoryStatus);

/// <summary>Full replay result from position 0 through the history's last position.</summary>
public sealed record VerifiedReplay<TState>(
    TimelineId Timeline,
    long FinalPosition,
    TState FinalState,
    string FinalStateHash,
    string LastRecordHash,
    HistoryStatus Status);

/// <summary>Source and alternative states at the same position. Domain adapters compute any outcome metrics.</summary>
public sealed record AlignedState<TState>(long Position, VerifiedState<TState> Source, VerifiedState<TState> Alternative);

/// <summary>
/// The one domain-facing Timeline lifecycle authority (Timeline API v1). Sources and branches share
/// <see cref="TimelineId"/>; a child is continued with the same <see cref="AppendAsync"/> as a source.
/// Expected failures are returned as <see cref="TimelineError"/>; arguments that violate the type's own
/// invariants (null, blank operation keys) throw <see cref="ArgumentException"/>.
/// </summary>
public interface ITimelineEngine<TState, TInput, TContext>
{
    /// <summary>Atomically commits manifest, complete position-0 checkpoint and pinned context before returning the id. Idempotent by operation key.</summary>
    Task<Result<TimelineId, TimelineError>> CreateSourceAsync(
        CreateSourceRequest<TState, TContext> source, string operationKey, CancellationToken ct);

    /// <summary>
    /// Appends step <c>expectedLastPosition + 1</c> to a Recording history (source or child).
    /// Wrong step position: InvalidPosition. Stale expected position, non-Recording status, duplicate
    /// InputId or reused key with different payload: Conflict.
    /// </summary>
    Task<Result<AppendReceipt, TimelineError>> AppendAsync(
        TimelineId history, long expectedLastPosition, StepRecord<TInput> nextStep,
        string operationKey, CancellationToken ct);

    /// <summary>Verifies the contiguous chain and terminal state hash, then moves Recording to Verified.</summary>
    Task<Result<CompletionReceipt, TimelineError>> CompleteAsync(
        TimelineId history, long expectedLastPosition, CancellationToken ct);

    /// <summary>State after exactly steps 1..position; requires <c>0 &lt;= position &lt;= LastPosition</c>.</summary>
    Task<Result<VerifiedState<TState>, TimelineError>> RestoreAsync(
        TimelineId history, long position, CancellationToken ct);

    Task<Result<VerifiedReplay<TState>, TimelineError>> ReplayAsync(
        TimelineId history, CancellationToken ct);

    /// <summary>
    /// Creates a Recording child sharing the verified source prefix through <paramref name="forkPosition"/>;
    /// the child context applies only from step forkPosition + 1. v1 requires a Verified source.
    /// Idempotent by (owner, source, position, operation key); a changed context for the same key is a Conflict.
    /// </summary>
    Task<Result<TimelineId, TimelineError>> ForkAsync(
        TimelineId source, long forkPosition, string operationKey,
        TContext pinnedBranchContext, CancellationToken ct);

    /// <summary>Verified states of both histories at each requested position, or a typed error.</summary>
    Task<Result<IReadOnlyList<AlignedState<TState>>, TimelineError>> CompareAlignedAsync(
        TimelineId source, TimelineId alternative, IReadOnlyList<long> positions,
        CancellationToken ct);
}
