using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Marrow.Kernel.Canonical;
using Revenant.Finance;
using Revenant.Observations;
using Revenant.Scenario;
using Revenant.Timeline;

namespace Revenant.Synthetic;

internal sealed record V4DatasetTable(string Name, long Rows, long Bytes, string Sha256);
internal sealed record V4DatasetRoot(string Key, V4DatasetSplit Split, string ManifestSha256, string BehaviorSha256, int Accounts, long Facts);
internal sealed record V4DatasetSplitSummary(V4DatasetSplit Split, int Roots, long Legitimate, long Fraud, long HardNegatives,
    long FraudScored, long LegitimateScored, long Anomalies, long AnomalyNegatives, long FullTargets,
    long Intake, long Screened, long Held, long Rejected, long Settled, long Returned, long InsufficientSettlement, long FraudPosted,
    IReadOnlyDictionary<EpisodeAttack, long> Attacks, IReadOnlyList<V4DatasetTable> Tables, string ModelSha256, string TruthSha256);
internal sealed record V4DatasetManifest(string Schema, string CodeRevision, V4EpisodeConfig Configuration,
    IReadOnlyList<V4DatasetRoot> Roots, IReadOnlyList<V4DatasetSplitSummary> Splits, string Sha256);

internal sealed class V4DatasetExporter : IDisposable
{
    internal const string Schema = "dataset-manifest/1";
    internal const string StreamSchema = "revenant-dataset-stream/1";
    private const string FraudStream = "fraud-asof";
    private const string BankStream = "bank";
    private const string AccountStream = "account";
    private const string TargetsStream = "targets";
    private const string AttemptStream = "attempt-outcomes";
    private const string PaymentTruthStream = "payment-truth";
    private const string AnomalyTruthStream = "anomaly-truth";
    private const string CampaignTruthStream = "campaign-truth";

    private static readonly string[] ModelStreams = [FraudStream, BankStream, AccountStream, TargetsStream];
    private static readonly string[] TruthStreams = [PaymentTruthStream, AnomalyTruthStream, CampaignTruthStream];
    private static readonly string[] StreamNames = [.. ModelStreams, AttemptStream, .. TruthStreams];

    private readonly V4EpisodeConfig configuration;
    private readonly string codeRevision;
    private readonly string? directory;
    private readonly V4RootGroup[] expected;
    private readonly List<V4DatasetRoot> roots = [];
    private readonly Dictionary<V4DatasetSplit, SplitSink> splits = [];
    private readonly HashSet<string> accountIdentities = new(StringComparer.Ordinal);
    private readonly HashSet<string> campaignIdentities = new(StringComparer.Ordinal);
    private readonly HashSet<string> campaignFingerprints = new(StringComparer.Ordinal);
    private readonly HashSet<string> historyFingerprints = new(StringComparer.Ordinal);
    private readonly HashSet<string> groups = new(StringComparer.Ordinal);
    private bool complete;

    internal V4DatasetExporter(V4EpisodeConfig configuration, string codeRevision, string? directory = null, bool writeTables = false)
    {
        if (codeRevision.Length != 40 || codeRevision.Any(c => !char.IsAsciiHexDigitLower(c))
            || (writeTables && directory is null))
        {
            throw new ArgumentException("An exact lowercase Git revision and valid export directory are required.");
        }
        expected = configuration.Roots().ToArray();
        this.configuration = configuration;
        this.codeRevision = codeRevision;
        this.directory = directory;
        if (directory is not null) Directory.CreateDirectory(directory);
        foreach (var split in expected.Select(x => x.Split).Distinct())
            splits.Add(split, new(split, writeTables ? directory : null));
    }

