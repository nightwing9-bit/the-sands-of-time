namespace Revenant.Contracts;

/// <summary>
/// Identifies one seeded Friday Ring execution. Every run owns its own bank/account state and every
/// scenario identifier is scoped to it. The value is supplied by the caller; the scenario generator never
/// invents one, so generation stays deterministic.
/// </summary>
public readonly record struct ScenarioRunId(Guid Value)
{
    public bool IsEmpty => Value == Guid.Empty;

    /// <summary>Stable run-scoped key for a scenario-local id: <c>{runId:N}/{localId}</c>.</summary>
    public string Scope(string localId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localId);
        return $"{Value:N}/{localId}";
    }

    public override string ToString() => Value.ToString("N");
}

/// <summary>
/// Synthetic ground truth assigned by the scenario generator. It is never a real-world fraud finding,
/// and policies must not read it; only metrics do.
/// </summary>
public enum SyntheticTruth : byte
{
    Legitimate = 1,
    FraudMuleRing = 2,
}

/// <summary>
/// Exogenous feature inputs, schema <see cref="ScenarioSchemas.FeatureSchemaVersion"/>.
/// Velocity is derived by the reducer from the intent stream and is deliberately not an input.
/// </summary>
public readonly record struct PaymentFeatures(bool NewBeneficiary, bool SanctionsHit);

/// <summary>
/// Immutable exogenous payment fact. Carries no decision, hold or settlement result, so a branch can
/// replay it under a different policy. <see cref="Sequence"/> is the logical ordering authority;
/// <see cref="EventTimeUtcMs"/> is business time only.
/// </summary>
public sealed record PaymentIntent
{
    public PaymentIntent(
        ScenarioRunId runId,
        string intentId,
        long sequence,
        long eventTimeUtcMs,
        string sourceBankId,
        string sourceAccountId,
        string destBankId,
        string destAccountId,
        Money amount,
        SyntheticTruth truth,
        PaymentFeatures features)
    {
        if (runId.IsEmpty)
        {
            throw new ArgumentException("ScenarioRunId must not be empty.", nameof(runId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(intentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceBankId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceAccountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(destBankId);
        ArgumentException.ThrowIfNullOrWhiteSpace(destAccountId);
        ArgumentOutOfRangeException.ThrowIfLessThan(sequence, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(eventTimeUtcMs);
        if (string.Equals(sourceAccountId, destAccountId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Source and destination accounts must differ.", nameof(destAccountId));
        }

        if (amount.Currency is null || amount.MinorUnits <= 0)
        {
            throw new ArgumentException("Amount must be a positive Money value.", nameof(amount));
        }

        if (!Enum.IsDefined(truth))
        {
            throw new ArgumentException("Unknown synthetic truth label.", nameof(truth));
        }

        RunId = runId;
        IntentId = intentId;
        Sequence = sequence;
        EventTimeUtcMs = eventTimeUtcMs;
        SourceBankId = sourceBankId;
        SourceAccountId = sourceAccountId;
        DestBankId = destBankId;
        DestAccountId = destAccountId;
        Amount = amount;
        Truth = truth;
        Features = features;
    }

    public ScenarioRunId RunId { get; }

    /// <summary>Scenario-local id, equal across runs of the same seed and version.</summary>
    public string IntentId { get; }

    public long Sequence { get; }

    public long EventTimeUtcMs { get; }

    public string SourceBankId { get; }

    public string SourceAccountId { get; }

    public string DestBankId { get; }

    public string DestAccountId { get; }

    public Money Amount { get; }

    public SyntheticTruth Truth { get; }

    public PaymentFeatures Features { get; }

    /// <summary>Run-scoped intent id and idempotency/business key: <c>(ScenarioRunId, intentId)</c>.</summary>
    public string RunScopedKey => RunId.Scope(IntentId);
}
