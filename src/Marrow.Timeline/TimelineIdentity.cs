using System.Globalization;

namespace Marrow.Timeline;

/// <summary>
/// The one addressable history identity. Identifies an original source OR a child branch; both are
/// read with the same Restore/Replay calls and continued with the same Append call.
/// </summary>
public readonly record struct TimelineId(Guid Value);

/// <summary>
/// Nonnegative logical position. 0 is the initial state before any step; the first step is 1.
/// Browser wire form is a canonical decimal string (no sign, no leading zeros, no fraction).
/// </summary>
public readonly record struct TimelinePosition
{
    public TimelinePosition(long value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        Value = value;
    }

    public long Value { get; }

    public static TimelinePosition Initial => default;

    public string ToWireString() => Value.ToString(CultureInfo.InvariantCulture);

    public static bool TryParseWire(string? text, out TimelinePosition position)
    {
        position = default;
        if (string.IsNullOrEmpty(text) || (text.Length > 1 && text[0] == '0'))
        {
            return false;
        }

        foreach (var c in text)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        position = new TimelinePosition(value);
        return true;
    }

    public override string ToString() => ToWireString();
}
