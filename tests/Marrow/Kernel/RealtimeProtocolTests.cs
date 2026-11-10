using System.Text.Json;
using FsCheck;
using FsCheck.Fluent;
using Marrow.Kernel.Realtime;
using Xunit;

namespace Marrow.Tests.Kernel;

public sealed class RealtimeProtocolTests
{
    [Theory]
    [InlineData("0", 0L)]
    [InlineData("1", 1L)]
    [InlineData("9223372036854775807", long.MaxValue)]
    public void Canonical_sequences_round_trip(string text, long value)
    {
        Assert.True(RealtimeWire.TryParseSequence(text, out var parsed));
        Assert.Equal(value, parsed);
        Assert.Equal(text, RealtimeWire.FormatSequence(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("01")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData("1.0")]
    [InlineData(" 1")]
    [InlineData("9223372036854775808")]
    [InlineData("٣")]
    public void Non_canonical_sequences_are_rejected(string text) => Assert.False(RealtimeWire.TryParseSequence(text, out _));

    [Theory]
    [InlineData("room:abc", true)]
    [InlineData("probe-room:A_b-9", true)]
    [InlineData("Room:abc", false)]
    [InlineData("room:", false)]
    [InlineData(":abc", false)]
    [InlineData("room:a:b", false)]
    [InlineData("room:a b", false)]
    [InlineData("room:../x", false)]
    public void Stream_ids_are_validated(string streamId, bool valid) =>
        Assert.Equal(valid, RealtimeWire.TryParseStreamId(streamId, out _, out _));

    [Fact]
    public void Envelope_serializes_with_exactly_the_canonical_field_names_and_a_string_sequence()
    {
        var envelope = new RealtimeEnvelope<int>(1, "room:a", RealtimeWire.FormatSequence(42), "e1", DateTimeOffset.UnixEpoch, "room.tick", 7);
        var json = JsonSerializer.SerializeToElement(envelope, JsonSerializerOptions.Web);
        Assert.Equal(RealtimeWire.EnvelopeFields, json.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(JsonValueKind.String, json.GetProperty("sequence").ValueKind);
        Assert.True(RealtimeWire.TryReadEnvelope(json, out var read, out var sequence));
        Assert.Equal(42, sequence);
        Assert.Equal("room.tick", read!.Kind);
    }

    [Theory]
    [InlineData("""{"schemaVersion":1,"streamId":"room:a","sequence":"1","eventId":"e","serverUtc":"2026-01-01T00:00:00Z","kind":"k","payload":1,"messageId":"x"}""")]
    [InlineData("""{"schemaVersion":1,"streamId":"room:a","sequence":"1","eventId":"e","serverTime":"2026-01-01T00:00:00Z","kind":"k","payload":1}""")]
    [InlineData("""{"schemaVersion":1,"streamId":"room:a","sequence":1,"eventId":"e","serverUtc":"2026-01-01T00:00:00Z","kind":"k","payload":1}""")]
    [InlineData("""{"schemaVersion":2,"streamId":"room:a","sequence":"1","eventId":"e","serverUtc":"2026-01-01T00:00:00Z","kind":"k","payload":1}""")]
    [InlineData("""{"schemaVersion":1,"streamId":"room:a","streamSequence":"1","eventId":"e","serverUtc":"2026-01-01T00:00:00Z","kind":"k","payload":1}""")]
    public void Strict_reader_rejects_aliases_extra_fields_and_numeric_sequences(string json) =>
        Assert.False(RealtimeWire.TryReadEnvelope(JsonDocument.Parse(json).RootElement, out _, out _));

    [Fact]
    public void Cursor_buffers_before_snapshot_drops_duplicates_and_reports_gaps()
    {
        var cursor = new RealtimeStreamCursor<long>("room:a");
        Assert.Equal(RealtimeCursorAction.Buffered, cursor.Accept(4, 4));
        Assert.Equal(RealtimeCursorAction.Buffered, cursor.Accept(5, 5));
        var outcome = cursor.ApplySnapshot(3);
        Assert.Equal([4L, 5L], outcome.Ready);
        Assert.False(outcome.GapRemains);
        Assert.Equal(RealtimeCursorAction.Duplicate, cursor.Accept(5, 5));
        Assert.Equal(RealtimeCursorAction.Apply, cursor.Accept(6, 6));
        Assert.Equal(RealtimeCursorAction.Gap, cursor.Accept(8, 8));
        Assert.Equal(RealtimeCursorAction.Buffered, cursor.Accept(9, 9));
        Assert.Equal([9L], cursor.ApplySnapshot(8).Ready);
        Assert.False(cursor.ApplySnapshot(2).Accepted);
        Assert.Equal(9, cursor.Sequence);
    }

    /// <summary>
    /// Property: for any pattern of lost, duplicated and locally swapped deliveries, a client that refetches the source
    /// snapshot whenever the cursor reports a gap applies events in strictly increasing order, its state always equals the
    /// source state at its applied sequence, and a final refetch converges it to the source.
    /// </summary>
    [Fact]
    public void Cursor_with_snapshot_refetch_converges_under_loss_duplication_and_reordering()
    {
        var gen =
            from length in Gen.Choose(1, 80)
            from copies in Gen.ArrayOf(Gen.Frequency((2, Gen.Constant(0)), (12, Gen.Constant(1)), (2, Gen.Constant(2))), length)
            from swaps in Gen.ArrayOf(Gen.Frequency((9, Gen.Constant(false)), (1, Gen.Constant(true))), length)
            from buffer in Gen.Choose(1, 8)
            select (copies, swaps, buffer);
        Prop.ForAll(gen.ToArbitrary(), c =>
        {
            // Source state after sequence s is the sum 1..s; each event carries its increment s.
            static long StateAt(long s) => s * (s + 1) / 2;
            var deliveries = new List<long>();
            for (var s = 1; s <= c.copies.Length; s++)
            {
                for (var k = 0; k < c.copies[s - 1]; k++)
                {
                    deliveries.Add(s);
                }
                if (c.swaps[s - 1] && deliveries.Count >= 2)
                {
                    (deliveries[^1], deliveries[^2]) = (deliveries[^2], deliveries[^1]);
                }
            }
            var cursor = new RealtimeStreamCursor<long>("room:a", c.buffer);
            long state = 0;
            long lastApplied = -1;
            long published = 0;
            void Snapshot()
            {
                var outcome = cursor.ApplySnapshot(published);
                if (!outcome.Accepted)
                {
                    return;
                }
                state = StateAt(published);
                Assert.True(published >= lastApplied);
                lastApplied = published;
                foreach (var s in outcome.Ready)
                {
                    Assert.True(s > lastApplied);
                    state += s;
                    lastApplied = s;
                }
            }
            Snapshot();
            foreach (var s in deliveries)
            {
                published = Math.Max(published, s);
                switch (cursor.Accept(s, s))
                {
                    case RealtimeCursorAction.Apply:
                        Assert.True(s > lastApplied);
                        state += s;
                        lastApplied = s;
                        break;
                    case RealtimeCursorAction.Gap:
                        Snapshot();
                        break;
                }
                if (!cursor.AwaitingSnapshot)
                {
                    Assert.Equal(StateAt(cursor.Sequence!.Value), state);
                }
            }
            published = c.copies.Length;
            Snapshot();
            Assert.Equal(c.copies.Length, cursor.Sequence);
            Assert.Equal(StateAt(c.copies.Length), state);
        }).Check(Config.QuickThrowOnFailure.WithMaxTest(300));
    }
}
