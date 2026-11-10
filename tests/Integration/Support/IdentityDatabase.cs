using System.Security.Cryptography;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Integration.Tests.Support;

/// <summary>
/// Real PostgreSQL 18 (the Compose image) migrated by the Spec-01 one-shot executor <c>scripts/db/migrate.sh</c>, run
/// inside the container against every module migration in the repository. The executor also provisions the per-host
/// runtime logins exactly as Compose does (<c>sands_api</c>, <c>sands_realtime</c>, <c>sands_worker</c>); the API and
/// Realtime hosts under test connect with those logins, never as the superuser. The superuser connection is used for
/// inspection and for proving what even the schema owner cannot do.
/// </summary>
public sealed class IdentityDatabase : IAsyncLifetime
{
    public const string Image = "postgres:18.6-alpine3.24";
    public const string ApiLogin = "sands_api";
    public const string RealtimeLogin = "sands_realtime";
    public const string WorkerLogin = "sands_worker";

    // Per-fixture random test passwords (letters/digits only, as the executor requires). Never committed.
    private readonly string _apiPassword = NewPassword();
    private readonly string _realtimePassword = NewPassword();
    private readonly string _workerPassword = NewPassword();

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(Image)
        .WithResourceMapping(new DirectoryInfo(Path.Combine(RepositoryRoot(), "scripts", "db")), "/sands/db/")
        .Build();

    public string FirstMigrationOutput { get; private set; } = "";
    public NpgsqlDataSource Owner { get; private set; } = null!;

    /// <summary>The API host's login (member of <c>marrow_identity_runtime</c> only).</summary>
    public string AppConnectionString { get; private set; } = "";

    /// <summary>The Realtime host's login (member of <c>marrow_identity_runtime</c> only).</summary>
    public string RealtimeConnectionString { get; private set; } = "";

    /// <summary>The Worker host's login (no schema role).</summary>
    public string WorkerConnectionString { get; private set; } = "";

    /// <summary>Every runtime password, so tests can assert none of them is ever printed.</summary>
    public IReadOnlyList<string> RuntimePasswords => [_apiPassword, _realtimePassword, _workerPassword];

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        FirstMigrationOutput = await RunMigratorAsync();
        var owner = new NpgsqlConnectionStringBuilder(_container.GetConnectionString());
        Owner = NpgsqlDataSource.Create(owner.ConnectionString);
        AppConnectionString = Login(owner, ApiLogin, _apiPassword);
        RealtimeConnectionString = Login(owner, RealtimeLogin, _realtimePassword);
        WorkerConnectionString = Login(owner, WorkerLogin, _workerPassword);
    }

    public async Task DisposeAsync()
    {
        await Owner.DisposeAsync();
        await _container.DisposeAsync();
    }

    /// <summary>Runs the executor with the same runtime-login variables Compose passes. Throws on a nonzero exit.</summary>
    public async Task<string> RunMigratorAsync()
    {
        var (exitCode, output) = await TryRunMigratorAsync(
            $"API_DB_PASSWORD={_apiPassword}", $"REALTIME_DB_PASSWORD={_realtimePassword}", $"WORKER_DB_PASSWORD={_workerPassword}");
        return exitCode == 0 ? output : throw new InvalidOperationException($"migrate.sh exited {exitCode}:\n{output}");
    }

    /// <summary>Runs the executor with only the given extra environment entries (NAME=value).</summary>
    public async Task<(long ExitCode, string Output)> TryRunMigratorAsync(params string[] extraEnvironment)
    {
        var result = await _container.ExecAsync([
            "env", "PGHOST=localhost", "PGPORT=5432", "PGUSER=postgres", "PGPASSWORD=postgres",
            "PGDATABASE=postgres", "MIGRATE_CONNECT_ATTEMPTS=5", .. extraEnvironment, "sh", "/sands/db/migrate.sh"]);
        return (result.ExitCode ?? -1, result.Stdout + result.Stderr);
    }

    public async Task<T?> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = Owner.CreateCommand(sql);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? default : (T)result;
    }

    private static string Login(NpgsqlConnectionStringBuilder owner, string user, string password) =>
        new NpgsqlConnectionStringBuilder(owner.ConnectionString) { Username = user, Password = password, MaxPoolSize = 40 }.ConnectionString;

    private static string NewPassword() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

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

[CollectionDefinition(Name)]
public sealed class IdentityCollection : ICollectionFixture<IdentityDatabase>
{
    public const string Name = "identity-postgres";
}
