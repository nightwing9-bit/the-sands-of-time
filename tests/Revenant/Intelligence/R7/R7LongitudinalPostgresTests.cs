using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Marrow.Infrastructure.Timeline;
using Marrow.Timeline;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Revenant.Contracts;
using Revenant.Intelligence;
using Revenant.Observations;
using Revenant.Payments;
using Revenant.Synthetic;
using Revenant.Tests.Ledger;
using Revenant.Tests.Payments;
using Revenant.Timeline;
using Xunit;

namespace Revenant.Tests.Intelligence.R7;

[Collection(PaymentCollection.Name)]
public sealed class R7LongitudinalPostgresTests(LedgerDatabase db)
{
    private const long Bucket = ObservedFinanceVersion.BucketMs;
    private const string Tenant = PaymentKit.TenantA;
    private const string Owner = PaymentKit.Operator;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    [Fact]
    [Trait("Category", "R7Longitudinal")]
    public async Task Generate_the_two_seven_day_PostgreSQL_pilot_roots()
    {
        Assert.True(OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64);
        var path = Environment.GetEnvironmentVariable("REVENANT_R7_LONGITUDINAL_OUTPUT")
            ?? throw new InvalidOperationException("Set the longitudinal evidence directory.");
        Directory.CreateDirectory(path);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(35));
        var logins = await db.ProvisionDeployedLoginsAsync();
        await using var writer = NpgsqlDataSource.Create(logins.WorkerLedger);
        var services = new ServiceCollection();
        services.AddSingleton(_ => NpgsqlDataSource.Create(logins.Worker));
        services.AddMarrowTimeline(ObservedFinanceTimelineVersions.All, ObservedFinanceTimelineVersions.CheckpointInterval);
        await using var host = services.BuildServiceProvider();
        var ledger = PaymentKit.Store(writer);
        var store = new V4SimulationStore(writer, ledger: ledger);
        var branch = new V4BranchExecution(store, host.GetRequiredService<ITimelineEngine<ObservedFinanceState, ObservedFinanceInput, ObservedFinanceContext>>());
        var executor = new V4EpisodeExecutor(store, branch, ledger);
        var summaries = await Task.WhenAll(V4LongitudinalPlanner.Roots(LongitudinalSplit.Pilot).Select(async root =>
        {
            var episode = V4LongitudinalPlanner.Plan(root, new ScenarioRunId(new Guid(Convert.FromHexString(root.Key[..32]), bigEndian: true)));
            var result = await executor.ExecuteAsync(Tenant, Owner, episode.Financial, timeout.Token, reviewTimes: episode.ReviewTimes);
            return Export(path, episode, result);
        }));
        File.WriteAllBytes(Path.Combine(path, "pilot.json"), JsonSerializer.SerializeToUtf8Bytes(new
        {
            Generator = V4LongitudinalPlanner.Version, Revision = Environment.GetEnvironmentVariable("REVENANT_R7_SOURCE_SHA"),
            Runtime = RuntimeInformation.FrameworkDescription, Platform = RuntimeInformation.RuntimeIdentifier, Roots = summaries,
        }, Json));
    }

    private static object Export(string directory, V4LongitudinalEpisode episode, V4ExecutedEpisode result)
    {
        var timer = Stopwatch.StartNew();
        var plan = episode.Financial;
        var projection = V4ObservationProjector.Project(result.History);
        var seals = episode.Root.Days * V4LongitudinalPlanner.BucketsPerDay;
        Assert.Equal(seals * 6, projection.Banks.Count);
        var origin = plan.Opening.Manifest.StartUtcMs;
        var accepted = result.History.Segments.SelectMany(s => s.Facts)
            .Where(f => f.Input.Kind == ObservedFactKind.PaymentIntent)
            .Select(f => Revenant.Scenario.PaymentIntentCodec.Instance.Decode(f.Input.Payload.AsSpan()))
            .ToLookup(p => (p.SourceAccountId, (p.EventTimeUtcMs - origin) / Bucket));
        var targets = V4BankTargets.Build(projection.Banks).ToLookup(t => t.BankId);
        var path = Path.Combine(directory, episode.Root.Key);
        Directory.CreateDirectory(path);
        var streams = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var accounts = plan.Opening.Manifest.Accounts.Select(a =>
        {
            var series = projection.AccountOutgoingSeries(a.AccountId, plan.EndMs);
            Assert.Equal(seals, series.Points.Length);
            Assert.All(series.Points.Select((p, i) => (p.Value, Expected: accepted[(a.AccountId, i)].LongCount())),
                entry => Assert.Equal(entry.Expected, entry.Value));
            Save(a.AccountId.Replace('/', '_') + ".series", series.Encode());
            var counts = series.Points.Select(p => p.Value).ToArray();
            var events = episode.Events.Where(e => e.Subject == a.AccountId).ToArray();
            var labeled = events.SelectMany(e => Enumerable.Range(e.Start, e.End - e.Start + 1)).ToHashSet();
            return new
            {
                a.AccountId, Accepted = counts.Sum(),
                Nonzero = counts.Count(x => x > 0), NormalNonzero = counts.Where((x, i) => x > 0 && !labeled.Contains(i)).Count(),
                NormalActiveDays = counts.Select((x, i) => (x, i)).Chunk(288)
                    .Count(d => d.Any(x => x.x > 0 && !labeled.Contains(x.i))),
                Qualified = counts.Take(288).Count(n => n > 0) >= 8,
                HardNegatives = events.Count(e => e.Kind == LongitudinalEventKind.LegitimateBurst),
                Anomalies = events.Count(e => e.Kind != LongitudinalEventKind.LegitimateBurst),
            };
        }).ToArray();
        var banks = plan.Opening.Manifest.Banks.Select(b =>
        {
            var series = projection.BankAvailableSeries(b.BankId, plan.EndMs);
            Save(b.BankId + ".series", series.Encode());
            var balances = series.Points.Select(p => p.Value).ToArray();
            var reference = series.ReferenceMinor!.Value;
            var floor = reference / 10;
            var changes = new[] { reference }.Concat(balances).Zip(balances, (before, after) => Math.Abs(after - before))
                .Where(x => x > 0).Order().ToArray();
            var normalize = new AdvisoryBalanceNormalization(reference, 1_024);
            var actual = targets[b.BankId].ToArray();
            var samples = actual.Select((t, i) => (t, i)).Where(x => x.t.FullyCovered).ToArray();
            foreach (var (t, i) in samples)
                Assert.Equal(t.Horizons.Select(h => h!.Balance.Available), balances.Skip(i + 1).Take(8));
            var persistence = samples.SelectMany(x => x.t.Horizons.Select(h => Math.Abs(h!.Balance.Available - balances[x.i]))).Average();
            var events = episode.Events.Where(e => e.Subject == b.BankId).Select(e => new
            {
                e.Kind, e.Start, e.End, Minimum = balances[e.Start..(e.End + 1)].Min(),
                Crossings = Enumerable.Range(e.Start, e.End - e.Start + 1)
                    .Count(i => balances[i - 1] >= floor && balances[i] < floor),
            }).ToArray();
            return new
            {
                b.BankId, ReferenceMinor = reference, FloorMinor = floor, Changed = changes.Length,
                ChangeP50 = changes.ElementAtOrDefault(changes.Length / 2),
                ChangeP90 = changes.ElementAtOrDefault(changes.Length * 9 / 10),
                NearFloor = balances.Count(v => v <= floor * 2), BelowFloor = balances.Count(v => v < floor),
                FullOrigins = samples.Length, PersistenceMae = persistence,
                MaxPrecisionError = balances.Max(v => Math.Abs(v - normalize.Restore(normalize.Normalize(v)))),
                Events = events,
            };
        }).ToArray();
        // Hidden event annotations are stored independently from the causal model inputs.
        Save("truth.json", JsonSerializer.SerializeToUtf8Bytes(new
        {
            Schema = V4LongitudinalPlanner.TruthSchema, DefaultAccountBucket = "Normal", Events = episode.Events,
        }, Json));
        var facts = result.History.Segments.SelectMany(s => s.Facts).ToArray();
        using var process = Process.GetCurrentProcess();
        return new
        {
            episode.Root, episode.BehaviorSha256, plan.Opening.Manifest.ContentHash, Execution = plan.Opening.RunId.Value, Seals = seals,
            Streams = streams, Accounts = accounts, Banks = banks,
            Attempted = plan.Payments.Length, Accepted = accepted.Sum(g => g.Count()),
            PostedSettlements = projection.Banks.Sum(b => b.SettledTransfers) / 2,
            SettlementFacts = facts.Count(f => f.Input.Kind == ObservedFactKind.Settlement),
            Held = projection.Banks.Sum(b => b.Held), ReservedMinor = projection.Banks.Sum(b => b.Reserved),
            Releases = facts.Count(f => f.Input.Kind == ObservedFactKind.ReviewDecision),
            ReleasedMinor = projection.Banks.Sum(b => b.Released), ReturnFacts = facts.Count(f => f.Input.Kind == ObservedFactKind.ReturnInstruction),
            ExecuteMs = result.Timing.Total.TotalMilliseconds, ProjectionAndExportMs = timer.Elapsed.TotalMilliseconds, process.PeakWorkingSet64,
        };

        void Save(string name, byte[] bytes)
        {
            File.WriteAllBytes(Path.Combine(path, name), bytes);
            streams.Add(name, ObservationEncoding.Hash(bytes));
        }
    }
}
