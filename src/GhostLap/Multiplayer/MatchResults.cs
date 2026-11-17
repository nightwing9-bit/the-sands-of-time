using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GhostLap.Contracts.Multiplayer;
using GhostLap.Simulation;
using Marrow.Kernel.Canonical;

namespace GhostLap.Multiplayer;

/// <summary>Per-slot counters a result needs that <see cref="PlayerState"/> does not carry, derived from the resolved event stream.</summary>
public readonly record struct SlotStats(int TimesHitByBurst, int ContactDrainsSuffered, int ChargeLostToContact);

/// <summary>
/// Accumulates <see cref="SlotStats"/> from each tick's resolved events (task 10.4). Written only by the match loop after a tick resolves; it is a
/// pure fold over the deterministic event stream, so equal applied inputs give equal stats. Not an archive of anything.
/// </summary>
public sealed class MpMatchStats
{
    private readonly int[] _timesHit;
    private readonly int[] _contacts;
    private readonly int[] _chargeLost;

    public MpMatchStats(int players)
    {
        _timesHit = new int[players];
        _contacts = new int[players];
        _chargeLost = new int[players];
    }

    public int PlayerCount => _timesHit.Length;

    public void Add(ImmutableArray<GameEvent> events)
    {
        foreach (var e in events)
        {
            switch (e.Kind)
            {
                case GameEventKind.BurstHit:
                    _timesHit[e.Slot]++;
                    break;
                case GameEventKind.ContactDrain:
                    _contacts[e.Slot]++;
                    _chargeLost[e.Slot] += e.Value;
                    break;
            }
        }
    }

    public SlotStats For(int slot) => new(_timesHit[slot], _contacts[slot], _chargeLost[slot]);
}

/// <summary>A built result: the document and its canonical digest.</summary>
public sealed record MatchResultRecord(MatchResultDocument Document, string Digest)
{
    public Guid MatchId => Guid.ParseExact(Document.MatchId, "N");

    /// <summary>The <c>MatchFinalizedV1</c> outbox payload (JSON, camelCase).</summary>
    public string PayloadJson() => MatchResults.ToJson(new MatchFinalizedV1(Document.MatchId, Digest, Document));
}

/// <summary>
/// Builds, encodes and checks the durable multiplayer result (task 10.4, ADR-P1-22 D11/D12/D13, ADR-P1-26). Pure: no clock, no I/O.
/// <list type="bullet">
/// <item>Outcome: a slot forfeited by tick 2700 is a forfeit; every other slot (including one still in grace) is a finisher.</item>
/// <item>Placement (<c>mp-placement-v1</c>): competition ranking, 1 + the number of players strictly ahead, where every finisher is ahead of every
/// forfeit and, within a class, a higher total is ahead of a lower one. Equal totals in a class share a placement. Slot order never breaks a tie.</item>
/// <item>Eligibility (<c>mp-eligibility-v1</c>): rated only with at least one finisher and at least two distinct guest principals in the sealed
/// roster. A service-side abort never reaches this code (it has no result at all).</item>
/// <item>The rating algorithm id is the one sealed at countdown; it is copied, never chosen here.</item>
/// </list>
/// </summary>
public static class MatchResults
{
    private const string GuestPrefix = "guest:";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static MatchResultRecord Build(SealedRoundConfig config, MpMatchState final, MpMatchStats stats)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(final);
        ArgumentNullException.ThrowIfNull(stats);
        if (!final.IsFinal || final.Tick != config.TargetTicks)
        {
            throw new ArgumentException("A result is built only from the resolved final tick.", nameof(final));
        }
        var n = config.Slots.Count;
        if (final.World.Players.Length != n || stats.PlayerCount != n)
        {
            throw new ArgumentException("State, stats and roster disagree on the player count.", nameof(final));
        }

        var finisher = new bool[n];
        var totals = new int[n];
        for (var slot = 0; slot < n; slot++)
        {
            finisher[slot] = !final.IsForfeited(slot);
            totals[slot] = final.World.Players[slot].TotalScore;
        }
        var players = new MatchPlayerResult[n];
        for (var slot = 0; slot < n; slot++)
        {
            var ahead = 0;
            for (var other = 0; other < n; other++)
            {
                if (other != slot && Ahead(finisher[other], totals[other], finisher[slot], totals[slot]))
                {
                    ahead++;
                }
            }
            var p = final.World.Players[slot];
            var s = stats.For(slot);
            var sealedSlot = config.Slots[slot];
            players[slot] = new MatchPlayerResult(
                slot, sealedSlot.MemberId, sealedSlot.Subject,
                finisher[slot] ? MatchResultContract.OutcomeFinisher : MatchResultContract.OutcomeForfeit,
                final.ForfeitTicks[slot], 1 + ahead, p.TotalScore, p.MovementPoints, p.PickupPoints, p.PenaltyPoints, p.DistanceFixed,
                p.Pickups, p.Charge, p.Bursts, p.BurstHits, s.TimesHitByBurst, s.ContactDrainsSuffered, s.ChargeLostToContact);
        }

