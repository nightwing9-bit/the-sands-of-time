using System.Diagnostics;
using System.Text.Json;
using Marrow.Infrastructure.Timeline;
using Marrow.Timeline;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Revenant.Contracts;
using Revenant.Finance;
using Revenant.Observations;
using Revenant.Payments;
using Revenant.Synthetic;
using Revenant.Tests.Ledger;
using Revenant.Timeline;
using Xunit;
using Xunit.Abstractions;

namespace Revenant.Tests.Payments;

[Collection(PaymentCollection.Name)]
public sealed class V4DatasetPostgresTests(LedgerDatabase db, ITestOutputHelper output)
{
    private const string Tenant = PaymentKit.TenantA;
    private const string Owner = PaymentKit.Operator;
    private const string TestRevision = "0000000000000000000000000000000000000000";

    [Fact]
    [Trait("Category", "Integration")]
    public async Task V2_six_bank_smoke_verifies_real_opening_risk_export_and_reader()
    {
        using var time = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        var config = V4EpisodeConfig.RiskSmoke;
        var directory = Directory.CreateTempSubdirectory("r5-smoke-");
        try
        {
            var logins = await db.ProvisionDeployedLoginsAsync();
            await using var host = Host(logins.Worker);
            var (store, executor) = Executor(host);
            var group = Assert.Single(config.Roots());
            var (design, execution, projection) = await ExecuteRiskRoot(config, group, store, executor, time.Token);
            V4DatasetManifest manifest;
            using (var exporter = new V4DatasetExporter(config, TestRevision, directory.FullName, writeTables: true))
            {
                exporter.Add(design, execution, projection);
                manifest = exporter.Complete();
            }
            var reader = new V4RiskDatasetReader(directory.FullName, manifest.Sha256);
            var rows = reader.ReadSplit(group.Split);
            Assert.Contains(rows, r => r.Truth.Malicious && r.Truth.Attack == EpisodeAttack.Ring);
            Assert.Contains(rows, r => r.Truth.HardNegative);
            Assert.Equal(576 * 6, projection.Banks.Count);
            var ordinary = design.Truth.Payments.First(x => !x.Malicious);
            var attack = design.Truth.Payments.First(x => x.Malicious);
            using (var relabeled = new V4DatasetExporter(config, TestRevision))
            {
                var changed = ordinary with { Malicious = true, Attack = attack.Attack, CampaignKey = attack.CampaignKey, HardNegative = false };
                relabeled.Add(design with { Truth = design.Truth with { Payments = design.Truth.Payments.Replace(ordinary, changed) } },
                    execution, projection);
                var changedSplit = Assert.Single(relabeled.Complete().Splits);
                Assert.Equal(manifest.Splits[0].ModelSha256, changedSplit.ModelSha256);
                Assert.NotEqual(manifest.Splits[0].TruthSha256, changedSplit.TruthSha256);
            }
            var damaged = Path.Combine(directory.FullName, "Train-risk-asof.bin");
            using (var stream = File.Open(damaged, FileMode.Open, FileAccess.Write))
                stream.WriteByte(255);
            Assert.Throws<Marrow.Kernel.Canonical.CanonicalFormatException>(() => reader.ReadSplit(group.Split));
            output.WriteLine($"V2 native smoke: {rows.Count} eligible rows, {manifest.Sha256}");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "R5Cohort")]
    public async Task Generate_native_R5_cohort()
    {
        var config = Environment.GetEnvironmentVariable("R5_DATASET_MODE") switch
        {
            "qualification" => V4EpisodeConfig.RiskQualification,
            "challenge" => V4EpisodeConfig.RiskRingChallenge,
            _ => throw new InvalidOperationException("Select qualification or the frozen independent challenge explicitly."),
        };
        var revision = Environment.GetEnvironmentVariable("R5_CODE_REVISION") ?? throw new InvalidOperationException("Source revision required.");
        var directory = Environment.GetEnvironmentVariable("R5_DATASET_OUTPUT") ?? throw new InvalidOperationException("Output directory required.");
        using var time = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        var logins = await db.ProvisionDeployedLoginsAsync();
        await using var host = Host(logins.Worker);
        var (store, executor) = Executor(host);
        V4DatasetManifest manifest;
        using (var exporter = new V4DatasetExporter(config, revision, directory, writeTables: true))
        {
            foreach (var group in config.Roots())
            {
                var (design, executed, projected) = await ExecuteRiskRoot(config, group, store, executor, time.Token);
                exporter.Add(design, executed, projected);
                output.WriteLine($"{group.Split} {group.Ordinal}: {projected.Banks.Count / 6} seals; {projected.RiskAsOf.Count} screening rows");
            }
            manifest = exporter.Complete();
        }
        Assert.Equal(config.Roots().Count() * 864 * 6, manifest.Splits.Sum(s => s.Tables.Single(t => t.Name == "bank").Rows));
        if (config.ShiftedRing)
        {
            output.WriteLine($"Independent ring challenge manifest {manifest.Sha256}");
            return;
        }
        var reader = new V4RiskDatasetReader(directory, manifest.Sha256);
        var developmentEvidence = new List<object>();
        foreach (var split in new[] { V4DatasetSplit.Train, V4DatasetSplit.Validation })
        {
            var rows = reader.ReadSplit(split);
            output.WriteLine($"{split}: {rows.Count} eligible opportunities, {rows.Count(r => r.Truth.Malicious)} malicious; "
                + $"{rows.Count(r => r.Screening == nameof(PaymentStatus.Held))} v7 holds");
            developmentEvidence.Add(new
            {
                Split = split.ToString(),
                Day = Distribution(rows, r => (r.At - r.OriginMs) / V4EpisodeConfig.WarmupMs),
                Weekday = Distribution(rows, r => r.AsOf.UtcDayOfWeek),
                Hour = Distribution(rows, r => r.AsOf.UtcMinuteOfDay / 60),
                HistoryCoverage = Distribution(rows, r => string.Join(",", r.AsOf.Windows.Select(w => w.Coverage))),
                AmountBand = Distribution(rows, r => r.AsOf.AmountMinor switch
                {
                    < 10_000 => "<100 USD", < 50_000 => "100–499 USD", < 100_000 => "500–999 USD",
                    < 200_000 => "1,000–1,999 USD", _ => "2,000+ USD",
                }),
                ReportedNovelty = Distribution(rows, r => r.AsOf.ReportedNewBeneficiary),
                Roots = Distribution(rows, r => r.Root),
                Attack = Distribution(rows.Where(r => r.Truth.Malicious), r => r.Truth.Attack!.Value.ToString()),
                Baselines = new[] { 2, 5, 10 }.Select(percent => new
                {
                    Percent = percent,
                    V7 = Review(V4RiskReviewBudget.Evaluate(rows, percent, true, null)),
                    Heuristic = Review(V4RiskReviewBudget.Evaluate(rows, percent, false, V4RiskReviewBudget.Heuristic)),
                    V7PlusHeuristic = Review(V4RiskReviewBudget.Evaluate(rows, percent, true, V4RiskReviewBudget.Heuristic)),
                }),
            });
        }
        File.WriteAllBytes(Path.Combine(directory, "development-evidence.json"), JsonSerializer.SerializeToUtf8Bytes(
            developmentEvidence, new JsonSerializerOptions { WriteIndented = true }));
        output.WriteLine($"R5 qualification export manifest {manifest.Sha256}; risk schema {Revenant.Intelligence.RiskFeatureVectorV2.SchemaHash}");

        static object Distribution<TKey>(IEnumerable<V4RiskResearchRow> rows, Func<V4RiskResearchRow, TKey> key) =>
            rows.GroupBy(key).OrderBy(g => g.Key).Select(g => new
            {
                Key = g.Key, Malicious = g.Count(r => r.Truth.Malicious), Legitimate = g.Count(r => !r.Truth.Malicious),
            }).ToArray();

        static object Review(V4ReviewResult r) => new
        {
            r.Opportunities, r.Rejections, r.MandatoryHolds, r.Slots, r.Additional, r.Reviewed, r.Malicious, r.Legitimate,
            r.HardNegatives, r.TotalMalicious, r.Overloaded, r.ReviewRate, r.Recall, r.Precision,
        };
    }

