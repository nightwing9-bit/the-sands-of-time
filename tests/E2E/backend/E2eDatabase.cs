using System.Security.Cryptography;
using Npgsql;
using Testcontainers.PostgreSql;
namespace Sands.E2E.Backend;
/// <summary>
/// Real PostgreSQL 18 (the Compose image) migrated by the Spec-01 one-shot executor <c>scripts/db/migrate.sh</c>, run inside the
/// container exactly as <c>tests/Integration</c> does. The API under test connects with one dedicated, non-superuser login that is a
/// member of the identity, Timeline and (task 6.5) Ghost Lap runtime roles only; the owner connection exists solely for fixture inspection and for the
/// storage-tamper scenario, and is never registered as the API's data source.
/// </summary>
public sealed class E2eDatabase : IAsyncDisposable
{
    public const string Image = "postgres:18.6-alpine3.24";
    public const string ApiLogin = "sands_e2e_api";
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(Image)
        .WithResourceMapping(new DirectoryInfo(Path.Combine(RepositoryRoot(), "scripts", "db")), "/sands/db/")
        .Build();
    /// <summary>Superuser connection to the container. Fixture inspection and tamper only.</summary>
    public NpgsqlDataSource Owner { get; private set; } = null!;
    /// <summary>Connection string of the API login (runtime roles only). Never printed.</summary>
    public string ApiConnectionString { get; private set; } = "";
    internal string R8Ledger { get; private set; } = "";
    internal string R8Replay { get; private set; } = "";
    public string MigrationOutput { get; private set; } = "";
    public static async Task<E2eDatabase> StartAsync(CancellationToken ct)
    {
        var db = new E2eDatabase();
        try
        {
            await db._container.StartAsync(ct);
            await db.MigrateAndProvisionAsync(ct);
            return db;
        }
        catch
        {
            await db.DisposeAsync();
            throw;
        }
    }
    private async Task MigrateAndProvisionAsync(CancellationToken ct)
    {
        var result = await _container.ExecAsync(
            ["env", "PGHOST=localhost", "PGPORT=5432", "PGUSER=postgres", "PGPASSWORD=postgres", "PGDATABASE=postgres",
             "MIGRATE_CONNECT_ATTEMPTS=5", "sh", "/sands/db/migrate.sh"], ct);
        MigrationOutput = result.Stdout + result.Stderr;
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"migrate.sh exited {result.ExitCode}:\n{MigrationOutput}");
        }
        var ownerBuilder = new NpgsqlConnectionStringBuilder(_container.GetConnectionString());
        Owner = NpgsqlDataSource.Create(ownerBuilder.ConnectionString);
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        await using (var command = Owner.CreateCommand(
            $"CREATE ROLE {ApiLogin} LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD '{password}'; " +
            $"GRANT marrow_identity_runtime, marrow_timeline_runtime, ghostlap_runtime TO {ApiLogin};"))
        {
            await command.ExecuteNonQueryAsync(ct);
        }
        if (Environment.GetEnvironmentVariable("E2E_R8") == "1")
        {
            var replayPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            await using var roles = Owner.CreateCommand($"""
                CREATE ROLE r8_e2e_ledger LOGIN PASSWORD '{password}' IN ROLE revenant_ledger_writer;
                CREATE ROLE sands_replay LOGIN PASSWORD '{replayPassword}' IN ROLE marrow_timeline_revenant_replay, revenant_replay_reader, revenant_counterfactual_worker;
                INSERT INTO revenant.counterfactual_worker_tenant VALUES ('sands_replay','r8-browser');
                """);
            await roles.ExecuteNonQueryAsync(ct);
            R8Ledger = new NpgsqlConnectionStringBuilder(ownerBuilder.ConnectionString) { Username = "r8_e2e_ledger", Password = password }.ConnectionString;
            R8Replay = new NpgsqlConnectionStringBuilder(ownerBuilder.ConnectionString) { Username = "sands_replay", Password = replayPassword }.ConnectionString;
        }
        ApiConnectionString = new NpgsqlConnectionStringBuilder(ownerBuilder.ConnectionString)
        {
            Username = ApiLogin,
            Password = password,
            MaxPoolSize = 40,
        }.ConnectionString;
    }
    public async ValueTask DisposeAsync()
    {
        if (Owner is not null)
        {
            await Owner.DisposeAsync();
        }
        await _container.DisposeAsync();
    }
    internal static string RepositoryRoot()
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
