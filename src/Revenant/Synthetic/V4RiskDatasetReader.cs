using System.Text.Json;
using System.Text.Json.Serialization;
using Marrow.Kernel.Canonical;
using Revenant.Finance;
using Revenant.Intelligence;
using Revenant.Observations;
using Revenant.Scenario;
using Revenant.Timeline;
using static Revenant.Synthetic.V4DatasetExporter;

namespace Revenant.Synthetic;

internal sealed record V4RiskResearchRow(string Root, long OriginMs, string IntentId, long Cutoff, long At, string Bank,
    V4FraudInputs AsOf, RiskFeatureVectorV2 Vector, V4PaymentTruth Truth, string Screening);

/// <summary>Offline research boundary. No scorer or prefix builder takes a research row.</summary>
internal sealed class V4RiskDatasetReader
{
    private readonly string directory;
    internal V4DatasetManifest Manifest { get; }

    internal V4RiskDatasetReader(string directory, string pinnedManifestSha256)
    {
        this.directory = directory;
        var binary = File.ReadAllBytes(Path.Combine(directory, "manifest.bin"));
        ObservationEncoding.RequireDigest(binary, pinnedManifestSha256);
        Manifest = JsonSerializer.Deserialize<V4DatasetManifest>(File.ReadAllBytes(Path.Combine(directory, "manifest.json")),
            new JsonSerializerOptions
            {
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
                Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
            }) ?? throw new CanonicalFormatException("Dataset manifest is missing.");
        var config = Manifest.Configuration;
        var expected = config.Roots().ToArray();
        Require(Manifest.Schema == RiskSchema && config.IsV2 && Manifest.Sha256 == pinnedManifestSha256
            && Manifest.CodeRevision is { Length: 40 } revision && revision.All(char.IsAsciiHexDigitLower)
            && Manifest.Roots.Count == expected.Length
            && Manifest.Roots.Select(r => r.BehaviorSha256).Distinct(StringComparer.Ordinal).Count() == expected.Length,
            "Invalid risk dataset manifest.");
        foreach (var (root, group) in Manifest.Roots.Zip(expected))
        {
            Require(root.Key == group.Key && root.Split == group.Split
                && root.OriginMs is { } origin && ObservedFinanceVersion.IsBoundary(origin)
                && origin >= config.OriginMs && origin < config.OriginMs + 56 * V4EpisodeConfig.WarmupMs
                && ObservedFinanceEncoding.IsHash(root.BehaviorSha256)
                && root.ManifestSha256.StartsWith("sha256:", StringComparison.Ordinal)
                && ObservedFinanceEncoding.IsHash(root.ManifestSha256[7..]) && root.Accounts is >= 18 and <= 36 && root.Facts > 0,
                "Invalid dataset root.");
        }
        var expectedSplits = expected.GroupBy(x => x.Split).ToArray();
        Require(Manifest.Splits.Select(x => x.Split).SequenceEqual(expectedSplits.Select(x => x.Key)), "Invalid split order.");
        foreach (var (split, group) in Manifest.Splits.Zip(expectedSplits))
        {
            Require(split.Roots == group.Count() && split.Tables.Select(x => x.Name).SequenceEqual(RiskTableNames)
                && split.Tables.All(x => x.Rows >= 0 && x.Bytes > 0 && ObservedFinanceEncoding.IsHash(x.Sha256))
                && split.ModelSha256 == ModelDigest(split.Tables, true) && split.TruthSha256 == TruthDigest(split.Tables),
                "Invalid split stream metadata.");
        }
        Require(binary.AsSpan().SequenceEqual(EncodeManifest(Manifest)), "JSON and canonical dataset manifest disagree.");
    }

