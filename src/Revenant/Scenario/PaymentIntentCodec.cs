using Marrow.Kernel.Canonical;
using Marrow.Timeline;
using Revenant.Contracts;

namespace Revenant.Scenario;

/// <summary>
/// Canonical, versioned codec for <see cref="PaymentIntent"/>, pinned as input schema
/// <see cref="ScenarioSchemas.InputSchemaVersion"/>. Bytes start with a format version; an unknown version,
/// truncated or trailing bytes, or a field that violates the intent's invariants fail closed with
/// <see cref="CanonicalFormatException"/>. It encodes exogenous facts only: no decision, hold or settlement.
/// </summary>
public sealed class PaymentIntentCodec : IInputCodec<PaymentIntent>
{
    public const int FormatVersion = 1;

    public static PaymentIntentCodec Instance { get; } = new();

    public byte[] EncodeCanonical(PaymentIntent input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var writer = new CanonicalWriter().WriteInt32(FormatVersion).WriteGuid(input.RunId.Value);
        WriteFacts(writer, input);
        return writer.ToArray();
    }

    public PaymentIntent Decode(ReadOnlySpan<byte> bytes)
    {
        var reader = new CanonicalReader(bytes);
        var version = reader.ReadInt32();
        if (version != FormatVersion)
        {
            throw new CanonicalFormatException($"Unsupported PaymentIntent format version {version}.");
        }

        var runId = new ScenarioRunId(reader.ReadGuid());
        var intentId = reader.ReadString();
        var sequence = reader.ReadInt64();
        var eventTime = reader.ReadInt64();
        var sourceBank = reader.ReadString();
        var sourceAccount = reader.ReadString();
        var destBank = reader.ReadString();
        var destAccount = reader.ReadString();
        var currency = reader.ReadString();
        var minorUnits = reader.ReadInt64();
        var truth = (SyntheticTruth)reader.ReadByte();
        var features = new PaymentFeatures(reader.ReadBoolean(), reader.ReadBoolean());
        reader.EnsureEnd();

        try
        {
            return new PaymentIntent(
                runId, intentId, sequence, eventTime, sourceBank, sourceAccount, destBank, destAccount,
                new Money(currency, minorUnits), truth, features);
        }
        catch (ArgumentException ex)
        {
            throw new CanonicalFormatException("PaymentIntent bytes violate intent invariants.", ex);
        }
    }

    /// <summary>
    /// Canonical bytes of the intent without its run identity, used to compare facts between runs.
    /// </summary>
    public static byte[] EncodeFacts(PaymentIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        var writer = new CanonicalWriter();
        WriteFacts(writer, intent);
        return writer.ToArray();
    }

    private static void WriteFacts(CanonicalWriter writer, PaymentIntent intent) => writer
        .WriteString(intent.IntentId)
        .WriteInt64(intent.Sequence)
        .WriteInt64(intent.EventTimeUtcMs)
        .WriteString(intent.SourceBankId)
        .WriteString(intent.SourceAccountId)
        .WriteString(intent.DestBankId)
        .WriteString(intent.DestAccountId)
        .WriteString(intent.Amount.Currency)
        .WriteInt64(intent.Amount.MinorUnits)
        .WriteByte((byte)intent.Truth)
        .WriteBoolean(intent.Features.NewBeneficiary)
        .WriteBoolean(intent.Features.SanctionsHit);
}
