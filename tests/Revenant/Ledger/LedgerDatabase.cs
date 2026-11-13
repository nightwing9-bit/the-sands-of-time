using Npgsql;
using Revenant.Contracts;
using Revenant.Scenario;
using Testcontainers.PostgreSql;
using Xunit;

namespace Revenant.Tests.Ledger;

/// <summary>
/// A real PostgreSQL container (the Compose image) migrated by <c>scripts/db/migrate.sh</c>. Tests write through two
/// LOGIN roles that are only members of the NOLOGIN runtime roles created by <c>revenant/0001_ledger_core.sql</c>:
/// <see cref="Writer"/> (revenant_ledger_writer) and <see cref="Replay"/> (revenant_replay_reader). The superuser
/// <see cref="Owner"/> connection stands for the migration owner.
/// </summary>
public sealed class LedgerDatabase : IAsyncLifetime
{
    private const string Image = "postgres:18.6-alpine3.24";
    private const string WriterUser = "revenant_writer_test";
    private const string WriterPassword = "revenant-writer-test-only";
    private const string ReplayUser = "revenant_replay_test";
    private const string ReplayPassword = "revenant-replay-test-only";

    private readonly string _databaseName = $"revenant_{Guid.NewGuid():N}";
    private readonly PostgreSqlContainer _container;

    public LedgerDatabase()
    {
        _container = new PostgreSqlBuilder(Image)
            .WithDatabase(_databaseName)
            .WithResourceMapping(new DirectoryInfo(Path.Combine(RepositoryRoot(), "scripts", "db")), "/sands/db/")
            .Build();
    }

    private NpgsqlDataSource? _owner;
    private NpgsqlDataSource? _writer;
    private NpgsqlDataSource? _replay;

