using System.Text.Json;
using System.Text.Json.Serialization;
using Revenant.Contracts;
using Revenant.Finance;
using Revenant.Observations;
using Revenant.Scenario;
using Revenant.Synthetic;
using Revenant.Tests.Observations;
using Revenant.Timeline;
using Xunit;

namespace Revenant.Tests.Intelligence.R7;

public sealed class R7HistoricalCensus
{
    private const long Bucket = ObservedFinanceVersion.BucketMs;
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    [Trait("Category", "OfflineTraining")]
    public void Reconstruct_and_audit_the_R4_R5_R6_histories()
    {
        var r6Directory = Environment.GetEnvironmentVariable("REVENANT_R6_CENSUS_INPUT")
            ?? throw new InvalidOperationException("Provide the retained R6 qualification artifact directory.");
        var outputPath = Environment.GetEnvironmentVariable("REVENANT_R7_CENSUS_OUTPUT")
            ?? throw new InvalidOperationException("Provide the census report path.");
        var r4 = ReadCohort("r4", V4EpisodeConfig.Qualification);
        var r5 = ReadCohort("r5", V4EpisodeConfig.RiskQualification);
        var r6 = ReadR6(r6Directory);
        File.WriteAllBytes(outputPath, JsonSerializer.SerializeToUtf8Bytes(new
        {
            Schema = "revenant-r7-historical-series-census/1",
            BufferFloor = "diagnostic 10% of opening available balance, rounded down",
            Percentiles = "nearest rank",
            R4 = r4,
            R5 = r5,
            R6 = r6,
        }, Json));
    }

