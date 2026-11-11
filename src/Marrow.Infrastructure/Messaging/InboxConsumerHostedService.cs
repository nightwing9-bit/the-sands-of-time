using Marrow.Infrastructure.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Marrow.Infrastructure.Messaging;

/// <summary>
/// Runs one module's <see cref="RedisStreamInboxConsumer"/> in a host. It waits for Redis through the host's lazy connection,
/// creates the consumer group, then consumes until shutdown. Outages and failed applies are handled by the consumer itself
/// (the entry stays pending and is reclaimed; after the delivery bound it is dead-lettered).
/// </summary>
internal sealed class InboxConsumerHostedService(
    NpgsqlDataSource db, RedisConnectionProvider redis, MessagingTables tables, InboxConsumerOptions options, InboxEffect effect,
    TimeSpan idle, MessagingLog log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        RedisStreamInboxConsumer? consumer = null;
        while (consumer is null && !stoppingToken.IsCancellationRequested)
        {
            try
            {
                var multiplexer = await redis.GetAsync(stoppingToken);
                consumer = new RedisStreamInboxConsumer(db, multiplexer, tables, options, effect, log);
                await consumer.EnsureGroupAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                consumer = null;
                log.ConsumerTransportUnavailable(options.ConsumerId, MessagingErrors.Sanitize(ex));
                await Task.Delay(idle, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
        if (consumer is not null)
        {
            await consumer.RunAsync(idle, stoppingToken);
        }
    }
}

public static class InboxConsumerServiceCollectionExtensions
{
    /// <summary>
    /// Registers a background inbox consumer for <paramref name="tables"/> when <c>{section}:StreamKey</c> is configured. It uses the
    /// host's main <see cref="NpgsqlDataSource"/>, whose login must be able to insert into the module inbox and perform
    /// <paramref name="effect"/>'s writes. Requires <see cref="MarrowHostingExtensions.AddMarrowDependencies"/> (PostgreSQL and Redis).
    /// Optional settings: <c>BatchSize</c>, <c>IdleDelayMs</c>, <c>ClaimIdleMs</c>, <c>MaxDeliveries</c>, <c>ConsumerName</c>.
    /// </summary>
    public static IServiceCollection AddMarrowInboxConsumerService(
        this IServiceCollection services, IConfiguration configuration, string section, MessagingTables tables, string consumerId, InboxEffect effect)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(effect);
        var streamKey = configuration[$"{section}:StreamKey"];
        if (string.IsNullOrWhiteSpace(streamKey))
        {
            return services;
        }
        var name = configuration[$"{section}:ConsumerName"];
        var options = new InboxConsumerOptions
        {
            StreamKey = streamKey,
            ConsumerId = consumerId,
            ConsumerName = string.IsNullOrWhiteSpace(name) ? $"{Environment.MachineName}-{Environment.ProcessId}" : name,
            BatchSize = int.TryParse(configuration[$"{section}:BatchSize"], out var b) ? b : 50,
            ClaimIdleTime = TimeSpan.FromMilliseconds(int.TryParse(configuration[$"{section}:ClaimIdleMs"], out var c) ? c : 30_000),
            MaxDeliveries = int.TryParse(configuration[$"{section}:MaxDeliveries"], out var m) ? m : 10,
        };
        options.Validate();
        var idleMs = int.TryParse(configuration[$"{section}:IdleDelayMs"], out var i) ? i : 500;
        services.TryAddSingleton<MessagingLog>();
        services.AddHostedService(sp => new InboxConsumerHostedService(
            sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<RedisConnectionProvider>(), tables, options, effect,
            TimeSpan.FromMilliseconds(Math.Max(10, idleMs)), sp.GetRequiredService<MessagingLog>()));
        return services;
    }
}
