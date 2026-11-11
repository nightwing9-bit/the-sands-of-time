using Marrow.Infrastructure.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Marrow.Infrastructure.Messaging;

/// <summary>Owns the relay's dedicated connection pool so the container disposes it.</summary>
internal sealed class OutboxRelayDataSource(NpgsqlDataSource dataSource) : IAsyncDisposable
{
    public NpgsqlDataSource DataSource { get; } = dataSource;
    public ValueTask DisposeAsync() => DataSource.DisposeAsync();
}

/// <summary>
/// Runs one module's <see cref="OutboxRelay"/> in a host (Spec 07 task 7.5 wires the Revenant outbox into the Worker). It waits for
/// Redis through the host's lazy connection, then relays until shutdown; outages are handled by the relay itself (rows stay pending).
/// </summary>
internal sealed class OutboxRelayHostedService(
    OutboxRelayDataSource db, RedisConnectionProvider redis, MessagingTables tables, OutboxRelayOptions options, TimeSpan idle, MessagingLog log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        StackExchange.Redis.IConnectionMultiplexer? multiplexer = null;
        while (multiplexer is null && !stoppingToken.IsCancellationRequested)
        {
            try
            {
                multiplexer = await redis.GetAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.TransportUnavailable(Guid.Empty, "-", MessagingErrors.Sanitize(ex), (long)idle.TotalMilliseconds);
                await Task.Delay(idle, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
        if (multiplexer is null)
        {
            return;
        }
        await new OutboxRelay(db.DataSource, multiplexer, tables, options, log).RunAsync(idle, stoppingToken);
    }
}

public static class OutboxRelayServiceCollectionExtensions
{
    /// <summary>
    /// Registers a background relay for <paramref name="tables"/> when <c>{section}:StreamKey</c> and
    /// <c>ConnectionStrings:{connectionStringName}</c> are both configured. The connection's login must hold the module's relay role
    /// (claim/confirm columns of its outbox only). Requires <see cref="MarrowHostingExtensions.AddMarrowDependencies"/> (Redis).
    /// Optional settings: <c>BatchSize</c>, <c>IdleDelayMs</c>.
    /// </summary>
    public static IServiceCollection AddMarrowOutboxRelayService(
        this IServiceCollection services, IConfiguration configuration, string section, string connectionStringName, MessagingTables tables)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(tables);
        var streamKey = configuration[$"{section}:StreamKey"];
        var connectionString = configuration.GetConnectionString(connectionStringName);
        if (string.IsNullOrWhiteSpace(streamKey) || string.IsNullOrWhiteSpace(connectionString))
        {
            return services;
        }
        var batch = int.TryParse(configuration[$"{section}:BatchSize"], out var b) ? b : 50;
        var idleMs = int.TryParse(configuration[$"{section}:IdleDelayMs"], out var i) ? i : 500;
        var options = new OutboxRelayOptions { StreamKey = streamKey, BatchSize = batch };
        options.Validate();
        NpgsqlDataSource dataSource;
        try
        {
            dataSource = new NpgsqlDataSourceBuilder(connectionString).Build();
        }
        catch (Exception)
        {
            throw new InvalidOperationException($"Configuration 'ConnectionStrings:{connectionStringName}' is not a valid connection string.");
        }
        services.TryAddSingleton<MessagingLog>();
        var owned = new OutboxRelayDataSource(dataSource);
        services.AddSingleton(owned);
        services.AddHostedService(sp => new OutboxRelayHostedService(
            owned, sp.GetRequiredService<RedisConnectionProvider>(), tables, options, TimeSpan.FromMilliseconds(Math.Max(10, idleMs)),
            sp.GetRequiredService<MessagingLog>()));
        return services;
    }
}
