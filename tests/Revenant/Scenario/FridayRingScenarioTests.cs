using System.Collections.Immutable;
using Revenant.Contracts;
using Revenant.Scenario;
using Xunit;

namespace Revenant.Tests.Scenario;

/// <summary>Task 5.1. **Validates: Requirements R05.01, R05.08, R05.09, R05.10**</summary>
public sealed class FridayRingScenarioTests
{
    private const long FifteenMinutesMs = 15 * 60 * 1000L;
    private static readonly ScenarioRunId RunA = new(Guid.Parse("00000000-0000-0000-0000-00000000000a"));
    private static readonly ScenarioRunId RunB = new(Guid.Parse("00000000-0000-0000-0000-00000000000b"));

    internal static ScenarioRun Create(long seed, ScenarioRunId run) =>
        FridayRingScenario.Create(seed, ScenarioSchemas.FridayRingVersion, run).Value;

    [Fact]
    public void Same_seed_and_version_give_identical_facts_for_two_run_ids()
    {
        var a = Create(42, RunA);
        var b = Create(42, RunB);

        Assert.Equal(ScenarioManifestCodec.EncodeContent(a.Manifest), ScenarioManifestCodec.EncodeContent(b.Manifest));
        Assert.Equal(a.Manifest.ContentHash, b.Manifest.ContentHash);
        Assert.Equal(a.ExogenousFactsHash, b.ExogenousFactsHash);
        Assert.Equal(a.Intents.Length, b.Intents.Length);
        for (var i = 0; i < a.Intents.Length; i++)
        {
            Assert.Equal(PaymentIntentCodec.EncodeFacts(a.Intents[i]), PaymentIntentCodec.EncodeFacts(b.Intents[i]));
            Assert.Equal(RunA, a.Intents[i].RunId);
            Assert.Equal(RunB, b.Intents[i].RunId);
        }

        // Repeating a run id reproduces the run exactly; nothing was mutated or reset in between.
        Assert.Equal(a.Intents.Select(PaymentIntentCodec.Instance.EncodeCanonical).Select(Convert.ToHexString),
            Create(42, RunA).Intents.Select(PaymentIntentCodec.Instance.EncodeCanonical).Select(Convert.ToHexString));
    }

    [Fact]
    public void Facts_hash_is_pinned_for_seed_42_version_1()
    {
        // Golden value: changing it means the pinned scenario version's facts changed and a new version is required.
        Assert.Equal(GoldenFactsHashSeed42, Create(42, RunA).ExogenousFactsHash);
    }

    private const string GoldenFactsHashSeed42 = "sha256:23ed8472b0d359a58ab81cbbc51786816dc4573e0854e7d9090f34e7eb9da1fd";

    [Fact]
    public void Different_seed_changes_facts_but_keeps_the_ring_structure()
    {
        var a = Create(42, RunA);
        var b = Create(43, RunA);

        Assert.NotEqual(a.ExogenousFactsHash, b.ExogenousFactsHash);
        Assert.NotEqual(a.Manifest.ContentHash, b.Manifest.ContentHash);
        Assert.NotEqual(a.Intents.Select(i => i.Amount.MinorUnits), b.Intents.Select(i => i.Amount.MinorUnits));
        Assert.NotEqual(a.Intents.Select(i => i.EventTimeUtcMs), b.Intents.Select(i => i.EventTimeUtcMs));
        Assert.Equal(RingShape(a), RingShape(b));
        Assert.Equal(a.Intents.Length, b.Intents.Length);
    }

    [Fact]
    public void Distinct_runs_never_share_intent_bank_or_account_keys()
    {
        var a = Create(42, RunA);
        var b = Create(42, RunB);

        static IEnumerable<string> Keys(ScenarioRun r) => r.Intents.Select(i => i.RunScopedKey)
            .Concat(r.InitialState.Banks.Select(x => x.RunScopedKey))
            .Concat(r.InitialState.Accounts.Select(x => x.RunScopedKey));

        var keysA = Keys(a).ToList();
        var keysB = Keys(b).ToList();
        Assert.Equal(keysA.Count, keysA.Distinct(StringComparer.Ordinal).Count());
        Assert.Empty(keysA.Intersect(keysB, StringComparer.Ordinal));
        Assert.Equal(RunA, a.InitialState.RunId);
        Assert.Equal(RunB, b.InitialState.RunId);
        Assert.Equal(a.InitialState.Accounts.Select(x => x.OpeningBalance), b.InitialState.Accounts.Select(x => x.OpeningBalance));
    }

    [Fact]
    public void Manifest_has_six_banks_with_consistent_opening_balances_and_a_valid_hash()
    {
        var m = Create(7, RunA).Manifest;

        Assert.Equal(6, m.Banks.Length);
        Assert.Equal(m.Banks.Length * 3, m.Accounts.Length);
        Assert.All(m.Banks, bank => Assert.Equal(
            bank.OpeningBalance.MinorUnits,
            m.Accounts.Where(a => a.BankId == bank.BankId).Sum(a => a.OpeningBalance.MinorUnits)));
        Assert.All(m.Accounts, a => Assert.Equal("USD", a.OpeningBalance.Currency));
        Assert.Equal(ScenarioSchemas.FridayRingVersion, m.ScenarioVersion);
        Assert.Equal(ScenarioSchemas.InputSchemaVersion, m.InputSchemaVersion);
        Assert.Equal(["newBeneficiary", "sanctionsHit"], m.FeatureNames.ToArray());
        Assert.Equal(["Legitimate", "FraudMuleRing"], m.LabelNames.ToArray());
        Assert.Equal(7, m.Seed);
        Assert.True(ScenarioManifestCodec.HasValidContentHash(m));
        Assert.False(ScenarioManifestCodec.HasValidContentHash(m with { Seed = 8 }));
    }

