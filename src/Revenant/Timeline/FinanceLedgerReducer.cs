using System.Collections.Immutable;
using Marrow.Kernel.Canonical;
using Marrow.Kernel.Results;
using Revenant.Cases;
using Revenant.Contracts;
using Revenant.Finance;
using Revenant.Scenario;

namespace Revenant.Timeline;

/// <summary>Typed failure of the V2 finance reducer. A failure means the ordered facts cannot describe a live run.</summary>
public enum LedgerFinanceErrorCode
{
    VersionMismatch,
    RunMismatch,
    OutOfOrderFact,
    MalformedFact,
    UnknownAccount,
    UnknownPayment,
    ConflictingDuplicate,
    IllegalStep,
    InconsistentFact,
}

public sealed record LedgerFinanceError(LedgerFinanceErrorCode Code, string Message)
{
    public override string ToString() => $"{Code}: {Message}";
}

/// <summary>One run-scoped account of the V2 state: posted balance and the sum of its active reservations.</summary>
public sealed record LedgerAccount(string AccountId, string BankId, long PostedMinor, long ReservedMinor)
{
    public long AvailableMinor => PostedMinor - ReservedMinor;
}

/// <summary>
/// One payment of the V2 state. <see cref="Trail"/> is every status the payment entered, in order, so its length is the live
/// row version. <see cref="ReservedMinor"/> is the payment's own active hold (0 when none). <see cref="Case"/> is null when the
/// payment never opened a review case.
/// </summary>
public sealed record LedgerPayment(
    PaymentIntent Intent,
    long IntentFact,
    int Velocity15m,
    PaymentStatus Status,
    PaymentReason Reason,
    ImmutableArray<PaymentStatus> Trail,
    long ReservedMinor,
    CaseStatus? Case,
    int RequiredApprovals,
    int Approvals,
    bool SettlementPosted,
    bool ReturnPosted);

/// <summary>Complete V2 state (state schema <see cref="FinanceLedgerVersion.StateSchemaVersion"/>) after facts 1..<see cref="LastFact"/>.</summary>
public sealed record LedgerFinanceState(
    ScenarioRunId RunId,
    string ScenarioVersion,
    string ManifestContentHash,
    string Currency,
    long ClockUtcMs,
    long LastIntentSequence,
    long LastFact,
    ImmutableSortedDictionary<string, LedgerAccount> Accounts,
    ImmutableSortedDictionary<string, LedgerPayment> Payments,
    ImmutableSortedDictionary<string, ImmutableArray<long>> VelocityWindows)
{
    public long TotalPostedMinor => Accounts.Values.Sum(a => a.PostedMinor);
}

/// <summary>
/// Pinned context of a V2 history (context schema <see cref="FinanceLedgerVersion.ContextSchemaVersion"/>): the scenario
/// manifest, the reducer version, the screening policy and the review policy version. Stored durably with the history, so a cold
/// replay never uses a "current" default.
/// </summary>
public sealed partial record LedgerFinanceContext
{
    private LedgerFinanceContext(string scenarioVersion, string manifestHash, string reducerVersion, PolicyRef policyRef, PolicyConfig policy, string reviewPolicyVersion)
    {
        (ScenarioVersion, ManifestContentHash, ReducerVersion, PolicyRef, Policy, ReviewPolicyVersion) =
            (scenarioVersion, manifestHash, reducerVersion, policyRef, policy, reviewPolicyVersion);
    }

    /// <summary>The screening policy every durable V2 run is screened with. A different live policy is a new version.</summary>
    public static PolicyConfig LivePolicy => PolicyCatalog.V7;

    public string ScenarioVersion { get; }
    public string ManifestContentHash { get; }
    public string ReducerVersion { get; }
    public PolicyRef PolicyRef { get; }
    public PolicyConfig Policy { get; }
    public string ReviewPolicyVersion { get; }

