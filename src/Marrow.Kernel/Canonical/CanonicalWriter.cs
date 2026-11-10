using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace Marrow.Kernel.Canonical;

/// <summary>
/// Canonical binary encoding rules shared by every state, input and context codec:
/// fixed-width big-endian integers, booleans as one byte 0/1, strict UTF-8 strings and byte
/// blobs prefixed by a big-endian int32 length, GUIDs as 16 big-endian bytes.
/// Floating-point values are deliberately not supported. Codecs must write fields in a fixed
/// order and write set/map members in a stable sorted order (ordinal for strings).
/// </summary>
public sealed class CanonicalWriter
{
    internal static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly ArrayBufferWriter<byte> _buffer = new();

    public int Length => _buffer.WrittenCount;

    public CanonicalWriter WriteByte(byte value)
    {
        _buffer.GetSpan(1)[0] = value;
        _buffer.Advance(1);
        return this;
    }

    public CanonicalWriter WriteBoolean(bool value) => WriteByte(value ? (byte)1 : (byte)0);

    public CanonicalWriter WriteInt32(int value)
    {
        BinaryPrimitives.WriteInt32BigEndian(_buffer.GetSpan(4), value);
        _buffer.Advance(4);
        return this;
    }

    public CanonicalWriter WriteInt64(long value)
    {
        BinaryPrimitives.WriteInt64BigEndian(_buffer.GetSpan(8), value);
        _buffer.Advance(8);
        return this;
    }

    /// <summary>Writes a non-negative element count for a following collection.</summary>
    public CanonicalWriter WriteCount(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return WriteInt32(count);
    }

    public CanonicalWriter WriteGuid(Guid value)
    {
        if (!value.TryWriteBytes(_buffer.GetSpan(16), bigEndian: true, out var written) || written != 16)
        {
            throw new InvalidOperationException("Could not write GUID bytes.");
        }

        _buffer.Advance(16);
        return this;
    }

    /// <summary>Writes the exact string as strict UTF-8 (no normalization). Lone surrogates are rejected.</summary>
    public CanonicalWriter WriteString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        byte[] bytes;
        try
        {
            bytes = StrictUtf8.GetBytes(value);
        }
        catch (EncoderFallbackException ex)
        {
            throw new ArgumentException("String is not valid Unicode and has no canonical UTF-8 form.", nameof(value), ex);
        }

        return WriteBytes(bytes);
    }

    public CanonicalWriter WriteBytes(ReadOnlySpan<byte> value)
    {
        WriteInt32(value.Length);
        value.CopyTo(_buffer.GetSpan(value.Length));
        _buffer.Advance(value.Length);
        return this;
    }

    public byte[] ToArray() => _buffer.WrittenSpan.ToArray();
}
