using System.Security.Cryptography;
using Marrow.Kernel.Canonical;
using Revenant.Contracts;
using Revenant.Timeline;

namespace Revenant.Observations;

internal enum ObservationCoverage : byte { Complete = 1, NotEnoughHistory = 2 }

internal sealed record V4SealIdentity(ScenarioRunId ExecutionRunId, long StartMs, long BoundaryMs,
    long FactSequence, string PrefixDigest, ObservationCoverage Coverage = ObservationCoverage.Complete)
{
    internal void Write(CanonicalWriter w)
    {
        if (ExecutionRunId.IsEmpty || FactSequence < 1 || !ObservedFinanceVersion.IsBoundary(BoundaryMs)
            || BoundaryMs < long.MinValue + ObservedFinanceVersion.BucketMs
            || StartMs != BoundaryMs - ObservedFinanceVersion.BucketMs || !ObservedFinanceEncoding.IsHash(PrefixDigest)
            || Coverage != ObservationCoverage.Complete)
        {
            throw new CanonicalFormatException("Invalid completed observation seal identity.");
        }

        w.WriteGuid(ExecutionRunId.Value).WriteInt64(StartMs).WriteInt64(BoundaryMs).WriteInt64(FactSequence)
            .WriteBytes(Convert.FromHexString(PrefixDigest)).WriteByte((byte)Coverage);
    }

    internal static V4SealIdentity Read(ref CanonicalReader r) => new(new(r.ReadGuid()), r.ReadInt64(), r.ReadInt64(),
        r.ReadInt64(), ObservedFinanceEncoding.ReadHash(ref r), (ObservationCoverage)r.ReadByte());
}

internal readonly record struct ObservationBalance(long Posted, long Reserved)
{
    internal long Available => checked(Posted - Reserved);

    internal void Write(CanonicalWriter w)
    {
        if (Reserved < 0 || Posted < Reserved)
        {
            throw new CanonicalFormatException("Invalid observation balance.");
        }
        w.WriteInt64(Posted).WriteInt64(Reserved).WriteInt64(Available);
    }

    internal static ObservationBalance Read(ref CanonicalReader r)
    {
        var balance = new ObservationBalance(r.ReadInt64(), r.ReadInt64());
        var available = r.ReadInt64();
        if (balance.Reserved < 0 || balance.Posted < balance.Reserved || available != balance.Available)
        {
            throw new CanonicalFormatException("Invalid observation balance.");
        }
        return balance;
    }
}

internal sealed record V4BankObservation(V4SealIdentity Seal, string BankId, ObservationBalance Balance,
    long Intents, long Submitted, long DistinctSenders, long DistinctReceivers, long Screened, long Cleared,
    long Held, long Rejected, long ScreeningPending, long SettledIn, long SettledOut, long SettledTransfers,
    long ReturnedIn, long ReturnedOut, long Returns, long Reserved, long Released)
{
    internal const string Schema = "bank-observation/1";
    internal long NetSettled => checked(SettledIn - SettledOut);
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
        if (string.IsNullOrWhiteSpace(BankId))
        {
            throw new CanonicalFormatException("Missing bank identity.");
        }

        w.WriteString(BankId);
        Balance.Write(w);
        foreach (var value in new[] { Intents, Submitted, DistinctSenders, DistinctReceivers, Screened, Cleared, Held,
            Rejected, ScreeningPending, SettledIn, SettledOut, SettledTransfers, ReturnedIn, ReturnedOut, Returns,
            Reserved, Released })
        {
            if (value < 0)
            {
                throw new CanonicalFormatException("Negative observation flow.");
            }
            w.WriteInt64(value);
        }

        w.WriteInt64(NetSettled);
    }

    internal static V4BankObservation Decode(ReadOnlySpan<byte> bytes, string digest)
    {
        ObservationEncoding.RequireDigest(bytes, digest);
        var r = new CanonicalReader(bytes);
        ObservationEncoding.Expect(ref r, Schema);
        var seal = V4SealIdentity.Read(ref r);
        var bank = r.ReadString();
        var balance = ObservationBalance.Read(ref r);
        var row = new V4BankObservation(seal, bank, balance,
            r.ReadInt64(), r.ReadInt64(), r.ReadInt64(), r.ReadInt64(), r.ReadInt64(), r.ReadInt64(),
            r.ReadInt64(), r.ReadInt64(), r.ReadInt64(), r.ReadInt64(), r.ReadInt64(), r.ReadInt64(),
            r.ReadInt64(), r.ReadInt64(), r.ReadInt64(), r.ReadInt64(), r.ReadInt64());
        var net = r.ReadInt64();
        r.EnsureEnd();
        if (net != row.NetSettled || !bytes.SequenceEqual(row.Encode()))
        {
            throw new CanonicalFormatException("Noncanonical bank observation.");
        }
        return row;
    }
}

internal static class ObservationEncoding
{
    internal static CanonicalWriter Begin(string schema) => new CanonicalWriter().WriteInt32(1).WriteString(schema);
    internal static void Expect(ref CanonicalReader r, string schema)
    {
        ObservedFinanceEncoding.ReadFormat(ref r);
        if (r.ReadString() != schema)
        {
            throw new CanonicalFormatException("Unknown observation schema.");
        }
    }

    internal static void WriteOptional(CanonicalWriter w, long? value)
    {
        w.WriteBoolean(value.HasValue);
        if (value is { } present) w.WriteInt64(present);
    }

    internal static long? ReadOptional(ref CanonicalReader r) => r.ReadBoolean() ? r.ReadInt64() : null;

    internal static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    internal static void RequireDigest(ReadOnlySpan<byte> bytes, string digest)
    {
        if (!ObservedFinanceEncoding.IsHash(digest) || Hash(bytes) != digest)
        {
            throw new CanonicalFormatException("Observation digest mismatch.");
        }
    }
}
