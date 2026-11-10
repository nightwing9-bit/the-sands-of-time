namespace Marrow.Timeline;

/// <summary>Recording histories accept appends; only CompleteAsync moves Recording to Verified. Failed is terminal and auditable.</summary>
public enum HistoryStatus
{
    Recording,
    Verified,
    Failed,
}

/// <summary>
/// Durable per-history manifest. Version fields pin the historical codec/reducer/context used to
/// reconstruct this history's own steps; lifecycle fields advance only by controlled updates.
/// </summary>
public sealed record HistoryManifest(
    TimelineId Id,
    string DomainId,
    string OwnerId,
    string InputSchemaVersion,
    string StateSchemaVersion,
    string ContextSchemaVersion,
    string ContextHash,
    string ContextArtifactRef,
    string ReducerVersion,
    string RulesHash,
    string SeedHash,
    long LastPosition,
    string LatestStateHash,
    string LastRecordHash,
    TimelineId? ParentId,
    long? ForkPosition,
    HistoryStatus Status)
{
    /// <summary>Key used to resolve this history's pinned codecs and reducer.</summary>
    public TimelineVersionKey Versions =>
        new(DomainId, ReducerVersion, InputSchemaVersion, StateSchemaVersion, ContextSchemaVersion);
}

/// <summary>
/// One logical step at <paramref name="Position"/> (>= 1). Zero inputs is valid (e.g. an empty game tick).
/// Record equality on <paramref name="Inputs"/> is by reference; idempotency compares canonical bytes.
/// </summary>
public sealed record StepRecord<TInput>(long Position, IReadOnlyList<OrderedInput<TInput>> Inputs)
{
    public IReadOnlyList<OrderedInput<TInput>> Inputs { get; init; } =
        Inputs ?? throw new ArgumentNullException(nameof(Inputs));
}

/// <summary>
/// One input inside a step. Applied in <c>(Ordinal, InputId)</c> order with ordinal string comparison;
/// <paramref name="InputId"/> is unique within a history. Business timestamps are payload, never the sort key.
/// </summary>
public sealed record OrderedInput<T>(string InputId, int Ordinal, T Payload)
{
    public string InputId { get; init; } = string.IsNullOrWhiteSpace(InputId)
        ? throw new ArgumentException("InputId must be a non-blank stable identifier.", nameof(InputId))
        : InputId;
}
