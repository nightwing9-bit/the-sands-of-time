using System.Collections.Immutable;
using Marrow.Kernel.Results;
using Revenant.Contracts;

namespace Revenant.Scenario;

/// <summary>
/// Generates the seeded six-bank Friday Ring (<see cref="ScenarioSchemas.FridayRingVersion"/>): ordinary
/// legitimate traffic from 08:00 UTC plus a mule chain A to B to C to D to E to F starting near 09:00 UTC.
/// Pure and deterministic: the same seed and version give the same facts for every run id, and nothing is
/// stored, reset or shared between runs.
///
/// Designed divergence, independent of seed: each of the five ring hops is a burst of six transfers by one
/// mule within under ten minutes, each USD 1,100.00 to 1,490.00. That reaches velocity 5 but never 8, so
/// policy v8 can hold what v7 clears. Two legitimate new-beneficiary payments of at least USD 1,500.00 give
/// v8 a labelled false positive. Legitimate payers repeat no more than once per 15 minutes.
/// </summary>
public static class FridayRingScenario
{
    private const int LegitIntentCount = 60;
    private const int HopCount = 5;
    private const int TransfersPerHop = 6;
    private const long CustomerOpeningMinor = 5_000_000;
    private const long MuleOpeningMinor = 1_000_000;
    private const ulong LegitStream = 1;
    private const ulong RingStream = 2;
    private static readonly int[] LargeNewBeneficiaryLegitIndexes = [20, 41];
    private static readonly string[] BankLetters = ["A", "B", "C", "D", "E", "F"];
    private static readonly string[] BankNames =
        ["Aster Bank", "Bramble Trust", "Cinder Savings", "Dune Credit", "Ember Bank", "Fjord Union"];

    // Friday 2026-01-09 08:00:00 UTC.
    private static readonly long StartUtcMs = new DateTimeOffset(2026, 1, 9, 8, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    public static Result<ScenarioRun, ScenarioError> Create(long seed, string scenarioVersion, ScenarioRunId runId)
    {
        if (runId.IsEmpty)
        {
            return Result<ScenarioRun, ScenarioError>.Failure(
                new ScenarioError(ScenarioErrorCode.InvalidRunId, "ScenarioRunId must not be empty."));
        }

        if (!string.Equals(scenarioVersion, ScenarioSchemas.FridayRingVersion, StringComparison.Ordinal))
        {
            return Result<ScenarioRun, ScenarioError>.Failure(new ScenarioError(
                ScenarioErrorCode.UnsupportedScenarioVersion,
                $"Scenario version '{scenarioVersion}' is not supported; this build pins '{ScenarioSchemas.FridayRingVersion}'."));
        }

        var manifest = BuildManifest(seed);
        var intents = BuildIntents(seed, runId);
        var run = new ScenarioRun(
            runId,
            manifest,
            BuildRunState(manifest, runId),
            intents,
            ScenarioManifestCodec.ComputeFactsHash(manifest, intents));
        return Result<ScenarioRun, ScenarioError>.Success(run);
    }

    public static string BankId(int index) => $"BANK-{BankLetters[index]}";

    internal static string BankName(int index) => BankNames[index];

    public static string CustomerAccountId(int bankIndex, int customer) => $"{BankId(bankIndex)}/ACC-{customer}";

    public static string MuleAccountId(int bankIndex) => $"{BankId(bankIndex)}/ACC-3";

    private static ScenarioManifest BuildManifest(long seed)
    {
        var banks = ImmutableArray.CreateBuilder<BankDescriptor>();
        var accounts = ImmutableArray.CreateBuilder<AccountDescriptor>();
        for (var b = 0; b < BankLetters.Length; b++)
        {
            banks.Add(new BankDescriptor(
                BankId(b), BankNames[b], Money.Usd((2 * CustomerOpeningMinor) + MuleOpeningMinor)));
            accounts.Add(new AccountDescriptor(CustomerAccountId(b, 1), BankId(b), Money.Usd(CustomerOpeningMinor)));
            accounts.Add(new AccountDescriptor(CustomerAccountId(b, 2), BankId(b), Money.Usd(CustomerOpeningMinor)));
            accounts.Add(new AccountDescriptor(MuleAccountId(b), BankId(b), Money.Usd(MuleOpeningMinor)));
        }

        var unhashed = new ScenarioManifest(
            ScenarioSchemas.FridayRingId,
            ScenarioSchemas.FridayRingVersion,
            ScenarioSchemas.InputSchemaVersion,
            ScenarioSchemas.FeatureSchemaVersion,
            ["newBeneficiary", "sanctionsHit"],
            ScenarioSchemas.LabelSchemaVersion,
            [nameof(SyntheticTruth.Legitimate), nameof(SyntheticTruth.FraudMuleRing)],
            seed,
            StartUtcMs,
            banks.ToImmutable(),
            accounts.ToImmutable(),
            ContentHash: string.Empty);
        return unhashed with { ContentHash = ScenarioManifestCodec.ComputeContentHash(unhashed) };
    }

    private static ScenarioRunState BuildRunState(ScenarioManifest manifest, ScenarioRunId runId) => new(
        runId,
        [.. manifest.Banks.Select(b => new RunBankState(b.BankId, runId.Scope(b.BankId), b.Name))],
        [.. manifest.Accounts.Select(a => new RunAccountState(a.AccountId, a.BankId, runId.Scope(a.AccountId), a.OpeningBalance))]);

    private sealed record Draft(
        long TimeMs, int Kind, int GenIndex, int SourceBank, string SourceAccount, int DestBank, string DestAccount,
        long AmountMinor, SyntheticTruth Truth, bool NewBeneficiary);

    private static ImmutableArray<PaymentIntent> BuildIntents(long seed, ScenarioRunId runId)
    {
        var drafts = new List<Draft>();
        AddLegitimate(drafts, SeededRandom.ForStream(seed, LegitStream));
        AddRing(drafts, SeededRandom.ForStream(seed, RingStream));

        // Business time orders the stream once at generation; logical Sequence is the authority afterwards.
        // Ties break by kind then generation index, so the order never depends on hash or insertion accidents.
        var ordered = drafts.OrderBy(d => d.TimeMs).ThenBy(d => d.Kind).ThenBy(d => d.GenIndex).ToList();
        var intents = ImmutableArray.CreateBuilder<PaymentIntent>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            var d = ordered[i];
            var sequence = i + 1L;
            intents.Add(new PaymentIntent(
                runId,
                $"FR-{sequence:D4}",
                sequence,
                d.TimeMs,
                BankId(d.SourceBank),
                d.SourceAccount,
                BankId(d.DestBank),
                d.DestAccount,
                Money.Usd(d.AmountMinor),
                d.Truth,
                new PaymentFeatures(d.NewBeneficiary, SanctionsHit: false)));
        }

        return intents.MoveToImmutable();
    }

