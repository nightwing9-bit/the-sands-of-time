using System.Collections.Immutable;
using Marrow.Kernel.Canonical;
using Revenant.Contracts;
using Revenant.Scenario;
using Revenant.Timeline;

namespace Revenant.Observations;

internal enum AdvisorySeriesCoverage : byte { Complete = 1, UnobservedOrigin = 2, UnknownSubject = 3 }

internal readonly record struct AdvisorySeriesPoint(V4SealIdentity Seal, long Value);

/// <summary>The digest identifies the canonical prefix for the subject. The physical execution is carried separately.</summary>
internal sealed record V4AdvisorySeries(string Schema, ScenarioRunId ExecutionRunId, string SubjectId, string Unit,
    long OpeningBoundaryMs, long? ReferenceMinor, long RequestedBoundaryMs, AdvisorySeriesCoverage Coverage,
    ImmutableArray<AdvisorySeriesPoint> Points)
{
    internal const string AccountSchema = "account-outgoing-velocity-series/1";
    internal const string BankSchema = "bank-available-balance-series/1";

    internal V4SealIdentity? Origin => Coverage == AdvisorySeriesCoverage.Complete ? Points[^1].Seal : null;
    internal string Digest => ObservationEncoding.Hash(Encode());

    internal byte[] Encode()
    {
        var validUnit = Schema switch
        {
            AccountSchema => Unit == "count" && ReferenceMinor is null,
            BankSchema => Unit == "USD" && (Coverage == AdvisorySeriesCoverage.UnknownSubject
                ? ReferenceMinor is null : ReferenceMinor is >= 0),
            _ => false,
        };
        if (ExecutionRunId.IsEmpty || string.IsNullOrWhiteSpace(SubjectId) || !Enum.IsDefined(Coverage)
            || !ObservedFinanceVersion.IsBoundary(OpeningBoundaryMs) || Points.IsDefault || !validUnit
            || (Coverage == AdvisorySeriesCoverage.UnknownSubject && !Points.IsEmpty))
            throw new CanonicalFormatException("Invalid advisory series identity or coverage.");

        var w = ObservationEncoding.Begin(Schema).WriteString(SubjectId).WriteString(Unit)
            .WriteInt64(OpeningBoundaryMs).WriteInt64(RequestedBoundaryMs).WriteByte((byte)Coverage);
        ObservationEncoding.WriteOptional(w, ReferenceMinor);
        w.WriteCount(Points.Length);
        var previousBoundary = OpeningBoundaryMs;
        long previousSequence = 0;
        foreach (var (seal, value) in Points)
        {
            if (seal.ExecutionRunId != ExecutionRunId || seal.StartMs != previousBoundary
                || seal.BoundaryMs != ObservedFinanceVersion.NextBoundary(previousBoundary)
                || seal.FactSequence <= previousSequence || seal.Coverage != ObservationCoverage.Complete
                || !ObservedFinanceEncoding.IsHash(seal.PrefixDigest) || value < 0 || seal.BoundaryMs > RequestedBoundaryMs)
                throw new CanonicalFormatException("Advisory series requires contiguous, verified observations from one execution.");

            w.WriteInt64(seal.BoundaryMs).WriteInt64(seal.FactSequence)
                .WriteBytes(Convert.FromHexString(seal.PrefixDigest)).WriteInt64(value);
            previousBoundary = seal.BoundaryMs;
            previousSequence = seal.FactSequence;
        }
        if (Coverage == AdvisorySeriesCoverage.Complete
            && (Points.IsEmpty || previousBoundary != RequestedBoundaryMs))
            throw new CanonicalFormatException("An eligible advisory series must end at its requested completed seal.");
        return w.ToArray();
    }
}

internal sealed partial class V4ObservationProjector
{
    internal V4AdvisorySeries AccountOutgoingSeries(string accountId, long throughBoundaryMs)
    {
        if (!accountsAtOpening.ContainsKey(accountId))
            return AccountSeries(AdvisorySeriesCoverage.UnknownSubject, []);

        // The R4 activity index records one Intent per committed intake. Consume only entries inside the
        // current seal, including an intake at its start and excluding activity at its end.
        var entries = accountHistory.GetValueOrDefault(accountId);
        var points = ImmutableArray.CreateBuilder<AdvisorySeriesPoint>();
        var index = 0;
        foreach (var seal in CompletedSealsThrough(throughBoundaryMs))
        {
            long count = 0;
            while (entries is not null && index < entries.Count && entries[index].Position < seal.FactSequence)
            {
                var activity = entries[index++];
                if (activity.At < seal.StartMs || activity.At >= seal.BoundaryMs)
                    throw new InvalidOperationException("Account activity is outside its completed observation interval.");
                if (activity.Kind == ActivityKind.Intent) count++;
            }
            points.Add(new(seal, count));
        }
        return AccountSeries(SeriesCoverage(throughBoundaryMs), points.ToImmutable());

        V4AdvisorySeries AccountSeries(AdvisorySeriesCoverage coverage, ImmutableArray<AdvisorySeriesPoint> values) =>
            new(V4AdvisorySeries.AccountSchema, executionRun, accountId, "count", origin, null, throughBoundaryMs, coverage, values);
    }

    internal V4AdvisorySeries BankAvailableSeries(string bankId, long throughBoundaryMs)
    {
        var opening = openingBalances.TryGetValue(bankId, out var balance) ? balance.Available : (long?)null;
        if (opening is null)
            return BankSeries(AdvisorySeriesCoverage.UnknownSubject, []);

        var points = ImmutableArray.CreateBuilder<AdvisorySeriesPoint>();
        var bankIndex = Array.IndexOf(BankIds, bankId);
        foreach (var seal in CompletedSealsThrough(throughBoundaryMs))
        {
            var offset = checked(points.Count * BankIds.Length);
            for (var i = 0; i < BankIds.Length; i++)
            {
                if (bankRows[offset + i].Seal != seal || bankRows[offset + i].BankId != BankIds[i])
                    throw new InvalidOperationException("A completed V4 seal must contain one authoritative observation for every bank.");
            }
            points.Add(new(seal, bankRows[offset + bankIndex].Balance.Available));
        }
        return BankSeries(SeriesCoverage(throughBoundaryMs), points.ToImmutable());

        V4AdvisorySeries BankSeries(AdvisorySeriesCoverage coverage, ImmutableArray<AdvisorySeriesPoint> values) =>
            new(V4AdvisorySeries.BankSchema, executionRun, bankId, prefix.Ledger.Currency, origin, opening, throughBoundaryMs,
                coverage, values);
    }

    private static readonly string[] BankIds = Enumerable.Range(0, BehavioralEpisodeScenario.BankCount)
        .Select(FridayRingScenario.BankId).Order(StringComparer.Ordinal).ToArray();

    private IEnumerable<V4SealIdentity> CompletedSealsThrough(long boundary) =>
        bankRows.Where((_, index) => index % BankIds.Length == 0).Select(r => r.Seal)
            .TakeWhile(seal => seal.BoundaryMs <= boundary);

    private AdvisorySeriesCoverage SeriesCoverage(long boundary) =>
        seals.ContainsKey(boundary) ? AdvisorySeriesCoverage.Complete : AdvisorySeriesCoverage.UnobservedOrigin;
}
