using System.Buffers.Binary;
using System.Text;

namespace Marrow.Kernel.Canonical;

/// <summary>Malformed canonical bytes (truncated, trailing, out-of-range or invalid UTF-8).</summary>
public sealed class CanonicalFormatException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// Strict reader for <see cref="CanonicalWriter"/> output. Any deviation from the canonical form
/// throws <see cref="CanonicalFormatException"/>; callers map it to a corrupt-history failure.
/// Call <see cref="EnsureEnd"/> after the last field so trailing bytes are rejected.
/// </summary>
public ref struct CanonicalReader(ReadOnlySpan<byte> bytes)
{
    private ReadOnlySpan<byte> _remaining = bytes;

    public readonly int Remaining => _remaining.Length;

    public byte ReadByte() => Take(1)[0];

    public bool ReadBoolean() => ReadByte() switch
    {
        0 => false,
        1 => true,
        var b => throw new CanonicalFormatException($"Boolean byte must be 0 or 1, found {b}."),
    };

    public int ReadInt32() => BinaryPrimitives.ReadInt32BigEndian(Take(4));

    public long ReadInt64() => BinaryPrimitives.ReadInt64BigEndian(Take(8));

    public int ReadCount()
    {
        var count = ReadInt32();
        return count >= 0 ? count : throw new CanonicalFormatException($"Negative count {count}.");
    }

    public Guid ReadGuid() => new(Take(16), bigEndian: true);

    public string ReadString()
    {
        var bytes = ReadBytes();
        try
        {
            return CanonicalWriter.StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException ex)
        {
            throw new CanonicalFormatException("String bytes are not valid UTF-8.", ex);
        }
    }

    public ReadOnlySpan<byte> ReadBytes()
    {
        var length = ReadInt32();
        if (length < 0)
        {
            throw new CanonicalFormatException($"Negative length {length}.");
        }

        return Take(length);
    }

    public readonly void EnsureEnd()
    {
        if (!_remaining.IsEmpty)
        {
            throw new CanonicalFormatException($"{_remaining.Length} unexpected trailing byte(s).");
        }
    }

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count > _remaining.Length)
        {
            throw new CanonicalFormatException($"Truncated input: needed {count} byte(s), {_remaining.Length} left.");
        }

        var slice = _remaining[..count];
        _remaining = _remaining[count..];
        return slice;
    }
}