    public NpgsqlDataSource Owner => _owner ?? throw new InvalidOperationException("Database not started.");
    public NpgsqlDataSource Writer => _writer ?? throw new InvalidOperationException("Database not started.");
    public NpgsqlDataSource Replay => _replay ?? throw new InvalidOperationException("Database not started.");
    public string MigrateOutput { get; private set; } = "";

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        var result = await _container.ExecAsync([
            "env", "PGHOST=localhost", "PGPORT=5432", "PGUSER=postgres", "PGPASSWORD=postgres",
            $"PGDATABASE={_databaseName}", "MIGRATE_CONNECT_ATTEMPTS=5", "sh", "/sands/db/migrate.sh"]);
        MigrateOutput = result.Stdout + result.Stderr;
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"migrate.sh exited {result.ExitCode}:\n{MigrateOutput}");
        }

        var owner = new NpgsqlConnectionStringBuilder(_container.GetConnectionString());
        _owner = NpgsqlDataSource.Create(owner.ConnectionString);
        await using (var cmd = _owner.CreateCommand(
            $"CREATE ROLE {WriterUser} LOGIN PASSWORD '{WriterPassword}' IN ROLE revenant_ledger_writer;" +
            $"CREATE ROLE {ReplayUser} LOGIN PASSWORD '{ReplayPassword}' IN ROLE revenant_replay_reader;"))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        _writer = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(owner.ConnectionString)
        {
            Username = WriterUser,
            Password = WriterPassword,
            MaxPoolSize = 40,
        }.ConnectionString);
        _replay = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(owner.ConnectionString)
        {
            Username = ReplayUser,
            Password = ReplayPassword,
        }.ConnectionString);
    }

    public async Task DisposeAsync()
    {
        foreach (var source in new[] { _writer, _replay, _owner })
        {
            if (source is not null)
            {
                await source.DisposeAsync();
            }
        }

        await _container.DisposeAsync();
    }

    /// <summary>A separate writer pool (same LOGIN, member of revenant_ledger_writer only), e.g. one connection for A→B→A tests.</summary>
    public NpgsqlDataSource CreateWriterPool(int maxPoolSize) =>
        NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Username = WriterUser,
            Password = WriterPassword,
            MaxPoolSize = maxPoolSize,
        }.ConnectionString);

    /// <summary>Connection strings of the deployed runtime logins that <c>migrate.sh</c> provisions (task 7.5 login mappings).</summary>
    public sealed record DeployedLogins(string Worker, string WorkerLedger, string ApiLedger, string RealtimeLedger, string MigrateOutput);

    private DeployedLogins? _deployed;

    /// <summary>
    /// Re-runs <c>migrate.sh</c> exactly as the Compose migrate job does, with the runtime-login password variables set, so the
    /// executor itself provisions <c>sands_worker</c>, <c>sands_worker_ledger</c>, <c>sands_api_ledger</c> and
    /// <c>sands_realtime_ledger</c> with their role memberships. Test-only passwords (letters and digits, at least 24).
    /// </summary>
    public async Task<DeployedLogins> ProvisionDeployedLoginsAsync()
    {
        if (_deployed is not null)
        {
            return _deployed;
        }
        const string worker = "workerTestOnlyPassword000001", workerLedger = "workerLedgerTestOnlyPassword01",
            apiLedger = "apiLedgerTestOnlyPassword000001", realtimeLedger = "realtimeLedgerTestOnlyPassword1";
        var result = await _container.ExecAsync([
            "env", "PGHOST=localhost", "PGPORT=5432", "PGUSER=postgres", "PGPASSWORD=postgres", $"PGDATABASE={_databaseName}",
            "MIGRATE_CONNECT_ATTEMPTS=5", $"WORKER_DB_PASSWORD={worker}", $"WORKER_LEDGER_DB_PASSWORD={workerLedger}",
            $"API_LEDGER_DB_PASSWORD={apiLedger}", $"REALTIME_LEDGER_DB_PASSWORD={realtimeLedger}", "sh", "/sands/db/migrate.sh"]);
        var output = result.Stdout + result.Stderr;
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"migrate.sh (logins) exited {result.ExitCode}:\n{output}");
        }
        string Login(string user, string password) => new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Username = user,
            Password = password,
            MaxPoolSize = 30,
        }.ConnectionString;
        return _deployed = new DeployedLogins(
            Login("sands_worker", worker), Login("sands_worker_ledger", workerLedger), Login("sands_api_ledger", apiLedger),
            Login("sands_realtime_ledger", realtimeLedger), output);
    }

    internal const string V4UpgradeDatabase = "revenant_v4_upgrade";

    /// <summary>Prepare an independent database at the original migration boundary inside the isolated fixture container.</summary>
    internal async Task<NpgsqlDataSource> CreatePreV4DatabaseAsync()
    {
        await using (var create = Owner.CreateCommand($"CREATE DATABASE {V4UpgradeDatabase}"))
        {
            await create.ExecuteNonQueryAsync();
        }
        var copy = await _container.ExecAsync(["sh", "-ec",
            "cp -R /sands/db/migrations /tmp/pre-v4-migrations && rm /tmp/pre-v4-migrations/revenant/0009_v4_causal_scheduler.sql /tmp/pre-v4-migrations/revenant/0010_v4_branch_execution.sql /tmp/pre-v4-migrations/revenant/0011_v4_observation_projections.sql /tmp/pre-v4-migrations/revenant/0012_v4_model_screening.sql /tmp/pre-v4-migrations/revenant/0013_v4_advisory_evidence.sql"]);
        if (copy.ExitCode != 0)
        {
            throw new InvalidOperationException(copy.Stderr);
        }

        await MigrateV4UpgradeDatabaseAsync("/tmp/pre-v4-migrations");
        return NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = V4UpgradeDatabase,
            Username = WriterUser,
            Password = WriterPassword,
        }.ConnectionString);
    }

    internal async Task<string> MigrateToR2Async()
    {
        const string directory = "/tmp/pre-r3-migrations";
        var copy = await _container.ExecAsync(["sh", "-ec",
            $"cp -R /sands/db/migrations {directory} && rm {directory}/revenant/0010_v4_branch_execution.sql {directory}/revenant/0011_v4_observation_projections.sql {directory}/revenant/0012_v4_model_screening.sql {directory}/revenant/0013_v4_advisory_evidence.sql"]);
        if (copy.ExitCode != 0)
        {
            throw new InvalidOperationException(copy.Stderr);
        }
        return await MigrateV4UpgradeDatabaseAsync(directory);
    }

    internal async Task<string> MigrateV4UpgradeDatabaseAsync(string migrations = "/sands/db/migrations")
    {
        var result = await _container.ExecAsync([
            "env", "PGHOST=localhost", "PGPORT=5432", "PGUSER=postgres", "PGPASSWORD=postgres", $"PGDATABASE={V4UpgradeDatabase}",
            $"MIGRATIONS_DIR={migrations}", "MIGRATE_CONNECT_ATTEMPTS=5", "sh", "/sands/db/migrate.sh"]);
        var output = result.Stdout + result.Stderr;
        return result.ExitCode == 0 ? output : throw new InvalidOperationException(output);
    }

    /// <summary>Re-run the migration executor (idempotence check).</summary>
    public async Task<(long ExitCode, string Output)> MigrateAgainAsync()
    {
        var result = await _container.ExecAsync([
            "env", "PGHOST=localhost", "PGPORT=5432", "PGUSER=postgres", "PGPASSWORD=postgres",
            $"PGDATABASE={_databaseName}", "MIGRATE_CONNECT_ATTEMPTS=5", "sh", "/sands/db/migrate.sh"]);
        return (result.ExitCode ?? -1, result.Stdout + result.Stderr);
    }

    /// <summary>
    /// Inserts the six-bank Friday Ring opening state for one run as the writer: distinct rows per
    /// <see cref="ScenarioRunId"/>, equal logical bank/account ids and opening balances for equal seeds.
    /// </summary>
    public async Task<SeededRun> SeedRunAsync(string tenant, ScenarioRunId? runId = null, long seed = 42)
    {
        var run = runId ?? new ScenarioRunId(Guid.NewGuid());
        var manifest = FridayRingScenario.Create(seed, ScenarioSchemas.FridayRingVersion, run).Value.Manifest;
        await using var conn = await Writer.OpenConnectionAsync();
        await using var tx = await Ledger.BeginTenantAsync(conn, tenant);
        var banks = new Dictionary<string, Guid>();
        foreach (var bank in manifest.Banks)
        {
            banks[bank.BankId] = await Ledger.ScalarAsync<Guid>(conn,
                "INSERT INTO revenant.bank (tenant_id, scenario_run_id, local_bank_id, name) VALUES (@t, @r, @b, @n) RETURNING id",
                ("t", tenant), ("r", run.Value), ("b", bank.BankId), ("n", bank.Name));
        }

        var accounts = new Dictionary<string, Guid>();
        var opening = new Dictionary<string, long>();
        foreach (var account in manifest.Accounts)
        {
            accounts[account.AccountId] = await Ledger.ScalarAsync<Guid>(conn,
                "INSERT INTO revenant.account (tenant_id, scenario_run_id, bank_id, local_account_id, currency, opening_balance_minor) " +
                "VALUES (@t, @r, @b, @a, @c, @o) RETURNING id",
                ("t", tenant), ("r", run.Value), ("b", banks[account.BankId]), ("a", account.AccountId),
                ("c", account.OpeningBalance.Currency), ("o", account.OpeningBalance.MinorUnits));
            opening[account.AccountId] = account.OpeningBalance.MinorUnits;
        }

        await tx.CommitAsync();
        return new SeededRun(tenant, run.Value, banks, accounts, opening);
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

public sealed record SeededRun(
    string Tenant,
    Guid RunId,
    IReadOnlyDictionary<string, Guid> Banks,
    IReadOnlyDictionary<string, Guid> Accounts,
    IReadOnlyDictionary<string, long> Opening)
{
    public Guid Account(string localId) => Accounts[localId];

    public long TotalOpening => Opening.Values.Sum();
}

/// <summary>Direct SQL helpers. They deliberately perform no balance checks: the database must.</summary>
public static class Ledger
{
    public static async Task<NpgsqlTransaction> BeginTenantAsync(NpgsqlConnection conn, string? tenant)
    {
        var tx = await conn.BeginTransactionAsync();
        if (tenant is not null)
        {
            await ExecAsync(conn, "SELECT set_config('revenant.tenant_id', @t, true)", ("t", tenant));
        }

        return tx;
    }

    public static async Task<int> ExecAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] args)
    {
        await using var cmd = Command(conn, sql, args);
        return await cmd.ExecuteNonQueryAsync();
    }

    public static async Task<T> ScalarAsync<T>(NpgsqlConnection conn, string sql, params (string Name, object Value)[] args)
    {
        await using var cmd = Command(conn, sql, args);
        var value = await cmd.ExecuteScalarAsync();
        return (T)value!;
    }

    public static Task<Guid> InsertPaymentAsync(
        NpgsqlConnection conn, SeededRun run, string intentId, Guid source, Guid dest, long amount, string currency = "USD") =>
        ScalarAsync<Guid>(conn,
            "INSERT INTO revenant.payment (tenant_id, scenario_run_id, intent_id, source_account_id, dest_account_id, currency, amount_minor, status) " +
            "VALUES (@t, @r, @i, @s, @d, @c, @a, 'Initiated') RETURNING id",
            ("t", run.Tenant), ("r", run.RunId), ("i", intentId), ("s", source), ("d", dest), ("c", currency), ("a", amount));

    public static async Task AdvanceAsync(NpgsqlConnection conn, Guid payment, params string[] statuses)
    {
        foreach (var status in statuses)
        {
            await ExecAsync(conn, "UPDATE revenant.payment SET status = @s, version = version + 1 WHERE id = @p",
                ("s", status), ("p", payment));
        }
    }

    public static Task<Guid> InsertGroupAsync(
        NpgsqlConnection conn, SeededRun run, Guid payment, string purpose, Guid? compensates = null, string currency = "USD") =>
        ScalarAsync<Guid>(conn,
            "INSERT INTO revenant.ledger_group (tenant_id, scenario_run_id, payment_id, currency, purpose, compensates_group_id) " +
            "VALUES (@t, @r, @p, @c, @u, @g) RETURNING id",
            ("t", run.Tenant), ("r", run.RunId), ("p", payment), ("c", currency), ("u", purpose),
            ("g", compensates is { } g ? g : DBNull.Value));

    public static Task<int> InsertLegAsync(
        NpgsqlConnection conn, SeededRun run, Guid group, Guid account, string direction, long amount, string currency = "USD") =>
        ExecAsync(conn,
            "INSERT INTO revenant.ledger_entry (group_id, tenant_id, scenario_run_id, account_id, currency, direction, amount_minor) " +
            "VALUES (@g, @t, @r, @a, @c, @d, @m)",
            ("g", group), ("t", run.Tenant), ("r", run.RunId), ("a", account), ("c", currency), ("d", direction), ("m", amount));

    /// <summary>Insert, screen, clear and settle a payment with a correct two-leg group in one transaction.</summary>
    public static async Task<(Guid Payment, Guid Group)> SettleAsync(
        NpgsqlDataSource source, SeededRun run, string intentId, Guid from, Guid to, long amount)
    {
        await using var conn = await source.OpenConnectionAsync();
        await using var tx = await BeginTenantAsync(conn, run.Tenant);
        var payment = await InsertPaymentAsync(conn, run, intentId, from, to, amount);
        await AdvanceAsync(conn, payment, "Screened", "Cleared", "Settled");
        var group = await InsertGroupAsync(conn, run, payment, "Settlement");
        await InsertLegAsync(conn, run, group, from, "Credit", amount);
        await InsertLegAsync(conn, run, group, to, "Debit", amount);
        await tx.CommitAsync();
        return (payment, group);
    }

    public static async Task<Dictionary<Guid, long>> PostedBalancesAsync(NpgsqlDataSource source, SeededRun run)
    {
        await using var conn = await source.OpenConnectionAsync();
        await using var tx = await BeginTenantAsync(conn, run.Tenant);
        await using var cmd = Command(conn,
            "SELECT account_id, posted_minor FROM revenant.account_posted_balance WHERE scenario_run_id = @r", ("r", run.RunId));
        var balances = new Dictionary<Guid, long>();
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                balances[reader.GetGuid(0)] = reader.GetInt64(1);
            }
        }

        await tx.CommitAsync();
        return balances;
    }

    private static NpgsqlCommand Command(NpgsqlConnection conn, string sql, params (string Name, object Value)[] args)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        return cmd;
    }
}
