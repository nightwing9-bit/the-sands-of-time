using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Npgsql;
using StackExchange.Redis;

namespace Marrow.Infrastructure.Hosting;

/// <summary>
/// Shared composition for the API, Realtime and Worker hosts: structured logging, pooled Postgres,
/// lazily connected Redis, and dependency-aware readiness. Hosts never run migrations; the one-shot
/// executor <c>scripts/db/migrate.sh</c> owns that. Contains no domain or gameplay/payment logic.
/// </summary>
public static class MarrowHostingExtensions
{
    private const string ReadyTag = "ready";
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(5);

    /// <summary>JSON console logs with trace/span ids in scope. Never log connection strings or secrets.</summary>
    public static WebApplicationBuilder AddMarrowHostDefaults(this WebApplicationBuilder builder)
    {
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(options =>
        {
            options.IncludeScopes = true;
            options.UseUtcTimestamp = true;
            options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
        });
        builder.Logging.Configure(options =>
            options.ActivityTrackingOptions = ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId | ActivityTrackingOptions.ParentId);
        return builder;
    }

    /// <summary>
    /// Registers pooled <see cref="NpgsqlDataSource"/>, a lazy Redis connection and readiness checks.
    /// Fails fast at startup if a connection string is missing or malformed (message never echoes its value).
    /// </summary>
    public static IServiceCollection AddMarrowDependencies(this IServiceCollection services, IConfiguration configuration)
    {
        var postgres = Require(configuration, "Postgres");
        var redis = Require(configuration, "Redis");

        NpgsqlDataSource dataSource;
        try
        {
            dataSource = new NpgsqlDataSourceBuilder(postgres).Build(); // pooling is on by default
        }
        catch (Exception)
        {
            throw new InvalidOperationException("Configuration 'ConnectionStrings:Postgres' is not a valid connection string.");
        }

        ConfigurationOptions redisOptions;
        try
        {
            redisOptions = ConfigurationOptions.Parse(redis);
        }
        catch (Exception)
        {
            throw new InvalidOperationException("Configuration 'ConnectionStrings:Redis' is not a valid connection string.");
        }

        // Bounded startup: never abort the host because Redis is down; bounded connect/command waits.
        redisOptions.AbortOnConnectFail = false;
        redisOptions.ConnectRetry = 3;
        redisOptions.ConnectTimeout = 3000;
        redisOptions.SyncTimeout = 3000;
        redisOptions.AsyncTimeout = 3000;

        // Factory registrations so the container owns and disposes both on shutdown.
        services.AddSingleton(_ => dataSource);
        services.AddSingleton(_ => new RedisConnectionProvider(redisOptions));
        services.AddHealthChecks()
            .AddCheck<PostgresHealthCheck>("postgres", HealthStatus.Unhealthy, [ReadyTag], CheckTimeout)
            .AddCheck<RedisHealthCheck>("redis", HealthStatus.Unhealthy, [ReadyTag], CheckTimeout);
        return services;
    }

    /// <summary>
    /// <c>/health/live</c>: process is serving, checks no dependency. <c>/health/ready</c>: 503 while any
    /// dependency is unreachable. Both describe infrastructure only, never gameplay or payment behavior.
    /// </summary>
    public static IEndpointRouteBuilder MapMarrowHealth(this IEndpointRouteBuilder endpoints, string serviceName)
    {
        endpoints.MapGet("/health/live", () => Results.Ok(new { status = "live", service = serviceName }));
        endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains(ReadyTag),
            AllowCachingResponses = false,
            ResultStatusCodes =
            {
                [HealthStatus.Healthy] = StatusCodes.Status200OK,
                [HealthStatus.Degraded] = StatusCodes.Status503ServiceUnavailable,
                [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
            },
            ResponseWriter = (context, report) => WriteReadiness(context, report, serviceName),
        });
        return endpoints;
    }

    private static Task WriteReadiness(HttpContext context, HealthReport report, string serviceName)
    {
        context.Response.ContentType = "application/json";
        var body = new
        {
            status = report.Status.ToString(),
            service = serviceName,
            scope = "infrastructure-dependencies",
            traceId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier,
            checks = report.Entries.Select(entry => new
            {
                name = entry.Key,
                status = entry.Value.Status.ToString(),
                description = entry.Value.Description,
                durationMs = (long)entry.Value.Duration.TotalMilliseconds,
            }),
        };
        return context.Response.WriteAsync(JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private static string Require(IConfiguration configuration, string name)
    {
        var value = configuration.GetConnectionString(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Configuration 'ConnectionStrings:{name}' is required. Set the environment variable 'ConnectionStrings__{name}' (see .env.example); there is no default.");
        }

        return value;
    }
}
