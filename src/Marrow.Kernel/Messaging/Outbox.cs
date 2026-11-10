namespace Marrow.Kernel.Messaging;

/// <summary>Outbox v1 record, stored in the initiating module schema in the same transaction as the business effect.</summary>
public sealed record OutboxMessage(
    Guid EventId,
    string EventType,
    int EventVersion,
    string AggregateId,
    DateTimeOffset OccurredUtc,
    string Payload,
    string CorrelationId);

/// <summary>Consumer inbox entry, unique on (ConsumerId, EventId).</summary>
public sealed record InboxRecord(string ConsumerId, Guid EventId, DateTimeOffset ReceivedUtc);

/// <summary>Port implemented in Infrastructure; the write must join the caller's business transaction.</summary>
public interface IOutboxWriter
{
    Task EnqueueAsync(OutboxMessage message, CancellationToken ct);
}

/// <summary>Port implemented in Infrastructure; dedupe must share the consumer's business-write transaction.</summary>
public interface IInboxStore
{
    /// <returns>true if first seen (caller proceeds), false if duplicate.</returns>
    Task<bool> TryRecordAsync(InboxRecord record, CancellationToken ct);
}
