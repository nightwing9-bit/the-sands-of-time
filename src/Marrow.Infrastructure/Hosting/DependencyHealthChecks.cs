using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using StackExchange.Redis;

namespace Marrow.Infrastructure.Hosting;

/// <summary>Opens a pooled connection and runs <c>SELECT 1</c>. Reports no connection details.</summary>
internal sealed class PostgresHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand("SELECT 1", connection);
            await command.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // The health-check timeout/shutdown is reported by the framework.
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL is unreachable.", ex);
        }
    }
}

/// <summary>PINGs Redis. Redis is a transport/cache, never the source of truth.</summary>
internal sealed class RedisHealthCheck(RedisConnectionProvider redis) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var multiplexer = await redis.GetAsync(cancellationToken);
            if (!multiplexer.IsConnected)
            {
                return HealthCheckResult.Unhealthy("Redis is not connected.");
            }

            await multiplexer.GetDatabase().PingAsync().WaitAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Redis is unreachable.", ex);
        }
    }
}

/// <summary>
/// Lazily creates the single process-wide Redis multiplexer so a host can start while Redis is down.
/// A failed creation is not cached; StackExchange.Redis reconnects in the background after an outage.
/// </summary>
internal sealed class RedisConnectionProvider(ConfigurationOptions options) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnectionMultiplexer? _multiplexer;

    public async Task<IConnectionMultiplexer> GetAsync(CancellationToken cancellationToken)
    {
        if (_multiplexer is not null)
        {
            return _multiplexer;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            _multiplexer ??= await ConnectionMultiplexer.ConnectAsync(options).WaitAsync(cancellationToken);
            return _multiplexer;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_multiplexer is not null)
        {
            await _multiplexer.DisposeAsync();
        }

        _gate.Dispose();
    }
}