    private static void AddLegitimate(List<Draft> drafts, SeededRandom rng)
    {
        // Twelve customer accounts paid in a shuffled round robin: a payer repeats every twelve intents,
        // at least 90 s apart each, so no legitimate payer exceeds one payment per 15 minutes.
        var payers = new List<(int Bank, string Account)>();
        for (var b = 0; b < BankLetters.Length; b++)
        {
            payers.Add((b, CustomerAccountId(b, 1)));
            payers.Add((b, CustomerAccountId(b, 2)));
        }

        var order = payers.ToArray();
        rng.Shuffle(order);

        var time = StartUtcMs;
        for (var i = 0; i < LegitIntentCount; i++)
        {
            time += (90 + rng.NextInt(0, 61)) * 1000L;
            var payer = order[i % order.Length];
            var destChoices = payers.Where(p => p.Bank != payer.Bank).ToArray();
            var dest = destChoices[rng.NextInt(0, destChoices.Length)];
            var amount = 2_000L + rng.NextInt(0, 88_001);
            var newBeneficiary = rng.NextInt(0, 10) == 0;
            if (Array.IndexOf(LargeNewBeneficiaryLegitIndexes, i) >= 0)
            {
                amount = 150_000L + rng.NextInt(0, 100_001);
                newBeneficiary = true;
            }

            drafts.Add(new Draft(time, 0, i, payer.Bank, payer.Account, dest.Bank, dest.Account,
                amount, SyntheticTruth.Legitimate, newBeneficiary));
        }
    }

    private static void AddRing(List<Draft> drafts, SeededRandom rng)
    {
        // Ring starts at 09:00 UTC plus up to five minutes of seeded offset.
        var hopStart = StartUtcMs + (60 * 60 * 1000L) + (rng.NextInt(0, 301) * 1000L);
        var index = 0;
        for (var hop = 0; hop < HopCount; hop++)
        {
            var time = hopStart;
            for (var t = 0; t < TransfersPerHop; t++)
            {
                if (t > 0)
                {
                    time += (60 + rng.NextInt(0, 51)) * 1000L;
                }

                var amount = 110_000L + rng.NextInt(0, 39_001);
                drafts.Add(new Draft(time, 1, index++, hop, MuleAccountId(hop), hop + 1, MuleAccountId(hop + 1),
                    amount, SyntheticTruth.FraudMuleRing, NewBeneficiary: t == 0));
            }

            hopStart = time + ((120 + rng.NextInt(0, 181)) * 1000L);
        }
    }
}
