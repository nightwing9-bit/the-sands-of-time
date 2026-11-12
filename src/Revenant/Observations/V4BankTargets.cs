using System.Collections.Immutable;
using Marrow.Kernel.Canonical;
using Revenant.Timeline;

namespace Revenant.Observations;

internal sealed record V4FutureFlow(long BoundaryMs, long SettledIn, long SettledOut, long Transfers,
    long ReturnedIn, long ReturnedOut, long Returns, ObservationBalance Balance)
{
    internal long NetSettled => checked(SettledIn - SettledOut);

    internal void Write(CanonicalWriter w)
    {
        if (new[] { SettledIn, SettledOut, Transfers, ReturnedIn, ReturnedOut, Returns }.Any(n => n < 0))
            throw new CanonicalFormatException("Negative future liquidity flow.");
        w.WriteInt64(SettledIn).WriteInt64(SettledOut).WriteInt64(NetSettled).WriteInt64(Transfers)
            .WriteInt64(ReturnedIn).WriteInt64(ReturnedOut).WriteInt64(Returns);
        Balance.Write(w);
    }

    internal static V4FutureFlow Read(long boundary, ref CanonicalReader r)
    {
        var incoming = r.ReadInt64();
        var outgoing = r.ReadInt64();
        var net = r.ReadInt64();
        var flow = new V4FutureFlow(boundary, incoming, outgoing, r.ReadInt64(), r.ReadInt64(), r.ReadInt64(), r.ReadInt64(),
            ObservationBalance.Read(ref r));
        if (net != flow.NetSettled) throw new CanonicalFormatException("Invalid future net settled flow.");
        return flow;
    }
}

internal sealed record V4BankTargets(V4SealIdentity Origin, string BankId, ImmutableArray<V4FutureFlow?> Horizons)
{
    internal const string Schema = "bank-eight-horizon-target/1";
    internal const int HorizonCount = 8;
    internal bool FullyCovered => Horizons.Length == HorizonCount && Horizons.All(h => h is not null);
    internal string Digest => ObservationEncoding.Hash(Encode());

    internal byte[] Encode()
    {
        var w = ObservationEncoding.Begin(Schema);
        Origin.Write(w);
        WriteContent(w);
        return w.ToArray();
    }

    internal void WriteContent(CanonicalWriter w)
    {
        if (Horizons.IsDefault || Horizons.Length != HorizonCount || string.IsNullOrWhiteSpace(BankId))
            throw new CanonicalFormatException("Expected a bank and eight future coverage entries.");
        w.WriteString(BankId).WriteBoolean(FullyCovered);
        for (var i = 0; i < HorizonCount; i++)
        {
            var at = checked(Origin.BoundaryMs + (i + 1) * ObservedFinanceVersion.BucketMs);
            var flow = Horizons[i];
            w.WriteInt64(at).WriteBoolean(flow is not null);
            if (flow is not null)
            {
                if (flow.BoundaryMs != at) throw new CanonicalFormatException("Incorrect future interval boundary.");
                flow.Write(w);
            }
        }
    }

    internal static V4BankTargets Decode(ReadOnlySpan<byte> bytes, string digest)
    {
        ObservationEncoding.RequireDigest(bytes, digest);
        var r = new CanonicalReader(bytes);
        ObservationEncoding.Expect(ref r, Schema);
        var origin = V4SealIdentity.Read(ref r);
        var bank = r.ReadString();
        var covered = r.ReadBoolean();
        var horizons = ImmutableArray.CreateBuilder<V4FutureFlow?>(HorizonCount);
        for (var i = 0; i < HorizonCount; i++)
        {
            var boundary = r.ReadInt64();
            if (boundary != checked(origin.BoundaryMs + (i + 1) * ObservedFinanceVersion.BucketMs))
                throw new CanonicalFormatException("Unexpected future interval boundary.");
            horizons.Add(r.ReadBoolean() ? V4FutureFlow.Read(boundary, ref r) : null);
        }
        var target = new V4BankTargets(origin, bank, horizons.MoveToImmutable());
        r.EnsureEnd();
        if (covered != target.FullyCovered || !bytes.SequenceEqual(target.Encode()))
            throw new CanonicalFormatException("Noncanonical future target record.");
        return target;
    }

    internal static IEnumerable<V4BankTargets> Build(IReadOnlyList<V4BankObservation> banks)
    {
        if (banks.Count == 0) yield break;
        var execution = banks[0].Seal.ExecutionRunId;
        var bySeal = banks.GroupBy(x => x.Seal.BoundaryMs).ToDictionary(g => g.Key, g => g.ToDictionary(x => x.BankId));
        foreach (var row in banks.OrderBy(x => x.Seal.BoundaryMs).ThenBy(x => x.BankId, StringComparer.Ordinal))
        {
            if (row.Seal.ExecutionRunId != execution || row.Seal.Coverage != ObservationCoverage.Complete)
                throw new InvalidOperationException("Targets require complete bank seals from one physical execution.");
            var horizons = ImmutableArray.CreateBuilder<V4FutureFlow?>(HorizonCount);
            for (var h = 1; h <= HorizonCount; h++)
            {
                var at = checked(row.Seal.BoundaryMs + h * ObservedFinanceVersion.BucketMs);
                var future = bySeal.GetValueOrDefault(at)?.GetValueOrDefault(row.BankId);
                horizons.Add(future is null ? null : new(at, future.SettledIn, future.SettledOut, future.SettledTransfers,
                    future.ReturnedIn, future.ReturnedOut, future.Returns, future.Balance));
            }
            yield return new(row.Seal, row.BankId, horizons.MoveToImmutable());
        }
    }
}
