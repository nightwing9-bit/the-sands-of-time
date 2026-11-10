namespace Marrow.Kernel.Realtime;

/// <summary>Realtime envelope v1. Sequence is a per-stream monotonic decimal string.</summary>
public sealed record RealtimeEnvelope<TPayload>(
    int SchemaVersion,
    string StreamId,
    string Sequence,
    string EventId,
    DateTimeOffset ServerUtc,
    string Kind,
    TPayload Payload)
{
    public const int CurrentSchemaVersion = 1;
}