    internal void Add(V4EpisodeDesign design, V4ExecutedEpisode execution, V4ObservationProjector observations)
    {
        if (complete || roots.Count >= expected.Length || design.Group != expected[roots.Count]
            || !groups.Add(design.Group.Key) || !historyFingerprints.Add(design.HistoryFingerprint)
            || design.CampaignFingerprints.Any(f => !campaignFingerprints.Add(f)))
        {
            throw new InvalidOperationException("Duplicate or incorrectly assigned root/campaign group.");
        }

        var opening = design.Financial.Opening;
        var seedManifest = opening.Manifest;
        var position = execution.History.Segments.Single().LastFact;
        if (seedManifest.Accounts.Any(a => !accountIdentities.Add(a.AccountId))
            || execution.History.ExecutionRunId != opening.RunId
            || execution.History.Genesis.Ledger.ManifestContentHash != seedManifest.ContentHash)
        {
            throw new InvalidOperationException("Reused identity or inconsistent executed episode opening.");
        }

        var bankCount = BehavioralEpisodeScenario.BankCount;
        var seals = checked((int)(configuration.Duration / ObservedFinanceVersion.BucketMs));
        if (observations.Banks.Count != seals * bankCount
            || observations.Banks[^1].Seal.BoundaryMs != design.Financial.EndMs)
        {
            throw new InvalidOperationException("Dataset requires the full completed bank series.");
        }

        var key = design.Group.Key;
        var sink = splits[design.Group.Split];
        sink.Roots++;
        var outcomes = execution.Outcomes.ToDictionary(x => x.IntentId, StringComparer.Ordinal);
        var truth = design.Truth.Payments.ToDictionary(x => x.IntentId, StringComparer.Ordinal);
        var screening = observations.FraudAsOf.ToDictionary(x => x.IntentId, StringComparer.Ordinal);
        if (outcomes.Count != design.Financial.Payments.Length || truth.Count != outcomes.Count
            || !outcomes.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(truth.Keys)
            || !outcomes.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(design.Financial.Payments.Select(p => p.Intent.IntentId))
            || screening.Count != outcomes.Values.Count(o => IsScreened(o.Screening))
            || observations.Banks.Sum(x => x.Intents) != outcomes.Values.Count(o => o.Intake == nameof(PaymentStatus.Initiated))
            || observations.Banks.Sum(x => x.Screened) != screening.Count)
        {
            throw new InvalidOperationException("Attempt, truth, screening and financial counts do not reconcile.");
        }

        foreach (var row in observations.FraudAsOf)
        {
            if (row.ExecutionRunId != opening.RunId || !outcomes.TryGetValue(row.IntentId, out var outcome) || !IsScreened(outcome.Screening))
                throw new InvalidOperationException("Feature row is not a legal Screening opportunity.");
            var w = Row("dataset-fraud-row/1", V4FraudAsOfObservation.Schema, key).WriteString(row.IntentId)
                .WriteInt64(row.CutoffFactSeq).WriteInt64(row.EffectiveTimeMs).WriteInt64(row.LastSealBoundaryMs);
            row.Inputs.Write(w);
            sink.Add(FraudStream, w.ToArray());
        }
        foreach (var row in observations.Banks)
        {
            var w = SealedRow("dataset-bank-row/1", V4BankObservation.Schema, key, row.Seal);
            row.WriteContent(w);
            sink.Add(BankStream, w.ToArray());
        }
        foreach (var row in observations.Accounts)
        {
            var w = SealedRow("dataset-account-row/1", V4AccountObservation.Schema, key, row.Seal);
            row.WriteContent(w);
            sink.Add(AccountStream, w.ToArray());
        }

        V4BankTargets? sampleTarget = null;
        foreach (var row in V4BankTargets.Build(observations.Banks))
        {
            var w = SealedRow("dataset-target-row/1", V4BankTargets.Schema, key, row.Origin);
            row.WriteContent(w);
            sink.Add(TargetsStream, w.ToArray());
            if (row.FullyCovered) sink.FullTargets++;
            sampleTarget ??= row;
        }

        foreach (var a in design.Truth.Payments)
        {
            if (a.EpisodeKey != key) throw new InvalidOperationException("Misbound payment truth.");
            var outcome = outcomes[a.IntentId];
            var screened = screening.ContainsKey(a.IntentId);
            var posted = outcome.Settlement == nameof(PaymentStatus.Settled);
            sink.Add(PaymentTruthStream, a.Encode());
            var w = Row("dataset-attempt-outcome/1", V4PaymentTruth.Schema, key).WriteString(a.IntentId)
                .WriteString(outcome.Intake);
            WriteOptionalString(w, outcome.Screening);
            WriteOptionalString(w, outcome.Settlement);
            WriteOptionalString(w, outcome.Return);
            w.WriteBoolean(screened).WriteBoolean(posted);
            sink.Add(AttemptStream, w.ToArray());
            if (a.Malicious)
            {
                sink.Fraud++;
                sink.Attacks[a.Attack!.Value]++;
                if (screened) sink.FraudScored++;
                if (posted) sink.FraudPosted++;
            }
            else
            {
                sink.Legitimate++;
                if (screened) sink.LegitimateScored++;
                if (a.HardNegative) sink.HardNegatives++;
            }
            if (outcome.Intake == nameof(PaymentStatus.Initiated)) sink.Intake++;
            if (screened) sink.Screened++;
            if (outcome.Screening == nameof(PaymentStatus.Held)) sink.Held++;
            if (outcome.Screening == nameof(PaymentStatus.Rejected)) sink.Rejected++;
            if (posted) sink.Settled++;
            if (outcome.Settlement == nameof(PaymentStatus.Returned)) sink.InsufficientSettlement++;
            if (outcome.Return == nameof(PaymentStatus.Returned)) sink.Returned++;
        }

        var episodeAccounts = seedManifest.Accounts.Select(a => a.AccountId).ToHashSet(StringComparer.Ordinal);
        var anomalyKeys = new HashSet<(string, long)>();
        foreach (var a in design.Truth.Anomalies)
        {
            if (a.EpisodeKey != key || !episodeAccounts.Contains(a.AccountId) || !anomalyKeys.Add((a.AccountId, a.BoundaryMs))
                || observations.AccountAsOf(a.AccountId, a.BoundaryMs) is null)
                throw new InvalidOperationException("Anomaly annotation is not linked to a covered account interval.");
            sink.Add(AnomalyTruthStream, a.Encode());
            if (a.Anomalous) sink.Anomalies++;
            else sink.AnomalyNegatives++;
        }

        foreach (var (campaign, index) in design.Truth.Payments.Where(x => x.Malicious).GroupBy(x => x.Attack!.Value)
                     .OrderBy(g => g.Key).Select((g, i) => (g, i)))
        {
            var campaignKey = campaign.Select(x => x.CampaignKey!).Distinct(StringComparer.Ordinal).Single();
            if (!campaignIdentities.Add(campaignKey) || index >= design.CampaignFingerprints.Length)
                throw new InvalidOperationException("Duplicate or missing campaign provenance.");
            var w = ObservationEncoding.Begin(V4PaymentTruth.Schema).WriteByte(3).WriteString(key).WriteString(campaignKey)
                .WriteByte((byte)campaign.Key).WriteString(design.CampaignFingerprints[index]).WriteCount(campaign.Count());
            foreach (var payment in campaign) w.WriteString(payment.IntentId);
            sink.Add(CampaignTruthStream, w.ToArray());
        }

        roots.Add(new(key, design.Group.Split, seedManifest.ContentHash, design.HistoryFingerprint, seedManifest.Accounts.Length, position));
        if (directory is not null && roots.Count == 1)
        {
            // Compact typed derived sample. Exclude truth and operational IDs.
            var sample = new
            {
                Episode = key,
                Fraud = observations.FraudAsOf.Take(2).Select(x => new { x.IntentId, x.EffectiveTimeMs, x.CutoffFactSeq, x.Inputs }),
                Banks = observations.Banks.Take(6).Select(x => new { x.BankId, x.Seal.BoundaryMs, x.Balance, x.SettledIn, x.SettledOut }),
                Account = observations.Accounts.Take(2).Select(x => new { x.AccountId, x.BankId, x.Seal.BoundaryMs, x.Windows }),
                Target = sampleTarget?.Horizons,
            };
            File.WriteAllBytes(Path.Combine(directory, "sample.json"), JsonSerializer.SerializeToUtf8Bytes(sample));
        }
    }