    // No split is opened by the constructor. Callers explicitly request development, calibration or test truth.
    internal IReadOnlyList<V4RiskResearchRow> ReadSplit(V4DatasetSplit split)
    {
        var metadata = Manifest.Splits.Single(x => x.Split == split);
        var roots = Manifest.Roots.Where(r => r.Split == split).ToArray();
        var order = roots.Select((root, i) => (root, i)).ToDictionary(x => x.root.Key, x => x.i, StringComparer.Ordinal);
        var truth = Read(PaymentTruthStream, Truth);
        var outcomes = Read(AttemptStream, Outcome);
        var features = Read(RiskStream, Risk);
        var observations = Read(FraudStream, AsOf);
        var campaigns = Read(CampaignTruthStream, Campaign);
        var truthByKey = Unique(truth, x => (x.EpisodeKey, x.IntentId));
        var screeningByKey = Unique(outcomes, x => (x.Root, x.Id));
        var asOfByKey = Unique(observations, x => (x.Root, x.Id));
        var riskByKey = Unique(features, x => (x.Root, x.Id));
        Require(truthByKey.Keys.ToHashSet().SetEquals(screeningByKey.Keys)
            && asOfByKey.Keys.ToHashSet().SetEquals(screeningByKey.Where(x => x.Value.Screened).Select(x => x.Key))
            && asOfByKey.Keys.ToHashSet().SetEquals(riskByKey.Keys), "Missing or unmatched attempt, truth or screening.");
        Require(campaigns.Count == roots.Length * Enum.GetValues<EpisodeAttack>().Length
            && campaigns.Select(c => c.CampaignKey).Distinct(StringComparer.Ordinal).Count() == campaigns.Count
            && campaigns.Select(c => c.Behavior).Distinct(StringComparer.Ordinal).Count() == campaigns.Count,
            "Reused campaign key or behavior.");
        var claimed = new HashSet<(string, string)>();
        foreach (var c in campaigns)
        {
            foreach (var id in c.Ids)
            {
                Require(claimed.Add((c.Root, id)) && truthByKey.TryGetValue((c.Root, id), out var t)
                    && t.Malicious && t.CampaignKey == c.CampaignKey && t.Attack == c.Family, "Inconsistent campaign truth.");
            }
        }
        Require(claimed.Count == truth.Count(x => x.Malicious), "Missing campaign truth.");

        var result = new List<V4RiskResearchRow>();
        foreach (var feature in features)
        {
            var key = (feature.Root, feature.Id);
            var observation = asOfByKey[key];
            var root = roots[order[feature.Root]];
            var start = root.OriginMs!.Value;
            var annotation = truthByKey[key];
            Require(feature.Cutoff == observation.Cutoff && feature.At == observation.At
                && feature.Cutoff < root.Facts && feature.At >= start && feature.At < start + Manifest.Configuration.Duration,
                "Feature and observation cutoffs disagree.");
            var eligible = feature.At - start >= V4EpisodeConfig.WarmupMs;
            Require(eligible || !annotation.Malicious, "Malicious payment inside warmup.");
            if (eligible) result.Add(new(feature.Root, start, feature.Id, feature.Cutoff, feature.At, feature.Bank,
                observation.Input, feature.Vector, annotation, screeningByKey[key].Status!));
        }
        Require(result.GroupBy(x => x.Root).Count() == roots.Length
            && result.GroupBy(x => x.Root).All(r => r.Any(x => x.Truth.Malicious) && r.Any(x => !x.Truth.Malicious)),
            "Scored split is missing a class.");
        return result;

        List<T> Read<T>(string name, DecodeRow<T> decode)
        {
            var table = metadata.Tables.Single(t => t.Name == name);
            var file = Path.Combine(directory, $"{split}-{name}.bin");
            Require(table.Bytes <= 32 * 1024 * 1024 && table.Rows <= 1_000_000
                && new FileInfo(file).Length == table.Bytes, $"Wrong length for {name}.");
            var bytes = File.ReadAllBytes(file);
            ObservationEncoding.RequireDigest(bytes, table.Sha256);
            var r = new CanonicalReader(bytes);
            ObservationEncoding.Expect(ref r, StreamSchema);
            Require(r.ReadString() == name, "Wrong stream kind.");
            var list = new List<T>();
            (int Root, long Position) previous = (-1, -1);
            for (long i = 0; i < table.Rows; i++)
            {
                var row = new CanonicalReader(r.ReadBytes());
                var decoded = decode(ref row);
                row.EnsureEnd();
                Require(order.TryGetValue(decoded.Root, out var root) && (root, decoded.Position).CompareTo(previous) > 0,
                    $"Noncanonical stream order or wrong split: {name}.");
                previous = (root, decoded.Position);
                list.Add(decoded.Row);
            }
            r.EnsureEnd();
            return list;
        }
    }

    private delegate (string Root, long Position, T Row) DecodeRow<T>(ref CanonicalReader r);
    private sealed record RiskData(string Root, string Id, long Cutoff, long At, string Bank, RiskFeatureVectorV2 Vector);
    private sealed record Observation(string Root, string Id, long Cutoff, long At, V4FraudInputs Input);
    private sealed record Attempt(string Root, string Id, bool Screened, string? Status);
    private sealed record CampaignData(string Root, string CampaignKey, EpisodeAttack Family, string Behavior, string[] Ids);

    private static (string, long, RiskData) Risk(ref CanonicalReader r)
    {
        ObservationEncoding.Expect(ref r, RiskRowSchema);
        Require(r.ReadString() == RiskFeatureVectorV2.Schema, "Wrong feature schema.");
        var root = r.ReadString();
        var id = r.ReadString();
        _ = IntentOrdinal(id);
        var cutoff = r.ReadInt64();
        var at = r.ReadInt64();
        var bank = r.ReadString();
        Require(cutoff >= 1 && Enumerable.Range(0, 6).Select(FridayRingScenario.BankId).Contains(bank), "Invalid risk row.");
        return (root, cutoff, new(root, id, cutoff, at, bank, RiskFeatureVectorV2.Decode(r.ReadBytes())));
    }

