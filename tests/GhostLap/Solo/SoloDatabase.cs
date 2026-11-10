using System.Diagnostics;
using System.Security.Claims;
using GhostLap.Simulation;
using GhostLap.Solo;
using GhostLap.Tests.Simulation;
using GhostLap.Timeline;
using Marrow.Infrastructure.Identity;
using Marrow.Infrastructure.Identity.Policy;
using Marrow.Infrastructure.Timeline;
using Marrow.Kernel.Results;
using Marrow.Kernel.Security;
using Marrow.Timeline;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace GhostLap.Tests.Solo;

/// <summary>
/// A real PostgreSQL container (the Compose image) migrated by the Spec-01 runner <c>scripts/db/migrate.sh</c>, which
/// applies <c>marrow_timeline</c>, <c>marrow_identity</c> and the new <c>ghostlap</c> module. Hosts connect as a LOGIN role
/// that is only a member of <c>marrow_timeline_runtime</c> and <c>ghostlap_runtime</c>; the superuser connection is used
/// solely to inspect rows and to prove the database guards. During initialization it records one complete solo original
/// for <see cref="OwnerA"/> with the real recorder, keeping the live per-tick trace as the uninterrupted reference.
/// </summary>
public sealed class SoloDatabase : IAsyncLifetime
{
    private const string Image = "postgres:18.6-alpine3.24";
    private const string AppUser = "ghostlap_solo_app";
    private const string AppPassword = "ghostlap-solo-test-only";
    public const long Seed = 42;

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(Image)
        .WithResourceMapping(new DirectoryInfo(Path.Combine(RepositoryRoot(), "scripts", "db")), "/sands/db/")
        .Build();

    private NpgsqlDataSource? _superuser;

    /// <summary>Superuser connections, for row inspection and guard checks only.</summary>
    public NpgsqlDataSource Superuser => _superuser ?? throw new InvalidOperationException("Database not started.");

    public string AppConnectionString { get; private set; } = "";

    public IActorContext OwnerA { get; } = Guest();

    /// <summary>The completed original recorded during initialization, as returned by <see cref="SoloRecording.CompleteAsync"/>.</summary>
    public SoloOriginal Original { get; private set; } = null!;

    public RecordedRun Run { get; private set; } = null!;

