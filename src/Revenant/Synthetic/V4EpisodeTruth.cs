using Marrow.Kernel.Canonical;
using Revenant.Observations;

namespace Revenant.Synthetic;

internal enum EpisodeAttack : byte { Ring = 1, Takeover = 2, Dispersal = 3, CoordinatedBurst = 4, BeneficiaryAbuse = 5 }
internal enum EpisodeAnomaly : byte { OperationalBurst = 1, BehaviorDeviation = 2, CoordinatedActivity = 3, None = 4 }

// This annotation travels with the offline exporter only. The projector and executor have no truth parameter.
internal sealed record V4PaymentTruth(string EpisodeKey, string IntentId, bool Malicious, EpisodeAttack? Attack,
    string? CampaignKey, bool HardNegative)
{
    internal const string Schema = "episode-truth/1";

    internal byte[] Encode()
    {
        if (string.IsNullOrWhiteSpace(EpisodeKey) || string.IsNullOrWhiteSpace(IntentId)
            || Malicious != Attack.HasValue || Malicious != !string.IsNullOrWhiteSpace(CampaignKey)
            || (Attack is { } family && !Enum.IsDefined(family)) || (Malicious && HardNegative))
        {
            throw new CanonicalFormatException("Invalid offline payment truth.");
        }
        var w = ObservationEncoding.Begin(Schema).WriteByte(1).WriteString(EpisodeKey).WriteString(IntentId)
            .WriteBoolean(Malicious).WriteBoolean(HardNegative);
        if (Attack is { } attack)
        {
            w.WriteByte((byte)attack).WriteString(CampaignKey!);
        }
        return w.ToArray();
    }
}

internal sealed record V4AnomalyTruth(string EpisodeKey, string AccountId, long IntervalStartMs, long BoundaryMs,
    EpisodeAnomaly Family, bool Anomalous)
{
    internal byte[] Encode()
    {
        if (string.IsNullOrWhiteSpace(EpisodeKey) || string.IsNullOrWhiteSpace(AccountId)
            || BoundaryMs != checked(IntervalStartMs + Timeline.ObservedFinanceVersion.BucketMs)
            || !Timeline.ObservedFinanceVersion.IsBoundary(BoundaryMs)
            || !Enum.IsDefined(Family) || Anomalous == (Family == EpisodeAnomaly.None))
        {
            throw new CanonicalFormatException("Invalid offline anomaly truth.");
        }
        return ObservationEncoding.Begin(V4PaymentTruth.Schema).WriteByte(2).WriteString(EpisodeKey).WriteString(AccountId)
            .WriteInt64(IntervalStartMs).WriteInt64(BoundaryMs).WriteByte((byte)Family).WriteBoolean(Anomalous).ToArray();
    }
}
