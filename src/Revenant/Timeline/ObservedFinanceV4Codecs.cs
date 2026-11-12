using System.Collections.Immutable;
using System.Security.Cryptography;
using Marrow.Kernel.Canonical;
using Marrow.Timeline;
using Revenant.Contracts;
using Revenant.Finance;
using Revenant.Intelligence;
using Revenant.Scenario;

namespace Revenant.Timeline;

internal static class ObservedFinanceEncoding
{
    internal static void ReadFormat(ref CanonicalReader r)
    {
        if (r.ReadInt32() != 1)
        {
            throw new CanonicalFormatException("Unsupported V4 canonical format.");
        }
    }

    internal static bool IsHash(string? value) =>
        value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal const string ManifestHashPrefix = "sha256:";

    internal static bool IsManifestHash(string? value) =>
        value is not null && value.StartsWith(ManifestHashPrefix, StringComparison.Ordinal) && IsHash(value[ManifestHashPrefix.Length..]);

    internal static string ReadHash(ref CanonicalReader r)
    {
        var bytes = r.ReadBytes();
        return bytes.Length == 32 ? Convert.ToHexStringLower(bytes) : throw new CanonicalFormatException("Expected a 32-byte SHA-256.");
    }

    internal static string Hash(CanonicalWriter w) => Convert.ToHexStringLower(SHA256.HashData(w.ToArray()));
}

public sealed class ObservedFinanceContextCodec : IContextCodec<ObservedFinanceContext>
{
    public static ObservedFinanceContextCodec Instance { get; } = new();

    public byte[] EncodeCanonical(ObservedFinanceContext c)
    {
        var w = new CanonicalWriter().WriteInt32(1).WriteString(ObservedFinanceVersion.ReducerVersion)
            .WriteString(c.ScenarioVersion).WriteBytes(Convert.FromHexString(c.ManifestSha256[ObservedFinanceEncoding.ManifestHashPrefix.Length..]))
            .WriteString(ObservedFinanceVersion.SchedulerContract).WriteString(ObservedFinanceVersion.ObservationContract)
            .WriteInt64(c.OriginMs).WriteInt64(ObservedFinanceVersion.BucketMs)
            .WriteString(c.BasePolicy.PolicyId).WriteString(c.BasePolicy.ConfigHash).WriteString(c.BasePolicy.ModelArtifactHash)
            .WriteString(c.BasePolicy.FeatureSchemaHash).WriteString(c.ReviewPolicy).WriteByte((byte)c.RiskMode).WriteString(c.RiskFeatureSchema);
        if (c.RiskModel is { } model)
        {
            w.WriteString(model.ModelId).WriteBytes(Convert.FromHexString(model.ArtifactSha256))
                .WriteString(RiskScoreNormalization.NormalizationId).WriteInt32(model.ThresholdPpm);
        }
        WriteModels(w, c.AnomalyModels);
        WriteModels(w, c.LiquidityModels);
        return w.ToArray();
    }

    public ObservedFinanceContext Decode(ReadOnlySpan<byte> bytes)
    {
        var r = new CanonicalReader(bytes);
        ObservedFinanceEncoding.ReadFormat(ref r);
        var family = r.ReadString();
        var scenario = r.ReadString();
        var manifest = ObservedFinanceEncoding.ManifestHashPrefix + ObservedFinanceEncoding.ReadHash(ref r);
        var scheduler = r.ReadString();
        var observation = r.ReadString();
        var origin = r.ReadInt64();
        var cadence = r.ReadInt64();
        if (family != ObservedFinanceVersion.ReducerVersion || scheduler != ObservedFinanceVersion.SchedulerContract
            || observation != ObservedFinanceVersion.ObservationContract || cadence != ObservedFinanceVersion.BucketMs)
        {
            throw new CanonicalFormatException("Unknown V4 family, contracts or cadence.");
        }

        var policy = new PolicyRef(r.ReadString(), r.ReadString(), r.ReadString(), r.ReadString());
        var review = r.ReadString();
        var mode = (ObservedRiskMode)r.ReadByte();
        var features = r.ReadString();
        ObservedRiskPin? risk = null;
        if (mode == ObservedRiskMode.ModelRequired)
        {
            var modelId = r.ReadString();
            var artifact = ObservedFinanceEncoding.ReadHash(ref r);
            if (r.ReadString() != RiskScoreNormalization.NormalizationId)
            {
                throw new CanonicalFormatException("Unknown V4 risk score normalization.");
            }
            risk = new(modelId, artifact, r.ReadInt32());
        }

        var context = new ObservedFinanceContext(scenario, manifest, origin, policy, review, mode, features, risk, ReadModels(ref r), ReadModels(ref r));
        r.EnsureEnd();
        return context;
    }

