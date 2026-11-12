using System.Collections.Immutable;

namespace Revenant.Contracts;

/// <summary>Schema and scenario version names pinned by every Friday Ring run.</summary>
public static class ScenarioSchemas
{
    public const string FridayRingId = "friday-ring";
    public const string FridayRingVersion = "friday-ring/1";
    public const string InputSchemaVersion = "revenant-payment-intent/1";
    public const string FeatureSchemaVersion = "revenant-features/1";
    public const string LabelSchemaVersion = "revenant-labels/1";
}

public sealed record BankDescriptor(string BankId, string Name, Money OpeningBalance);

public sealed record AccountDescriptor(string AccountId, string BankId, Money OpeningBalance);

/// <summary>
/// Run-independent description of a seeded scenario: seed, banks, accounts, opening balances, label and
/// feature schema, version and content hash. It contains no <see cref="ScenarioRunId"/>.
/// </summary>
public sealed record ScenarioManifest(
    string ScenarioId,
    string ScenarioVersion,
    string InputSchemaVersion,
    string FeatureSchemaVersion,
    ImmutableArray<string> FeatureNames,
    string LabelSchemaVersion,
    ImmutableArray<string> LabelNames,
    long Seed,
    long StartUtcMs,
    ImmutableArray<BankDescriptor> Banks,
    ImmutableArray<AccountDescriptor> Accounts,
    string ContentHash)
{
    /// <summary>Duration pinned by the V4 behavioral episode manifest extension.</summary>
    public long? V4EpisodeDurationMs { get; init; }
}

public sealed record RunBankState(string BankId, string RunScopedKey, string Name);

public sealed record RunAccountState(string AccountId, string BankId, string RunScopedKey, Money OpeningBalance);

/// <summary>
/// The state namespace and opening balance snapshot owned by exactly one run. A new run never reuses or
/// resets another run's accounts.
/// </summary>
public sealed record ScenarioRunState(
    ScenarioRunId RunId,
    ImmutableArray<RunBankState> Banks,
    ImmutableArray<RunAccountState> Accounts);

/// <summary>
/// One generated run: manifest, run-scoped opening state and the immutable ordered intent stream.
/// <see cref="ExogenousFactsHash"/> ignores run identity, so equal seed and version give equal values.
/// </summary>
public sealed record ScenarioRun(
    ScenarioRunId RunId,
    ScenarioManifest Manifest,
    ScenarioRunState InitialState,
    ImmutableArray<PaymentIntent> Intents,
    string ExogenousFactsHash);

public enum ScenarioErrorCode
{
    InvalidRunId,
    UnsupportedScenarioVersion,
}

public sealed record ScenarioError(ScenarioErrorCode Code, string Message);
