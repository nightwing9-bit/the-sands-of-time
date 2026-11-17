namespace GhostLap.Simulation;

[Flags]
internal enum CompetitivePhases
{
    Legacy = 0,
    Pickups = 1,
    Contact = 2,
    Burst = 4,
    All = Pickups | Contact | Burst,
}

internal static class SeededContest
{
    // The mixer is a permutation on ulong. For the same contest, distinct slots have distinct ranks.
    internal static ulong Priority(long seed, long tick, int site, int slot) =>
        Mix(Mix(unchecked((ulong)seed) ^ 0xA7C80F13436D892BUL) ^ (((ulong)tick << 8) | (uint)site) ^ ((ulong)slot << 56));

    private static ulong Mix(ulong n)
    {
        unchecked
        {
            n = (n ^ (n >> 30)) * 0xBF58476D1CE4E5B9UL;
            n = (n ^ (n >> 27)) * 0x94D049BB133111EBUL;
            return n ^ (n >> 31);
        }
    }
}
