using System.Collections.Immutable;
using Marrow.Kernel.Results;
using Revenant.Contracts;

namespace Revenant.Finance;

/// <summary>
/// Pure causal payment reducer (<see cref="Version"/>). It advances exogenous <see cref="PaymentIntent"/>s
/// in logical sequence order under a pinned <see cref="FinanceContext"/>; it reads no clock, randomness,
/// database or ambient setting, and writes nothing outside the returned state.
///
/// Per intent: Initiated, Screened, then
/// <list type="bullet">
/// <item>currency differs from the run currency: Held, Rejected(UnsupportedCurrency); no FX is applied;</item>
/// <item>policy hold and source available &gt;= amount: Held, the amount is reserved, posted balance unchanged;</item>
/// <item>policy hold and source available &lt; amount: Held, Rejected(InsufficientAvailable);</item>
/// <item>cleared and source available &gt;= amount: Cleared, Settled; posted moves from source to destination;</item>
/// <item>cleared and source available &lt; amount: Cleared, Returned(InsufficientAvailable).</item>
/// </list>
/// Held payments stay pending: input schema <see cref="ScenarioSchemas.InputSchemaVersion"/> carries no
/// review action, so nothing in this slice approves, rejects or returns a hold after the fact.
/// </summary>
public static class FinanceReducer
{
    public const string Version = "revenant-finance-reducer/1";