    private static void WriteModels(CanonicalWriter w, ImmutableArray<ObservedModelPin> models)
    {
        w.WriteCount(models.Length);
        foreach (var model in models)
        {
            w.WriteString(model.ModelId).WriteBytes(Convert.FromHexString(model.ArtifactSha256));
        }
    }

    private static ImmutableArray<ObservedModelPin> ReadModels(ref CanonicalReader r)
    {
        var models = ImmutableArray.CreateBuilder<ObservedModelPin>();
        var count = r.ReadCount();
        string? previous = null;
        for (var i = 0; i < count; i++)
        {
            var id = r.ReadString();
            if (string.CompareOrdinal(previous, id) >= 0)
            {
                throw new CanonicalFormatException("V4 model identities must be strictly ordinal-sorted.");
            }
            models.Add(new(id, ObservedFinanceEncoding.ReadHash(ref r)));
            previous = id;
        }
        return models.ToImmutable();
    }
}

public sealed class ObservedFinanceStateCodec : IStateCodec<ObservedFinanceState>
{
    public static ObservedFinanceStateCodec Instance { get; } = new();

    public byte[] EncodeCanonical(ObservedFinanceState s)
    {
        Validate(s);
        var w = new CanonicalWriter().WriteInt32(1).WriteBytes(LedgerFinanceStateCodec.Instance.EncodeCanonical(s.Ledger))
            .WriteInt64(s.LastEffectiveTimeMs).WriteInt64(s.LastSealedBoundaryMs).WriteBytes(Convert.FromHexString(s.PrefixDigest));
        // Optional extension only for states that have committed a post-warmup model Screening.
        if (s.ReviewCounts != default)
            w.WriteString(V4ReviewCounts.Policy).WriteInt64(s.ReviewCounts.Eligible)
                .WriteInt64(s.ReviewCounts.Mandatory).WriteInt64(s.ReviewCounts.Learned);
        return w.ToArray();
    }

    public ObservedFinanceState Decode(ReadOnlySpan<byte> bytes)
    {
        var r = new CanonicalReader(bytes);
        ObservedFinanceEncoding.ReadFormat(ref r);
        var legacyBytes = r.ReadBytes();
        var ledger = LedgerFinanceStateCodec.Instance.Decode(legacyBytes);
        if (!legacyBytes.SequenceEqual(LedgerFinanceStateCodec.Instance.EncodeCanonical(ledger)))
        {
            throw new CanonicalFormatException("Noncanonical inherited V4 ledger bytes.");
        }
        var state = new ObservedFinanceState(ledger, r.ReadInt64(), r.ReadInt64(), ObservedFinanceEncoding.ReadHash(ref r));
        if (r.Remaining != 0)
        {
            if (r.ReadString() != V4ReviewCounts.Policy)
                throw new CanonicalFormatException("Unknown V4 capacity state extension.");
            state = state with { ReviewCounts = new(r.ReadInt64(), r.ReadInt64(), r.ReadInt64()) };
            if (state.ReviewCounts == default)
                throw new CanonicalFormatException("Redundant V4 capacity extension.");
        }
        r.EnsureEnd();
        Validate(state);
        return state;
    }

    private static void Validate(ObservedFinanceState s)
    {
        if (s.RunId.IsEmpty || !ObservedFinanceEncoding.IsHash(s.PrefixDigest) || !ObservedFinanceEncoding.IsManifestHash(s.Ledger.ManifestContentHash)
            || s.LastFact < 0 || !s.ReviewCounts.Valid || s.ReviewCounts.Eligible > s.LastFact || !ObservedFinanceVersion.IsBoundary(s.LastSealedBoundaryMs)
            || s.LastEffectiveTimeMs < s.LastSealedBoundaryMs
            || (s.LastSealedBoundaryMs <= long.MaxValue - ObservedFinanceVersion.BucketMs
                && s.LastEffectiveTimeMs >= ObservedFinanceVersion.NextBoundary(s.LastSealedBoundaryMs))
            || (s.LastFact == 0 && (s.LastEffectiveTimeMs != s.LastSealedBoundaryMs || s.PrefixDigest != ObservedFinanceReducer.InitialDigest(s.RunId))))
        {
            throw new CanonicalFormatException("Invalid V4 state position, clock or digest.");
        }
    }
}

public static class ObservationSealCodec
{
    public static byte[] Encode(ObservationSealV1 seal)
    {
        Validate(seal);
        return new CanonicalWriter().WriteInt32(1).WriteString(ObservedFinanceVersion.SealSchema).WriteGuid(seal.RunId.Value)
            .WriteInt64(seal.BoundaryMs).WriteInt64(seal.IntervalStartMs).WriteInt64(seal.PrefixFactSeq)
            .WriteBytes(Convert.FromHexString(seal.PrefixFactDigestSha256)).WriteBytes(Convert.FromHexString(seal.ResolvedCommandDigestSha256))
            .ToArray();
    }

