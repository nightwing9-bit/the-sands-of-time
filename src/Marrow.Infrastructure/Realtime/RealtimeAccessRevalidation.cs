using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Claims;
using Marrow.Infrastructure.Identity;
using Marrow.Infrastructure.Identity.Guest;
using Marrow.Infrastructure.Identity.Staff;
using Marrow.Kernel.Realtime;
using Marrow.Kernel.Security;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Marrow.Infrastructure.Realtime;

/// <summary>The server-side session a hub connection authenticated with (read once from the verified principal).</summary>
internal readonly record struct RealtimeSession(PrincipalKind Kind, Guid PrincipalId, Guid SessionId, string Subject)
{
    public static bool TryRead(ClaimsPrincipal? user, out RealtimeSession session)
    {
        session = default;
        var actor = ClaimsActorContext.From(user);
        if (!actor.IsAuthenticated)
        {
            return false;
        }
        var prefix = actor.PrincipalKind == PrincipalKind.Staff ? "staff:" : "guest:";
        var sid = user?.FindFirst(IdentityClaimTypes.SessionId)?.Value;
        if (!Guid.TryParse(sid, out var sessionId) || !Guid.TryParse(actor.SubjectId.AsSpan(prefix.Length), out var principalId))
        {
            return false;
        }
        session = new RealtimeSession(actor.PrincipalKind, principalId, sessionId, actor.SubjectId);
        return true;
    }
}

/// <summary>One open hub connection. <see cref="Gate"/> serializes its subscription changes and its revalidation.</summary>
internal sealed class RealtimeConnection(HubCallerContext context, RealtimeSession session)
{
    public HubCallerContext Context { get; } = context;
    public RealtimeSession Session { get; } = session;
    public SemaphoreSlim Gate { get; } = new(1, 1);

    /// <summary>Stream ids this connection's group memberships correspond to. Guarded by <see cref="Gate"/>.</summary>
    public HashSet<string> Subscriptions { get; } = new(StringComparer.Ordinal);

    public string ConnectionId => Context.ConnectionId;
}

/// <summary>Open hub connections of this (single) Realtime process.</summary>
public sealed class RealtimeConnections
{
    private readonly ConcurrentDictionary<string, RealtimeConnection> _open = new(StringComparer.Ordinal);

    public int Count => _open.Count;

    internal RealtimeConnection? Add(HubCallerContext context)
    {
        if (!RealtimeSession.TryRead(context.User, out var session))
        {
            return null;
        }
        var connection = new RealtimeConnection(context, session);
        return _open.TryAdd(context.ConnectionId, connection) ? connection : null;
    }

    internal RealtimeConnection? Find(string connectionId) => _open.GetValueOrDefault(connectionId);

    internal void Remove(string connectionId) => _open.TryRemove(connectionId, out _);

    internal RealtimeConnection[] Snapshot() => [.. _open.Values];

    /// <summary>Closes every open connection (used when this process loses room authority).</summary>
    internal void AbortAll()
    {
        foreach (var connection in _open.Values)
        {
            connection.Context.Abort();
        }
    }
}

/// <summary>
/// Current authorization of a hub connection. The session and the staff roles/MFA state are re-read from the identity
/// database (the API host may have revoked, banned or changed them); stream access goes through the domain's
/// deny-by-default <see cref="IMembershipAuthorizer"/> or <see cref="IResourceAuthorizer"/>. Nothing is cached.
/// </summary>
internal sealed class RealtimeAccess(RealtimeStreamRegistry streams)
{
    /// <summary>The verified actor as of now, or null when the session is no longer live.</summary>
    public async Task<ClaimsActorContext?> CurrentActorAsync(IServiceProvider services, RealtimeSession session, CancellationToken ct)
    {
        var sid = session.SessionId.ToString("D", CultureInfo.InvariantCulture);
        if (session.Kind == PrincipalKind.Staff)
        {
            var staff = services.GetRequiredService<StaffAuthService>();
            var state = await staff.GetSessionStateAsync(session.SessionId, session.PrincipalId, ct);
            if (!state.Active)
            {
                return null;
            }
            var claims = new List<Claim>
            {
                new(IdentityClaimTypes.Subject, session.Subject),
                new(IdentityClaimTypes.SessionId, sid),
                new(IdentityClaimTypes.Kind, IdentityClaimTypes.KindStaff),
            };
            claims.AddRange(state.Roles.Select(role => new Claim(IdentityClaimTypes.Role, role)));
            if (state.TenantId is { } tenant)
            {
                // Re-read with the session on every check: a revoked tenant membership drops tenant-scoped streams.
                claims.Add(new Claim(IdentityClaimTypes.Tenant, tenant));
            }
            if (staff.IsMfaFresh(state.MfaVerifiedAt))
            {
                claims.Add(new Claim(IdentityClaimTypes.Mfa, "true"));
            }
            return Actor(claims, IdentitySchemes.StaffBearer);
        }

        var guests = services.GetRequiredService<GuestSessionService>();
        if (!await guests.IsSessionLiveAsync(session.SessionId, session.PrincipalId, ct))
        {
            return null;
        }
        return Actor(
            [
                new(IdentityClaimTypes.Subject, session.Subject),
                new(IdentityClaimTypes.SessionId, sid),
                new(IdentityClaimTypes.Kind, IdentityClaimTypes.KindGuest),
                new(IdentityClaimTypes.Role, IdentityRoles.Guest),
            ],
            IdentitySchemes.GuestSession);
    }

