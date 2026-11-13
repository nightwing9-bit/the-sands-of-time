using System.Diagnostics;
using System.Buffers.Binary;
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

    // Generator-3 truth bytes stay identical: schema-2 event fields and rhythms are omitted when null.
    private static readonly JsonSerializerOptions TruthJson = new(Json) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    [Fact]
    [Trait("Category", "R7Longitudinal")]
    public Task Generate_the_two_seven_day_PostgreSQL_pilot_roots() =>
        Generate(V4LongitudinalPlanner.Roots(LongitudinalSplit.Pilot).ToArray(), "pilot.json", V4LongitudinalPlanner.Version);

    /// <summary>Generator-4 development pilot: three independent seven-day roots (train, calibrate, evaluate).</summary>
    [Fact]
    [Trait("Category", "R7Longitudinal")]
    public Task Generate_the_generator_4_PostgreSQL_development_pilot() =>
        Generate(V4LongitudinalPlannerG4.Roots(LongitudinalSplit.Pilot).Take(3).ToArray(), "pilot.json", V4LongitudinalPlannerG4.Version);

    [Fact]
    [Trait("Category", "R7Longitudinal")]
    public Task Generate_one_independent_PostgreSQL_qualification_root()
    {
        var split = Enum.Parse<LongitudinalSplit>(Environment.GetEnvironmentVariable("REVENANT_R7_COHORT_SPLIT") ?? "");
        var ordinal = int.Parse(Environment.GetEnvironmentVariable("REVENANT_R7_COHORT_ORDINAL") ?? "",
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.NotEqual(LongitudinalSplit.Pilot, split);
        // R7/2 qualification uses generator 4 only. Generator 3 remains reproducible for its archived pilot.
        var root = V4LongitudinalPlannerG4.Roots(split).Single(r => r.Index == ordinal);
        return Generate([root], "qualification.json", V4LongitudinalPlannerG4.Version);
    }

    private async Task Generate(LongitudinalRoot[] roots, string manifestName, string generator)
    {
        // Qualification evidence is generated on Linux AMD64. A development pilot (pilot.json, non-model-required roots) may be
        // generated on another host when explicitly allowed; the manifest records the runtime identifier either way.
        var developmentHost = manifestName == "pilot.json"
            && Environment.GetEnvironmentVariable("REVENANT_R7_ALLOW_DEVELOPMENT_HOST") == "1";
        Assert.True(developmentHost || (OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64));
        var path = Environment.GetEnvironmentVariable("REVENANT_R7_LONGITUDINAL_OUTPUT")
            ?? throw new InvalidOperationException("Set the longitudinal evidence directory.");
        Directory.CreateDirectory(path);
        var minutes = int.TryParse(Environment.GetEnvironmentVariable("REVENANT_R7_GENERATION_TIMEOUT_MINUTES"), out var m) && m > 0 ? m : 35;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(minutes));
        var logins = await db.ProvisionDeployedLoginsAsync();
        await ExecutePilot(roots, manifestName, generator, path, logins, timeout.Token);
    }

    internal static async Task ExecutePilot(LongitudinalRoot[] roots, string manifestName, string generator, string path,
        LedgerDatabase.DeployedLogins logins, CancellationToken cancellation, bool residualTraining = false)
    {
        Directory.CreateDirectory(path);
        await using var writer = NpgsqlDataSource.Create(logins.WorkerLedger);
        var services = new ServiceCollection();
        services.AddSingleton(_ => NpgsqlDataSource.Create(logins.Worker));
        services.AddMarrowTimeline(ObservedFinanceTimelineVersions.All, ObservedFinanceTimelineVersions.CheckpointInterval);
        await using var host = services.BuildServiceProvider();
        var ledger = PaymentKit.Store(writer);
        var riskModel = new Lazy<Marrow.Kernel.Results.Result<BehavioralRiskScorerV2, BehavioralRiskFailureV2>>(() =>
            BehavioralRiskScorerV2.Load(Path.Combine(AppContext.BaseDirectory, "Intelligence", "Artifacts",
                BehavioralRiskScorerV2.ArtifactDirectory), BehavioralRiskScorerV2.FeatureSchema, BehavioralRiskScorerV2.PublishedPin));
        var store = new V4SimulationStore(writer, ledger: ledger, model: riskModel);
        var branch = new V4BranchExecution(store, host.GetRequiredService<ITimelineEngine<ObservedFinanceState, ObservedFinanceInput, ObservedFinanceContext>>());
        var executor = new V4EpisodeExecutor(store, branch, ledger);
        var summaries = await Task.WhenAll(roots.Select(async root =>
        {
            var id = new ScenarioRunId(new Guid(Convert.FromHexString(root.Key[..32]), bigEndian: true));
            var episode = generator switch
            {
                V4LongitudinalPlannerG5.Version when residualTraining => V4LongitudinalPlannerG4.Plan(root, id, generator, residualTraining: true),
                V4LongitudinalPlannerG5.Version => V4LongitudinalPlannerG5.Plan(root, id),
                V4LongitudinalPlannerG4.Version => V4LongitudinalPlannerG4.Plan(root, id),
                _ => V4LongitudinalPlanner.Plan(root, id),
            };
            var riskMode = root.Split == LongitudinalSplit.Challenge ? ObservedRiskMode.ModelRequired : ObservedRiskMode.NotRequired;
            var result = await executor.ExecuteAsync(Tenant, Owner, episode.Financial, cancellation, riskMode,
                reviewTimes: episode.ReviewTimes);
            return Export(path, episode, result);
        }));
        var manifest = new Dictionary<string, object?>
        {
            ["Generator"] = generator, ["Revision"] = Environment.GetEnvironmentVariable("REVENANT_R7_SOURCE_SHA"),
            ["Runtime"] = RuntimeInformation.FrameworkDescription, ["Platform"] = RuntimeInformation.RuntimeIdentifier, ["Roots"] = summaries,
        };
        if (residualTraining) manifest.Add("TrainingVariant", V4LongitudinalPlannerG5.ResidualTrainingVariant);
        File.WriteAllBytes(Path.Combine(path, manifestName), JsonSerializer.SerializeToUtf8Bytes(manifest, Json));
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
        // Every planned intent is a committed intake, so planned and observed per-bucket counts are identical.
        Assert.Equal(plan.Payments.Length, accepted.Sum(g => g.Count()));
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
            using (var stream = new MemoryStream())
            {
                var header = ObservationEncoding.Begin(V4BankTargets.Schema).WriteCount(actual.Length).ToArray();
                stream.Write(header);
                Span<byte> size = stackalloc byte[4];
                foreach (var target in actual)
                {
                    var bytes = target.Encode();
                    BinaryPrimitives.WriteInt32BigEndian(size, bytes.Length);
                    stream.Write(size);
                    stream.Write(bytes);
                }
                Save(b.BankId + ".targets", stream.ToArray());
            }
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
            Schema = episode.TruthSchema, DefaultAccountBucket = "Normal", Events = episode.Events, episode.Rhythms,
        }, TruthJson));
        var facts = result.History.Segments.SelectMany(s => s.Facts).ToArray();
        using var process = Process.GetCurrentProcess();
        return new
        {
            episode.Root, episode.BehaviorSha256, plan.Opening.Manifest.ContentHash, Execution = plan.Opening.RunId.Value, Seals = seals,
            Streams = streams, Accounts = accounts, Banks = banks,
            Attempted = plan.Payments.Length, Accepted = accepted.Sum(g => g.Count()), FinanceFacts = facts.Length,
            PostedSettlements = projection.Banks.Sum(b => b.SettledTransfers) / 2,
            SettlementFacts = facts.Count(f => f.Input.Kind == ObservedFactKind.Settlement),
            Held = projection.Banks.Sum(b => b.Held), ReservedMinor = projection.Banks.Sum(b => b.Reserved),
            LearnedHolds = facts.Count(f => f.Input.Kind == ObservedFactKind.Screening
                && f.Input.PayloadSchema == V4ModelScreeningEvidence.Schema && V4ModelScreeningEvidence.Decode(f.Input).Admitted),
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

public sealed class R7IsolatedDrainPostgresTests
{
    [Fact]
    [Trait("Category", "R7Longitudinal")]
    public async Task Execute_G5_drain_pilot_on_isolated_PostgreSQL()
    {
        var path = Environment.GetEnvironmentVariable("REVENANT_R7_LONGITUDINAL_OUTPUT")
            ?? throw new InvalidOperationException("Set the longitudinal evidence directory.");
        // This entry point uses a caller-created disposable PostgreSQL instance without Testcontainers.
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(90));
        await R7LongitudinalPostgresTests.ExecutePilot(V4LongitudinalPlannerG5.Roots().ToArray(), "pilot.json", V4LongitudinalPlannerG5.Version,
            path, new(Required("R7_PG_WORKER"), Required("R7_PG_LEDGER"), "", "", ""), timeout.Token);

        static string Required(string key) => Environment.GetEnvironmentVariable(key)
            ?? throw new InvalidOperationException($"Set {key} for the isolated development database.");
    }

    [Fact]
    [Trait("Category", "R7Longitudinal")]
    public async Task Execute_one_G5_residual_training_history_on_isolated_PostgreSQL()
    {
        string Require(string key) => Environment.GetEnvironmentVariable(key) ?? throw new InvalidOperationException($"Set {key}.");
        var output = Require("REVENANT_R7_LONGITUDINAL_OUTPUT");
        Assert.False(File.Exists(Path.Combine(output, "residual-train.json")), "Reuse the committed training export.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(90));
        await R7LongitudinalPostgresTests.ExecutePilot([V4LongitudinalPlannerG5.ResidualTrainingRoot()], "residual-train.json",
            V4LongitudinalPlannerG5.Version, output, new(Require("R7_PG_WORKER"), Require("R7_PG_LEDGER"), "", "", ""),
            timeout.Token, residualTraining: true);
    }
}