    public static ObservationSealV1 Decode(ReadOnlySpan<byte> bytes)
    {
        var r = new CanonicalReader(bytes);
        ObservedFinanceEncoding.ReadFormat(ref r);
        if (r.ReadString() != ObservedFinanceVersion.SealSchema)
        {
            throw new CanonicalFormatException("Unknown observation seal schema.");
        }
        var seal = new ObservationSealV1(new(r.ReadGuid()), r.ReadInt64(), r.ReadInt64(), r.ReadInt64(),
            ObservedFinanceEncoding.ReadHash(ref r), ObservedFinanceEncoding.ReadHash(ref r));
        r.EnsureEnd();
        Validate(seal);
        return seal;
    }

    private static void Validate(ObservationSealV1 s)
    {
        if (s.RunId.IsEmpty || s.PrefixFactSeq is < 0 or long.MaxValue || !ObservedFinanceVersion.IsBoundary(s.BoundaryMs)
            || s.BoundaryMs < long.MinValue + ObservedFinanceVersion.BucketMs
            || s.IntervalStartMs != s.BoundaryMs - ObservedFinanceVersion.BucketMs
            || !ObservedFinanceEncoding.IsHash(s.PrefixFactDigestSha256) || !ObservedFinanceEncoding.IsHash(s.ResolvedCommandDigestSha256))
        {
            throw new CanonicalFormatException("Invalid observation seal.");
        }
    }
}

public sealed class ObservedFinanceInputCodec : IInputCodec<ObservedFinanceInput>
{
    public static ObservedFinanceInputCodec Instance { get; } = new();

    public byte[] EncodeCanonical(ObservedFinanceInput input)
    {
        Validate(input);
        var w = new CanonicalWriter().WriteInt32(1).WriteGuid(input.RunId.Value).WriteInt64(input.Sequence)
            .WriteInt64(input.EffectiveTimeMs).WriteByte((byte)input.Kind).WriteString(input.PayloadSchema).WriteBytes(input.Payload.AsSpan())
            .WriteBoolean(input.EvidenceSchema is not null);
        if (input.EvidenceSchema is { } evidence)
        {
            w.WriteString(evidence).WriteBytes(Convert.FromHexString(input.EvidenceSha256!));
        }
        return w.ToArray();
    }

    public ObservedFinanceInput Decode(ReadOnlySpan<byte> bytes)
    {
        var r = new CanonicalReader(bytes);
        ObservedFinanceEncoding.ReadFormat(ref r);
        var input = new ObservedFinanceInput(new(r.ReadGuid()), r.ReadInt64(), r.ReadInt64(), (ObservedFactKind)r.ReadByte(),
            r.ReadString(), [.. r.ReadBytes()]);
        if (r.ReadBoolean())
        {
            input = input with { EvidenceSchema = r.ReadString(), EvidenceSha256 = ObservedFinanceEncoding.ReadHash(ref r) };
        }
        r.EnsureEnd();
        Validate(input);
        return input;
    }

    internal static void Validate(ObservedFinanceInput input)
    {
        if (input.RunId.IsEmpty || input.Sequence < 1 || !Enum.IsDefined(input.Kind) || string.IsNullOrWhiteSpace(input.PayloadSchema)
            || input.Payload.IsDefaultOrEmpty || (input.EvidenceSchema is null ? input.EvidenceSha256 is not null
                : string.IsNullOrWhiteSpace(input.EvidenceSchema) || !ObservedFinanceEncoding.IsHash(input.EvidenceSha256)))
        {
            throw new CanonicalFormatException("Invalid V4 fact envelope.");
        }

        if (input.Kind == ObservedFactKind.ObservationSeal)
        {
            var seal = ObservationSealCodec.Decode(input.Payload.AsSpan());
            if (input.PayloadSchema != ObservedFinanceVersion.SealSchema || input.EvidenceSchema is not null || seal.RunId != input.RunId
                || seal.PrefixFactSeq + 1 != input.Sequence || seal.BoundaryMs != input.EffectiveTimeMs)
            {
                throw new CanonicalFormatException("Observation seal does not match its V4 envelope.");
            }
        }
        else if (input.PayloadSchema == ObservedFinanceVersion.SealSchema)
        {
            throw new CanonicalFormatException("Observation seal schema requires the seal kind.");
        }
        else if (input.Kind == ObservedFactKind.PaymentIntent && (input.PayloadSchema != ScenarioSchemas.InputSchemaVersion
            || PaymentIntentCodec.Instance.Decode(input.Payload.AsSpan()).RunId != input.RunId))
        {
            throw new CanonicalFormatException("Payment intent does not match its V4 envelope.");
        }
    }
}
