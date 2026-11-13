using System.Collections.Immutable;
using System.Globalization;
using Marrow.Kernel.Canonical;
using Revenant.Contracts;
using Revenant.Finance;
using Revenant.Timeline;

namespace Revenant.Scenario;

internal static class BehavioralEpisodeScenario
{
    internal const string Version = "revenant-behavioral-episodes/1";
    internal const string GeneratorVersion = "revenant-episode-generator/1";
    internal const string GeneratorVersionV2 = "revenant-episode-generator/2";
    internal const string GeneratorVersionV3 = "revenant-episode-generator/3";
    internal const int BankCount = 6;
    private const ulong PopulationStream = 0x52563401;
    private const string ManifestExtension = "revenant-behavioral-episode-manifest/1";
    private static readonly ImmutableArray<string> Commands =
        ["PaymentIntent", "ReviewDecision", "ReturnInstruction", "Screening", "Settlement"];

    internal static ScenarioRun Create(ScenarioRunId runId, long seed, long originMs, long durationMs,
        ImmutableArray<int> accountsPerBank, string? generator = null)
    {
        if (runId.IsEmpty || !ValidDimensions(originMs, durationMs, accountsPerBank) || !ValidGenerator(generator))
        {
            throw new ArgumentException("Expected a run ID, aligned UTC episode and a valid population for all six banks.");
        }

        var manifest = BuildManifest(seed, originMs, durationMs, accountsPerBank, generator);
        return new(runId, manifest, Opening(manifest, runId), [], ScenarioManifestCodec.ComputeFactsHash(manifest, []));
    }

    internal static bool MatchesOpening(ScenarioRun run, long originMs)
    {
        var m = run.Manifest;
        if (m.ScenarioVersion != Version || m.StartUtcMs != originMs || m.V4EpisodeDurationMs is not { } duration
            || !ValidGenerator(m.V4EpisodeGenerator)
            || m.Banks.IsDefault || m.Accounts.IsDefault || m.Banks.Length != BankCount)
        {
            return false;
        }

        var populations = Enumerable.Range(0, BankCount)
            .Select(i => m.Accounts.Count(a => a.BankId == FridayRingScenario.BankId(i))).ToImmutableArray();
        if (!ValidDimensions(originMs, duration, populations) || populations.Sum() != m.Accounts.Length)
        {
            return false;
        }

        var expected = BuildManifest(m.Seed, originMs, duration, populations, m.V4EpisodeGenerator);
        var opening = Opening(expected, run.RunId);
        return m.ContentHash == expected.ContentHash
            && !m.FeatureNames.IsDefault && !m.LabelNames.IsDefault
            && EncodeManifest(m).AsSpan().SequenceEqual(EncodeManifest(expected))
            && !run.InitialState.Banks.IsDefault && run.InitialState.Banks.SequenceEqual(opening.Banks)
            && !run.InitialState.Accounts.IsDefault && run.InitialState.Accounts.SequenceEqual(opening.Accounts);
    }

    internal static byte[] EncodeManifest(ScenarioManifest manifest)
    {
        if (manifest.ScenarioVersion != Version || manifest.V4EpisodeDurationMs is not { } duration
            || !ValidGenerator(manifest.V4EpisodeGenerator))
        {
            throw new ArgumentException("Expected a behavioral episode manifest.", nameof(manifest));
        }

        // Reuse the existing manifest encoding; only V4 needs the duration and execution contract trailer.
        var policy = PolicyCatalog.RefOf(PolicyCatalog.V7);
        var w = new CanonicalWriter().WriteInt32(1).WriteString(ManifestExtension)
            .WriteBytes(ScenarioManifestCodec.EncodeContent(manifest))
            .WriteString(manifest.V4EpisodeGenerator ?? GeneratorVersion).WriteInt64(duration)
            .WriteString(policy.PolicyId).WriteString(policy.ConfigHash).WriteString(policy.ModelArtifactHash)
            .WriteString(policy.FeatureSchemaHash).WriteString(Cases.ReviewPolicy.Version).WriteCount(Commands.Length);
        foreach (var command in Commands)
        {
            w.WriteString(command);
        }
        return w.ToArray();
    }

    private static bool ValidDimensions(long originMs, long durationMs, ImmutableArray<int> populations) =>
        originMs >= 0 && ObservedFinanceVersion.IsBoundary(originMs) && durationMs > 0
        && durationMs <= DateTimeOffset.MaxValue.ToUnixTimeMilliseconds() - originMs
        && ObservedFinanceVersion.IsBoundary(durationMs)
        && !populations.IsDefault && populations.Length == BankCount && populations.All(n => n is >= 2 and <= 1_000);

    private static bool ValidGenerator(string? generator) => generator is null or GeneratorVersionV2 or GeneratorVersionV3;

    private static ScenarioManifest BuildManifest(long seed, long origin, long duration, ImmutableArray<int> populations,
        string? generator)
    {
        var random = SeededRandom.ForStream(seed, PopulationStream);
        var banks = ImmutableArray.CreateBuilder<BankDescriptor>(BankCount);
        var accounts = ImmutableArray.CreateBuilder<AccountDescriptor>(populations.Sum());
        var accountNamespace = unchecked((ulong)seed).ToString("x16", CultureInfo.InvariantCulture);
        for (var i = 0; i < BankCount; i++)
        {
            var bank = FridayRingScenario.BankId(i);
            long total = 0;
            for (var j = 0; j < populations[i]; j++)
            {
                var amount = random.NextInt(500_000, 5_000_001);
                var account = string.Create(CultureInfo.InvariantCulture, $"{bank}/{accountNamespace}/ACC-{j + 1:D4}");
                total = checked(total + amount);
                accounts.Add(new(account, bank, Money.Usd(amount)));
            }
            banks.Add(new(bank, FridayRingScenario.BankName(i), Money.Usd(total)));
        }

        var m = new ScenarioManifest("revenant-behavioral-episodes", Version, ScenarioSchemas.InputSchemaVersion,
            ScenarioSchemas.FeatureSchemaVersion, ["newBeneficiary", "sanctionsHit"], ScenarioSchemas.LabelSchemaVersion,
            [nameof(SyntheticTruth.Legitimate)], seed, origin, banks.MoveToImmutable(), accounts.MoveToImmutable(), "")
        {
            V4EpisodeDurationMs = duration,
            V4EpisodeGenerator = generator,
        };
        return m with { ContentHash = ScenarioManifestCodec.Sha256Hex(EncodeManifest(m)) };
    }

    private static ScenarioRunState Opening(ScenarioManifest manifest, ScenarioRunId runId) => new(runId,
        [.. manifest.Banks.Select(b => new RunBankState(b.BankId, runId.Scope(b.BankId), b.Name))],
        [.. manifest.Accounts.Select(a => new RunAccountState(a.AccountId, a.BankId, runId.Scope(a.AccountId), a.OpeningBalance))]);
}
