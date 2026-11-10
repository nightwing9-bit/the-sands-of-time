using Marrow.Kernel.Results;

namespace Marrow.Timeline;

/// <summary>Frozen v1 error codes (docs/architecture/03-contracts.md). Adding a code requires an ADR.</summary>
public enum TimelineErrorCode
{
    NotFound,
    Forbidden,
    InvalidPosition,
    IncompatibleVersion,
    CorruptHistory,
    Conflict,
    Cancelled,
}

/// <summary>Typed Timeline failure. <see cref="Message"/> is diagnostic text and must not contain secrets.</summary>
public sealed record TimelineError(TimelineErrorCode Code, string Message);

/// <summary>Factory helpers for <c>Result&lt;T, TimelineError&gt;</c>, the concrete form of the contract's <c>TimelineResult&lt;T&gt;</c>.</summary>
public static class TimelineResult
{
    public static Result<T, TimelineError> Ok<T>(T value) => Result<T, TimelineError>.Success(value);

    public static Result<T, TimelineError> Fail<T>(TimelineErrorCode code, string message) =>
        Result<T, TimelineError>.Failure(new TimelineError(code, message));

    public static Result<T, TimelineError> Fail<T>(TimelineError error) =>
        Result<T, TimelineError>.Failure(error);
}
