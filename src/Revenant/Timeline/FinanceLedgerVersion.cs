using System.Security.Cryptography;
using Marrow.Kernel.Canonical;

namespace Revenant.Timeline;

/// <summary>
/// Identity of the additive finance history version for NEWLY created durable runs (ADR-P1-17). It is a new version key
/// beside <see cref="FinanceTimelineVersions.V1"/>, never a replacement: V1 histories, bytes, hashes, the v7/v8 policy
/// identities and the within-V1 comparisons are untouched, and V1 stays registered. Task 7.2 defined the identifiers, the
/// ordered canonical fact envelope and the persistence of facts; task 7.5 registers the V2 reducer and codecs
/// (<see cref="FinanceLedgerTimelineVersions"/>) and the restartable projection that reads <c>revenant.finance_fact</c>.
/// The set of <see cref="FinanceFactKind"/> values is frozen from task 7.5 on (the first V2 history); any new kind is a new version.
/// </summary>
public static class FinanceLedgerVersion
{
    public const string DomainId = FinanceTimelineVersions.DomainId;
    public const string ReducerVersion = "revenant-finance-reducer/2";
    public const string InputSchemaVersion = "revenant-finance-fact/2";
    public const string StateSchemaVersion = "revenant-finance-state/2";
    public const string ContextSchemaVersion = "revenant-finance-context/2";
}

/// <summary>Kinds of externally committed fact a durable run records, in run-local order. Values are part of the canonical bytes.</summary>
public enum FinanceFactKind : byte
{
    /// <summary>An exogenous <c>PaymentIntent</c>; payload is its <c>revenant-payment-intent/1</c> canonical bytes.</summary>
    PaymentIntent = 1,

    /// <summary>A committed human review decision on a held payment (task 7.4); payload <see cref="ReviewDecisionCodec"/>.</summary>
    ReviewDecision = 2,

    /// <summary>An authorized compensating return of a settled payment (task 7.4); payload <see cref="ReturnInstructionCodec"/>.</summary>
    ReturnInstruction = 3,
    /// <summary>
    /// The LiveOps screening step committed for an Initiated payment, at this point of the run's order (task 7.5); payload
    /// <see cref="PaymentStepCodec"/> with schema <see cref="PaymentStepCodec.ScreeningSchema"/>. It names only which payment was
    /// screened when; the outcome (hold, clear, reject) is derived by the reducer from the pinned policy.
    /// </summary>
    Screening = 4,
    /// <summary>
    /// The LiveOps settlement attempt committed for an Approved or Cleared payment, at this point of the run's order (task 7.5);
    /// payload <see cref="PaymentStepCodec"/> with schema <see cref="PaymentStepCodec.SettlementSchema"/>. Concurrent workers decide
    /// the commit order; the outcome (settled, or returned for insufficient funds) is derived by the reducer.
    /// </summary>
    Settlement = 5,
}
/// <summary>
/// Canonical payload of the <see cref="FinanceFactKind.Screening"/> and <see cref="FinanceFactKind.Settlement"/> ordering facts:
/// the intent id only. Decisions are never carried, so a replay recomputes them from the pinned context.
/// </summary>
public static class PaymentStepCodec
{
    public const string ScreeningSchema = "revenant-screening/1";
    public const string SettlementSchema = "revenant-settlement/1";
    private const int FormatVersion = 1;
    public static byte[] Encode(string intentId)
    {
        if (string.IsNullOrEmpty(intentId))
        {
            throw new CanonicalFormatException("A payment step fact needs an intent id.");
        }
        return new CanonicalWriter().WriteInt32(FormatVersion).WriteString(intentId).ToArray();
    }
    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        var reader = new CanonicalReader(bytes);
        if (reader.ReadInt32() != FormatVersion)
        {
            throw new CanonicalFormatException("Unsupported payment step format version.");
        }
        var intentId = reader.ReadString();
        reader.EnsureEnd();
        return string.IsNullOrEmpty(intentId) ? throw new CanonicalFormatException("A payment step fact needs an intent id.") : intentId;
    }
}

