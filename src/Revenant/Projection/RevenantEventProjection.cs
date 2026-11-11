using System.Text.Json;
using Marrow.Kernel.Messaging;
using Npgsql;
using Revenant.Payments;

namespace Revenant.Projection;

/// <summary>
/// The idempotent read projection of the financial outbox events (R07.09). It is the consumer's business effect: the host's shared
/// inbox consumer records <c>(consumer_id, event_id)</c> and calls <see cref="ApplyAsync"/> in the same PostgreSQL transaction, so the
/// inbox row and the read-model write commit or roll back together and a redelivered event has no second effect.
/// <para>
/// Writes only <c>payment_view</c>, <c>hold_view</c> and <c>posting_view</c> (login role <c>revenant_read_projector</c>); it cannot
/// touch the ledger. Order independence: a payment row only moves to a strictly higher version, a hold only from Active to a terminal
/// status, and a posting row is immutable, so a late or duplicate event never moves a row backwards. Event types it does not project
/// (review-case events) are recorded in the inbox and otherwise ignored. A malformed or unsupported-version payload throws, which rolls
/// back and leaves the entry to the consumer's bounded retry and dead-letter handling.
/// </para>
/// </summary>
public static class RevenantEventProjection
{
    /// <summary>The logical consumer: the Redis consumer group and the inbox <c>consumer_id</c>.</summary>
    public const string ConsumerId = "revenant-read-projection";

    public static async Task ApplyAsync(OutboxMessage message, NpgsqlTransaction transaction, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(transaction);
        switch (message.EventType)
        {
            case PaymentLedgerStore.EventPaymentInitiated:
            case PaymentLedgerStore.EventPaymentTransitioned:
                await ApplyPaymentAsync(message, transaction, ct);
                break;
            case PaymentLedgerStore.EventHoldReserved:
            case PaymentLedgerStore.EventHoldResolved:
                await ApplyHoldAsync(message, transaction, ct);
                break;
            case PaymentLedgerStore.EventLedgerPosted:
                await ApplyPostingAsync(message, transaction, ct);
                break;
        }
    }

    private static async Task ApplyPaymentAsync(OutboxMessage message, NpgsqlTransaction tx, CancellationToken ct)
    {
        var p = Parse(message).RootElement;
        // Strictly newer only: an older or equal version arriving late (or again) changes nothing.
        await ExecAsync(tx,
            """
            INSERT INTO revenant.payment_view (payment_id, scenario_run_id, intent_id, status, version, last_event_id)
            VALUES (@payment, @run, @intent, @status, @version, @event)
            ON CONFLICT (payment_id) DO UPDATE
               SET status = EXCLUDED.status, version = EXCLUDED.version, last_event_id = EXCLUDED.last_event_id, updated_utc = now()
             WHERE EXCLUDED.version > revenant.payment_view.version
            """, ct,
            ("payment", p.GetProperty("paymentId").GetGuid()), ("run", p.GetProperty("runId").GetGuid()),
            ("intent", p.GetProperty("intentId").GetString() ?? throw new JsonException("intentId missing")),
            ("status", p.GetProperty("toStatus").GetString() ?? throw new JsonException("toStatus missing")),
            ("version", p.GetProperty("version").GetInt64()), ("event", message.EventId));
    }

    private static async Task ApplyHoldAsync(OutboxMessage message, NpgsqlTransaction tx, CancellationToken ct)
    {
        var p = Parse(message).RootElement;
        // Active may be inserted, but never overwrite a terminal row; a terminal status always wins over Active.
        await ExecAsync(tx,
            """
            INSERT INTO revenant.hold_view (hold_id, scenario_run_id, payment_id, status, amount_minor, last_event_id)
            VALUES (@hold, @run, @payment, @status, @amount, @event)
            ON CONFLICT (hold_id) DO UPDATE
               SET status = EXCLUDED.status, last_event_id = EXCLUDED.last_event_id, updated_utc = now()
             WHERE revenant.hold_view.status = 'Active' AND EXCLUDED.status <> 'Active'
            """, ct,
            ("hold", p.GetProperty("holdId").GetGuid()), ("run", p.GetProperty("runId").GetGuid()),
            ("payment", p.GetProperty("paymentId").GetGuid()),
            ("status", p.GetProperty("status").GetString() ?? throw new JsonException("status missing")),
            ("amount", p.GetProperty("amountMinor").GetInt64()), ("event", message.EventId));
    }

    private static async Task ApplyPostingAsync(OutboxMessage message, NpgsqlTransaction tx, CancellationToken ct)
    {
        var p = Parse(message).RootElement;
        object compensates = p.TryGetProperty("compensatesGroupId", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetGuid() : DBNull.Value;
        await ExecAsync(tx,
            """
            INSERT INTO revenant.posting_view (group_id, scenario_run_id, payment_id, purpose, currency, amount_minor, compensates_group_id, event_id)
            VALUES (@group, @run, @payment, @purpose, @currency, @amount, @compensates, @event)
            ON CONFLICT (group_id) DO NOTHING
            """, ct,
            ("group", p.GetProperty("groupId").GetGuid()), ("run", p.GetProperty("runId").GetGuid()),
            ("payment", p.GetProperty("paymentId").GetGuid()),
            ("purpose", p.GetProperty("purpose").GetString() ?? throw new JsonException("purpose missing")),
            ("currency", p.GetProperty("currency").GetString() ?? throw new JsonException("currency missing")),
            ("amount", p.GetProperty("amountMinor").GetInt64()), ("compensates", compensates), ("event", message.EventId));
    }

    private static JsonDocument Parse(OutboxMessage message)
    {
        if (message.EventVersion != 1)
        {
            throw new InvalidOperationException($"Unsupported {message.EventType} event version {message.EventVersion}.");
        }
        return JsonDocument.Parse(message.Payload);
    }

    private static async Task ExecAsync(NpgsqlTransaction tx, string sql, CancellationToken ct, params (string Name, object Value)[] args)
    {
        await using var cmd = new NpgsqlCommand(sql, tx.Connection, tx);
        foreach (var (name, value) in args)
        {
            cmd.Parameters.AddWithValue(name, value);
        }
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
