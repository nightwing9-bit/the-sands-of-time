using System.Collections.Immutable;
using Revenant.Contracts;

namespace Revenant.Finance;

/// <summary>
/// One run-scoped account in the projection. <see cref="PostedMinor"/> moves only on settlement;
/// <see cref="ReservedMinor"/> is outgoing availability held back by pending holds.
/// </summary>
public sealed record AccountPosition(string AccountId, string BankId, string RunScopedKey, long PostedMinor, long ReservedMinor)
{
    public long AvailableMinor => PostedMinor - ReservedMinor;
}

/// <summary>
/// Derived decision evidence for one exogenous intent under the policy that screened it. It is evidence of
/// this branch only and is never fed back into another branch as an input.
/// </summary>
public sealed record PaymentRecord(
    PaymentIntent Intent,
    string PolicyId,
    int Velocity15m,
    PaymentStatus Status,
    PaymentReason Reason,
    ImmutableArray<PaymentStatus> Trail,
    long ScreenedAtUtcMs,
    long? HeldAtUtcMs,
    long? SettledAtUtcMs)
{
    public bool IsPendingHold => Status == PaymentStatus.Held;
    public bool EverSettled => Trail.Contains(PaymentStatus.Settled);
}

/// <summary>Per-bank aggregate of its accounts. Liquidity shown to users is <see cref="AvailableMinor"/>.</summary>
public sealed record BankPosition(string BankId, long PostedMinor, long ReservedMinor)
{
    public long AvailableMinor => PostedMinor - ReservedMinor;
}

/// <summary>
/// Counts over synthetic scenario labels (<see cref="ScenarioSchemas.LabelSchemaVersion"/>), not real-world
/// fraud findings. A pending fraud hold is <see cref="FraudPendingHold"/>, never counted as stopped.
/// </summary>
public sealed record SyntheticLabelCounters(
    string LabelSchemaVersion,
    int FraudSettled,
    int FraudPendingHold,
    int FraudTerminallyStopped,
    int LegitimateSettled,
    int LegitimatePendingHold,
    int LegitimateTerminallyStopped);

/// <summary>
/// Complete pure projection state of one run under one or more pinned policy segments: account balances and
/// reservations, payment statuses, payer velocity windows and the scenario clock. Not a ledger; nothing in
/// it has been posted to a database.
/// </summary>
public sealed record FinanceProjectionState(
    ScenarioRunId RunId,
    string ScenarioVersion,
    string ManifestContentHash,
    string Currency,
    long ClockUtcMs,
    long LastSequence,
    ImmutableSortedDictionary<string, AccountPosition> Accounts,
    ImmutableSortedDictionary<string, PaymentRecord> Payments,
    ImmutableSortedDictionary<string, ImmutableArray<long>> VelocityWindows)
{
    public long TotalPostedMinor => Accounts.Values.Sum(a => a.PostedMinor);

    public long TotalReservedMinor => Accounts.Values.Sum(a => a.ReservedMinor);

    /// <summary>Banks in ordinal id order, each the sum of its accounts.</summary>
    public ImmutableArray<BankPosition> Banks =>
    [
        .. Accounts.Values
            .GroupBy(a => a.BankId, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new BankPosition(g.Key, g.Sum(a => a.PostedMinor), g.Sum(a => a.ReservedMinor))),
    ];

    public SyntheticLabelCounters Counters
    {
        get
        {
            int Count(SyntheticTruth truth, Func<PaymentRecord, bool> predicate) =>
                Payments.Values.Count(p => p.Intent.Truth == truth && predicate(p));
            static bool Stopped(PaymentRecord p) =>
                p.Status is PaymentStatus.Rejected or PaymentStatus.Returned && !p.EverSettled;
            return new SyntheticLabelCounters(
                ScenarioSchemas.LabelSchemaVersion,
                Count(SyntheticTruth.FraudMuleRing, p => p.Status == PaymentStatus.Settled),
                Count(SyntheticTruth.FraudMuleRing, p => p.IsPendingHold),
                Count(SyntheticTruth.FraudMuleRing, Stopped),
                Count(SyntheticTruth.Legitimate, p => p.Status == PaymentStatus.Settled),
                Count(SyntheticTruth.Legitimate, p => p.IsPendingHold),
                Count(SyntheticTruth.Legitimate, Stopped));
        }
    }
}
