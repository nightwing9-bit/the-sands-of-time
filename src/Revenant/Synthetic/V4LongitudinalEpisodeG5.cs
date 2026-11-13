using System.Buffers.Binary;
using System.Security.Cryptography;
using Revenant.Contracts;
using Revenant.Observations;
using Revenant.Scenario;

namespace Revenant.Synthetic;

/// <summary>Development scenario with bank-specific operational clearing runs and independently sized liquidity episodes.</summary>
internal static class V4LongitudinalPlannerG5
{
    internal const string Version = BehavioralEpisodeScenario.GeneratorVersionV5;

    internal static IEnumerable<LongitudinalRoot> Roots()
    {
        const int days = 7;
        for (var i = 0; i < 3; i++)
        {
            var hash = SHA256.HashData(ObservationEncoding.Begin(Version).WriteString("root")
                .WriteByte((byte)LongitudinalSplit.Pilot).WriteInt32(days).WriteInt32(i).ToArray());
            yield return new(LongitudinalSplit.Pilot, i, Convert.ToHexStringLower(hash), BinaryPrimitives.ReadInt64BigEndian(hash), days);
        }
    }

    internal static V4LongitudinalEpisode Plan(LongitudinalRoot root, ScenarioRunId execution) =>
        V4LongitudinalPlannerG4.Plan(root, execution, Version);

    internal const string ResidualTrainingVariant = "revenant-g5-residual-training/1";
    internal const string ResidualTrainingBank = "BANK-A";

    internal static LongitudinalRoot ResidualTrainingRoot()
    {
        const int days = 10;
        var hash = SHA256.HashData(ObservationEncoding.Begin(ResidualTrainingVariant)
            .WriteByte((byte)LongitudinalSplit.Train).WriteInt32(days).ToArray());
        return new(LongitudinalSplit.Train, 0, Convert.ToHexStringLower(hash), BinaryPrimitives.ReadInt64BigEndian(hash), days);
    }

    internal sealed record Sweep(int Bucket, int From, int To, long Amount);

    /// <summary>Each originator clears a finite batch to a partner and receives an independently timed funding sweep later that day.</summary>
    internal static IEnumerable<Sweep> OperationalSweeps(LongitudinalRoot root, long[] opening)
    {
        var random = SeededRandom.ForStream(root.Seed, 50);
        const int banks = V4LongitudinalPlannerG4.BankCount;
        const int daySize = V4LongitudinalPlannerG4.BucketsPerDay;
        for (var bank = 0; bank < banks; bank++)
        {
            var counterparty = (bank + random.NextInt(1, banks)) % banks;
            var phase = random.NextInt(20, 125);
            var cadence = random.NextInt(1, 3);
            var basisPoints = random.NextInt(110, 220);
            for (var day = 0; day < root.Days; day++)
            {
                if (random.NextInt(0, 8) == 0) continue;
                var start = day * daySize + phase + random.NextInt(-7, 8);
                var count = random.NextInt(5, 15);
                var funding = start + random.NextInt(45, 85);
                for (var i = 0; i < count; i++)
                {
                    if (random.NextInt(0, 12) == 0) continue;
                    var amount = opening[bank] * basisPoints * random.NextInt(65, 136) / 1_000_000;
                    yield return new(start + i * cadence, bank, counterparty, amount);
                    yield return new(funding + i * cadence, counterparty, bank, amount * random.NextInt(94, 107) / 100);
                }
            }
        }
    }
}
