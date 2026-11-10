using Marrow.Infrastructure.Timeline;
using Marrow.Timeline.Internal;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Marrow.Tests.Timeline.Persistence;

/// <summary>
/// A real PostgreSQL 18 container (the Compose image) migrated by the Spec-01 one-shot runner
/// <c>scripts/db/migrate.sh</c>, executed inside the container with its own psql. The store under test
/// connects as a LOGIN role that is only a member of <c>marrow_timeline_runtime</c>; the superuser
/// connection is used solely to inspect rows and to prove that even the owner cannot rewrite history.
/// </summary>
public sealed class TimelinePostgresDatabase : IAsyncLifetime
{
    public const string Image = "postgres:18.6-alpine3.24";
    private const string AppUser = "timeline_app";
    private const string AppPassword = "timeline-app-test-only";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(Image)
        .WithResourceMapping(new DirectoryInfo(Path.Combine(RepositoryRoot(), "scripts", "db")), "/sands/db/")
        .Build();

    private NpgsqlDataSource? _app;
    private NpgsqlDataSource? _owner;

    public string FirstMigrationOutput { get; private set; } = "";

    /// <summary>Connections as the least-privilege runtime login.</summary>
    public NpgsqlDataSource App => _app ?? throw new InvalidOperationException("Database not started.");

    /// <summary>Superuser (schema owner) connections, for inspection and owner-level immutability proofs.</summary>
    public NpgsqlDataSource Owner => _owner ?? throw new InvalidOperationException("Database not started.");

    internal ITimelineStore Store { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        FirstMigrationOutput = await RunMigratorAsync();
        Connect();
        await using var cmd = Owner.CreateCommand(
            $"CREATE ROLE {AppUser} LOGIN PASSWORD '{AppPassword}' IN ROLE marrow_timeline_runtime");
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        await DisconnectAsync();
        await _container.DisposeAsync();
    }

    /// <summary>Runs the Spec-01 migration executor; throws with its output when it exits nonzero.</summary>
    public async Task<string> RunMigratorAsync()
    {
        var result = await _container.ExecAsync([
            "env", "PGHOST=localhost", "PGPORT=5432", "PGUSER=postgres", "PGPASSWORD=postgres",
            "PGDATABASE=postgres", "MIGRATE_CONNECT_ATTEMPTS=5", "sh", "/sands/db/migrate.sh"]);
        var output = result.Stdout + result.Stderr;
        return result.ExitCode == 0
            ? output
            : throw new InvalidOperationException($"migrate.sh exited {result.ExitCode}:\n{output}");
    }

    /// <summary>Stops and restarts the PostgreSQL server process (same container filesystem), dropping all pooled connections.</summary>
    public async Task RestartAsync()
    {
        await DisconnectAsync();
        await _container.StopAsync();
        await _container.StartAsync();
        Connect();
    }

    /// <summary>
    /// A store over a brand-new connection pool as the runtime login, as a freshly started process would
    /// have. Disposed with the fixture or on <see cref="RestartAsync"/>.
    /// </summary>
    internal ITimelineStore NewColdStore()
    {
        var dataSource = NpgsqlDataSource.Create(_appConnectionString ?? throw new InvalidOperationException("Database not started."));
        _coldSources.Add(dataSource);
        return new PostgresTimelineStore(dataSource);
    }

    private readonly List<NpgsqlDataSource> _coldSources = [];
    private string? _appConnectionString;

    private void Connect()
    {
        var owner = new NpgsqlConnectionStringBuilder(_container.GetConnectionString());
        _owner = NpgsqlDataSource.Create(owner.ConnectionString);
        var app = new NpgsqlConnectionStringBuilder(owner.ConnectionString) { Username = AppUser, Password = AppPassword };
        _appConnectionString = app.ConnectionString;
        _app = NpgsqlDataSource.Create(app.ConnectionString);
        Store = new PostgresTimelineStore(_app);
    }

    private async Task DisconnectAsync()
    {
        foreach (var cold in _coldSources)
        {
            await cold.DisposeAsync();
        }

        _coldSources.Clear();

        if (_app is not null)
        {
            await _app.DisposeAsync();
            _app = null;
        }

        if (_owner is not null)
        {
            await _owner.DisposeAsync();
            _owner = null;
        }
    }

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
