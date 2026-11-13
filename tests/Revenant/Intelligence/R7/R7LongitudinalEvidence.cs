using System.Text.Json;
using System.Text.Json.Serialization;
using Marrow.Kernel.Canonical;
using Revenant.Contracts;
using Revenant.Observations;
using Revenant.Synthetic;

namespace Revenant.Tests.Intelligence.R7;

internal sealed record LongitudinalRootEvidence(LongitudinalRoot Root, string BehaviorSha256, string ContentHash,
    Guid Execution, int Seals, SortedDictionary<string, string> Streams, long Accepted, long Held, long Releases, long ReturnFacts);
internal sealed record LongitudinalEvidence(string Generator, string Revision, LongitudinalRootEvidence[] Roots);
internal sealed record LongitudinalTruth(string Schema, string DefaultAccountBucket, LongitudinalEvent[] Events,
    Dictionary<string, string>? Rhythms = null);
internal sealed record LongitudinalSample(LongitudinalRootEvidence Manifest, V4AdvisorySeries[] Accounts,
    V4AdvisorySeries[] Banks, LongitudinalEvent[] Events, IReadOnlyDictionary<string, long?[][]> Targets,
    IReadOnlyDictionary<string, string>? Rhythms = null)
{
    internal static readonly string[] Generators = [V4LongitudinalPlanner.Version, V4LongitudinalPlannerG4.Version];

    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    internal static LongitudinalEvidence ReadManifest(string path, string name)
    {
        var manifest = JsonSerializer.Deserialize<LongitudinalEvidence>(File.ReadAllBytes(Path.Combine(path, name)), Json)
            ?? throw new InvalidDataException("Missing longitudinal manifest.");
        if (!Generators.Contains(manifest.Generator) || manifest.Roots.Length == 0
            || manifest.Roots.Select(r => r.Root.Key).Distinct().Count() != manifest.Roots.Length
            || manifest.Roots.Select(r => r.BehaviorSha256).Distinct().Count() != manifest.Roots.Length)
            throw new InvalidDataException("Invalid longitudinal manifest or repeated roots.");
        return manifest;
    }

    internal static LongitudinalSample Read(string directory, LongitudinalRootEvidence root)
    {
        if (root.Root.Key.Length != 64 || root.Root.Key.Any(c => !char.IsAsciiHexDigitLower(c))
            || root.Seals != root.Root.Days * V4LongitudinalPlanner.BucketsPerDay)
            throw new InvalidDataException("Invalid longitudinal root identity or coverage.");
        var path = Path.Combine(directory, root.Root.Key);
        var execution = new ScenarioRunId(root.Execution);
        var series = root.Streams.Where(pair => pair.Key.EndsWith(".series", StringComparison.Ordinal)).Select(pair =>
            V4AdvisorySeries.Decode(ReadStream(pair.Key, pair.Value), pair.Value, execution)).ToArray();
        var truth = JsonSerializer.Deserialize<LongitudinalTruth>(ReadStream("truth.json", root.Streams["truth.json"]), Json)
            ?? throw new InvalidDataException("Missing longitudinal truth.");
        var accounts = series.Where(s => s.Schema == V4AdvisorySeries.AccountSchema).ToArray();
        var banks = series.Where(s => s.Schema == V4AdvisorySeries.BankSchema).ToArray();
        if (truth.Schema is not (V4LongitudinalPlanner.TruthSchema or V4LongitudinalPlannerG4.TruthSchema)
            || (truth.Schema == V4LongitudinalPlannerG4.TruthSchema) != (truth.Rhythms is not null)
            || truth.DefaultAccountBucket != "Normal"
            || accounts.Length == 0 || banks.Length != 6
            || series.Select(s => s.SubjectId).Distinct().Count() != series.Length
            || series.Any(s => s.Coverage != AdvisorySeriesCoverage.Complete || s.Points.Length != root.Seals)
            || truth.Events.Any(e => e.Start < 0 || e.End < e.Start || e.End >= root.Seals
                || !series.Any(s => s.SubjectId == e.Subject) || !Enum.IsDefined(e.Kind)))
            throw new InvalidDataException("Longitudinal evidence is incomplete.");
        foreach (var s in series)
            if (!s.Points.Select(p => p.Seal).SequenceEqual(banks[0].Points.Select(p => p.Seal)))
                throw new InvalidDataException("Series do not refer to the same completed seals.");
        var targets = new Dictionary<string, long?[][]>(StringComparer.Ordinal);
        if (root.Root.Split != LongitudinalSplit.Pilot)
            foreach (var bank in banks)
            {
                var name = bank.SubjectId + ".targets";
                var reader = new CanonicalReader(ReadStream(name, root.Streams[name]));
                ObservationEncoding.Expect(ref reader, V4BankTargets.Schema);
                if (reader.ReadCount() != root.Seals) throw new InvalidDataException("Incomplete bank target stream.");
                var horizons = new long?[root.Seals][];
                for (var i = 0; i < horizons.Length; i++)
                {
                    var bytes = reader.ReadBytes();
                    var target = V4BankTargets.Decode(bytes, ObservationEncoding.Hash(bytes));
                    if (target.Origin != bank.Points[i].Seal || target.BankId != bank.SubjectId)
                        throw new InvalidDataException("Bank target origin does not match its causal series.");
                    horizons[i] = target.Horizons.Select(h => h?.Balance.Available).ToArray();
                    for (var h = 0; h < horizons[i].Length; h++)
                    {
                        var future = i + h + 1;
                        long? expected = future < bank.Points.Length ? bank.Points[future].Value : null;
                        if (horizons[i][h] != expected) throw new InvalidDataException("Bank target coverage or value mismatch.");
                    }
                }
                reader.EnsureEnd();
                targets.Add(bank.SubjectId, horizons);
            }
        return new(root, accounts, banks, truth.Events, targets, truth.Rhythms);

        byte[] ReadStream(string name, string hash)
        {
            if (Path.GetFileName(name) != name) throw new InvalidDataException("Invalid longitudinal stream name.");
            var bytes = File.ReadAllBytes(Path.Combine(path, name));
            ObservationEncoding.RequireDigest(bytes, hash);
            return bytes;
        }
    }
}
