using GhostLap.Contracts.Multiplayer;
using GhostLap.Multiplayer;
using Marrow.Infrastructure.Identity;
using Marrow.Infrastructure.Identity.Csrf;
using Marrow.Infrastructure.Messaging;
using Marrow.Infrastructure.Realtime;
using Marrow.Kernel.Realtime;
using Marrow.Kernel.Security;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace GhostLapHost;

/// <summary>
/// Routes by resource/membership kind so two domains share the one pair of Kernel authorizer ports without either replacing the
/// other. Ghost Lap kinds (anything starting <c>ghostlap</c>) are decided only by Ghost Lap, default deny; every other kind goes to
/// the authorizer that was registered before Ghost Lap (Revenant's staff policy, or the identity layer's deny-all).
/// </summary>
public sealed class KindRoutingResourceAuthorizer(IResourceAuthorizer ghostLap, IResourceAuthorizer fallback) : IResourceAuthorizer
{
    public IResourceAuthorizer GhostLap { get; } = ghostLap;

    public IResourceAuthorizer Fallback { get; } = fallback;

    public ValueTask<AccessDecision> AuthorizeAsync(IActorContext actor, ResourceRef resource, ResourceOperation operation, CancellationToken ct) =>
        GhostLapKinds.IsGhostLap(resource.Kind)
            ? GhostLap.AuthorizeAsync(actor, resource, operation, ct)
            : Fallback.AuthorizeAsync(actor, resource, operation, ct);
}

public sealed class KindRoutingMembershipAuthorizer(IMembershipAuthorizer ghostLap, IMembershipAuthorizer fallback) : IMembershipAuthorizer
{
    public IMembershipAuthorizer GhostLap { get; } = ghostLap;

    public IMembershipAuthorizer Fallback { get; } = fallback;

    public ValueTask<AccessDecision> AuthorizeAsync(IActorContext actor, MembershipScope scope, CancellationToken ct) =>
        GhostLapKinds.IsGhostLap(scope.Kind)
            ? GhostLap.AuthorizeAsync(actor, scope, ct)
            : Fallback.AuthorizeAsync(actor, scope, ct);
}

/// <summary>The read-only room stream on the shared hub: <c>ghostlap-room:{roomId:N}</c>, sequence = room lifecycle version.</summary>
public sealed class GhostLapRoomStreamSource(IActiveRoomOwner owner) : IRealtimeStreamSource
{
    public string StreamKind => GhostLapWire.RoomStreamKind;

    public RealtimeStreamAccess ResolveAccess(string streamKey) =>
        RealtimeStreamAccess.RequireMembership(new MembershipScope(GhostLapKinds.Room, streamKey));

    public ValueTask<RealtimeSourceSnapshot?> GetSnapshotAsync(string streamId, CancellationToken ct)
    {
        if (!RealtimeWire.TryParseStreamId(streamId, out var kind, out var key) || kind != StreamKind || !Guid.TryParseExact(key, "N", out var roomId))
        {
            return ValueTask.FromResult<RealtimeSourceSnapshot?>(null);
        }
        return ValueTask.FromResult(owner.PublicSnapshot(roomId) is { } snapshot
            ? new RealtimeSourceSnapshot(snapshot.Version, snapshot.View)
            : null);
    }
}

public static class GhostLapRealtimeRegistration
{
    private const string FallbackResourceKey = "ghostlap.fallback.resource";
    private const string FallbackMembershipKey = "ghostlap.fallback.membership";