    /// <summary>Opening state for one run's own account namespace and opening balances.</summary>
    public static Result<FinanceProjectionState, FinanceError> Initial(ScenarioRun run, FinanceContext context)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(context);
        if (!string.Equals(run.Manifest.ContentHash, context.ManifestContentHash, StringComparison.Ordinal)
            || !string.Equals(run.Manifest.ScenarioVersion, context.ScenarioVersion, StringComparison.Ordinal))
        {
            return FinanceError.Fail<FinanceProjectionState>(
                FinanceErrorCode.VersionMismatch, "Run manifest does not match the pinned context.");
        }
        if (run.InitialState.RunId != run.RunId)
        {
            return FinanceError.Fail<FinanceProjectionState>(
                FinanceErrorCode.RunMismatch, "Run state namespace belongs to a different run.");
        }
        var currency = context.Policy.Currency;
        var accounts = ImmutableSortedDictionary.CreateBuilder<string, AccountPosition>(StringComparer.Ordinal);
        foreach (var account in run.InitialState.Accounts)
        {
            if (!string.Equals(account.OpeningBalance.Currency, currency, StringComparison.Ordinal))
            {
                return FinanceError.Fail<FinanceProjectionState>(
                    FinanceErrorCode.VersionMismatch, $"Account {account.AccountId} is not in {currency}.");
            }
            accounts.Add(account.AccountId, new AccountPosition(
                account.AccountId, account.BankId, account.RunScopedKey, account.OpeningBalance.MinorUnits, 0));
        }
        return Result<FinanceProjectionState, FinanceError>.Success(new FinanceProjectionState(
            run.RunId,
            run.Manifest.ScenarioVersion,
            run.Manifest.ContentHash,
            currency,
            run.Manifest.StartUtcMs,
            LastSequence: 0,
            accounts.ToImmutable(),
            ImmutableSortedDictionary.Create<string, PaymentRecord>(StringComparer.Ordinal),
            ImmutableSortedDictionary.Create<string, ImmutableArray<long>>(StringComparer.Ordinal)));
    }

    /// <summary>Applies one exogenous intent. An exact retry of an applied intent returns the state unchanged.</summary>
    public static Result<FinanceProjectionState, FinanceError> Apply(
        FinanceProjectionState state,
        PaymentIntent intent,
        FinanceContext context)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(context);

        if (!string.Equals(context.ReducerVersion, Version, StringComparison.Ordinal)
            || !string.Equals(context.ManifestContentHash, state.ManifestContentHash, StringComparison.Ordinal)
            || !string.Equals(context.ScenarioVersion, state.ScenarioVersion, StringComparison.Ordinal)
            || !string.Equals(context.Policy.Currency, state.Currency, StringComparison.Ordinal))
        {
            return Fail(FinanceErrorCode.VersionMismatch, "Pinned context is not compatible with this projection state.");
        }
        if (intent.RunId != state.RunId)
        {
            return Fail(FinanceErrorCode.RunMismatch, $"Intent {intent.IntentId} belongs to run {intent.RunId}, not {state.RunId}.");
        }
        if (state.Payments.TryGetValue(intent.IntentId, out var existing))
        {
            return existing.Intent == intent
                ? Result<FinanceProjectionState, FinanceError>.Success(state)
                : Fail(FinanceErrorCode.ConflictingDuplicate, $"Intent {intent.IntentId} was already applied with different facts.");
        }
        if (intent.Sequence <= state.LastSequence)
        {
            return Fail(FinanceErrorCode.OutOfOrderInput, $"Sequence {intent.Sequence} is not after {state.LastSequence}.");
        }
        if (intent.EventTimeUtcMs < state.ClockUtcMs)
        {
            return Fail(FinanceErrorCode.OutOfOrderInput, $"Event time {intent.EventTimeUtcMs} is before clock {state.ClockUtcMs}.");
        }
        if (!state.Accounts.TryGetValue(intent.SourceAccountId, out var source)
            || !string.Equals(source.BankId, intent.SourceBankId, StringComparison.Ordinal)
            || !state.Accounts.TryGetValue(intent.DestAccountId, out var dest)
            || !string.Equals(dest.BankId, intent.DestBankId, StringComparison.Ordinal))
        {
            return Fail(FinanceErrorCode.UnknownAccount, $"Intent {intent.IntentId} names an account outside this run.");
        }

        var policy = context.Policy;
        var now = intent.EventTimeUtcMs;
        var windowStart = now - policy.VelocityWindowMs;
        var priorTimes = state.VelocityWindows.TryGetValue(intent.SourceAccountId, out var w) ? w : [];
        // Clock is non-decreasing, so entries before windowStart can never re-enter a later window.
        var window = priorTimes.Where(t => t >= windowStart).Append(now).ToImmutableArray();
        var velocity = window.Length;

        var accounts = state.Accounts;
        var amount = intent.Amount.MinorUnits;
        var trail = ImmutableArray.CreateBuilder<PaymentStatus>();
        trail.Add(PaymentStatus.Initiated);
        trail.Add(PaymentStatus.Screened);
        PaymentStatus status;
        PaymentReason reason;
        long? heldAt = null;
        long? settledAt = null;

        if (!string.Equals(intent.Amount.Currency, state.Currency, StringComparison.Ordinal))
        {
            trail.Add(PaymentStatus.Held);
            (status, reason) = (PaymentStatus.Rejected, PaymentReason.UnsupportedCurrency);
        }
        else
        {
            var holdReason = policy.Screen(new ScreeningFeatures(
                velocity, amount, intent.Features.NewBeneficiary, intent.Features.SanctionsHit));
            if (holdReason != PaymentReason.None)
            {
                trail.Add(PaymentStatus.Held);
                if (source.AvailableMinor >= amount)
                {
                    accounts = accounts.SetItem(source.AccountId, source with
                    {
                        ReservedMinor = checked(source.ReservedMinor + amount),
                    });
                    (status, reason, heldAt) = (PaymentStatus.Held, holdReason, now);
                }
                else
                {
                    (status, reason) = (PaymentStatus.Rejected, PaymentReason.InsufficientAvailable);
                }
            }
            else
            {
                trail.Add(PaymentStatus.Cleared);
                if (source.AvailableMinor >= amount)
                {
                    accounts = accounts
                        .SetItem(source.AccountId, source with { PostedMinor = checked(source.PostedMinor - amount) })
                        .SetItem(dest.AccountId, dest with { PostedMinor = checked(dest.PostedMinor + amount) });
                    (status, reason, settledAt) = (PaymentStatus.Settled, PaymentReason.None, now);
                }
                else
                {
                    (status, reason) = (PaymentStatus.Returned, PaymentReason.InsufficientAvailable);
                }
            }
        }
        if (status != trail[^1])
        {
            trail.Add(status);
        }

        var record = new PaymentRecord(
            intent, policy.PolicyId, velocity, status, reason, trail.ToImmutable(), now, heldAt, settledAt);
        return Result<FinanceProjectionState, FinanceError>.Success(state with
        {
            ClockUtcMs = now,
            LastSequence = intent.Sequence,
            Accounts = accounts,
            Payments = state.Payments.Add(intent.IntentId, record),
            VelocityWindows = state.VelocityWindows.SetItem(intent.SourceAccountId, window),
        });
    }

    /// <summary>Applies intents in the given order, stopping at the first typed failure.</summary>
    public static Result<FinanceProjectionState, FinanceError> ApplyAll(
        FinanceProjectionState state,
        IEnumerable<PaymentIntent> intents,
        FinanceContext context)
    {
        ArgumentNullException.ThrowIfNull(intents);
        var current = state;
        foreach (var intent in intents)
        {
            var next = Apply(current, intent, context);
            if (!next.TryGetValue(out var value))
            {
                return next;
            }
            current = value;
        }
        return Result<FinanceProjectionState, FinanceError>.Success(current);
    }

    private static Result<FinanceProjectionState, FinanceError> Fail(FinanceErrorCode code, string message) =>
        FinanceError.Fail<FinanceProjectionState>(code, message);
}