    internal V4DatasetManifest Complete()
    {
        if (complete || roots.Count != expected.Length) throw new InvalidOperationException("The dataset is incomplete.");
        complete = true;
        var summaries = splits.Values.Select(s => s.Finish()).ToArray();
        var w = ObservationEncoding.Begin(Schema).WriteString(codeRevision)
            .WriteString(BehavioralEpisodeScenario.Version).WriteString(BehavioralEpisodeScenario.GeneratorVersion)
            .WriteString(V4EpisodeConfig.Assignment).WriteInt64(configuration.MasterSeed).WriteInt64(configuration.OriginMs)
            .WriteInt64(configuration.Duration).WriteString(V4BankObservation.Schema).WriteString(V4AccountObservation.Schema)
            .WriteString(V4FraudAsOfObservation.Schema).WriteString(V4BankTargets.Schema).WriteString(V4PaymentTruth.Schema)
            .WriteCount(BehavioralEpisodeScenario.BankCount);
        for (var i = 0; i < BehavioralEpisodeScenario.BankCount; i++) w.WriteString(FridayRingScenario.BankId(i));
        w.WriteCount(roots.Count);
        foreach (var root in roots)
            w.WriteString(root.Key).WriteByte((byte)root.Split).WriteString(root.ManifestSha256).WriteString(root.BehaviorSha256)
                .WriteInt32(root.Accounts).WriteInt64(root.Facts);
        w.WriteCount(summaries.Length);
        foreach (var split in summaries)
        {
            w.WriteByte((byte)split.Split).WriteCount(split.Roots).WriteBytes(Convert.FromHexString(split.ModelSha256))
                .WriteBytes(Convert.FromHexString(split.TruthSha256)).WriteCount(split.Tables.Count);
            foreach (var table in split.Tables)
                w.WriteString(table.Name).WriteInt64(table.Rows).WriteInt64(table.Bytes).WriteBytes(Convert.FromHexString(table.Sha256));
        }
        var canonical = w.ToArray();
        var manifest = new V4DatasetManifest(Schema, codeRevision, configuration, roots.ToArray(), summaries, ObservationEncoding.Hash(canonical));
        if (directory is not null)
        {
            File.WriteAllBytes(Path.Combine(directory, "manifest.bin"), canonical);
            File.WriteAllBytes(Path.Combine(directory, "manifest.json"), JsonSerializer.SerializeToUtf8Bytes(manifest,
                new JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));
        }
        return manifest;
    }