    [Fact]
    public void Stream_is_ordered_and_contains_a_labelled_chain_ring_with_interleaved_legitimate_traffic()
    {
        var run = Create(42, RunA);
        var intents = run.Intents;

        Assert.Equal(Enumerable.Range(1, intents.Length).Select(i => (long)i), intents.Select(i => i.Sequence));
        Assert.Equal(intents.Select(i => i.EventTimeUtcMs).Order(), intents.Select(i => i.EventTimeUtcMs));
        Assert.Equal(intents.Length, intents.Select(i => i.IntentId).Distinct().Count());
        Assert.All(intents, i => Assert.Equal("USD", i.Amount.Currency));
        Assert.All(intents, i => Assert.False(i.Features.SanctionsHit));

        var ring = intents.Where(i => i.Truth == SyntheticTruth.FraudMuleRing).ToList();
        Assert.Equal(30, ring.Count);
        var hops = ring.GroupBy(i => (i.SourceBankId, i.DestBankId)).OrderBy(g => g.Key.SourceBankId).ToList();
        Assert.Equal(
            [("BANK-A", "BANK-B"), ("BANK-B", "BANK-C"), ("BANK-C", "BANK-D"), ("BANK-D", "BANK-E"), ("BANK-E", "BANK-F")],
            hops.Select(g => g.Key));
        Assert.All(hops, g => Assert.Equal(6, g.Count()));
        Assert.All(ring, i => Assert.Equal(FridayRingScenario.MuleAccountId(BankIndex(i.SourceBankId)), i.SourceAccountId));

        // Legitimate traffic is interleaved with the ring, not before or after it.
        var firstRing = intents.ToList().FindIndex(i => i.Truth == SyntheticTruth.FraudMuleRing);
        var lastRing = intents.ToList().FindLastIndex(i => i.Truth == SyntheticTruth.FraudMuleRing);
        Assert.Contains(intents.Skip(firstRing).Take(lastRing - firstRing), i => i.Truth == SyntheticTruth.Legitimate);

        // The ring begins between 09:00 and 09:05 UTC on Friday 2026-01-09.
        var nineAm = new DateTimeOffset(2026, 1, 9, 9, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        Assert.InRange(ring[0].EventTimeUtcMs, nineAm, nineAm + (300 * 1000L));
    }

    [Fact]
    public void Inputs_are_built_so_that_v7_and_v8_thresholds_can_diverge_without_reading_truth_labels()
    {
        var run = Create(42, RunA);
        var velocities = run.Intents.ToDictionary(i => i.IntentId, i => Velocity15m(run.Intents, i));
        var ring = run.Intents.Where(i => i.Truth == SyntheticTruth.FraudMuleRing).ToList();
        var legit = run.Intents.Where(i => i.Truth == SyntheticTruth.Legitimate).ToList();

        // Documented policy v7: velocity >= 8 and amount >= 100000. No ring payer reaches 8.
        Assert.All(ring, i => Assert.True(velocities[i.IntentId] < 8));
        Assert.All(ring, i => Assert.InRange(i.Amount.MinorUnits, 110_000, 149_000));

        // Documented policy v8: velocity >= 5. Two per hop reach it; ordinary customers never do.
        Assert.Equal(10, ring.Count(i => velocities[i.IntentId] >= 5));
        Assert.All(legit, i => Assert.True(velocities[i.IntentId] < 5));

        // v8 second rule: new beneficiary and amount >= 150000 yields labelled legitimate false positives.
        Assert.Equal(2, legit.Count(i => i.Features.NewBeneficiary && i.Amount.MinorUnits >= 150_000));
        Assert.DoesNotContain(ring, i => i.Amount.MinorUnits >= 150_000);
    }

    [Fact]
    public void Opening_balances_cover_the_worst_case_outgoing_total_of_every_payer()
    {
        var run = Create(42, RunA);
        foreach (var group in run.Intents.GroupBy(i => i.SourceAccountId))
        {
            var opening = run.InitialState.Accounts.Single(a => a.AccountId == group.Key).OpeningBalance.MinorUnits;
            Assert.True(group.Sum(i => i.Amount.MinorUnits) <= opening, group.Key);
        }
    }

    [Fact]
    public void Unsupported_version_and_empty_run_id_fail_with_typed_errors()
    {
        var version = FridayRingScenario.Create(1, "friday-ring/2", RunA);
        Assert.Equal(ScenarioErrorCode.UnsupportedScenarioVersion, version.Error.Code);

        var empty = FridayRingScenario.Create(1, ScenarioSchemas.FridayRingVersion, default);
        Assert.Equal(ScenarioErrorCode.InvalidRunId, empty.Error.Code);
    }

    private static int BankIndex(string bankId) => "ABCDEF".IndexOf(bankId[^1]);

    private static string RingShape(ScenarioRun run) => string.Join(
        ";", run.Intents.Where(i => i.Truth == SyntheticTruth.FraudMuleRing)
            .GroupBy(i => (i.SourceAccountId, i.DestAccountId)).Select(g => $"{g.Key.SourceAccountId}>{g.Key.DestAccountId}x{g.Count()}"));

    internal static int Velocity15m(ImmutableArray<PaymentIntent> all, PaymentIntent intent) => all.Count(o =>
        o.SourceAccountId == intent.SourceAccountId
        && o.EventTimeUtcMs <= intent.EventTimeUtcMs
        && intent.EventTimeUtcMs - o.EventTimeUtcMs <= FifteenMinutesMs);
}