    public TimeSpan RecordingElapsed { get; private set; }

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        var result = await _container.ExecAsync([
            "env", "PGHOST=localhost", "PGPORT=5432", "PGUSER=postgres", "PGPASSWORD=postgres",
            "PGDATABASE=postgres", "MIGRATE_CONNECT_ATTEMPTS=5", "sh", "/sands/db/migrate.sh"]);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"migrate.sh exited {result.ExitCode}:\n{result.Stdout}{result.Stderr}");
        }

        MigrationLog = result.Stdout + result.Stderr;
        var owner = new NpgsqlConnectionStringBuilder(_container.GetConnectionString());
        _superuser = NpgsqlDataSource.Create(owner.ConnectionString);
        await using (var cmd = _superuser.CreateCommand(
            $"CREATE ROLE {AppUser} LOGIN PASSWORD '{AppPassword}' IN ROLE marrow_timeline_runtime, ghostlap_runtime"))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        AppConnectionString = new NpgsqlConnectionStringBuilder(owner.ConnectionString)
        {
            Username = AppUser,
            Password = AppPassword,
        }.ConnectionString;

        await using var host = NewHost();
        var watch = Stopwatch.StartNew();
        var recording = Ok(await host.Originals.StartAsync(OwnerA, Seed, 2, "fixture-original", CancellationToken.None));
        Run = await RecordAsync(recording, GhostLap.Rules.GameRulesV1.RoundTicks);
        Original = Ok(await recording.CompleteAsync(CancellationToken.None));
        RecordingElapsed = watch.Elapsed;
    }

    public string MigrationLog { get; private set; } = "";

    public async Task DisposeAsync()
    {
        if (_superuser is not null)
        {
            await _superuser.DisposeAsync();
        }

        await _container.DisposeAsync();
    }

    /// <summary>
    /// A freshly composed host with its own connection pool, as a newly started (or refreshed) server process has.
    /// <paramref name="registrations"/> defaults to every Ghost Lap version this build implements.
    /// </summary>
    public SoloHost NewHost(
        IEnumerable<TimelineComponents<GameState, GameInput, GameContext>>? registrations = null, string? connectionString = null)
    {
        var cs = connectionString ?? AppConnectionString;
        var services = new ServiceCollection();
        services.AddSingleton(_ => NpgsqlDataSource.Create(cs));
        services.AddMarrowTimeline(registrations ?? GhostLapTimelineVersions.All, GhostLapTimelineVersions.CheckpointInterval);
        return new SoloHost(services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }));
    }

    /// <summary>
    /// Plays <paramref name="rec"/> to <paramref name="untilTick"/> with <see cref="CandleSeekingDriver"/>: every input goes
    /// through the recorder's admission and every tick (with or without inputs) is committed before the next one.
    /// </summary>
    public static async Task<RecordedRun> RecordAsync(SoloRecording rec, long untilTick)
    {
        var driver = new CandleSeekingDriver(rec.Context.PlayerCount);
        var trace = new List<GameState> { rec.State };
        var steps = new List<GameInput[]>();
        var receipts = new List<SoloTickReceipt>();
        while (rec.State.Tick < untilTick)
        {
            var inputs = driver.Inputs(rec.State, rec.Context);
            foreach (var g in inputs)
            {
                var admitted = rec.Submit(new SubmittedInput(g.Slot, g.Sequence, g.Tick, g.MoveX, g.MoveY, g.RequestBurst));
                Assert.True(admitted.IsSuccess, $"tick {g.Tick}: {admitted}");
                Assert.Equal(g, admitted.Value);
            }

            var receipt = Ok(await rec.CommitTickAsync(CancellationToken.None));
            receipts.Add(receipt);
            steps.Add(inputs);
            trace.Add(receipt.State);
        }

        return new RecordedRun(rec.Context, trace, steps, receipts);
    }

    public static IActorContext Guest() => Actor(IdentityClaimTypes.KindGuest, "guest:" + Guid.NewGuid().ToString("D"));

    public static IActorContext Staff() => Actor(IdentityClaimTypes.KindStaff, "staff:" + Guid.NewGuid().ToString("D"));

    /// <summary>
    /// The verified actor exactly as the Spec 03 authentication handlers produce it: <see cref="ClaimsActorContext.From"/>
    /// over an authenticated principal carrying the kind and subject claims.
    /// </summary>
    private static IActorContext Actor(string kind, string subject)
    {
        var identity = new ClaimsIdentity(
            [new Claim(IdentityClaimTypes.Kind, kind), new Claim(IdentityClaimTypes.Subject, subject)], "ghostlap-test");
        var actor = ClaimsActorContext.From(new ClaimsPrincipal(identity));
        Assert.True(actor.IsAuthenticated);
        return actor;
    }

    public static T Ok<T>(Result<T, SoloError> result) =>
        result.TryGetValue(out var value) ? value : throw new Xunit.Sdk.XunitException($"Expected success, got {result.Error}");

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "TheSandsOfTime.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Repository root (TheSandsOfTime.slnx) not found.");
    }
}

/// <summary>The live run: states per tick (index = tick, 0 = initial), admitted inputs per step (index = tick - 1) and receipts.</summary>
public sealed record RecordedRun(
    GameContext Context, IReadOnlyList<GameState> Trace, IReadOnlyList<GameInput[]> Steps, IReadOnlyList<SoloTickReceipt> Receipts);

/// <summary>One composed host: the shared engine, the Ghost Lap store and the solo handler with the real owner policy.</summary>
public sealed class SoloHost(ServiceProvider services) : IAsyncDisposable
{
    public ITimelineEngine<GameState, GameInput, GameContext> Engine { get; } =
        services.GetRequiredService<ITimelineEngine<GameState, GameInput, GameContext>>();

    public SoloOriginalStore Store => field ??= new SoloOriginalStore(services.GetRequiredService<NpgsqlDataSource>());

    public SoloOriginals Originals => field ??= new SoloOriginals(Engine, Store, new OwnerResourceAuthorizer(new SoloOwnershipLookup(Store)));

    public SoloBranches Branches => field ??= new SoloBranches(Engine, Store, new OwnerResourceAuthorizer(new SoloOwnershipLookup(Store)));

    public ValueTask DisposeAsync() => services.DisposeAsync();
}