        var guests = config.Slots.Select(x => x.Subject).Where(x => x.StartsWith(GuestPrefix, StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).Count();
        var reason = !finisher.Any(f => f)
            ? MatchResultContract.ReasonNoFinisher
            : guests < 2 ? MatchResultContract.ReasonTooFewGuestPrincipals : MatchResultContract.ReasonEligible;

        var document = new MatchResultDocument(
            MatchResultContract.ResultSchema,
            config.MatchId.ToString("N"),
            config.RoomId.ToString("N"),
            config.RoundOrdinal,
            config.AuthorityGeneration.ToString("N"),
            config.ConfigDigest,
            config.RulesId,
            config.LayoutId,
            config.RulesHash,
            config.LayoutFingerprint,
            config.Seed.ToString(CultureInfo.InvariantCulture),
            config.TargetTicks,
            final.Tick,
            MpStateCodec.Hash(final),
            MatchResultContract.PlacementPolicy,
            MatchResultContract.EligibilityPolicy,
            reason == MatchResultContract.ReasonEligible,
            reason,
            config.RatingAlgorithmId,
            players);
        return new MatchResultRecord(document, Digest(document));
    }

    /// <summary>True when a player (class, total) ranks strictly ahead of another.</summary>
    private static bool Ahead(bool finisherA, int totalA, bool finisherB, int totalB) =>
        finisherA != finisherB ? finisherA : totalA > totalB;

    /// <summary>
    /// SHA-256 (lower-case hex) over (schema, canonical encoding): every field in declaration order with fixed-width integers and length-prefixed
    /// UTF-8 strings (<see cref="CanonicalWriter"/>), players in slot order. Independent of JSON formatting and property order.
    /// </summary>
    public static string Digest(MatchResultDocument d)
    {
        ArgumentNullException.ThrowIfNull(d);
        var w = new CanonicalWriter()
            .WriteString(d.Schema).WriteString(d.MatchId).WriteString(d.RoomId).WriteInt32(d.RoundOrdinal).WriteString(d.GenerationId)
            .WriteString(d.ConfigDigest).WriteString(d.RulesId).WriteString(d.LayoutId).WriteString(d.RulesHash).WriteString(d.LayoutFingerprint)
            .WriteString(d.Seed).WriteInt32(d.TargetTicks).WriteInt64(d.FinalTick).WriteString(d.FinalStateHash).WriteString(d.PlacementPolicy)
            .WriteString(d.EligibilityPolicy).WriteBoolean(d.RatedEligible).WriteString(d.EligibilityReason).WriteString(d.RatingAlgorithmId)
            .WriteCount(d.Players.Count);
        foreach (var p in d.Players)
        {
            w.WriteInt32(p.Slot).WriteString(p.MemberId).WriteString(p.Subject).WriteString(p.Outcome).WriteInt64(p.ForfeitTick)
                .WriteInt32(p.CompetitivePlacement).WriteInt32(p.TotalScore).WriteInt32(p.MovementPoints).WriteInt32(p.PickupPoints)
                .WriteInt32(p.PenaltyPoints).WriteInt64(p.DistanceFixed).WriteInt32(p.Pickups).WriteInt32(p.FinalCharge).WriteInt32(p.Bursts)
                .WriteInt32(p.BurstHits).WriteInt32(p.TimesHitByBurst).WriteInt32(p.ContactDrainsSuffered).WriteInt32(p.ChargeLostToContact);
        }
        return Convert.ToHexStringLower(SHA256.HashData(w.ToArray()));
    }

    public static string ToJson(MatchFinalizedV1 payload) => JsonSerializer.Serialize(payload, Json);

    public static MatchFinalizedV1 PayloadFromJson(string json) =>
        JsonSerializer.Deserialize<MatchFinalizedV1>(json, Json) ?? throw new FormatException("Empty MatchFinalizedV1 payload.");

    public static string DocumentToJson(MatchResultDocument document) => JsonSerializer.Serialize(document, Json);

    public static MatchResultDocument DocumentFromJson(string json) =>
        JsonSerializer.Deserialize<MatchResultDocument>(json, Json) ?? throw new FormatException("Empty result document.");

    /// <summary>
    /// The outbox event id of a match's <c>MatchFinalizedV1</c>: derived from the match id (SHA-256, RFC 9562 version 8 layout), so a retried
    /// finalization can only ever produce the same id and the outbox primary key is a second uniqueness guard.
    /// </summary>
    public static Guid FinalizedEventId(Guid matchId)
    {
        var bytes = SHA256.HashData(Encoding.ASCII.GetBytes(MatchResultContract.FinalizedEventType + "/" + matchId.ToString("N")));
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes.AsSpan(0, 16), bigEndian: true);
    }

    /// <summary>The members' view of a durable result: no guest principal ids.</summary>
    public static MatchOutcomeView FinalizedView(MatchResultDocument d, string digest) => new(
        d.MatchId, MatchOutcomeDecisions.Finalized, null, digest, d.RatedEligible, d.EligibilityReason, d.RatingAlgorithmId,
        [.. d.Players.Select(p => new MatchPlayerResultView(
            p.Slot, p.MemberId, p.Outcome, p.ForfeitTick, p.CompetitivePlacement, p.TotalScore, p.MovementPoints, p.PickupPoints, p.PenaltyPoints,
            p.Pickups, p.Bursts, p.BurstHits, p.TimesHitByBurst, p.ContactDrainsSuffered))]);
}