public enum ReviewDecisionAction : byte { Approve = 1, Reject = 2 }

/// <summary>
/// Canonical payload of a <see cref="FinanceFactKind.ReviewDecision"/> fact: what a reviewer decided, in which approval slot,
/// under which policy, and the case status it produced. The reviewer's identity is audit data on the live case, not a replay
/// input. Holds, releases and postings that follow from it are derived.
/// </summary>
public sealed record ReviewDecisionFact(
    string IntentId, ReviewDecisionAction Action, int ApprovalOrder, int RequiredApprovals, string ResultStatus, string PolicyVersion);

public static class ReviewDecisionCodec
{
    public const string SchemaVersion = "revenant-review-decision/1";
    private const int FormatVersion = 1;

    public static byte[] Encode(ReviewDecisionFact fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        Validate(fact);
        return new CanonicalWriter()
            .WriteInt32(FormatVersion).WriteString(fact.IntentId).WriteByte((byte)fact.Action).WriteInt32(fact.ApprovalOrder)
            .WriteInt32(fact.RequiredApprovals).WriteString(fact.ResultStatus).WriteString(fact.PolicyVersion).ToArray();
    }

    public static ReviewDecisionFact Decode(ReadOnlySpan<byte> bytes)
    {
        var reader = new CanonicalReader(bytes);
        if (reader.ReadInt32() != FormatVersion)
        {
            throw new CanonicalFormatException("Unsupported review decision format version.");
        }
        var fact = new ReviewDecisionFact(
            reader.ReadString(), (ReviewDecisionAction)reader.ReadByte(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadString(),
            reader.ReadString());
        reader.EnsureEnd();
        Validate(fact);
        return fact;
    }

    private static void Validate(ReviewDecisionFact fact)
    {
        if (string.IsNullOrEmpty(fact.IntentId) || !Enum.IsDefined(fact.Action) || fact.RequiredApprovals is < 1 or > 2
            || (fact.Action == ReviewDecisionAction.Approve ? fact.ApprovalOrder < 1 || fact.ApprovalOrder > fact.RequiredApprovals : fact.ApprovalOrder != 0)
            || string.IsNullOrEmpty(fact.ResultStatus) || string.IsNullOrEmpty(fact.PolicyVersion))
        {
            throw new CanonicalFormatException("A review decision fact is malformed.");
        }
    }
}

/// <summary>Canonical payload of a <see cref="FinanceFactKind.ReturnInstruction"/> fact: which settled payment, and why.</summary>
public sealed record ReturnInstructionFact(string IntentId, string Reason);

public static class ReturnInstructionCodec
{
    public const string SchemaVersion = "revenant-return-instruction/1";
    private const int FormatVersion = 1;

    public static byte[] Encode(ReturnInstructionFact fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        if (string.IsNullOrEmpty(fact.IntentId) || string.IsNullOrEmpty(fact.Reason))
        {
            throw new CanonicalFormatException("A return instruction fact needs an intent id and a reason.");
        }
        return new CanonicalWriter().WriteInt32(FormatVersion).WriteString(fact.IntentId).WriteString(fact.Reason).ToArray();
    }

