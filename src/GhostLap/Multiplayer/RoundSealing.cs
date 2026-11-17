using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using GhostLap.Rules;

namespace GhostLap.Multiplayer;

public sealed record RosterEntry(string MemberId, string Subject);

public sealed record SealedSlot(int Slot, string MemberId, string Subject, int SpawnXFixed, int SpawnYFixed);

/// <summary>
/// Everything that is fixed before kickoff (design section 1): rules/layout identity, seed, slot and spawn assignment, the opaque
/// rating algorithm id and the prospective match id. Equal inputs always give an equal value (including <see cref="ConfigDigest"/>),
/// so an idempotent start retry or a restart after a cancelled countdown re-seals the identical round.
/// </summary>
public sealed record SealedRoundConfig(
    Guid RoomId,
    int RoundOrdinal,
    Guid MatchId,
    string RulesId,
    string LayoutId,
    string RulesHash,
    string LayoutFingerprint,
    long Seed,
    string RatingAlgorithmId,
    int TargetTicks,
    int CountdownMs,
    Guid AuthorityGeneration,
    IReadOnlyList<SealedSlot> Slots,
    string ConfigDigest);

/// <summary>
/// Deterministic sealing from a per-room secret the players never see.
/// <list type="bullet">
/// <item>The seed depends only on (secret, round ordinal): cancelling and restarting a countdown cannot re-roll the candle schedule.</item>
/// <item>Slot order is the order of per-principal keyed hashes (secret, ordinal, subject). It does not depend on join order, host choice,
/// ready order, countdown count or request ids, and the relative order of two principals is the same in every roster that contains both.
/// A player therefore cannot improve their slot by cancelling, restarting, re-ordering joins or leaving and rejoining; only excluding
/// other players changes who is in the roster, and that is an ordinary roster decision.</item>
/// <item>The match id is derived from the digest, so one match id never names two different rosters/configs.</item>
/// </list>
/// The authority generation is non-durable in task 10.1 (a process-local id); task 10.2 replaces it with the durable generation.
/// </summary>
public static class RoundSealing
{
    public const string ConfigVersion = "ghost-mp-round-config/1";

    public static SealedRoundConfig Seal(
        ReadOnlySpan<byte> roomSecret,
        Guid roomId,
        int roundOrdinal,
        IReadOnlyCollection<RosterEntry> roster,
        MultiplayerOptions options,
        Guid authorityGeneration)
    {
        if (roomSecret.Length < 32)
        {
            throw new ArgumentException("The room secret must be at least 32 bytes.", nameof(roomSecret));
        }
        if (roster.Count is < MultiplayerOptions.MinCompetitorsFixed or > MultiplayerOptions.MaxCompetitorsCeiling)
        {
            throw new ArgumentOutOfRangeException(nameof(roster), roster.Count, "A round has 2 to 4 competitors.");
        }
        if (roster.Select(r => r.Subject).Distinct(StringComparer.Ordinal).Count() != roster.Count)
        {
            throw new ArgumentException("Competitors must be distinct principals.", nameof(roster));
        }

        var secret = roomSecret.ToArray();
        var ordinal = Encoding.ASCII.GetBytes(roundOrdinal.ToString(CultureInfo.InvariantCulture));
        var seedBytes = Mac(secret, "seed/1", ordinal);
        var seed = (long)(BitConverter.ToUInt64(seedBytes, 0) & long.MaxValue);

        var ordered = roster
            .Select(r => (Entry: r, Key: Mac(secret, "slot/1", ordinal, Encoding.UTF8.GetBytes(r.Subject))))
            .OrderBy(x => x.Key, ByteComparer.Instance)
            .ThenBy(x => x.Entry.Subject, StringComparer.Ordinal)
            .Select(x => x.Entry)
            .ToArray();
        var sockets = ArenaV1.SpawnSockets(ordered.Length);
        var slots = ordered
            .Select((entry, slot) => new SealedSlot(slot, entry.MemberId, entry.Subject, sockets[slot].X, sockets[slot].Y))
            .ToArray();

        var digest = Digest(roomId, roundOrdinal, seed, slots, options, authorityGeneration);
        var matchBytes = Mac(secret, "match/1", Encoding.ASCII.GetBytes(digest));
        var matchId = new Guid(matchBytes.AsSpan(0, 16), bigEndian: true);
        return new SealedRoundConfig(
            roomId, roundOrdinal, matchId, MpRulesProfile.RulesId, MpRulesProfile.LayoutId, MpRulesProfile.RulesHash,
            MpRulesProfile.LayoutFingerprint, seed, options.RatingAlgorithmId, MpRulesProfile.TargetTicks, options.CountdownMilliseconds,
            authorityGeneration, slots, digest);
    }

    private static string Digest(Guid roomId, int ordinal, long seed, SealedSlot[] slots, MultiplayerOptions options, Guid generation)
    {
        var text = new StringBuilder();
        text.Append(ConfigVersion).Append('\n')
            .Append("room=").Append(roomId.ToString("N")).Append('\n')
            .Append("ordinal=").Append(ordinal.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append("rules=").Append(MpRulesProfile.RulesHash).Append('\n')
            .Append("layout=").Append(MpRulesProfile.LayoutId).Append('\n')
            .Append("layoutFingerprint=").Append(MpRulesProfile.LayoutFingerprint).Append('\n')
            .Append("seed=").Append(seed.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append("rating=").Append(options.RatingAlgorithmId).Append('\n')
            .Append("ticks=").Append(MpRulesProfile.TargetTicks.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append("countdownMs=").Append(options.CountdownMilliseconds.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append("generation=").Append(generation.ToString("N")).Append('\n');
        foreach (var s in slots)
        {
            text.Append("slot=").Append(s.Slot.ToString(CultureInfo.InvariantCulture))
                .Append('|').Append(s.Subject)
                .Append('|').Append(s.SpawnXFixed.ToString(CultureInfo.InvariantCulture))
                .Append(',').Append(s.SpawnYFixed.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static byte[] Mac(ReadOnlySpan<byte> key, string purpose, params byte[][] parts)
    {
        var data = new List<byte>(Encoding.ASCII.GetBytes(purpose));
        foreach (var part in parts)
        {
            data.Add(0);
            data.AddRange(part);
        }
        return HMACSHA256.HashData(key, data.ToArray());
    }

    private sealed class ByteComparer : IComparer<byte[]>
    {
        public static readonly ByteComparer Instance = new();

        public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y.AsSpan());
    }
}
