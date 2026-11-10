using System.Net;
using System.Net.Sockets;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Marrow.Infrastructure.Messaging;
using Marrow.Kernel.Messaging;
using Microsoft.Extensions.Logging;
using Npgsql;
using StackExchange.Redis;
using Testcontainers.PostgreSql;
using Xunit;
namespace Integration.Tests.Support;
/// <summary>
/// Real PostgreSQL 18.6 and Redis 7.4 containers (the Compose images). Redis runs with the Compose transport
/// settings (password, append-only file fsynced on every write) on a fixed host port so it can be stopped and
/// restarted mid-test without the clients' endpoint changing.
/// </summary>
public sealed class MessagingServices : IAsyncLifetime
{
    public const string RedisImage = "redis:7.4-alpine";
    private const string RedisPassword = "redis-test-only-password";
    private readonly int _redisPort = FreePort();
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder(IdentityDatabase.Image).Build();
    private readonly IContainer _redis;
    public MessagingServices()
    {
        _redis = new ContainerBuilder(RedisImage)
            .WithPortBinding(_redisPort, 6379)
            .WithCommand("redis-server", "--requirepass", RedisPassword, "--save", "", "--appendonly", "yes", "--appendfsync", "always")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Ready to accept connections"))
            .Build();
    }
    public string PostgresConnectionString => _postgres.GetConnectionString();
    /// <summary>Same timeouts a host would configure: never abort on connect failure, bounded command waits.</summary>
    public string RedisConnectionString =>
        $"127.0.0.1:{_redisPort},password={RedisPassword},abortConnect=false,connectTimeout=1000,syncTimeout=2000,asyncTimeout=2000,connectRetry=1";
    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());
    }
    public async Task DisposeAsync()
    {
        await _redis.DisposeAsync();
        await _postgres.DisposeAsync();
    }
    public Task StopRedisAsync() => _redis.StopAsync();
    public Task StartRedisAsync() => _redis.StartAsync();

    /// <summary>
    /// Crashes Redis with SIGKILL (<c>docker kill</c>): no graceful shutdown, no final AOF flush or rewrite. What survives
    /// is exactly what Redis had written to its append-only file before replying. (A process kill does not drop the OS
    /// page cache, so this proves write-before-acknowledge, not fsync against power loss.)
    /// </summary>
    public async Task KillRedisAsync()
    {
        var start = new System.Diagnostics.ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("kill");
        start.ArgumentList.Add(_redis.Id);
        using var process = System.Diagnostics.Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("docker kill failed: " + await process.StandardError.ReadToEndAsync());
        }
    }

    /// <summary>Runs redis-cli inside the Redis container (admin commands such as ACL).</summary>
    public async Task<string> RedisCliAsync(params string[] args)
    {
        var result = await _redis.ExecAsync(["redis-cli", "--no-auth-warning", "-a", RedisPassword, .. args]);
        return result.ExitCode == 0 ? result.Stdout.Trim() : throw new InvalidOperationException("redis-cli failed: " + result.Stderr);
    }
    /// <summary>A separate "host": its own Postgres pool and its own Redis multiplexer.</summary>
    public async Task<TestHost> NewHostAsync(LogCapture logs)
    {
        var redis = await ConnectionMultiplexer.ConnectAsync(RedisConnectionString);
        var db = NpgsqlDataSource.Create(PostgresConnectionString);
        var factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        return new TestHost(db, redis, new MessagingLog(factory.CreateLogger<MessagingLog>()), factory);
    }
    /// <summary>Creates a test-owned module schema with a business table, an outbox and an inbox (the documented shape).</summary>
    public async Task<MessagingTables> CreateModuleAsync(string prefix)
    {
        var tables = new MessagingTables($"{prefix}_{Guid.NewGuid():N}"[..30]);
        var s = tables.Schema;
        await using var db = NpgsqlDataSource.Create(PostgresConnectionString);
        await using var command = db.CreateCommand(
            $"""
            CREATE SCHEMA {s};
            CREATE TABLE {s}.outbox (
              seq bigint GENERATED ALWAYS AS IDENTITY UNIQUE,
              event_id uuid PRIMARY KEY,
              event_type text NOT NULL, event_version integer NOT NULL CHECK (event_version > 0),
              aggregate_id text NOT NULL, occurred_utc timestamptz NOT NULL,
              payload text NOT NULL, correlation_id text NOT NULL,
              status text NOT NULL DEFAULT 'pending' CHECK (status IN ('pending','published','poisoned')),
              attempts integer NOT NULL DEFAULT 0, next_attempt_at timestamptz NOT NULL DEFAULT now(),
              lease_token uuid NULL, lease_until timestamptz NULL, last_error text NULL,
              enqueued_at timestamptz NOT NULL DEFAULT now(), published_at timestamptz NULL, stream_entry_id text NULL,
              poisoned_at timestamptz NULL);
            CREATE INDEX ON {s}.outbox (next_attempt_at, seq) WHERE status = 'pending';
            CREATE TABLE {s}.inbox (
              consumer_id text NOT NULL, event_id uuid NOT NULL, received_utc timestamptz NOT NULL DEFAULT now(),
              PRIMARY KEY (consumer_id, event_id));
            -- Test-owned business tables: an order (producer side) and a deliberately non-idempotent effect (consumer side).
            CREATE TABLE {s}.orders (order_id uuid PRIMARY KEY, amount bigint NOT NULL);
            CREATE TABLE {s}.effects (effect_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, event_id uuid NOT NULL, amount bigint NOT NULL);
            """);
        await command.ExecuteNonQueryAsync();
        return tables;
    }
    public async Task<long> CountAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var db = NpgsqlDataSource.Create(PostgresConnectionString);
        await using var command = db.CreateCommand(sql);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
    public async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var db = NpgsqlDataSource.Create(PostgresConnectionString);
        await using var command = db.CreateCommand(sql);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        return (T)(await command.ExecuteScalarAsync())!;
    }
    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
