using System.Collections.Immutable;
using Marrow.Kernel.Canonical;
using Revenant.Contracts;
using Revenant.Timeline;

namespace Revenant.Observations;

internal sealed record V4ActivityWindow(long DurationMs, ObservationCoverage Coverage,
    long IncomingCount, long IncomingMinor, long OutgoingCount, long OutgoingMinor,
    long SubmittedCount, long SubmittedMinor, long HeldCount, long HeldMinor,
    long ReturnInCount, long ReturnInMinor, long ReturnOutCount, long ReturnOutMinor,
    long Counterparties, long Beneficiaries, long NewBeneficiaryCount, long FamiliarBeneficiaryCount)
{
    internal static readonly ImmutableArray<long> Durations = [300_000, 1_800_000, 3_600_000, 86_400_000];

    internal static void WriteAll(CanonicalWriter w, ImmutableArray<V4ActivityWindow> windows)
    {
        if (windows.IsDefault || !windows.Select(x => x.DurationMs).SequenceEqual(Durations))
        {
            throw new CanonicalFormatException("Expected the four defined activity windows in order.");
        }
        foreach (var a in windows)
        {
            if (!Enum.IsDefined(a.Coverage))
            {
                throw new CanonicalFormatException("Unknown activity coverage.");
            }
            w.WriteInt64(a.DurationMs).WriteByte((byte)a.Coverage);
            foreach (var n in new[] { a.IncomingCount, a.IncomingMinor, a.OutgoingCount, a.OutgoingMinor,
                a.SubmittedCount, a.SubmittedMinor, a.HeldCount, a.HeldMinor, a.ReturnInCount, a.ReturnInMinor,
                a.ReturnOutCount, a.ReturnOutMinor, a.Counterparties, a.Beneficiaries, a.NewBeneficiaryCount, a.FamiliarBeneficiaryCount })
            {
                if (n < 0) throw new CanonicalFormatException("Negative activity count or principal.");
                w.WriteInt64(n);
            }
        }
    }

    internal static ImmutableArray<V4ActivityWindow> ReadAll(ref CanonicalReader r)
    {
        var result = ImmutableArray.CreateBuilder<V4ActivityWindow>(Durations.Length);
        foreach (var _ in Durations)
        {
            result.Add(new(r.ReadInt64(), (ObservationCoverage)r.ReadByte(),
                r.ReadInt64(), r.ReadInt64(), r.ReadInt64(), r.ReadInt64(), r.ReadInt64(), r.ReadInt64(), r.ReadInt64(), r.ReadInt64(),
                r.ReadInt64(), r.ReadInt64(), r.ReadInt64(), r.ReadInt64(), r.ReadInt64(), r.ReadInt64(), r.ReadInt64(), r.ReadInt64()));
        }
        return result.MoveToImmutable();
    }
}

internal sealed record V4AccountObservation(V4SealIdentity Seal, string AccountId, string BankId,
    ObservationBalance Balance, ImmutableArray<V4ActivityWindow> Windows, long? LastSuccessfulActivityMs, long? SinceActivityMs)
{
    internal const string Schema = "account-activity-observation/1";
    internal string Digest => ObservationEncoding.Hash(Encode());

    internal byte[] Encode()
    {
        var w = ObservationEncoding.Begin(Schema);
        Seal.Write(w);
        WriteContent(w);
        return w.ToArray();
    }

    internal void WriteContent(CanonicalWriter w)
    {
        if (string.IsNullOrWhiteSpace(AccountId) || string.IsNullOrWhiteSpace(BankId)
            || LastSuccessfulActivityMs >= Seal.BoundaryMs || SinceActivityMs <= 0)
        {
            throw new CanonicalFormatException("Invalid account observation identity or activity time.");
        }
        w.WriteString(AccountId).WriteString(BankId);
        Balance.Write(w);
        V4ActivityWindow.WriteAll(w, Windows);
        ObservationEncoding.WriteOptional(w, LastSuccessfulActivityMs);
        ObservationEncoding.WriteOptional(w, SinceActivityMs);
    }

    internal static V4AccountObservation Decode(ReadOnlySpan<byte> bytes, string digest)
    {
        ObservationEncoding.RequireDigest(bytes, digest);
        var r = new CanonicalReader(bytes);
        ObservationEncoding.Expect(ref r, Schema);
        var row = new V4AccountObservation(V4SealIdentity.Read(ref r), r.ReadString(), r.ReadString(), ObservationBalance.Read(ref r),
            V4ActivityWindow.ReadAll(ref r), ObservationEncoding.ReadOptional(ref r), ObservationEncoding.ReadOptional(ref r));
        r.EnsureEnd();
        if (!bytes.SequenceEqual(row.Encode())) throw new CanonicalFormatException("Noncanonical account activity observation.");
        return row;
    }
}

