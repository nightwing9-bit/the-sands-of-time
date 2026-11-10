using System.Collections.Immutable;
using Marrow.Kernel.Canonical;
using Marrow.Tests.TestDomains;
using Xunit;

namespace Marrow.Tests.Kernel;

public sealed class CanonicalCodecTests
{
    [Fact]
    public void Writer_uses_fixed_big_endian_layout()
    {
        var bytes = new CanonicalWriter()
            .WriteInt32(1).WriteInt64(-2).WriteBoolean(true).WriteString("é")
            .WriteGuid(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"))
            .ToArray();

        byte[] expected =
        [
            0, 0, 0, 1,
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFE,
            1,
            0, 0, 0, 2, 0xC3, 0xA9,
            0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF,
        ];
        Assert.Equal(expected, bytes);
    }

    [Fact]
    public void Reader_round_trips_writer_output()
    {
        var guid = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        var bytes = new CanonicalWriter().WriteInt32(-7).WriteInt64(long.MaxValue).WriteBoolean(false)
            .WriteString("").WriteString("ghost").WriteBytes([9, 8]).WriteGuid(guid).WriteCount(3).ToArray();

        var r = new CanonicalReader(bytes);
        Assert.Equal(-7, r.ReadInt32());
        Assert.Equal(long.MaxValue, r.ReadInt64());
        Assert.False(r.ReadBoolean());
        Assert.Equal("", r.ReadString());
        Assert.Equal("ghost", r.ReadString());
        Assert.Equal(new byte[] { 9, 8 }, r.ReadBytes().ToArray());
        Assert.Equal(guid, r.ReadGuid());
        Assert.Equal(3, r.ReadCount());
        r.EnsureEnd();
    }

    [Fact]
    public void Reader_rejects_truncated_trailing_and_out_of_range_bytes()
    {
        Assert.Throws<CanonicalFormatException>(() => new CanonicalReader([0, 0, 1]).ReadInt32());
        Assert.Throws<CanonicalFormatException>(() => new CanonicalReader([2]).ReadBoolean());
        Assert.Throws<CanonicalFormatException>(() => new CanonicalReader([0xFF, 0xFF, 0xFF, 0xFF]).ReadCount());
        Assert.Throws<CanonicalFormatException>(() => new CanonicalReader([0, 0, 0, 5, 1]).ReadBytes());
        Assert.Throws<CanonicalFormatException>(() => new CanonicalReader([0, 0, 0, 1, 0xFF]).ReadString());
        Assert.Throws<CanonicalFormatException>(() =>
        {
            var r = new CanonicalReader([1, 0]);
            r.ReadBoolean();
            r.EnsureEnd();
        });
    }

    [Fact]
    public void Writer_rejects_strings_without_a_canonical_utf8_form()
    {
        Assert.Throws<ArgumentException>(() => new CanonicalWriter().WriteString("\uD800"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CanonicalWriter().WriteCount(-1));
    }

    [Fact]
    public void Equal_set_state_encodes_identically_regardless_of_insertion_order()
    {
        var codec = new TickWorldCodec();
        var a = new TickWorld(5, 1, 0, 0, ImmutableSortedSet.Create(StringComparer.Ordinal, "b", "a", "c"));
        var b = new TickWorld(5, 1, 0, 0, ImmutableSortedSet.Create(StringComparer.Ordinal, "c", "b", "a"));

        Assert.Equal(codec.EncodeCanonical(a), codec.EncodeCanonical(b));
        Assert.Equal(codec.EncodeCanonical(a), codec.EncodeCanonical(codec.Decode(codec.EncodeCanonical(a))));
    }

    [Fact]
    public void Test_domain_codecs_round_trip_and_reject_corrupt_bytes()
    {
        var input = new TickInputCodec();
        Assert.Equal(new TickInput(TickAction.Collect, "c-1"), input.Decode(input.EncodeCanonical(new TickInput(TickAction.Collect, "c-1"))));
        Assert.Throws<CanonicalFormatException>(() => input.Decode([7, 0, 0, 0, 0]));

        var counter = new CounterStateCodec();
        var state = new CounterState(-5, 2, 1_000);
        Assert.Equal(state, counter.Decode(counter.EncodeCanonical(state)));
        Assert.Throws<CanonicalFormatException>(() => counter.Decode(counter.EncodeCanonical(state)[..^1]));

        var ctx = new CounterContextCodec();
        Assert.Equal(new CounterContext(10), ctx.Decode(ctx.EncodeCanonical(new CounterContext(10))));
        var tickCtx = new TickContextCodec();
        Assert.Throws<CanonicalFormatException>(() => tickCtx.Decode([.. tickCtx.EncodeCanonical(new TickContext(2)), 0]));
    }
}