    /// <summary>
    /// Registers the Ghost Lap lobby owner, hub, room stream, background services and the kind-routing authorizers. Call after
    /// <c>AddMarrowIdentity</c>, <c>AddMarrowRealtime</c> and <c>AddRevenantRealtime</c>: the routers wrap whatever authorizer is
    /// registered at that moment, and the startup check below refuses to start if anything replaced them afterwards.
    /// </summary>
    public static IServiceCollection AddGhostLapRealtime(this IServiceCollection services, IConfiguration configuration)
    {
        if (services.Any(d => d.ServiceType == typeof(GhostLapCompositionMarker)))
        {
            throw new InvalidOperationException("AddGhostLapRealtime was called twice.");
        }
        services.AddSingleton<GhostLapCompositionMarker>();
        services.AddOptions<MultiplayerOptions>().Bind(configuration.GetSection(MultiplayerOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<MultiplayerOptions>, MultiplayerOptionsValidator>();
        services.TryAddSingleton(TimeProvider.System);
        // Task 10.2: the durable process generation. Its id is fixed here so every sealed round names it; GhostLapGenerationService
        // registers it in ghostlap.realtime_generation (retiring predecessors) before the host serves requests.
        services.AddSingleton(new GhostLapAuthorityGeneration(Guid.CreateVersion7()));
        services.TryAddSingleton<IRoomRandom, CryptoRoomRandom>();
        services.AddSingleton(sp => GhostLapMatchDataSource.Create(sp.GetRequiredService<IConfiguration>()));
        // Task 10.4: the finalizer writes MatchFinalizedV1 with the shared Outbox v1 writer into ghostlap.outbox, on its own transaction.
        services.AddSingleton(sp => new MatchAuthorityStore(
            sp.GetRequiredService<GhostLapMatchDataSource>().DataSource,
            tx => new PostgresOutboxWriter(tx, GhostLapOutbox.Tables)));
        services.AddSingleton<GhostLapRealtimeAuthority>();
        services.AddSingleton<IRealtimeAuthority>(sp => sp.GetRequiredService<GhostLapRealtimeAuthority>());
        // Task 10.3: per-recipient delivery. Each connection has its own sender task inside the match fan-out; this is only the SignalR edge.
        services.TryAddSingleton<IMatchTransport, GhostLapSignalRTransport>();
        services.AddSingleton(sp => new MatchRegistry(
            sp.GetRequiredService<IOptions<MultiplayerOptions>>().Value,
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IRealtimeAuthority>(),
            sp.GetRequiredService<MatchAuthorityStore>(),
            transport: sp.GetRequiredService<IMatchTransport>()));
        services.TryAddSingleton<IMatchResync>(sp => sp.GetRequiredService<MatchRegistry>());
        // Liveness is a hub-level (not game-level) setting: per-hub options are ignored unless set through AddHubOptions. A client that sent
        // nothing for HubClientTimeoutSeconds is closed by the server, which is how a half-open connection becomes a server-observed disconnect.
        services.AddSignalR().AddHubOptions<GhostLapHub>(_ => { });
        // The per-hub options start as a copy of the global ones (receive size, parallel invocations, the shared rate filter); only liveness differs.
        services.AddOptions<HubOptions<GhostLapHub>>().Configure<IOptions<MultiplayerOptions>>((hub, mp) =>
        {
            hub.KeepAliveInterval = TimeSpan.FromSeconds(mp.Value.HubKeepAliveSeconds);
            hub.ClientTimeoutInterval = TimeSpan.FromSeconds(mp.Value.HubClientTimeoutSeconds);
        });
        // The one match owner is the start admission (receipt before running), the gameplay sink (per-room mailboxes) and the presence
        // target. TryAdd keeps a test's explicit seam if it registered one first.
        services.TryAddSingleton<IMatchStartAdmission>(sp => sp.GetRequiredService<MatchRegistry>());
        services.TryAddSingleton<IGameplayInputSink>(sp => sp.GetRequiredService<MatchRegistry>());
        services.TryAddSingleton<IMatchPresence>(sp => sp.GetRequiredService<MatchRegistry>());
        services.AddSingleton(sp => new ActiveRoomOwner(
            sp.GetRequiredService<IOptions<MultiplayerOptions>>().Value,
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IMatchStartAdmission>(),
            sp.GetRequiredService<IGameplayInputSink>(),
            sp.GetRequiredService<IRoomRandom>(),
            sp.GetRequiredService<GhostLapAuthorityGeneration>().Id,
            sp.GetRequiredService<IMatchPresence>()));
        services.AddSingleton<IActiveRoomOwner>(sp => sp.GetRequiredService<ActiveRoomOwner>());
        services.AddSingleton<GhostLapAuthorizer>();
        services.AddSingleton<IRealtimeStreamSource, GhostLapRoomStreamSource>();
        services.AddSingleton<GhostLapLog>();
        services.AddSingleton<GhostLapConnections>();
        services.AddSingleton<GhostLapSessionAccess>();

        services.RouteGhostLapAuthorizers();

        services.AddAuthorizationBuilder().AddPolicy(GhostLapPolicies.Connect, p => p
            .AddAuthenticationSchemes(IdentitySchemes.GuestSession)
            .RequireAuthenticatedUser()
            .RequireAssertion(c =>
            {
                var actor = ClaimsActorContext.From(c.User);
                return actor.IsAuthenticated && actor.PrincipalKind == PrincipalKind.Guest;
            }));

        // Every service below is required; the startup check refuses to run without any of them. The generation service is first:
        // it needs the guard (registered earlier by AddMarrowRealtime) and must register the generation before anything is admitted.
        services.AddHostedService<GhostLapGenerationService>();
        services.AddHostedService<GhostLapSweepService>();
        services.AddHostedService<GhostLapPublicationPump>();
        services.AddHostedService<GhostLapNoticePump>();
        services.AddHostedService<GhostLapAccessRevalidator>();
        services.AddHostedService<GhostLapLoadReporter>(); // task 10.5: inert unless GhostLap:Multiplayer:ProfileOutputPath is set
        services.AddHostedService<GhostLapCompositionVerifier>();
        return services;
    }

    private static readonly Func<IServiceProvider, object> ResourceRouter = sp => new KindRoutingResourceAuthorizer(
        sp.GetRequiredService<GhostLapAuthorizer>(), sp.GetRequiredKeyedService<IResourceAuthorizer>(FallbackResourceKey));

    private static readonly Func<IServiceProvider, object> MembershipRouter = sp => new KindRoutingMembershipAuthorizer(
        sp.GetRequiredService<GhostLapAuthorizer>(), sp.GetRequiredKeyedService<IMembershipAuthorizer>(FallbackMembershipKey));

    /// <summary>
    /// Makes the Ghost Lap kind routers the registered <see cref="IResourceAuthorizer"/> and <see cref="IMembershipAuthorizer"/>,
    /// keeping whatever was registered last (Revenant's staff policy, the identity deny-all default, a test policy) reachable as the
    /// routers' fallback instead of removing it. Idempotent: already-routed ports are left alone. Any code that later REPLACES either
    /// port (a plain <c>services.Replace</c>, as <c>AddRevenantRealtime</c> does) removes the router, which the startup verifier
    /// turns into a refusal to start; such code must call this again after its replacement.
    /// </summary>
    public static IServiceCollection RouteGhostLapAuthorizers(this IServiceCollection services)
    {
        Route<IResourceAuthorizer>(services, FallbackResourceKey, ResourceRouter);
        Route<IMembershipAuthorizer>(services, FallbackMembershipKey, MembershipRouter);
        return services;
    }

    private static void Route<T>(IServiceCollection services, string key, Func<IServiceProvider, object> router)
        where T : class
    {
        var prior = services.LastOrDefault(d => d.ServiceType == typeof(T) && !d.IsKeyedService)
            ?? throw new InvalidOperationException(
                $"No {typeof(T).Name} is registered. Call AddMarrowIdentity (and AddRevenantRealtime) before AddGhostLapRealtime.");
        if (ReferenceEquals(prior.ImplementationFactory, router))
        {
            return;
        }
        services.Remove(prior);
        if (prior.ImplementationInstance is { } instance)
        {
            services.Add(new ServiceDescriptor(typeof(T), key, instance));
        }
        else if (prior.ImplementationFactory is { } factory)
        {
            services.Add(new ServiceDescriptor(typeof(T), key, (sp, _) => factory(sp), prior.Lifetime));
        }
        else
        {
            services.Add(new ServiceDescriptor(typeof(T), key, prior.ImplementationType!, prior.Lifetime));
        }
        services.Add(new ServiceDescriptor(typeof(T), router, ServiceLifetime.Scoped));
    }

    /// <summary>
    /// Origin check for every request to the Ghost Lap hub path, separate from <c>UseMarrowRealtime</c> (which covers
    /// <c>/hubs/realtime</c> only). A cookie-authenticated request needs exactly one <c>Origin</c> equal to the request's own origin or
    /// an entry of <c>GhostLap:Multiplayer:AllowedOrigins</c>. <c>Identity:AllowedOrigins</c> is deliberately NOT consulted: allowing
    /// an origin for the API does not allow it to open a game connection. Call before <c>UseMarrowIdentity</c>.
    /// </summary>
    public static IApplicationBuilder UseGhostLapRealtime(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var request = context.Request;
            if ((request.Path.StartsWithSegments(GhostLapWire.HubPath) || request.Path.StartsWithSegments(GhostLap.Contracts.Solo.GhostLapSoloWire.HubPath))
                && !string.IsNullOrEmpty(request.Cookies[IdentityCookies.GuestSession]))
            {
                var allowed = context.RequestServices.GetRequiredService<IOptions<MultiplayerOptions>>().Value.AllowedOrigins;
                if (!IsAllowedOrigin(request, allowed))
                {
                    context.RequestServices.GetRequiredService<GhostLapLog>().OriginRejected();
                    await AuthProblem.WriteAsync(context, StatusCodes.Status403Forbidden, AuthFailure.OriginRejected);
                    return;
                }
            }
            await next(context);
        });

    internal static bool IsAllowedOrigin(HttpRequest request, string[] allowed)
    {
        if (request.Headers.Origin.Count != 1)
        {
            return false;
        }
        var origin = request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin) || origin == "null")
        {
            return false;
        }
        var own = $"{request.Scheme}://{request.Host.Value}";
        return string.Equals(origin, own, StringComparison.OrdinalIgnoreCase) || allowed.Contains(origin, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Maps the Ghost Lap hub. WebSockets only: gameplay input is a client-to-server stream, which long polling cannot carry.</summary>
    public static IEndpointConventionBuilder MapGhostLapRealtime(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapHub<GhostLapHub>(GhostLapWire.HubPath, o => o.Transports = HttpTransportType.WebSockets);
}

/// <summary>Marks that Ghost Lap registration ran once (and gives the startup check something to find).</summary>
public sealed class GhostLapCompositionMarker;

/// <summary>Ghost Lap's Outbox v1 tables (migration <c>ghostlap/0005_mp_results.sql</c>), in the shared Marrow shape.</summary>
public static class GhostLapOutbox
{
    public static readonly MessagingTables Tables = new("ghostlap");
}

public sealed class MultiplayerOptionsValidator : IValidateOptions<MultiplayerOptions>
{
    public ValidateOptionsResult Validate(string? name, MultiplayerOptions options)
    {
        var errors = MultiplayerStartup.Validate(options);
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

/// <summary>Runs the lobby timers (countdown elapse and start admission, disconnect grace, expiry). Never exits on an iteration failure.</summary>
public sealed class GhostLapSweepService(
    IActiveRoomOwner owner, MatchRegistry matches, GhostLapConnections connections, RealtimeInstanceGuard guard, GhostLapLog log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    if (guard.AuthorityLost)
                    {
                        connections.AbortAll(); // the shared guard already closes the shared hub's connections; close ours too
                        continue;
                    }
                    await owner.SweepAsync(stoppingToken);
                    matches.Sweep(); // orphaned receipts (admitted, never activated) are aborted; ended matches are forgotten
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    log.ServiceIterationFailed(e, nameof(GhostLapSweepService));
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}

/// <summary>
/// Drains room publications to the shared hub's read-only stream, off the command path. A slow group send delays later
/// publications, never a command. The shared publisher keeps one small per-stream sequence record for the life of the process
/// (it has no remove), which is a known bound to review in task 10.5 together with room churn.
/// </summary>
public sealed class GhostLapPublicationPump(IActiveRoomOwner owner, IRealtimePublisher publisher, GhostLapLog log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var publication in owner.Channels.Publications.ReadAllAsync(stoppingToken))
            {
                try
                {
                    var result = await publisher.PublishAsync(
                        GhostLapWire.RoomStreamId(publication.RoomId), publication.Version, "ghostlap.room", publication.View, stoppingToken);
                    if (result != RealtimePublishResult.Published)
                    {
                        log.PublishRejected(result.ToString());
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    log.ServiceIterationFailed(e, nameof(GhostLapPublicationPump));
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}

/// <summary>
/// Delivers owner notices (controller superseded, room closed) to the affected connection. Sends are dispatched independently with a time limit
/// (task 10.3), so one connection that does not read cannot delay another's notice; the limit aborts that connection. At most
/// <see cref="MaxInFlight"/> are outstanding: beyond that a notice is dropped, which is repairable because the same facts are answered by the next
/// rejected input (<c>controller_superseded</c>) and by any room command (<c>room_unavailable</c>).
/// </summary>
public sealed class GhostLapNoticePump(
    IActiveRoomOwner owner,
    IHubContext<GhostLapHub, IGhostLapClient> hub,
    GhostLapConnections connections,
    IOptions<MultiplayerOptions> options,
    GhostLapLog log) : BackgroundService
{
    public const int MaxInFlight = 64;

    private int _inFlight;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var notice in owner.Channels.Notices.ReadAllAsync(stoppingToken))
            {
                if (Volatile.Read(ref _inFlight) >= MaxInFlight)
                {
                    continue;
                }
                Interlocked.Increment(ref _inFlight);
                _ = DeliverAsync(notice, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task DeliverAsync(ConnectionNotice notice, CancellationToken stoppingToken)
    {
        try
        {
            var client = hub.Clients.Client(notice.ConnectionId);
            var send = notice.Code == GhostLapFailure.Superseded ? client.ControllerSuperseded() : client.RoomClosed();
            await send.WaitAsync(TimeSpan.FromMilliseconds(options.Value.SendTimeoutMilliseconds), stoppingToken);
        }
        catch (TimeoutException)
        {
            if (connections.TryGet(notice.ConnectionId, out var connection))
            {
                connection.Context.Abort(); // it does not read: cut it (a competitor takes the normal disconnect path)
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            log.ServiceIterationFailed(e, nameof(GhostLapNoticePump));
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }
}

/// <summary>
/// Fails host startup (no silent degraded multiplayer) unless: the rules/layout facts hold; the resolved authorizers are the Ghost
/// Lap kind routers (nothing replaced them after registration); no other domain's stream kind is claimed by the Ghost Lap prefix;
/// and every background service Ghost Lap needs is actually registered.
/// </summary>
public sealed class GhostLapCompositionVerifier(
    IServiceProvider services,
    IServiceScopeFactory scopes,
    IEnumerable<IRealtimeStreamSource> streams,
    GhostLapAuthorityGeneration generation,
    GhostLapLog log) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Resolved here, not injected: a hosted service that takes IEnumerable<IHostedService> in its constructor is a circular dependency.
        var hosted = services.GetServices<IHostedService>().Select(h => h.GetType()).ToHashSet();
        var errors = Verify(scopes, hosted, streams.Select(s => s.StreamKind).ToArray());
        if (errors.Count > 0)
        {
            throw new InvalidOperationException("Ghost Lap multiplayer composition is invalid: " + string.Join(" ", errors));
        }
        log.CompositionVerified(generation.Id.ToString("N"));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal static List<string> Verify(IServiceScopeFactory scopes, ISet<Type> hostedTypes, string[] streamKinds)
    {
        var errors = new List<string>();
        MultiplayerStartup.ValidateRulesAndLayout(errors);
        using var scope = scopes.CreateScope();
        if (scope.ServiceProvider.GetRequiredService<IResourceAuthorizer>() is not KindRoutingResourceAuthorizer)
        {
            errors.Add("IResourceAuthorizer is not the Ghost Lap kind router (it was replaced after AddGhostLapRealtime).");
        }
        if (scope.ServiceProvider.GetRequiredService<IMembershipAuthorizer>() is not KindRoutingMembershipAuthorizer)
        {
            errors.Add("IMembershipAuthorizer is not the Ghost Lap kind router (it was replaced after AddGhostLapRealtime).");
        }
        foreach (var kind in streamKinds.Where(k => k != GhostLapWire.RoomStreamKind && GhostLapKinds.IsGhostLap(k)))
        {
            errors.Add($"Stream kind '{kind}' uses the reserved Ghost Lap prefix but is not the Ghost Lap room stream.");
        }
        if (!streamKinds.Contains(GhostLapWire.RoomStreamKind))
        {
            errors.Add("The Ghost Lap room stream source is not registered.");
        }
        foreach (var required in new[]
                 {
                     typeof(GhostLapSweepService), typeof(GhostLapPublicationPump), typeof(GhostLapNoticePump), typeof(GhostLapAccessRevalidator),
                     typeof(GhostLapGenerationService),
                 })
        {
            if (!hostedTypes.Contains(required))
            {
                errors.Add($"Required hosted service {required.Name} is not registered.");
            }
        }
        return errors;
    }
}