    private static object ReadCohort(string name, V4EpisodeConfig config)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TheSandsOfTime.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var manifestPath = Path.Combine(directory.FullName, "docs", "recovery", $"{name}-qualified-dataset-manifest.json");
        var published = JsonSerializer.Deserialize<V4DatasetManifest>(File.ReadAllBytes(manifestPath), Json)!;
        Assert.Equal(published.Sha256, ObservationEncoding.Hash(V4DatasetExporter.EncodeManifest(published)));
        using var exporter = new V4DatasetExporter(config, published.CodeRevision);
        var accounts = new List<AccountActivity>();
        var banks = new List<object>();
        foreach (var group in config.Roots())
        {
            var design = V4EpisodePlanner.Plan(config, group, new(Guid.NewGuid()));
            var episode = ReconstructRulesOnly(design.Financial);
            var projection = V4ObservationProjector.Project(episode.History, captureRisk: config.IsV2);
            exporter.Add(design, episode, projection);
            var origin = design.Financial.Opening.Manifest.StartUtcMs;
            accounts.AddRange(AccountActivities(design, CommittedIntents(episode.History), projection.Banks.Count / 6, projection));
            banks.Add(new { group.Key, group.Split, Banks = BankActivities(design, projection.Banks) });
            Assert.Equal(origin + config.Duration, projection.Banks[^1].Seal.BoundaryMs);
        }
        // This checks every stream hash as well as every normalized opening and behavior fingerprint.
        var regenerated = exporter.Complete();
        Assert.Equal(published.Sha256, regenerated.Sha256);
        return new { published.Sha256, Accounts = Summarize(accounts), Roots = banks };
    }

    private static object ReadR6(string directory)
    {
        var bankPath = Path.Combine(directory, "model-aware-bank-observations.jsonl");
        var hash = ObservationEncoding.Hash(File.ReadAllBytes(bankPath));
        Assert.Equal("cf1775026e3d48e278f77a4ca69519dcf930aa0cbb5b1497bc13173a7c51a3bb", hash);
        using var report = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "qualification.json")));
        var root = report.RootElement;
        Assert.Equal("4ad02158b5b626738bc7ae88d7ffa4dee099b5d5", root.GetProperty("Revision").GetString());
        var execution = new ScenarioRunId(root.GetProperty("LearnedExecution").GetProperty("Value").GetGuid());
        var config = V4EpisodeConfig.RiskRingChallenge;
        var group = config.Roots().Single(g => g.Ordinal == root.GetProperty("RootOrdinal").GetInt32());
        var design = V4EpisodePlanner.Plan(config, group, execution);
        Assert.Equal(root.GetProperty("SharedPlan").GetString(), design.HistoryFingerprint);
        var rows = File.ReadLines(bankPath).Select(line =>
        {
            using var json = JsonDocument.Parse(line);
            var item = json.RootElement;
            var row = V4BankObservation.Decode(item.GetProperty("Canonical").GetBytesFromBase64(), item.GetProperty("Digest").GetString()!);
            Assert.Equal(row.BankId, item.GetProperty("BankId").GetString());
            Assert.Equal(row.Seal.BoundaryMs, item.GetProperty("BoundaryMs").GetInt64());
            Assert.Equal(execution, row.Seal.ExecutionRunId);
            return row;
        }).ToArray();

        // The R6 retained stream proves all planned attempts were accepted: compare each bank and bucket,
        // including the extra seal that contains the authorized review and settlement but no additional intents.
        var intents = design.Financial.Payments.Select(p => p.Intent).ToArray();
        var perBank = intents.GroupBy(p => (p.SourceBankId, ClosingBoundary(p.EventTimeUtcMs)))
            .ToDictionary(g => g.Key, g => g.LongCount());
        Assert.Equal(intents.LongLength, rows.Sum(r => r.Intents));
        Assert.All(rows, r => Assert.Equal(perBank.GetValueOrDefault((r.BankId, r.Seal.BoundaryMs)), r.Intents));
        var origin = design.Financial.Opening.Manifest.StartUtcMs;
        Assert.Equal(origin + Bucket, rows[0].Seal.BoundaryMs);
        Assert.Equal(design.Financial.EndMs + Bucket, rows[^1].Seal.BoundaryMs);
        Assert.Equal(5_190, rows.Length);
        var baseline = V4ObservationProjector.Project(ReconstructRulesOnly(design.Financial).History).Banks;
        var actual = rows.ToDictionary(r => (r.BankId, r.Seal.BoundaryMs));
        var differences = baseline.Select(b =>
        {
            var model = actual[(b.BankId, b.Seal.BoundaryMs)];
            return (Available: model.Balance.Available - b.Balance.Available,
                Posted: model.Balance.Posted - b.Balance.Posted, Reserved: model.Balance.Reserved - b.Balance.Reserved);
        }).ToArray();
        return new
        {
            BankStreamSha256 = hash, group.Key, group.Split,
            OpeningBoundaryMs = origin, PlannedEndMs = design.Financial.EndMs,
            ObservedClosingBoundaryMs = rows[^1].Seal.BoundaryMs,
            Accounts = Summarize(AccountActivities(design, intents, rows.Length / 6).ToList()),
            Banks = BankActivities(design, rows),
            LearnedHolds = root.GetProperty("LearnedHolds").GetInt32(),
            ReviewedPrincipalMinor = root.GetProperty("Example").GetProperty("PrincipalReserved").GetInt64(),
            ComparedWithRulesOnly = new
            {
                ComparedRows = differences.Length,
                ChangedAvailableRows = differences.Count(d => d.Available != 0),
                ChangedPostedRows = differences.Count(d => d.Posted != 0),
                ChangedReservedRows = differences.Count(d => d.Reserved != 0),
                MaxAbsoluteAvailableChangeMinor = differences.Max(d => Math.Abs(d.Available)),
            },
        };
    }

    private static V4ExecutedEpisode ReconstructRulesOnly(V4EpisodePlan plan)
    {
        var fixture = new ObservationFixture(plan.Opening);
        var outcomes = new List<V4AttemptOutcome>();
        foreach (var payment in plan.Payments)
        {
            var p = payment.Intent;
            while (p.EventTimeUtcMs >= ObservedFinanceVersion.NextBoundary(fixture.State.LastSealedBoundaryMs))
                fixture.Seal();
            fixture.Intake(p);
            fixture.Screen(p, p.EventTimeUtcMs);
            var screening = fixture.State.Ledger.Payments[p.IntentId].Status;
            string? settled = null, returned = null;
            if (screening == PaymentStatus.Cleared)
            {
                fixture.Settle(p, p.EventTimeUtcMs);
                var settlement = fixture.State.Ledger.Payments[p.IntentId].Status;
                settled = settlement.ToString();
                if (settlement == PaymentStatus.Settled && payment.RequestReturn)
                {
                    fixture.Return(p, p.EventTimeUtcMs);
                    returned = nameof(PaymentStatus.Returned);
                }
            }
            outcomes.Add(new(p.IntentId, nameof(PaymentStatus.Initiated), screening.ToString(), settled, returned));
        }
        while (fixture.State.LastSealedBoundaryMs < plan.EndMs) fixture.Seal();
        return new(fixture.History(), [.. outcomes], new(TimeSpan.Zero, TimeSpan.Zero, []));
    }

    private static IEnumerable<PaymentIntent> CommittedIntents(V4ProjectionHistory history) =>
        history.Segments.SelectMany(s => s.Facts).Where(f => f.Input.Kind == ObservedFactKind.PaymentIntent)
            .Select(f => PaymentIntentCodec.Instance.Decode(f.Input.Payload.AsSpan()));

    private sealed record AccountActivity(long[] Counts, int ActiveDays, int Positive, int Normal, int KnownNormalBursts,
        int MultiIntentLegitimate, int HardNegativeOutsidePositive,
        int NonzeroAfter48, int EligibleOriginsAfter48);

    private static IEnumerable<AccountActivity> AccountActivities(V4EpisodeDesign design, IEnumerable<PaymentIntent> intents,
        int seals, V4ObservationProjector? projection = null)
    {
        var origin = design.Financial.Opening.Manifest.StartUtcMs;
        var bySender = intents.ToLookup(p => p.SourceAccountId, StringComparer.Ordinal);
        var truth = design.Truth.Anomalies.ToLookup(a => a.AccountId, StringComparer.Ordinal);
        var paymentTruth = design.Truth.Payments.ToDictionary(p => p.IntentId, StringComparer.Ordinal);
        foreach (var account in design.Financial.Opening.Manifest.Accounts)
        {
            var counts = new long[seals];
            var hardCounts = new long[seals];
            var legitimateCounts = new long[seals];
            foreach (var p in bySender[account.AccountId])
            {
                var index = checked((int)((ClosingBoundary(p.EventTimeUtcMs) - origin) / Bucket - 1));
                counts[index]++;
                var label = paymentTruth[p.IntentId];
                if (label.HardNegative) hardCounts[index]++;
                if (!label.Malicious) legitimateCounts[index]++;
            }
            var labels = truth[account.AccountId];
            if (projection is not null)
            {
                var series = projection.AccountOutgoingSeries(account.AccountId, origin + seals * Bucket);
                Assert.Equal(AdvisorySeriesCoverage.Complete, series.Coverage);
                Assert.Equal(counts, series.Points.Select(p => p.Value));
            }
            var positive = labels.Where(a => a.Anomalous).Select(a => a.BoundaryMs).ToHashSet();
            var normal = labels.Where(a => !a.Anomalous).Select(a => a.BoundaryMs).ToHashSet();
            yield return new(counts, counts.Chunk(288).Count(day => day.Any(n => n > 0)),
                positive.Count, normal.Count, normal.Count(b => counts[(b - origin) / Bucket - 1] >= 2),
                legitimateCounts.Where((n, i) => n == counts[i]).Count(n => n >= 2),
                hardCounts.Select((n, i) => (n, i)).Count(x => x.n >= 2 && !positive.Contains(origin + (x.i + 1) * Bucket)),
                counts.Skip(48).Count(n => n > 0), Math.Max(0, seals - 48));
        }
    }

    private static object Summarize(List<AccountActivity> accounts)
    {
        var counts = accounts.SelectMany(a => a.Counts).ToArray();
        var nonzero = counts.Where(n => n > 0).ToArray();
        var anomaly = accounts.Sum(a => a.Positive);
        var normal = accounts.Sum(a => a.Normal);
        return new
        {
            DistinctAccounts = accounts.Count, AcceptedIntents = counts.Sum(),
            OutgoingPerAccount = Distribution(accounts.Select(a => a.Counts.Sum())),
            NonzeroBucketsPerAccount = Distribution(accounts.Select(a => a.Counts.LongCount(n => n > 0))),
            ZeroOnlyAccounts = accounts.Count(a => a.Counts.All(n => n == 0)),
            ActiveDays = Distribution(accounts.Select(a => (long)a.ActiveDays)),
            ActiveOnAtLeastTwoDays = accounts.Count(a => a.ActiveDays >= 2),
            Buckets = counts.Length, NonzeroBuckets = nonzero.Length, ZeroFraction = 1d - (double)nonzero.Length / counts.Length,
            ActiveBucketSize = Distribution(nonzero), BurstBuckets = nonzero.Count(n => n >= 2),
            Historical48PriorBuckets = accounts.Sum(a => a.EligibleOriginsAfter48),
            NonzeroAfter48PriorBuckets = accounts.Sum(a => a.NonzeroAfter48),
            SequencesWith49Buckets = accounts.Count(a => a.Counts.Length >= 49),
            SequencesWith49BucketsAnd16ActiveBuckets = accounts.Count(a => a.Counts.Length >= 49 && a.Counts.Count(n => n > 0) >= 16),
            ExplicitPositive = anomaly, ExplicitNormal = normal, Unlabeled = counts.Length - anomaly - normal,
            KnownNormalBurstBuckets = accounts.Sum(a => a.KnownNormalBursts),
            AllLegitimateMultiIntentBuckets = accounts.Sum(a => a.MultiIntentLegitimate),
            MultiIntentHardNegativeBucketsOutsidePositiveTruth = accounts.Sum(a => a.HardNegativeOutsidePositive),
            PerAccountLag1 = Correlations(accounts.Select(a => a.Counts), 1),
            PerAccountLag8 = Correlations(accounts.Select(a => a.Counts), 8),
            PerAccountLag288 = Correlations(accounts.Select(a => a.Counts), 288),
        };
    }

    private static object[] BankActivities(V4EpisodeDesign design, IReadOnlyList<V4BankObservation> rows)
    {
        var opening = design.Financial.Opening.Manifest;
        var targets = V4BankTargets.Build(rows).ToLookup(t => t.BankId, StringComparer.Ordinal);
        return rows.GroupBy(r => r.BankId).Select(group =>
        {
            var bank = group.Key;
            var start = opening.Accounts.Where(a => a.BankId == bank).Sum(a => a.OpeningBalance.MinorUnits);
            var floor = start / 10;
            var ordered = group.ToArray();
            Assert.Equal(Enumerable.Range(1, ordered.Length).Select(i => opening.StartUtcMs + i * Bucket),
                ordered.Select(r => r.Seal.BoundaryMs));
            var available = ordered.Select(r => r.Balance.Available).ToArray();
            var changes = new[] { start }.Concat(available).Zip(available, (previous, next) => next - previous).ToArray();
            var magnitude = changes.Where(n => n != 0).Select(Math.Abs).ToArray();
            return (object)new
            {
                Bank = bank, Seals = ordered.Length, FirstBoundaryMs = ordered[0].Seal.BoundaryMs,
                LastBoundaryMs = ordered[^1].Seal.BoundaryMs, OpeningAvailableMinor = start,
                PostedMinor = Distribution(ordered.Select(r => r.Balance.Posted)),
                ReservedMinor = Distribution(ordered.Select(r => r.Balance.Reserved)),
                AvailableMinor = Distribution(available), ChangedBuckets = magnitude.Length,
                ConstantFraction = 1d - (double)magnitude.Length / ordered.Length,
                ChangedMagnitudeMinor = Distribution(magnitude),
                SettlementChangeBuckets = ordered.Count(r => r.NetSettled != 0),
                ReturnChangeBuckets = ordered.Count(r => r.ReturnedIn != r.ReturnedOut),
                ReservationChangeBuckets = ordered.Count(r => r.Reserved != r.Released),
                SettledInMinor = ordered.Sum(r => r.SettledIn), SettledOutMinor = ordered.Sum(r => r.SettledOut),
                ReservedFlowMinor = ordered.Sum(r => r.Reserved), ReleasedFlowMinor = ordered.Sum(r => r.Released),
                FloorMinor = floor, BelowFloorBuckets = available.Count(a => a < floor),
                BelowFloorEntries = new[] { start }.Concat(available).Zip(available).Count(p => p.First >= floor && p.Second < floor),
                AtOrBelowTwiceFloorBuckets = available.Count(a => a <= 2 * floor),
                ForecastOrigins = ordered.Length, FullTargetOrigins = targets[bank].Count(t => t.FullyCovered),
                PerHorizonCoverage = Enumerable.Range(0, 8).Select(i => targets[bank].Count(t => t.Horizons[i] is not null)).ToArray(),
                Lag1Change = Correlation(changes, 1), Lag8Change = Correlation(changes, 8), Lag288Change = Correlation(changes, 288),
                DailyChangedBuckets = changes.Chunk(288).Select(day => day.Count(n => n != 0)).ToArray(),
            };
        }).ToArray();
    }

    private static long ClosingBoundary(long at) => checked(at - at % Bucket + Bucket);

    private static object? Distribution(IEnumerable<long> values)
    {
        var ordered = values.Order().ToArray();
        return ordered.Length == 0 ? null : new
        {
            Min = ordered[0], P50 = Percentile(0.5), P90 = Percentile(0.9), P99 = Percentile(0.99), Max = ordered[^1],
        };
        long Percentile(double fraction) => ordered[(int)Math.Ceiling(ordered.Length * fraction) - 1];
    }

    private static object Correlations(IEnumerable<long[]> series, int lag)
    {
        var defined = series.Select(x => Correlation(x, lag)).OfType<double>().Order().ToArray();
        return new
        {
            Defined = defined.Length,
            Median = defined.Length == 0 ? (double?)null : defined[(defined.Length - 1) / 2],
            P90 = defined.Length == 0 ? (double?)null : defined[(int)Math.Ceiling(0.9 * defined.Length) - 1],
        };
    }

    private static double? Correlation(long[] series, int lag)
    {
        if (series.Length <= lag) return null;
        var pairs = series.Zip(series.Skip(lag)).ToArray();
        var x = pairs.Average(p => (double)p.First);
        var y = pairs.Average(p => (double)p.Second);
        double covariance = 0, xx = 0, yy = 0;
        foreach (var (a, b) in pairs)
        {
            covariance += (a - x) * (b - y);
            xx += (a - x) * (a - x);
            yy += (b - y) * (b - y);
        }
        return xx == 0 || yy == 0 ? null : covariance / Math.Sqrt(xx * yy);
    }
}
