using System.Globalization;
using Npgsql;
using StackExchange.Redis;

namespace Marrow.Infrastructure.Messaging;

/// <summary>Result of <see cref="RedisStreamMaintenance.TrimAsync"/>.</summary>
/// <param name="Removed">Entries removed.</param>
/// <param name="MinRetainedId">Every entry with a smaller id was removed; entries at or above it are kept.</param>
/// <param name="Length">Stream length after the trim.</param>
/// <param name="OverBound">The stream is still longer than the configured bound because the remaining entries are unread or
/// unacknowledged by some group, or younger than the retention window. Safety wins over the bound; an operator should look.</param>
public sealed record StreamTrimResult(long Removed, string? MinRetainedId, long Length, bool OverBound);

/// <summary>
/// Spec 07 task 7.5 operations on the shared Redis Stream transport. Redis is transport only: PostgreSQL outbox rows remain the
/// source of every message, and consumers deduplicate through their inbox, so these operations may deliver again but never lose
/// or invent a message.
/// </summary>
public static class RedisStreamMaintenance
{
    /// <summary>
    /// Safe, bounded trim. An entry is removed only when EVERY consumer group has read it (id at or below the group's last-delivered
    /// id) and acknowledged it (id below the group's oldest pending entry), AND it is older than <paramref name="minRetention"/>.
    /// A stream without consumer groups is never trimmed (nobody has read it). Within those limits the stream is cut to at most
    /// <paramref name="maxLength"/> entries; anything the limits protect stays, and <see cref="StreamTrimResult.OverBound"/> reports it.
    /// Uses exact <c>XTRIM MINID</c> (not approximate), so the boundary is precise.
    /// </summary>
    public static async Task<StreamTrimResult> TrimAsync(IDatabase redis, string streamKey, long maxLength, TimeSpan minRetention, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(redis);
        ArgumentException.ThrowIfNullOrWhiteSpace(streamKey);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 0);
        if (!await redis.KeyExistsAsync(streamKey))
        {
            return new StreamTrimResult(0, null, 0, false);
        }
        var groups = await redis.StreamGroupInfoAsync(streamKey);
        var length = await redis.StreamLengthAsync(streamKey);
        if (groups.Length == 0)
        {
            return new StreamTrimResult(0, null, length, length > maxLength);
        }
        // Safety boundary: the smallest id some group still needs.
        StreamId? boundary = null;
        foreach (var group in groups)
        {
            StreamId needed;
            if (group.PendingMessageCount > 0)
            {
                var pending = await redis.StreamPendingAsync(streamKey, group.Name);
                needed = StreamId.Parse(pending.LowestPendingMessageId.ToString());
            }
            else
            {
                needed = group.LastDeliveredId is { Length: > 0 } last ? StreamId.Parse(last).Next() : StreamId.Zero;
            }
            boundary = boundary is null || needed.CompareTo(boundary.Value) < 0 ? needed : boundary;
        }
        var retention = new StreamId(Math.Max(0, (now - minRetention).ToUnixTimeMilliseconds()), 0);
        var safe = boundary!.Value.CompareTo(retention) < 0 ? boundary.Value : retention;