    /// <summary>Domain decision for one valid stream id. An unknown kind is denied exactly like a forbidden stream.</summary>
    public async Task<(AccessDecision Decision, IRealtimeStreamSource? Source)> AuthorizeAsync(
        IMembershipAuthorizer membership, IResourceAuthorizer resources, IActorContext actor, string kind, string key, CancellationToken ct)
    {
        if (!actor.IsAuthenticated)
        {
            return (AccessDecision.Deny(AccessDenial.Unauthenticated), null);
        }
        var source = streams.Find(kind);
        if (source is null)
        {
            return (AccessDecision.Deny(AccessDenial.NoPolicy), null);
        }
        var decision = source.ResolveAccess(key) switch
        {
            { Membership: { } scope } => await membership.AuthorizeAsync(actor, scope, ct),
            { Resource: { } resource } => await resources.AuthorizeAsync(actor, resource, ResourceOperation.Read, ct),
            _ => AccessDecision.Deny(AccessDenial.NoPolicy),
        };
        return (decision, decision.IsAllowed ? source : null);
    }

    private static ClaimsActorContext Actor(IEnumerable<Claim> claims, string scheme) =>
        ClaimsActorContext.From(new ClaimsPrincipal(new ClaimsIdentity(claims, scheme, IdentityClaimTypes.Subject, IdentityClaimTypes.Role)));
}

/// <summary>
/// SignalR authenticates a connection once and never removes a group membership by itself. This background service
/// re-checks every open connection every <see cref="MarrowRealtimeOptions.AccessRevalidationSeconds"/>: a connection
/// whose session was revoked, banned, disabled or expired is closed, and a subscription the domain no longer allows is
/// removed from its group. A check that cannot complete within <see cref="CheckTimeoutSeconds"/> (identity database or
/// domain lookup unavailable) closes the connection: authorization is never assumed to still hold.
/// </summary>
internal sealed class RealtimeAccessRevalidator(
    RealtimeConnections connections,
    RealtimeAccess access,
    IServiceScopeFactory scopes,
    IHubContext<MarrowRealtimeHub, IRealtimeClient> hub,
    IOptions<MarrowRealtimeOptions> options,
    RealtimeLog log) : BackgroundService
{
    public const int CheckTimeoutSeconds = 5;
    private const int Parallelism = 8;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.AccessRevalidationSeconds));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await Parallel.ForEachAsync(
                    connections.Snapshot(),
                    new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = stoppingToken },
                    RevalidateAsync);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async ValueTask RevalidateAsync(RealtimeConnection connection, CancellationToken stoppingToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, connection.Context.ConnectionAborted);
        cts.CancelAfter(TimeSpan.FromSeconds(CheckTimeoutSeconds));
        var gateHeld = false;
        try
        {
            await connection.Gate.WaitAsync(cts.Token);
            gateHeld = true;
            await using var scope = scopes.CreateAsyncScope();
            var actor = await access.CurrentActorAsync(scope.ServiceProvider, connection.Session, cts.Token);
            if (actor is null)
            {
                log.ConnectionClosedForAccess(connection.ConnectionId, "session_inactive");
                connection.Context.Abort();
                return;
            }
            var membership = scope.ServiceProvider.GetRequiredService<IMembershipAuthorizer>();
            var resources = scope.ServiceProvider.GetRequiredService<IResourceAuthorizer>();
            foreach (var streamId in connection.Subscriptions.ToArray())
            {
                RealtimeWire.TryParseStreamId(streamId, out var kind, out var key);
                var (decision, _) = await access.AuthorizeAsync(membership, resources, actor, kind, key, cts.Token);
                if (decision.IsAllowed)
                {
                    continue;
                }
                connection.Subscriptions.Remove(streamId);
                await hub.Groups.RemoveFromGroupAsync(connection.ConnectionId, MarrowRealtimeHub.GroupName(streamId), CancellationToken.None);
                log.SubscriptionRevoked(connection.ConnectionId, kind, decision.Denial);
            }
        }
        catch (Exception) when (stoppingToken.IsCancellationRequested || connection.Context.ConnectionAborted.IsCancellationRequested)
        {
            // Host stopping or the connection already closed: nothing left to protect.
        }
        catch (Exception)
        {
            // Timeout, unreachable identity database or failing domain lookup. Never logged with details (may echo hosts/users).
            log.ConnectionClosedForAccess(connection.ConnectionId, "revalidation_failed");
            connection.Context.Abort();
        }
        finally
        {
            if (gateHeld)
            {
                connection.Gate.Release();
            }
        }
    }
}
