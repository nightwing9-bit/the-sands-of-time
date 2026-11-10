using System.Text;
using Marrow.Kernel.Messaging;
using Npgsql;
namespace Marrow.Infrastructure.Messaging;
/// <summary>
/// Kernel <see cref="IOutboxWriter"/> bound to the caller's open business transaction. There is no overload that
/// saves on its own connection: the outbox row commits or rolls back with the business row, never separately.
/// A duplicate <see cref="OutboxMessage.EventId"/> fails the insert (and therefore the caller's transaction).
/// </summary>
public sealed class PostgresOutboxWriter(NpgsqlTransaction transaction, MessagingTables tables) : IOutboxWriter
{
    /// <summary>Upper bound on a payload (UTF-8 bytes); keeps stream entries and retries bounded.</summary>
    public const int MaxPayloadBytes = 256 * 1024;
    public async Task EnqueueAsync(OutboxMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        Validate(message);
        var connection = transaction.Connection
            ?? throw new InvalidOperationException("The business transaction has already completed; an outbox row cannot be saved outside it.");
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {tables.Outbox} (event_id, event_type, event_version, aggregate_id, occurred_utc, payload, correlation_id)
            VALUES (@id, @type, @version, @aggregate, @occurred, @payload, @correlation)
            """, connection, transaction);
        command.Parameters.AddWithValue("id", message.EventId);
        command.Parameters.AddWithValue("type", message.EventType);
        command.Parameters.AddWithValue("version", message.EventVersion);
        command.Parameters.AddWithValue("aggregate", message.AggregateId);
        command.Parameters.AddWithValue("occurred", message.OccurredUtc.ToUniversalTime());
        command.Parameters.AddWithValue("payload", message.Payload);
        command.Parameters.AddWithValue("correlation", message.CorrelationId);
        await command.ExecuteNonQueryAsync(ct);
    }
    internal static void Validate(OutboxMessage message)
    {
        if (message.EventId == Guid.Empty)
        {
            throw new ArgumentException("EventId must not be empty.", nameof(message));
        }
        if (string.IsNullOrWhiteSpace(message.EventType) || message.EventType.Length > 200)
        {
            throw new ArgumentException("EventType must be 1-200 characters.", nameof(message));
        }
        if (message.EventVersion <= 0)
        {
            throw new ArgumentException("EventVersion must be positive.", nameof(message));
        }
        if (string.IsNullOrEmpty(message.AggregateId) || message.AggregateId.Length > 200)
        {
            throw new ArgumentException("AggregateId must be 1-200 characters.", nameof(message));
        }
        if (string.IsNullOrEmpty(message.CorrelationId) || message.CorrelationId.Length > 200)
        {
            throw new ArgumentException("CorrelationId must be 1-200 characters.", nameof(message));
        }
        if (message.Payload is null || Encoding.UTF8.GetByteCount(message.Payload) > MaxPayloadBytes)
        {
            throw new ArgumentException($"Payload is required and must not exceed {MaxPayloadBytes} UTF-8 bytes.", nameof(message));
        }
    }
}
/// <summary>
/// Kernel <see cref="IInboxStore"/> bound to the consumer's business transaction: the <c>(consumer_id,event_id)</c>
/// row and the business effect commit together. A concurrent duplicate waits on the unique key and then sees the
/// committed row (returns false); if the first transaction rolls back, the duplicate records it instead.
/// </summary>
public sealed class PostgresInboxStore(NpgsqlTransaction transaction, MessagingTables tables) : IInboxStore
{
    public async Task<bool> TryRecordAsync(InboxRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (string.IsNullOrWhiteSpace(record.ConsumerId) || record.ConsumerId.Length > 200 || record.EventId == Guid.Empty)
        {
            throw new ArgumentException("ConsumerId (1-200 characters) and a non-empty EventId are required.", nameof(record));
        }
        var connection = transaction.Connection
            ?? throw new InvalidOperationException("The consumer transaction has already completed.");
        await using var command = new NpgsqlCommand(
            $"INSERT INTO {tables.Inbox} (consumer_id, event_id, received_utc) VALUES (@consumer, @id, @received) ON CONFLICT DO NOTHING",
            connection, transaction);
        command.Parameters.AddWithValue("consumer", record.ConsumerId);
        command.Parameters.AddWithValue("id", record.EventId);
        command.Parameters.AddWithValue("received", record.ReceivedUtc.ToUniversalTime());
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }
}