    private static (string, long, Observation) AsOf(ref CanonicalReader r)
    {
        ObservationEncoding.Expect(ref r, "dataset-fraud-row/1");
        Require(r.ReadString() == V4FraudAsOfObservation.Schema, "Wrong as-of schema.");
        var root = r.ReadString();
        var id = r.ReadString();
        _ = IntentOrdinal(id);
        var cutoff = r.ReadInt64();
        var at = r.ReadInt64();
        var boundary = r.ReadInt64();
        Require(cutoff >= 1 && ObservedFinanceVersion.IsBoundary(boundary) && at >= boundary
            && at - boundary < ObservedFinanceVersion.BucketMs, "Invalid as-of cutoff.");
        var input = V4FraudInputs.Read(ref r);
        input.Write(new CanonicalWriter()); // Validate ranges and canonical window layout.
        return (root, cutoff, new(root, id, cutoff, at, input));
    }

    private static (string, long, V4PaymentTruth) Truth(ref CanonicalReader r)
    {
        ObservationEncoding.Expect(ref r, V4PaymentTruth.Schema);
        Require(r.ReadByte() == 1, "Wrong truth record kind.");
        var root = r.ReadString();
        var id = r.ReadString();
        var malicious = r.ReadBoolean();
        var hard = r.ReadBoolean();
        var row = new V4PaymentTruth(root, id, malicious, malicious ? (EpisodeAttack)r.ReadByte() : null,
            malicious ? r.ReadString() : null, hard);
        _ = row.Encode();
        return (root, IntentOrdinal(id), row);
    }

    private static (string, long, Attempt) Outcome(ref CanonicalReader r)
    {
        ObservationEncoding.Expect(ref r, "dataset-attempt-outcome/1");
        Require(r.ReadString() == V4PaymentTruth.Schema, "Wrong outcome schema.");
        var root = r.ReadString();
        var id = r.ReadString();
        var intake = r.ReadString();
        var screening = Optional(ref r);
        var settlement = Optional(ref r);
        var returned = Optional(ref r);
        var screened = r.ReadBoolean();
        var posted = r.ReadBoolean();
        Require(!string.IsNullOrWhiteSpace(intake) && screened == IsScreened(screening)
            && (!screened || intake == nameof(PaymentStatus.Initiated))
            && posted == (settlement == nameof(PaymentStatus.Settled))
            && (!posted || screening == nameof(PaymentStatus.Cleared))
            && (returned != nameof(PaymentStatus.Returned) || posted), "Invalid attempt outcome.");
        return (root, IntentOrdinal(id), new(root, id, screened, screening));
        static string? Optional(ref CanonicalReader r) => r.ReadBoolean() ? r.ReadString() : null;
    }

    private static (string, long, CampaignData) Campaign(ref CanonicalReader r)
    {
        ObservationEncoding.Expect(ref r, V4PaymentTruth.Schema);
        Require(r.ReadByte() == 3, "Wrong campaign record kind.");
        var root = r.ReadString();
        var campaign = r.ReadString();
        var attack = (EpisodeAttack)r.ReadByte();
        var hash = r.ReadString();
        var count = r.ReadCount();
        Require(campaign.StartsWith(root + "/", StringComparison.Ordinal) && campaign.Length == root.Length + 17
            && campaign[(root.Length + 1)..].All(char.IsAsciiHexDigitLower) && Enum.IsDefined(attack)
            && ObservedFinanceEncoding.IsHash(hash) && count is > 0 and <= 1_000, "Invalid campaign identity.");
        var ids = new string[count];
        long last = 0;
        for (var i = 0; i < count; i++)
        {
            ids[i] = r.ReadString();
            var n = IntentOrdinal(ids[i]);
            Require(n > last, "Out-of-order campaign attempt.");
            last = n;
        }
        return (root, (byte)attack, new(root, campaign, attack, hash, ids));
    }

    private static int IntentOrdinal(string id)
    {
        if (id.Length == 10 && id.StartsWith("P-", StringComparison.Ordinal)
            && id[2..].All(char.IsAsciiDigit) && int.TryParse(id.AsSpan(2), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var n) && n > 0) return n;
        throw new CanonicalFormatException("Invalid generated intent ID.");
    }

    private static Dictionary<TKey, T> Unique<T, TKey>(IEnumerable<T> rows, Func<T, TKey> key) where TKey : notnull
    {
        var result = new Dictionary<TKey, T>();
        foreach (var row in rows) Require(result.TryAdd(key(row), row), "Duplicate dataset join key.");
        return result;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new CanonicalFormatException(message);
    }
}
