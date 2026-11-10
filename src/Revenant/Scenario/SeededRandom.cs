namespace Revenant.Scenario;

/// <summary>
/// SplitMix64. Implemented here because <see cref="Random"/> does not promise the same sequence across
/// runtime versions, and the scenario facts must be byte-stable for a pinned scenario version.
/// </summary>
internal sealed class SeededRandom
{
    private ulong _state;

    private SeededRandom(ulong state) => _state = state;

    /// <summary>Independent stream for <paramref name="seed"/>; different stream ids do not share draws.</summary>
    public static SeededRandom ForStream(long seed, ulong streamId)
    {
        var random = new SeededRandom(unchecked((ulong)seed) ^ unchecked(streamId * 0xD6E8FEB86659FD93UL));
        random.NextUInt64();
        return random;
    }

    public ulong NextUInt64()
    {
        unchecked
        {
            _state += 0x9E3779B97F4A7C15UL;
            var z = _state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    /// <summary>Uniform integer in [minInclusive, maxExclusive) by rejection sampling.</summary>
    public int NextInt(int minInclusive, int maxExclusive)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(minInclusive, maxExclusive);
        var range = (ulong)((long)maxExclusive - minInclusive);
        var limit = ulong.MaxValue - (ulong.MaxValue % range);
        ulong value;
        do
        {
            value = NextUInt64();
        }
        while (value >= limit);

        return (int)(minInclusive + (long)(value % range));
    }

    /// <summary>Fisher-Yates shuffle in place.</summary>
    public void Shuffle<T>(T[] items)
    {
        for (var i = items.Length - 1; i > 0; i--)
        {
            var j = NextInt(0, i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }
}
