using Marrow.Infrastructure.Identity;
using Marrow.Infrastructure.Identity.Csrf;
using Marrow.Kernel.Realtime;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Marrow.Infrastructure.Realtime;

public static class RealtimeServiceCollectionExtensions
{
    /// <summary>
    /// Registers the typed realtime hub, the Kernel <see cref="IRealtimePublisher"/>, the per-connection rate limit and
    /// payload bounds, and the connect policy (staff bearer or guest session). Requires <c>AddMarrowIdentity</c>.
    /// No Redis backplane is added: the hosting process is the single Realtime instance, enforced at startup by
    /// <see cref="RealtimeInstanceGuard"/> (PostgreSQL advisory lock). Out-of-range bounds fail startup.
    /// </summary>
    public static IServiceCollection AddMarrowRealtime(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<MarrowRealtimeOptions>()
            .Bind(configuration.GetSection(MarrowRealtimeOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<MarrowRealtimeOptions>, MarrowRealtimeOptionsValidator>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<RealtimeLog>();
        // Registered before the web server starts listening, so a refused lock means no hub connection is ever accepted.
        services.AddSingleton<RealtimeInstanceGuard>();
        services.AddHostedService(sp => sp.GetRequiredService<RealtimeInstanceGuard>());
        services.AddSingleton<RealtimeStreamRegistry>();
        // Open connections and their periodic re-authorization (SignalR never re-checks an established connection).
        services.AddSingleton<RealtimeConnections>();
        services.AddSingleton<RealtimeAccess>();
        services.AddHostedService<RealtimeAccessRevalidator>();
        services.AddSingleton<RealtimeRateLimitFilter>();
        services.AddSingleton<SignalRRealtimePublisher>();
        services.AddSingleton<IRealtimePublisher>(sp => sp.GetRequiredService<SignalRRealtimePublisher>());
        services.AddSignalR(o =>
        {
            o.EnableDetailedErrors = false;
            o.MaximumParallelInvocationsPerClient = 1;
            o.AddFilter<RealtimeRateLimitFilter>();
        });
        // Global hub options: per-hub HubOptions<T> values are ignored unless set through AddHubOptions. This is the only hub.
        services.AddOptions<HubOptions>()
            .Configure<IOptions<MarrowRealtimeOptions>>((hub, realtime) => hub.MaximumReceiveMessageSize = realtime.Value.MaxInboundMessageBytes);
        services.AddAuthorizationBuilder().AddPolicy(RealtimePolicies.Connect, p => p
            .AddAuthenticationSchemes(IdentitySchemes.StaffBearer, IdentitySchemes.GuestSession)
            .RequireAuthenticatedUser()
            .RequireAssertion(c => ClaimsActorContext.From(c.User).IsAuthenticated));
        // Browsers cannot set an Authorization header on a WebSocket, so the staff access token may arrive as the
        // access_token query parameter, on the hub path only. The host filters request-start logs (see UseMarrowRealtime).
        services.PostConfigure<JwtBearerOptions>(IdentitySchemes.StaffBearer, o =>
        {
            var prior = o.Events.OnMessageReceived;
            o.Events.OnMessageReceived = async context =>
            {
                await prior(context);
                if (context.Token is null
                    && context.Request.Headers.Authorization.Count == 0
                    && context.Request.Path.StartsWithSegments(RealtimeWire.HubPath)
                    && context.Request.Query.TryGetValue("access_token", out var token)
                    && token.Count == 1)
                {
                    context.Token = token[0];
                }
            };
        });
        return services;
    }

    /// <summary>Registers a domain-owned stream source. Each stream kind may have one source only.</summary>
    public static IServiceCollection AddMarrowRealtimeStream<TSource>(this IServiceCollection services)
        where TSource : class, IRealtimeStreamSource
    {
        services.AddSingleton<IRealtimeStreamSource, TSource>();
        return services;
    }

    /// <summary>
    /// Refuses a cookie-authenticated hub request (guest session cookie present) whose <c>Origin</c> is missing or not
    /// allowed, which blocks cross-site WebSocket hijacking; the GET upgrade is not covered by the CSRF middleware.
    /// Call before <c>UseMarrowIdentity</c>.
    /// </summary>
    public static IApplicationBuilder UseMarrowRealtime(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var request = context.Request;
            if (request.Path.StartsWithSegments(RealtimeWire.HubPath)
                && !string.IsNullOrEmpty(request.Cookies[IdentityCookies.GuestSession]))
            {
                var identity = context.RequestServices.GetRequiredService<IOptions<MarrowIdentityOptions>>().Value;
                if (request.Headers.Origin.Count != 1 || !CsrfOriginMiddleware.IsAllowedOrigin(request, request.Headers.Origin.ToString(), identity))
                {
                    context.RequestServices.GetRequiredService<RealtimeLog>().OriginRejected();
                    await AuthProblem.WriteAsync(context, StatusCodes.Status403Forbidden, AuthFailure.OriginRejected);
                    return;
                }
            }
            await next(context);
        });

    /// <summary>Maps the hub at <see cref="RealtimeWire.HubPath"/>. A connection closes when its authentication expires.</summary>
    public static IEndpointConventionBuilder MapMarrowRealtime(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapHub<MarrowRealtimeHub>(RealtimeWire.HubPath, o => o.CloseOnAuthenticationExpiration = true);

    /// <summary>
    /// Request-start/finish logs include the query string, which on the hub path can carry the staff access token.
    /// This keeps them at Warning or above regardless of configuration.
    /// </summary>
    public static ILoggingBuilder AddMarrowRealtimeLogFilters(this ILoggingBuilder logging) =>
        logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning)
            .AddFilter("Microsoft.AspNetCore.Routing", LogLevel.Warning)
            .AddFilter("Microsoft.AspNetCore.Http.Connections", LogLevel.Warning);
}