    public static Result<LedgerFinanceContext, LedgerFinanceError> Pin(ScenarioManifest manifest, PolicyRef policy)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return ScenarioManifestCodec.HasValidContentHash(manifest)
            ? FromPins(manifest.ScenarioVersion, manifest.ContentHash, FinanceLedgerVersion.ReducerVersion, policy, ReviewPolicy.Version)
            : Fail("Scenario manifest content hash does not match its content.");
    }

    public static Result<LedgerFinanceContext, LedgerFinanceError> FromPins(
        string scenarioVersion, string manifestHash, string reducerVersion, PolicyRef policy, string reviewPolicyVersion)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!string.Equals(scenarioVersion, ScenarioSchemas.FridayRingVersion, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(manifestHash))
        {
            return Fail($"Scenario '{scenarioVersion}' is not supported by {FinanceLedgerVersion.ReducerVersion}.");
        }
        if (!string.Equals(reducerVersion, FinanceLedgerVersion.ReducerVersion, StringComparison.Ordinal))
        {
            return Fail($"Reducer version '{reducerVersion}' is not '{FinanceLedgerVersion.ReducerVersion}'.");
        }
        if (!string.Equals(reviewPolicyVersion, ReviewPolicy.Version, StringComparison.Ordinal))
        {
            return Fail($"Review policy '{reviewPolicyVersion}' is not '{ReviewPolicy.Version}'.");
        }
        var resolved = PolicyCatalog.Resolve(policy);
        if (!resolved.TryGetValue(out var config))
        {
            return Fail(resolved.Error.Message);
        }
        return Result<LedgerFinanceContext, LedgerFinanceError>.Success(
            new LedgerFinanceContext(scenarioVersion, manifestHash, reducerVersion, policy, config, reviewPolicyVersion));
    }

    public byte[] EncodeCanonical() => new CanonicalWriter()
        .WriteInt32(1)
        .WriteString(ScenarioVersion)
        .WriteString(ManifestContentHash)
        .WriteString(ReducerVersion)
        .WriteString(PolicyRef.PolicyId)
        .WriteString(PolicyRef.ConfigHash)
        .WriteString(PolicyRef.ModelArtifactHash)
        .WriteString(PolicyRef.FeatureSchemaHash)
        .WriteString(ReviewPolicyVersion)
        .ToArray();

    public string ContentHash => ScenarioManifestCodec.Sha256Hex(EncodeCanonical());

    private static Result<LedgerFinanceContext, LedgerFinanceError> Fail(string message) =>
        Result<LedgerFinanceContext, LedgerFinanceError>.Failure(new LedgerFinanceError(LedgerFinanceErrorCode.VersionMismatch, message));
}