public sealed class TestHost(NpgsqlDataSource db, ConnectionMultiplexer redis, MessagingLog log, ILoggerFactory loggers) : IAsyncDisposable
{
    public NpgsqlDataSource Db { get; } = db;
    public ConnectionMultiplexer Redis { get; } = redis;
    public MessagingLog Log { get; } = log;
    /// <summary>
    /// The atomic business+outbox unit of work: the order row and its event commit (or roll back) in one transaction.
    /// </summary>
    public async Task<OutboxMessage> PlaceOrderAsync(MessagingTables module, long amount, bool commit = true, Guid? orderId = null, string? note = null)
    {
        var id = orderId ?? Guid.NewGuid();
        var message = new OutboxMessage(Guid.NewGuid(), "test.order_placed", 1, id.ToString("D"), DateTimeOffset.UtcNow,
            $$"""{"orderId":"{{id:D}}","amount":{{amount}},"note":"{{note ?? ""}}"}""", $"corr-{Guid.NewGuid():N}");
        await using var connection = await Db.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var insert = new NpgsqlCommand($"INSERT INTO {module.Schema}.orders (order_id, amount) VALUES (@id, @amount)", connection, transaction))
        {
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("amount", amount);
            await insert.ExecuteNonQueryAsync();
        }
        await new PostgresOutboxWriter(transaction, module).EnqueueAsync(message, CancellationToken.None);
        if (commit)
        {
            await transaction.CommitAsync();
        }
        else
        {
            await transaction.RollbackAsync();
        }
        return message;
    }
    /// <summary>A non-idempotent consumer effect: every call inserts a new row, so a second application would be visible.</summary>
    public static InboxEffect RecordEffect(MessagingTables consumerModule) => async (message, transaction, ct) =>
    {
        var amount = System.Text.Json.JsonDocument.Parse(message.Payload).RootElement.GetProperty("amount").GetInt64();
        await using var command = new NpgsqlCommand($"INSERT INTO {consumerModule.Schema}.effects (event_id, amount) VALUES (@id, @amount)",
            transaction.Connection, transaction);
        command.Parameters.AddWithValue("id", message.EventId);
        command.Parameters.AddWithValue("amount", amount);
        await command.ExecuteNonQueryAsync(ct);
    };
    public async ValueTask DisposeAsync()
    {
        await Redis.DisposeAsync();
        await Db.DisposeAsync();
        loggers.Dispose();
    }
}
[CollectionDefinition(Name)]
public sealed class MessagingCollection : ICollectionFixture<MessagingServices>
{
    public const string Name = "messaging-postgres-redis";
}