    private static bool IsScreened(string? status) =>
        status is nameof(PaymentStatus.Cleared) or nameof(PaymentStatus.Held) or nameof(PaymentStatus.Rejected);

    private static CanonicalWriter Row(string tag, string content, string key) =>
        ObservationEncoding.Begin(tag).WriteString(content).WriteString(key);

    private static CanonicalWriter SealedRow(string tag, string content, string key, V4SealIdentity seal) =>
        Row(tag, content, key).WriteInt64(seal.StartMs).WriteInt64(seal.BoundaryMs).WriteInt64(seal.FactSequence).WriteByte((byte)seal.Coverage);

    private static void WriteOptionalString(CanonicalWriter w, string? value)
    {
        w.WriteBoolean(value is not null);
        if (value is not null) w.WriteString(value);
    }

    public void Dispose()
    {
        foreach (var sink in splits.Values) sink.Dispose();
    }

    private sealed class SplitSink : IDisposable
    {
        private readonly V4DatasetSplit split;
        private readonly Dictionary<string, TableSink> sinks;
        internal readonly Dictionary<EpisodeAttack, long> Attacks = Enum.GetValues<EpisodeAttack>().ToDictionary(x => x, _ => 0L);
        internal int Roots;
        internal long Legitimate, Fraud, HardNegatives, FraudScored, LegitimateScored, Anomalies, AnomalyNegatives, FullTargets,
            Intake, Screened, Held, Rejected, Settled, Returned, InsufficientSettlement, FraudPosted;

        internal SplitSink(V4DatasetSplit split, string? directory)
        {
            this.split = split;
            sinks = StreamNames.ToDictionary(name => name,
                name => new TableSink(name, directory is null ? null : Path.Combine(directory, $"{split}-{name}.bin")), StringComparer.Ordinal);
        }

        internal void Add(string name, byte[] row) => sinks[name].Add(row);

        internal V4DatasetSplitSummary Finish()
        {
            if (FraudScored == 0 || LegitimateScored == 0 || HardNegatives == 0 || Anomalies == 0 || AnomalyNegatives == 0
                || Attacks.Values.Any(count => count == 0))
                throw new InvalidOperationException("An episode split lacks required behavior or truth coverage.");
            var tables = StreamNames.Select(name => sinks[name].Finish()).ToArray();
            return new(split, Roots, Legitimate, Fraud, HardNegatives, FraudScored, LegitimateScored, Anomalies, AnomalyNegatives,
                FullTargets, Intake, Screened, Held, Rejected, Settled, Returned, InsufficientSettlement, FraudPosted, Attacks, tables,
                Combined("dataset-model-streams/1", ModelStreams), Combined("dataset-truth-streams/1", TruthStreams));

            string Combined(string schema, string[] names)
            {
                var w = ObservationEncoding.Begin(schema).WriteCount(names.Length);
                foreach (var name in names)
                {
                    var table = tables.Single(x => x.Name == name);
                    w.WriteString(name).WriteBytes(Convert.FromHexString(table.Sha256));
                }
                return ObservationEncoding.Hash(w.ToArray());
            }
        }

        public void Dispose()
        {
            foreach (var sink in sinks.Values) sink.Dispose();
        }
    }

    private sealed class TableSink : IDisposable
    {
        private readonly string name;
        private readonly Stream? stream;
        private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long rows;
        private long bytes;

        internal TableSink(string name, string? path)
        {
            this.name = name;
            stream = path is null ? null : File.Open(path, FileMode.CreateNew);
            Write(ObservationEncoding.Begin(StreamSchema).WriteString(name).ToArray());
        }

        internal void Add(byte[] row)
        {
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, row.Length);
            Write(length);
            Write(row);
            rows = checked(rows + 1);
        }

        private void Write(ReadOnlySpan<byte> content)
        {
            hash.AppendData(content);
            stream?.Write(content);
            bytes = checked(bytes + content.Length);
        }

        internal V4DatasetTable Finish()
        {
            stream?.Flush();
            return new(name, rows, bytes, Convert.ToHexStringLower(hash.GetHashAndReset()));
        }

        public void Dispose()
        {
            stream?.Dispose();
            hash.Dispose();
        }
    }
}