        // Bound: the id of the entry that would become the first of the newest maxLength entries.
        StreamId? bound = null;
        if (length > maxLength)
        {
            var keep = maxLength == 0
                ? []
                : await redis.StreamRangeAsync(streamKey, "-", "+", count: (int)Math.Min(int.MaxValue, maxLength), messageOrder: Order.Descending);
            bound = keep.Length == 0 ? StreamId.Max : StreamId.Parse(keep[^1].Id.ToString());
        }
        var cut = bound is { } b && b.CompareTo(safe) < 0 ? b : safe;
        var removed = cut.CompareTo(StreamId.Zero) > 0
            ? (long)await redis.ExecuteAsync("XTRIM", streamKey, "MINID", "=", cut.ToString())
            : 0;
        var after = await redis.StreamLengthAsync(streamKey);
        return new StreamTrimResult(removed, cut.ToString(), after, after > maxLength);
    }

    /// <summary>
    /// Moves up to <paramref name="max"/> entries of a consumer's dead-letter stream back onto the main stream with their original
    /// Outbox v1 fields (same event id), then deletes them from the dead-letter stream. A consumer that already applied the event
    /// (or another group that receives the re-added entry) finds its inbox row and has no second effect; a crash between the add and
    /// the delete only re-adds the entry once more, which is deduplicated the same way.
    /// </summary>
    public static async Task<int> RequeueDeadLettersAsync(IDatabase redis, string deadLetterKey, string streamKey, int max)
    {
        ArgumentNullException.ThrowIfNull(redis);
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
        var entries = await redis.StreamRangeAsync(deadLetterKey, "-", "+", count: max);
        var moved = 0;
        foreach (var entry in entries)
        {
            var original = entry.Values.Where(v => !v.Name.ToString().StartsWith("dead_", StringComparison.Ordinal)).ToArray();
            if (StreamEntryCodec.TryDecode(original) is null)
            {
                continue; // a malformed entry stays dead-lettered for an operator; requeueing it would only dead-letter it again
            }
            await redis.StreamAddAsync(streamKey, original);
            await redis.StreamDeleteAsync(deadLetterKey, [entry.Id]);
            moved++;
        }
        return moved;
    }

    /// <summary>A Redis stream id (<c>ms-seq</c>).</summary>
    internal readonly record struct StreamId(long Ms, long Seq) : IComparable<StreamId>
    {
        public static StreamId Zero => new(0, 0);
        public static StreamId Max => new(long.MaxValue, long.MaxValue);

        public static StreamId Parse(string value)
        {
            var dash = value.IndexOf('-', StringComparison.Ordinal);
            return dash < 0
                ? new StreamId(long.Parse(value, CultureInfo.InvariantCulture), 0)
                : new StreamId(long.Parse(value.AsSpan(0, dash), CultureInfo.InvariantCulture), long.Parse(value.AsSpan(dash + 1), CultureInfo.InvariantCulture));
        }

        public StreamId Next() => Seq == long.MaxValue ? new StreamId(Ms + 1, 0) : this with { Seq = Seq + 1 };

        public int CompareTo(StreamId other) => Ms != other.Ms ? Ms.CompareTo(other.Ms) : Seq.CompareTo(other.Seq);

        public override string ToString() => $"{Ms.ToString(CultureInfo.InvariantCulture)}-{Seq.ToString(CultureInfo.InvariantCulture)}";
    }
}

/// <summary>
/// Operator recovery of a module's PostgreSQL outbox (Spec 07 task 7.5). Both operations only move rows back to <c>pending</c>; the
/// shared <see cref="OutboxRelay"/> then publishes them again and consumers deduplicate through their inbox. Runs as a login holding
/// the module's relay role (UPDATE of the relay columns only).
/// </summary>
public static class OutboxRecovery
{
    /// <summary>Requeues the named poisoned rows (attempt count reset). Rows that are not poisoned are left alone.</summary>
    public static async Task<int> RequeuePoisonedAsync(NpgsqlDataSource db, MessagingTables tables, IReadOnlyCollection<Guid> eventIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(eventIds);
        await using var command = db.CreateCommand(
            $"""
            UPDATE {tables.Outbox}
            SET status = 'pending', attempts = 0, next_attempt_at = now(), poisoned_at = NULL, last_error = NULL, lease_token = NULL, lease_until = NULL
            WHERE status = 'poisoned' AND event_id = ANY(@ids)
            """);
        command.Parameters.AddWithValue("ids", eventIds.ToArray());
        return await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Redis-volume loss procedure: rows published at or after <paramref name="since"/> (choose a point safely before the last
    /// Redis state known to be durable) go back to <c>pending</c> and are published again. This is an operator action outside the
    /// automatic delivery guarantee: consumers that already applied an event deduplicate it, and consumers whose inbox rows are
    /// intact lose nothing. Returns the number of rows requeued.
    /// </summary>
    public static async Task<int> RepublishSinceAsync(NpgsqlDataSource db, MessagingTables tables, DateTimeOffset since, CancellationToken ct)
    {
        await using var command = db.CreateCommand(
            $"""
            UPDATE {tables.Outbox}
            SET status = 'pending', attempts = 0, next_attempt_at = now(), stream_entry_id = NULL, lease_token = NULL, lease_until = NULL
            WHERE status = 'published' AND published_at >= @since
            """);
        command.Parameters.AddWithValue("since", since);
        return await command.ExecuteNonQueryAsync(ct);
    }
}
