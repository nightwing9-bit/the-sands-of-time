using Marrow.Kernel.Results;
using Marrow.Timeline;

namespace GhostLap.Solo;

public enum SoloErrorCode
{
    /// <summary>Unknown history, not a Ghost Lap solo original, or not the actor's own. Deliberately one code (no existence oracle).</summary>
    NotFound,
    /// <summary>The actor is not authenticated, so it cannot own a recording.</summary>
    Unauthenticated,
    InvalidRequest,
    /// <summary>Tick outside 0..LastPosition of the history.</summary>
    InvalidTick,
    /// <summary>The stored versions have no registered reducer/codec in this build; history is never reinterpreted.</summary>
    IncompatibleVersion,
    CorruptHistory,
    /// <summary>Stale position, conflicting payload for a reused operation key, or a terminal lifecycle state.</summary>
    Conflict,
    Cancelled,
    /// <summary>The database could not be reached or refused the operation; nothing was acknowledged.</summary>
    Unavailable,
    /// <summary>The round has not reached its final tick, or the original was never completed and verified.</summary>
    NotComplete,
    /// <summary>The persisted history does not reproduce the recorded hashes or the live final state.</summary>
    VerificationFailed,
    /// <summary>
    /// A fork was requested where <c>ghost-v1</c> forbids one: the final tick is inspect-only (task 6.4). Ticks outside
    /// 0..final are <see cref="InvalidTick"/>.
    /// </summary>
    ForkNotAllowed,
}

/// <summary>Typed solo-original failure. <see cref="Message"/> is diagnostic text without secrets.</summary>
public sealed record SoloError(SoloErrorCode Code, string Message)
{
    public static SoloError From(TimelineError error) => new(error.Code switch
    {
        // An engine-level Forbidden is reported like an unknown history, as for a non-owner.
        TimelineErrorCode.NotFound or TimelineErrorCode.Forbidden => SoloErrorCode.NotFound,
        TimelineErrorCode.InvalidPosition => SoloErrorCode.InvalidTick,
        TimelineErrorCode.IncompatibleVersion => SoloErrorCode.IncompatibleVersion,
        TimelineErrorCode.CorruptHistory => SoloErrorCode.CorruptHistory,
        TimelineErrorCode.Conflict => SoloErrorCode.Conflict,
        TimelineErrorCode.Cancelled => SoloErrorCode.Cancelled,
        _ => SoloErrorCode.CorruptHistory,
    }, error.Message);

    public override string ToString() => $"{Code}: {Message}";
}

internal static class SoloResult
{
    public static Result<T, SoloError> Ok<T>(T value) => Result<T, SoloError>.Success(value);

    public static Result<T, SoloError> Fail<T>(SoloErrorCode code, string message) =>
        Result<T, SoloError>.Failure(new SoloError(code, message));

    public static Result<T, SoloError> Fail<T>(SoloError error) => Result<T, SoloError>.Failure(error);

    public static Result<T, SoloError> Fail<T>(TimelineError error) => Result<T, SoloError>.Failure(SoloError.From(error));

    /// <summary>
    /// Runs a database-backed call and turns a driver failure or cancellation into a typed error, so a failed commit is
    /// visible to the caller instead of escaping as an exception or being reported as success.
    /// </summary>
    public static async Task<Result<T, SoloError>> GuardAsync<T>(Func<Task<Result<T, SoloError>>> operation, CancellationToken ct)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Fail<T>(SoloErrorCode.Cancelled, "Operation was cancelled; the last committed tick is unchanged.");
        }
        catch (System.Data.Common.DbException ex)
        {
            return Fail<T>(SoloErrorCode.Unavailable, $"Database unavailable ({ex.GetType().Name}); nothing was acknowledged.");
        }
    }
}