    private static async Task<(V4EpisodeDesign Design, V4ExecutedEpisode Executed, V4ObservationProjector Projected)>
        ExecuteRiskRoot(V4EpisodeConfig config, V4RootGroup group, V4SimulationStore store, V4EpisodeExecutor executor,
            CancellationToken ct)
    {
        var design = V4EpisodePlanner.Plan(config, group, new(Guid.NewGuid()));
        var execution = await executor.ExecuteAsync(Tenant, Owner, design.Financial, ct);
        var projection = V4ObservationProjector.Project(execution.History, captureRisk: true);
        var segment = Assert.Single(execution.History.Segments);
        var firstEligible = projection.RiskAsOf.First(x => x.EffectiveTimeMs - design.Financial.Opening.Manifest.StartUtcMs
            >= V4EpisodeConfig.WarmupMs);
        var prefix = execution.History with
        {
            Segments = [segment with { Facts = segment.Facts.Take(checked((int)firstEligible.CutoffFactSeq)).ToArray(),
                LastFact = firstEligible.CutoffFactSeq }],
        };
        var live = V4ObservationProjector.RiskAt(prefix, firstEligible.IntentId, firstEligible.EffectiveTimeMs);
        Assert.Equal(firstEligible.CutoffPrefixDigest, live.CutoffPrefixDigest);
        Assert.Equal(firstEligible.Vector.Encode(), live.Vector.Encode());
        await store.MaterializeBanksAsync(Tenant, execution.History.ExecutionRunId, projection.Banks, ct);
        await store.MaterializeAccountsAsync(Tenant, execution.History.ExecutionRunId, projection.Accounts, ct);
        return (design, execution, projection);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Generated_smoke_reproduces_semantic_exports_under_different_execution_ids()
    {
        using var time = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = time.Token;
        var config = V4EpisodeConfig.Smoke;
        var group = Assert.Single(config.Roots());
        var logins = await db.ProvisionDeployedLoginsAsync();
        await using var host = Host(logins.Worker);
        var (store, executor) = Executor(host);
        using var one = new V4DatasetExporter(config, TestRevision);
        using var two = new V4DatasetExporter(config, TestRevision);
        var repeatA = V4EpisodePlanner.Plan(config, group, new(Guid.NewGuid()));
        var repeatB = V4EpisodePlanner.Plan(config, group, new(Guid.NewGuid()));
        var a = await executor.ExecuteAsync(Tenant, Owner, repeatA.Financial, ct);
        var b = await executor.ExecuteAsync(Tenant, Owner, repeatB.Financial, ct);
        var observationsA = V4ObservationProjector.Project(a.History);
        var observationsB = V4ObservationProjector.Project(b.History);
        Assert.NotEqual(a.History.ExecutionRunId, b.History.ExecutionRunId);
        Assert.NotEqual(observationsA.Banks[0].Seal.PrefixDigest, observationsB.Banks[0].Seal.PrefixDigest);
        Assert.Equal(144, observationsA.Banks.Count);
        Assert.NotEmpty(observationsA.Accounts);
        await store.MaterializeBanksAsync(Tenant, a.History.ExecutionRunId, observationsA.Banks, ct);
        await store.MaterializeAccountsAsync(Tenant, a.History.ExecutionRunId, observationsA.Accounts, ct);
        one.Add(repeatA, a, observationsA);
        two.Add(repeatB, b, observationsB);
        var manifest = one.Complete();
        Assert.Equal(manifest.Sha256, two.Complete().Sha256);

        // Change only the offline annotation, retaining the original commands, SQL facts, and observations.
        var original = repeatA.Truth.Payments.First(x => !x.Malicious);
        var attack = repeatA.Truth.Payments.First(x => x.Malicious);
        var flipped = original with { Malicious = true, Attack = attack.Attack, CampaignKey = attack.CampaignKey, HardNegative = false };
        using var relabeled = new V4DatasetExporter(config, TestRevision);
        relabeled.Add(repeatA with { Truth = repeatA.Truth with { Payments = repeatA.Truth.Payments.Replace(original, flipped) } },
            a, observationsA);
        var relabeledManifest = relabeled.Complete();
        Assert.Equal(manifest.Splits[0].ModelSha256, relabeledManifest.Splits[0].ModelSha256);
        Assert.NotEqual(manifest.Splits[0].TruthSha256, relabeledManifest.Splits[0].TruthSha256);
        var split = manifest.Splits[0];
        output.WriteLine($"2h generated smoke repeated under distinct execution IDs: {manifest.Sha256}; {split.Screened} screenings, {split.Settled} settlements, {split.Held} holds, {split.Returned} returns, {observationsA.Accounts.Count} account rows; root execution times {a.Timing.Total.TotalSeconds:F3}s/{b.Timing.Total.TotalSeconds:F3}s");
    }

    [Fact]
    [Trait("Category", "R4Cohort")]
    public async Task Generate_native_R4_cohort()
    {
        var mode = Environment.GetEnvironmentVariable("R4_DATASET_MODE");
        var config = mode switch
        {
            "pilot" => V4EpisodeConfig.Pilot,
            "qualification" => V4EpisodeConfig.Qualification,
            _ => throw new InvalidOperationException("Select pilot or qualification explicitly."),
        };
        var revision = Environment.GetEnvironmentVariable("R4_CODE_REVISION") ?? throw new InvalidOperationException("Source revision required.");
        var directory = Environment.GetEnvironmentVariable("R4_DATASET_OUTPUT") ?? throw new InvalidOperationException("Output directory required.");
        var full = Environment.GetEnvironmentVariable("R4_WRITE_TABLES") == "1";
        using var time = new CancellationTokenSource(TimeSpan.FromMinutes(mode == "pilot" ? 10 : 30));
        var ct = time.Token;
        using var export = new V4DatasetExporter(config, revision, directory, full);
        var timer = Stopwatch.StartNew();
        var logins = await db.ProvisionDeployedLoginsAsync();
        await using var host = Host(logins.Worker);
        var (store, executor) = Executor(host);
        var reports = new List<object>();
        using var process = Process.GetCurrentProcess();
        foreach (var group in config.Roots())
        {
            ct.ThrowIfCancellationRequested();
            var design = V4EpisodePlanner.Plan(config, group, new(Guid.NewGuid()));
            var executed = await executor.ExecuteAsync(Tenant, Owner, design.Financial, ct);
            var start = Stopwatch.GetTimestamp();
            var projected = V4ObservationProjector.Project(executed.History);
            var projectionMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            Assert.Equal(config.Duration / ObservedFinanceVersion.BucketMs * 6, projected.Banks.Count);
            await store.MaterializeBanksAsync(Tenant, executed.History.ExecutionRunId, projected.Banks, ct);
            await store.MaterializeAccountsAsync(Tenant, executed.History.ExecutionRunId, projected.Accounts, ct);
            export.Add(design, executed, projected);
            process.Refresh();
            reports.Add(new
            {
                group.Key, group.Split,
                Accounts = design.Financial.Opening.Manifest.Accounts.Length,
                Facts = executed.History.Segments[0].LastFact,
                Seals = projected.Banks.Count / 6, Bank = projected.Banks.Count, Activity = projected.Accounts.Count,
                AsOf = projected.FraudAsOf.Count, Attempts = design.Financial.Payments.Length,
                ExecutionMs = executed.Timing.Total.TotalMilliseconds,
                ReplayMs = executed.Timing.Replay.TotalMilliseconds, ProjectionMs = projectionMs,
                PeakWorkingSetBytes = process.PeakWorkingSet64,
                Bands = executed.Timing.Bands.Select(b => new { b.Band, b.Commands, DispatchMs = b.DispatchTime.TotalMilliseconds }),
            });
            output.WriteLine($"{group.Split} {group.Ordinal}: {projected.Banks.Count / 6} seals; {executed.History.Segments[0].LastFact} facts; {executed.Timing.Total.TotalSeconds:F3}s");
        }

        var manifest = export.Complete();
        var totalBanks = manifest.Splits.Sum(split => split.Tables.Single(table => table.Name == "bank").Rows);
        var expectedBanks = mode == "pilot" ? 6_912 : 124_416;
        Assert.Equal(expectedBanks, totalBanks);
        Assert.Equal(expectedBanks - manifest.Roots.Count * 8 * 6, manifest.Splits.Sum(split => split.FullTargets));
        File.WriteAllBytes(Path.Combine(directory, "performance.json"), JsonSerializer.SerializeToUtf8Bytes(new
        {
            Mode = mode, Revision = revision, Roots = manifest.Roots.Count, TotalMs = timer.Elapsed.TotalMilliseconds,
            PeakWorkingSetBytes = process.PeakWorkingSet64, RootsEvidence = reports,
        }, new JsonSerializerOptions { WriteIndented = true }));
        output.WriteLine($"{mode}: {manifest.Roots.Count} roots; {totalBanks / 6} seals; {totalBanks} bank records; manifest {manifest.Sha256}; {timer.Elapsed.TotalSeconds:F3}s");
    }

    private (V4SimulationStore Store, V4EpisodeExecutor Executor) Executor(ServiceProvider host)
    {
        var engine = host.GetRequiredService<ITimelineEngine<ObservedFinanceState, ObservedFinanceInput, ObservedFinanceContext>>();
        var ledger = PaymentKit.Store(db.Writer);
        var store = new V4SimulationStore(db.Writer, ledger: ledger);
        return (store, new(store, new V4BranchExecution(store, engine), ledger));
    }

    private static ServiceProvider Host(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        services.AddMarrowTimeline(ObservedFinanceTimelineVersions.All, ObservedFinanceTimelineVersions.CheckpointInterval);
        return services.BuildServiceProvider();
    }
}
