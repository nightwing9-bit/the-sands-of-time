using System.Diagnostics;
using System.Text.Json;
using Marrow.Infrastructure.Timeline;
using Marrow.Timeline;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Revenant.Contracts;
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