/// <summary>
/// Pure V2 finance reducer (<see cref="FinanceLedgerVersion.ReducerVersion"/>, ADR-P1-17). It folds a durable run's ordered
/// canonical facts (<see cref="FinanceFact"/>) into the payment, reservation, case and posted-balance state the live ledger must
/// hold after the same facts. It reads no clock, randomness, database or ambient setting. Facts are inputs; every decision is
/// derived here from the pinned context:
/// <list type="bullet">
/// <item><b>PaymentIntent</b>: a new Initiated payment; its payer velocity is fixed from the intents before it.</item>
/// <item><b>Screening</b>: Initiated → Screened, then the pinned policy decides. A hold reserves the amount on the source and
/// opens a review case when the source's available balance covers it, otherwise Held → Rejected(insufficient). No hold → Cleared.</item>
/// <item><b>ReviewDecision</b>: a committed human decision (exogenous). Approvals are counted; the final one moves Held → Approved;
/// a rejection moves Held → Rejected and releases the reservation.</item>
/// <item><b>Settlement</b>: Approved → Cleared (when approved), then Settled with a source→destination posting when the source's
/// available balance plus the payment's own reservation covers it, else Returned(insufficient); the reservation is resolved once.</item>
/// <item><b>ReturnInstruction</b>: Settled → Returned with the mirror posting.</item>
/// </list>
/// </summary>
public static class FinanceLedgerReducer
{
    public static Result<LedgerFinanceState, LedgerFinanceError> Initial(ScenarioRun run, LedgerFinanceContext context)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(context);
        if (!string.Equals(run.Manifest.ContentHash, context.ManifestContentHash, StringComparison.Ordinal)
            || !string.Equals(run.Manifest.ScenarioVersion, context.ScenarioVersion, StringComparison.Ordinal))
        {
            return Fail(LedgerFinanceErrorCode.VersionMismatch, "Run manifest does not match the pinned context.");
        }
        var accounts = ImmutableSortedDictionary.CreateBuilder<string, LedgerAccount>(StringComparer.Ordinal);
        foreach (var account in run.InitialState.Accounts)
        {
            if (!string.Equals(account.OpeningBalance.Currency, context.Policy.Currency, StringComparison.Ordinal))
            {
                return Fail(LedgerFinanceErrorCode.VersionMismatch, $"Account {account.AccountId} is not in {context.Policy.Currency}.");
            }
            accounts.Add(account.AccountId, new LedgerAccount(account.AccountId, account.BankId, account.OpeningBalance.MinorUnits, 0));
        }
        // The business clock starts where the live run cursor starts (0): intake orders intents by sequence and non-decreasing
        // time, not by the scenario's nominal start, and the fold must accept exactly what the live ledger accepted.
        return Ok(new LedgerFinanceState(
            run.RunId, run.Manifest.ScenarioVersion, run.Manifest.ContentHash, context.Policy.Currency, 0, 0, 0,
            accounts.ToImmutable(),
            ImmutableSortedDictionary.Create<string, LedgerPayment>(StringComparer.Ordinal),
            ImmutableSortedDictionary.Create<string, ImmutableArray<long>>(StringComparer.Ordinal)));
    }

    /// <summary>Applies fact <c>LastFact + 1</c>. Anything else (gap, repeat, reorder) is <see cref="LedgerFinanceErrorCode.OutOfOrderFact"/>.</summary>
    public static Result<LedgerFinanceState, LedgerFinanceError> Apply(LedgerFinanceState state, FinanceFact fact, LedgerFinanceContext context)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(fact);
        ArgumentNullException.ThrowIfNull(context);
        if (!string.Equals(context.ReducerVersion, FinanceLedgerVersion.ReducerVersion, StringComparison.Ordinal)
            || !string.Equals(context.ManifestContentHash, state.ManifestContentHash, StringComparison.Ordinal)
            || !string.Equals(context.ScenarioVersion, state.ScenarioVersion, StringComparison.Ordinal)
            || !string.Equals(context.Policy.Currency, state.Currency, StringComparison.Ordinal))
        {
            return Fail(LedgerFinanceErrorCode.VersionMismatch, "Pinned context is not compatible with this state.");
        }
        if (fact.Sequence != state.LastFact + 1)
        {
            return Fail(LedgerFinanceErrorCode.OutOfOrderFact, $"Fact {fact.Sequence} does not follow fact {state.LastFact}.");
        }
        Result<LedgerFinanceState, LedgerFinanceError> next;
        try
        {
            next = fact.Kind switch
            {
                FinanceFactKind.PaymentIntent => Expect(fact, ScenarioSchemas.InputSchemaVersion)
                    ?? ApplyIntent(state, PaymentIntentCodec.Instance.Decode(fact.Payload), fact.Sequence, context),
                FinanceFactKind.Screening => Expect(fact, PaymentStepCodec.ScreeningSchema)
                    ?? ApplyScreening(state, PaymentStepCodec.Decode(fact.Payload), context),
                FinanceFactKind.ReviewDecision => Expect(fact, ReviewDecisionCodec.SchemaVersion)
                    ?? ApplyReview(state, ReviewDecisionCodec.Decode(fact.Payload), context),
                FinanceFactKind.Settlement => Expect(fact, PaymentStepCodec.SettlementSchema)
                    ?? ApplySettlement(state, PaymentStepCodec.Decode(fact.Payload)),
                FinanceFactKind.ReturnInstruction => Expect(fact, ReturnInstructionCodec.SchemaVersion)
                    ?? ApplyReturn(state, ReturnInstructionCodec.Decode(fact.Payload)),
                _ => Fail(LedgerFinanceErrorCode.MalformedFact, $"Unknown fact kind {(byte)fact.Kind}."),
            };
        }
        catch (CanonicalFormatException ex)
        {
            return Fail(LedgerFinanceErrorCode.MalformedFact, $"Fact {fact.Sequence}: {ex.Message}");
        }
        return next.TryGetValue(out var value) ? Ok(value with { LastFact = fact.Sequence }) : next;
    }

    public static Result<LedgerFinanceState, LedgerFinanceError> ApplyAll(
        LedgerFinanceState state, IEnumerable<FinanceFact> facts, LedgerFinanceContext context)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var current = state;
        foreach (var fact in facts)
        {
            var next = Apply(current, fact, context);
            if (!next.TryGetValue(out current!))
            {
                return next;
            }
        }
        return Ok(current);
    }

    /// <summary>
    /// The payer velocity of an intent under <paramref name="policy"/>: earlier intents from the same source account (in fact order)
    /// whose business time is inside the closed window ending at this intent, plus the intent itself. Used by the reducer and by the
    /// live screening step, so both count the same intents.
    /// </summary>
    public static int Velocity(IEnumerable<long> earlierTimesSameSource, long now, PolicyConfig policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var windowStart = now - policy.VelocityWindowMs;
        return earlierTimesSameSource.Count(t => t >= windowStart) + 1;
    }

    /// <summary>The pinned policy's hold reason for a screened payment (<see cref="PaymentReason.None"/> clears it).</summary>
    public static PaymentReason ScreeningReason(PaymentIntent intent, int velocity, PolicyConfig policy) =>
        policy.Screen(new ScreeningFeatures(velocity, intent.Amount.MinorUnits, intent.Features.NewBeneficiary, intent.Features.SanctionsHit));

    private static Result<LedgerFinanceState, LedgerFinanceError> ApplyIntent(
        LedgerFinanceState state, PaymentIntent intent, long factSeq, LedgerFinanceContext context)
    {
        if (intent.RunId != state.RunId)
        {
            return Fail(LedgerFinanceErrorCode.RunMismatch, $"Intent {intent.IntentId} belongs to another run.");
        }
        if (state.Payments.ContainsKey(intent.IntentId))
        {
            return Fail(LedgerFinanceErrorCode.ConflictingDuplicate, $"Intent {intent.IntentId} already has a payment.");
        }
        if (intent.Sequence <= state.LastIntentSequence || intent.EventTimeUtcMs < state.ClockUtcMs)
        {
            return Fail(LedgerFinanceErrorCode.OutOfOrderFact, $"Intent {intent.IntentId} does not follow the last accepted intent.");
        }
        if (!state.Accounts.TryGetValue(intent.SourceAccountId, out var source) || source.BankId != intent.SourceBankId
            || !state.Accounts.TryGetValue(intent.DestAccountId, out var dest) || dest.BankId != intent.DestBankId)
        {
            return Fail(LedgerFinanceErrorCode.UnknownAccount, $"Intent {intent.IntentId} names an account outside this run.");
        }
        if (!string.Equals(intent.Amount.Currency, state.Currency, StringComparison.Ordinal))
        {
            return Fail(LedgerFinanceErrorCode.InconsistentFact, $"Intent {intent.IntentId} is not in {state.Currency}; intake has no FX path.");
        }
        var now = intent.EventTimeUtcMs;
        var prior = state.VelocityWindows.TryGetValue(intent.SourceAccountId, out var w) ? w : [];
        var velocity = Velocity(prior, now, context.Policy);
        // Clock is non-decreasing, so times before the window start never re-enter a later window.
        var window = prior.Where(t => t >= now - context.Policy.VelocityWindowMs).Append(now).ToImmutableArray();
        var payment = new LedgerPayment(intent, factSeq, velocity, PaymentStatus.Initiated, PaymentReason.None,
            [PaymentStatus.Initiated], 0, null, 0, 0, false, false);
        return Ok(state with
        {
            ClockUtcMs = now,
            LastIntentSequence = intent.Sequence,
            Payments = state.Payments.Add(intent.IntentId, payment),
            VelocityWindows = state.VelocityWindows.SetItem(intent.SourceAccountId, window),
        });
    }

    private static Result<LedgerFinanceState, LedgerFinanceError> ApplyScreening(
        LedgerFinanceState state, string intentId, LedgerFinanceContext context)
    {
        if (!state.Payments.TryGetValue(intentId, out var p))
        {
            return Fail(LedgerFinanceErrorCode.UnknownPayment, $"Screening names unknown intent {intentId}.");
        }
        if (p.Status != PaymentStatus.Initiated)
        {
            return Fail(LedgerFinanceErrorCode.IllegalStep, $"Intent {intentId} is {p.Status}, not Initiated, at screening.");
        }
        var reason = ScreeningReason(p.Intent, p.Velocity15m, context.Policy);
        var source = state.Accounts[p.Intent.SourceAccountId];
        var amount = p.Intent.Amount.MinorUnits;
        if (reason == PaymentReason.None)
        {
            return Ok(Put(state, p with { Status = PaymentStatus.Cleared, Trail = [.. p.Trail, PaymentStatus.Screened, PaymentStatus.Cleared] }));
        }
        if (source.AvailableMinor < amount)
        {
            return Ok(Put(state, p with
            {
                Status = PaymentStatus.Rejected,
                Reason = PaymentReason.InsufficientAvailable,
                Trail = [.. p.Trail, PaymentStatus.Screened, PaymentStatus.Held, PaymentStatus.Rejected],
            }));
        }
        var held = Put(state, p with
        {
            Status = PaymentStatus.Held,
            Reason = reason,
            Trail = [.. p.Trail, PaymentStatus.Screened, PaymentStatus.Held],
            ReservedMinor = amount,
            Case = CaseStatus.Open,
            RequiredApprovals = ReviewPolicy.RequiredApprovals(p.Intent.Amount.Currency, amount),
        });
        return Ok(held with { Accounts = held.Accounts.SetItem(source.AccountId, source with { ReservedMinor = checked(source.ReservedMinor + amount) }) });
    }

    private static Result<LedgerFinanceState, LedgerFinanceError> ApplyReview(
        LedgerFinanceState state, ReviewDecisionFact fact, LedgerFinanceContext context)
    {
        if (!state.Payments.TryGetValue(fact.IntentId, out var p))
        {
            return Fail(LedgerFinanceErrorCode.UnknownPayment, $"Review names unknown intent {fact.IntentId}.");
        }
        if (p.Status != PaymentStatus.Held || p.Case is not (CaseStatus.Open or CaseStatus.AwaitingSecondApproval))
        {
            return Fail(LedgerFinanceErrorCode.IllegalStep, $"Intent {fact.IntentId} has no open review on a held payment.");
        }
        if (!string.Equals(fact.PolicyVersion, context.ReviewPolicyVersion, StringComparison.Ordinal) || fact.RequiredApprovals != p.RequiredApprovals)
        {
            return Fail(LedgerFinanceErrorCode.InconsistentFact, $"Review of {fact.IntentId} does not match the pinned review policy.");
        }
        if (fact.Action == ReviewDecisionAction.Reject)
        {
            if (fact.ResultStatus != nameof(CaseStatus.Rejected))
            {
                return Fail(LedgerFinanceErrorCode.InconsistentFact, $"Rejection of {fact.IntentId} records {fact.ResultStatus}.");
            }
            var rejected = Put(state, p with
            {
                Status = PaymentStatus.Rejected,
                Trail = [.. p.Trail, PaymentStatus.Rejected],
                ReservedMinor = 0,
                Case = CaseStatus.Rejected,
            });
            return Ok(Release(rejected, p.Intent.SourceAccountId, p.ReservedMinor));
        }
        var approvals = p.Approvals + 1;
        var released = approvals == p.RequiredApprovals;
        var expected = released ? CaseStatus.Released : CaseStatus.AwaitingSecondApproval;
        if (fact.ApprovalOrder != approvals || fact.ResultStatus != expected.ToString())
        {
            return Fail(LedgerFinanceErrorCode.InconsistentFact,
                $"Approval {fact.ApprovalOrder} of {fact.IntentId} recording {fact.ResultStatus} does not follow {p.Approvals} of {p.RequiredApprovals}.");
        }
        return Ok(Put(state, released
            ? p with { Approvals = approvals, Case = expected, Status = PaymentStatus.Approved, Trail = [.. p.Trail, PaymentStatus.Approved] }
            : p with { Approvals = approvals, Case = expected }));
    }

    private static Result<LedgerFinanceState, LedgerFinanceError> ApplySettlement(LedgerFinanceState state, string intentId)
    {
        if (!state.Payments.TryGetValue(intentId, out var p))
        {
            return Fail(LedgerFinanceErrorCode.UnknownPayment, $"Settlement names unknown intent {intentId}.");
        }
        if (p.Status is not (PaymentStatus.Approved or PaymentStatus.Cleared))
        {
            return Fail(LedgerFinanceErrorCode.IllegalStep, $"Intent {intentId} is {p.Status}, not Approved or Cleared, at settlement.");
        }
        var trail = p.Status == PaymentStatus.Approved ? [.. p.Trail, PaymentStatus.Cleared] : p.Trail;
        var amount = p.Intent.Amount.MinorUnits;
        var source = state.Accounts[p.Intent.SourceAccountId];
        // The payment's own reservation protected exactly these funds, so it counts toward its own settlement.
        if (source.AvailableMinor + p.ReservedMinor >= amount)
        {
            var dest = state.Accounts[p.Intent.DestAccountId];
            var accounts = state.Accounts.SetItem(source.AccountId, source with
            {
                PostedMinor = checked(source.PostedMinor - amount),
                ReservedMinor = checked(source.ReservedMinor - p.ReservedMinor),
            });
            dest = accounts[dest.AccountId];
            accounts = accounts.SetItem(dest.AccountId, dest with { PostedMinor = checked(dest.PostedMinor + amount) });
            return Ok(Put(state with { Accounts = accounts }, p with
            {
                Status = PaymentStatus.Settled,
                Trail = [.. trail, PaymentStatus.Settled],
                ReservedMinor = 0,
                SettlementPosted = true,
            }));
        }
        var returned = Put(state, p with
        {
            Status = PaymentStatus.Returned,
            Reason = PaymentReason.InsufficientAvailable,
            Trail = [.. trail, PaymentStatus.Returned],
            ReservedMinor = 0,
        });
        return Ok(Release(returned, p.Intent.SourceAccountId, p.ReservedMinor));
    }

    private static Result<LedgerFinanceState, LedgerFinanceError> ApplyReturn(LedgerFinanceState state, ReturnInstructionFact fact)
    {
        if (!state.Payments.TryGetValue(fact.IntentId, out var p))
        {
            return Fail(LedgerFinanceErrorCode.UnknownPayment, $"Return names unknown intent {fact.IntentId}.");
        }
        if (p.Status != PaymentStatus.Settled)
        {
            return Fail(LedgerFinanceErrorCode.IllegalStep, $"Intent {fact.IntentId} is {p.Status}; only a Settled payment is returned.");
        }
        var amount = p.Intent.Amount.MinorUnits;
        var dest = state.Accounts[p.Intent.DestAccountId];
        if (dest.AvailableMinor < amount)
        {
            // The live ledger refuses such a return and records no fact, so a fact here contradicts the fold.
            return Fail(LedgerFinanceErrorCode.InconsistentFact, $"Return of {fact.IntentId} exceeds the destination's available balance.");
        }
        var accounts = state.Accounts.SetItem(dest.AccountId, dest with { PostedMinor = dest.PostedMinor - amount });
        var source = accounts[p.Intent.SourceAccountId];
        accounts = accounts.SetItem(source.AccountId, source with { PostedMinor = checked(source.PostedMinor + amount) });
        return Ok(Put(state with { Accounts = accounts }, p with
        {
            Status = PaymentStatus.Returned,
            Trail = [.. p.Trail, PaymentStatus.Returned],
            ReturnPosted = true,
        }));
    }

    private static LedgerFinanceState Put(LedgerFinanceState state, LedgerPayment payment) =>
        state with { Payments = state.Payments.SetItem(payment.Intent.IntentId, payment) };

    private static LedgerFinanceState Release(LedgerFinanceState state, string accountId, long amount)
    {
        if (amount == 0)
        {
            return state;
        }
        var account = state.Accounts[accountId];
        return state with { Accounts = state.Accounts.SetItem(accountId, account with { ReservedMinor = account.ReservedMinor - amount }) };
    }

    private static Result<LedgerFinanceState, LedgerFinanceError>? Expect(FinanceFact fact, string schema) =>
        string.Equals(fact.PayloadSchema, schema, StringComparison.Ordinal)
            ? null
            : Fail(LedgerFinanceErrorCode.MalformedFact, $"Fact {fact.Sequence} ({fact.Kind}) has payload schema '{fact.PayloadSchema}', not '{schema}'.");

    private static Result<LedgerFinanceState, LedgerFinanceError> Ok(LedgerFinanceState state) =>
        Result<LedgerFinanceState, LedgerFinanceError>.Success(state);

    private static Result<LedgerFinanceState, LedgerFinanceError> Fail(LedgerFinanceErrorCode code, string message) =>
        Result<LedgerFinanceState, LedgerFinanceError>.Failure(new LedgerFinanceError(code, message));
}