// Join keys and proof live on the enclosing row. Only this type can enter the predictor column export.
internal sealed record V4FraudInputs(long AmountMinor, string Currency, ObservationBalance SourceBalance,
    bool ReportedNewBeneficiary, bool SanctionsHit, bool BeneficiarySeenBefore, bool CounterpartySeenBefore,
    long? SinceOutgoingMs, int UtcMinuteOfDay, int UtcDayOfWeek, ImmutableArray<V4ActivityWindow> Windows)
{
    internal long? AmountToAvailablePpm => SourceBalance.Available == 0 ? null
        : (long)Int128.Min(long.MaxValue, (Int128)AmountMinor * 1_000_000 / SourceBalance.Available);

    internal void Write(CanonicalWriter w)
    {
        if (AmountMinor <= 0 || Currency != "USD" || SinceOutgoingMs < 0 || UtcMinuteOfDay is < 0 or > 1439
            || UtcDayOfWeek is < 0 or > 6)
        {
            throw new CanonicalFormatException("Invalid pre-screening feature value.");
        }

        w.WriteInt64(AmountMinor).WriteString(Currency);
        SourceBalance.Write(w);
        ObservationEncoding.WriteOptional(w, AmountToAvailablePpm);
        w.WriteBoolean(ReportedNewBeneficiary).WriteBoolean(SanctionsHit).WriteBoolean(BeneficiarySeenBefore)
            .WriteBoolean(CounterpartySeenBefore);
        ObservationEncoding.WriteOptional(w, SinceOutgoingMs);
        w.WriteInt32(UtcMinuteOfDay).WriteInt32(UtcDayOfWeek);
        V4ActivityWindow.WriteAll(w, Windows);
    }

    internal static V4FraudInputs Read(ref CanonicalReader r)
    {
        var amount = r.ReadInt64();
        var currency = r.ReadString();
        var balance = ObservationBalance.Read(ref r);
        var fraction = ObservationEncoding.ReadOptional(ref r);
        var result = new V4FraudInputs(amount, currency, balance, r.ReadBoolean(), r.ReadBoolean(), r.ReadBoolean(), r.ReadBoolean(),
            ObservationEncoding.ReadOptional(ref r), r.ReadInt32(), r.ReadInt32(), V4ActivityWindow.ReadAll(ref r));
        if (fraction != result.AmountToAvailablePpm) throw new CanonicalFormatException("Invalid amount-to-available ratio.");
        return result;
    }
}

internal sealed record V4FraudAsOfObservation(ScenarioRunId ExecutionRunId, string IntentId, long EffectiveTimeMs,
    long CutoffFactSeq, long LastSealBoundaryMs, string CutoffPrefixDigest, V4FraudInputs Inputs)
{
    internal const string Schema = "fraud-asof-observation/1";
    internal string Digest => ObservationEncoding.Hash(Encode());

    internal byte[] Encode()
    {
        if (ExecutionRunId.IsEmpty || string.IsNullOrWhiteSpace(IntentId) || CutoffFactSeq < 1
            || !ObservedFinanceVersion.IsBoundary(LastSealBoundaryMs) || LastSealBoundaryMs > EffectiveTimeMs
            || EffectiveTimeMs >= ObservedFinanceVersion.NextBoundary(LastSealBoundaryMs)
            || !ObservedFinanceEncoding.IsHash(CutoffPrefixDigest))
        {
            throw new CanonicalFormatException("Invalid pre-screening snapshot identity.");
        }

        var w = ObservationEncoding.Begin(Schema).WriteGuid(ExecutionRunId.Value).WriteString(IntentId)
            .WriteInt64(EffectiveTimeMs).WriteInt64(CutoffFactSeq).WriteInt64(LastSealBoundaryMs)
            .WriteBytes(Convert.FromHexString(CutoffPrefixDigest));
        Inputs.Write(w);
        return w.ToArray();
    }

    internal static V4FraudAsOfObservation Decode(ReadOnlySpan<byte> bytes, string digest)
    {
        ObservationEncoding.RequireDigest(bytes, digest);
        var r = new CanonicalReader(bytes);
        ObservationEncoding.Expect(ref r, Schema);
        var row = new V4FraudAsOfObservation(new(r.ReadGuid()), r.ReadString(), r.ReadInt64(), r.ReadInt64(), r.ReadInt64(),
            ObservedFinanceEncoding.ReadHash(ref r), V4FraudInputs.Read(ref r));
        r.EnsureEnd();
        if (!bytes.SequenceEqual(row.Encode())) throw new CanonicalFormatException("Noncanonical pre-screening observation.");
        return row;
    }
}
