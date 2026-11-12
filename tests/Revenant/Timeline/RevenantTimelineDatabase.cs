using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Revenant.Tests.Timeline;

/// <summary>
/// A real PostgreSQL container (the Compose image) migrated by the Spec-01 runner <c>scripts/db/migrate.sh</c>.
/// The Timeline engine connects as a LOGIN role that is only a member of <c>marrow_timeline_runtime</c>; the
/// superuser connection is used solely to read rows for byte comparisons.
/// </summary>
public sealed class RevenantTimelineDatabase : IAsyncLifetime
{
    private const string Image = "postgres:18.6-alpine3.24";
    private const string AppUser = "revenant_timeline_app";
    private const string AppPassword = "revenant-timeline-test-only";

    private readonly string _databaseName = $"revenant_timeline_{Guid.NewGuid():N}";
    private readonly PostgreSqlContainer _container;

    public RevenantTimelineDatabase()
    {
        _container = new PostgreSqlBuilder(Image)
            .WithDatabase(_databaseName)
            .WithResourceMapping(new DirectoryInfo(Path.Combine(RepositoryRoot(), "scripts", "db")), "/sands/db/")
            .Build();
    }

    private NpgsqlDataSource? _owner;

    /// <summary>Superuser connections, for row inspection only.</summary>
    public NpgsqlDataSource Owner => _owner ?? throw new InvalidOperationException("Database not started.");

    /// <summary>Least-privilege runtime login connection string.</summary>
    public string AppConnectionString { get; private set; } = "";

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        var result = await _container.ExecAsync([
            "env", "PGHOST=localhost", "PGPORT=5432", "PGUSER=postgres", "PGPASSWORD=postgres",
            $"PGDATABASE={_databaseName}", "MIGRATE_CONNECT_ATTEMPTS=5", "sh", "/sands/db/migrate.sh"]);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"migrate.sh exited {result.ExitCode}:\n{result.Stdout}{result.Stderr}");
        }

        var owner = new NpgsqlConnectionStringBuilder(_container.GetConnectionString());
        _owner = NpgsqlDataSource.Create(owner.ConnectionString);
        await using (var cmd = _owner.CreateCommand(
            $"CREATE ROLE {AppUser} LOGIN PASSWORD '{AppPassword}' IN ROLE marrow_timeline_runtime"))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        AppConnectionString = new NpgsqlConnectionStringBuilder(owner.ConnectionString)
        {
            Username = AppUser,
            Password = AppPassword,
        }.ConnectionString;
    }

    public async Task DisposeAsync()
    {
        if (_owner is not null)
        {
            await _owner.DisposeAsync();
        }

        await _container.DisposeAsync();
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