    public static ReturnInstructionFact Decode(ReadOnlySpan<byte> bytes)
    {
        var reader = new CanonicalReader(bytes);
        if (reader.ReadInt32() != FormatVersion)
        {
            throw new CanonicalFormatException("Unsupported return instruction format version.");
        }
        var fact = new ReturnInstructionFact(reader.ReadString(), reader.ReadString());
        reader.EnsureEnd();
        return string.IsNullOrEmpty(fact.IntentId) || string.IsNullOrEmpty(fact.Reason)
            ? throw new CanonicalFormatException("A return instruction fact needs an intent id and a reason.")
            : fact;
    }
}

/// <summary>
/// The minimum boundary for future pinned inference evidence: an opaque schema id and SHA-256 of evidence bytes that live
/// elsewhere. Spec 07 neither creates nor interprets it; Spec 08 owns concrete evidence, normalization and model integration.
/// </summary>
public sealed record FactEvidenceRef(string Schema, byte[] Sha256);

/// <summary>
/// One ordered canonical fact of a durable run. <see cref="Sequence"/> is the run-local, gap-free commit order assigned inside
/// the live transaction; the Timeline projection replays facts in exactly that order. Decisions, holds and settlement results
/// are derived and are never facts.
/// </summary>
public sealed record FinanceFact(
    FinanceFactKind Kind, long Sequence, string PayloadSchema, byte[] Payload, FactEvidenceRef? Evidence);

/// <summary>
/// Canonical encoding of <see cref="FinanceFact"/> (input schema <see cref="FinanceLedgerVersion.InputSchemaVersion"/>):
/// format version, kind, sequence, payload schema, payload bytes, then an optional evidence reference. Unknown format,
/// unknown kind, a non-positive sequence, an empty payload, a malformed evidence hash, truncation and trailing bytes all fail
/// closed with <see cref="CanonicalFormatException"/>.
/// </summary>
public static class FinanceFactCodec
{
    public const int FormatVersion = 1;

    public static byte[] Encode(FinanceFact fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        Validate(fact.Kind, fact.Sequence, fact.PayloadSchema, fact.Payload, fact.Evidence);
        var writer = new CanonicalWriter()
            .WriteInt32(FormatVersion).WriteByte((byte)fact.Kind).WriteInt64(fact.Sequence)
            .WriteString(fact.PayloadSchema).WriteBytes(fact.Payload);
        if (fact.Evidence is { } evidence)
        {
            writer.WriteBoolean(true).WriteString(evidence.Schema).WriteBytes(evidence.Sha256);
        }
        else
        {
            writer.WriteBoolean(false);
        }
        return writer.ToArray();
    }

    public static FinanceFact Decode(ReadOnlySpan<byte> bytes)
    {
        var reader = new CanonicalReader(bytes);
        var version = reader.ReadInt32();
        if (version != FormatVersion)
        {
            throw new CanonicalFormatException($"Unsupported finance fact format version {version}.");
        }
        var kind = (FinanceFactKind)reader.ReadByte();
        var sequence = reader.ReadInt64();
        var schema = reader.ReadString();
        var payload = reader.ReadBytes().ToArray();
        var evidence = reader.ReadBoolean() ? new FactEvidenceRef(reader.ReadString(), reader.ReadBytes().ToArray()) : null;
        reader.EnsureEnd();
        Validate(kind, sequence, schema, payload, evidence);
        return new FinanceFact(kind, sequence, schema, payload, evidence);
    }

    /// <summary>SHA-256 of the payload, as stored in <c>finance_fact.canonical_sha256</c>.</summary>
    public static byte[] PayloadHash(ReadOnlySpan<byte> payload) => SHA256.HashData(payload);

    private static void Validate(FinanceFactKind kind, long sequence, string schema, byte[] payload, FactEvidenceRef? evidence)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new CanonicalFormatException($"Unknown finance fact kind {(byte)kind}.");
        }
        if (sequence < 1)
        {
            throw new CanonicalFormatException("A finance fact sequence starts at 1.");
        }
        if (string.IsNullOrEmpty(schema) || payload is null || payload.Length == 0)
        {
            throw new CanonicalFormatException("A finance fact needs a payload schema and a non-empty payload.");
        }
        if (evidence is not null && (string.IsNullOrEmpty(evidence.Schema) || evidence.Sha256 is not { Length: 32 }))
        {
            throw new CanonicalFormatException("A fact evidence reference needs a schema id and a 32-byte SHA-256.");
        }
    }
}
