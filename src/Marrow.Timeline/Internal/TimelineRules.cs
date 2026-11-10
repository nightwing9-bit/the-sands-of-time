using Marrow.Kernel.Results;

namespace Marrow.Timeline.Internal;

/// <summary>
/// Pure lifecycle/position rules shared by every engine operation, so each failure maps to one
/// frozen <see cref="TimelineErrorCode"/>. Internal: not part of the domain-facing contract.
/// </summary>
internal static class TimelineRules
{
    /// <summary>Stable application order: Ordinal ascending, then InputId by ordinal string comparison.</summary>
    public static int CompareInputs<T>(OrderedInput<T> a, OrderedInput<T> b)
    {
        var byOrdinal = a.Ordinal.CompareTo(b.Ordinal);
        return byOrdinal != 0 ? byOrdinal : string.CompareOrdinal(a.InputId, b.InputId);
    }

    /// <summary>
    /// Validates that <paramref name="step"/> is the contiguous next step and returns its inputs in
    /// canonical order. Gap/reorder: InvalidPosition. Duplicate InputId within the step: Conflict.
    /// </summary>
    public static Result<IReadOnlyList<OrderedInput<TInput>>, TimelineError> CanonicalizeNextStep<TInput>(
        long expectedLastPosition, StepRecord<TInput> step)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (expectedLastPosition < 0)
        {
            return TimelineResult.Fail<IReadOnlyList<OrderedInput<TInput>>>(
                TimelineErrorCode.InvalidPosition, $"Expected last position {expectedLastPosition} is negative.");
        }

        if (expectedLastPosition == long.MaxValue || step.Position != expectedLastPosition + 1)
        {
            return TimelineResult.Fail<IReadOnlyList<OrderedInput<TInput>>>(
                TimelineErrorCode.InvalidPosition,
                $"Step position {step.Position} is not the next position after {expectedLastPosition}.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var input in step.Inputs)
        {
            ArgumentNullException.ThrowIfNull(input, nameof(step));
            if (!seen.Add(input.InputId))
            {
                return TimelineResult.Fail<IReadOnlyList<OrderedInput<TInput>>>(
                    TimelineErrorCode.Conflict, $"Duplicate InputId '{input.InputId}' in step {step.Position}.");
            }
        }

        var ordered = step.Inputs.ToArray();
        Array.Sort(ordered, CompareInputs);
        return TimelineResult.Ok<IReadOnlyList<OrderedInput<TInput>>>(ordered);
    }

    /// <summary>Append precondition: Recording status and an optimistic expected-last match (else Conflict).</summary>
    public static TimelineError? CheckAppendable(HistoryManifest manifest, long expectedLastPosition)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.Status != HistoryStatus.Recording)
        {
            return new(TimelineErrorCode.Conflict, $"History is {manifest.Status}; only Recording histories accept appends.");
        }

        return expectedLastPosition != manifest.LastPosition
            ? new(TimelineErrorCode.Conflict, $"Expected last position {expectedLastPosition} but history is at {manifest.LastPosition}.")
            : null;
    }

    /// <summary>Restore/compare bound: <c>0 &lt;= position &lt;= LastPosition</c> (else InvalidPosition).</summary>
    public static TimelineError? CheckReadablePosition(HistoryManifest manifest, long position)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return position < 0 || position > manifest.LastPosition
            ? new(TimelineErrorCode.InvalidPosition, $"Position {position} is outside 0..{manifest.LastPosition}.")
            : null;
    }

    /// <summary>
    /// Fork precondition (v1): Verified source (else Conflict) and <c>0 &lt;= T &lt;= LastPosition</c>
    /// (else InvalidPosition). Forking at the final position is allowed by core; a domain adapter may forbid it.
    /// </summary>
    public static TimelineError? CheckForkable(HistoryManifest source, long forkPosition)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Status != HistoryStatus.Verified)
        {
            return new(TimelineErrorCode.Conflict, $"Source is {source.Status}; v1 forks require a Verified source.");
        }

        return CheckReadablePosition(source, forkPosition);
    }
}
