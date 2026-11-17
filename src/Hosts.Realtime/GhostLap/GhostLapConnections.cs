using System.Collections.Concurrent;
using System.Globalization;
using GhostLap.Multiplayer;
using Marrow.Infrastructure.Identity;
using Marrow.Infrastructure.Identity.Guest;
using Marrow.Infrastructure.Realtime;
using Marrow.Kernel.Security;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;

namespace GhostLapHost;

/// <summary>The server-side guest session a Ghost Lap connection authenticated with (read once from the verified principal).</summary>
public readonly record struct GhostLapSession(Guid GuestId, Guid SessionId, string Subject)
{
    public static bool TryRead(System.Security.Claims.ClaimsPrincipal? user, out GhostLapSession session)
    {
        session = default;
        var actor = ClaimsActorContext.From(user);
        if (!actor.IsAuthenticated || actor.PrincipalKind != PrincipalKind.Guest)
        {
            return false;
        }
        const string prefix = "guest:";
        if (!Guid.TryParse(user?.FindFirst(IdentityClaimTypes.SessionId)?.Value, out var sessionId)
            || !Guid.TryParse(actor.SubjectId.AsSpan(prefix.Length), out var guestId))
        {
            return false;
        }
        session = new GhostLapSession(guestId, sessionId, actor.SubjectId);
        return true;
    }
}

public sealed class GhostLapConnection(HubCallerContext context, GhostLapSession session, InputBudget budget)
{
    public HubCallerContext Context { get; } = context;

    public GhostLapSession Session { get; } = session;

    public InputBudget Budget { get; } = budget;

    /// <summary>1 while this connection's single gameplay stream is open.</summary>
    public int StreamOpen;
}

/// <summary>
/// Open Ghost Lap hub connections. The shared hub's registry is internal and covers only <c>/hubs/realtime</c>, so the Ghost Lap hub
/// has its own, with its own per-principal cap and its own revalidation.
/// </summary>
public sealed class GhostLapConnections(TimeProvider time, IOptions<MultiplayerOptions> options)
{
    private readonly ConcurrentDictionary<string, GhostLapConnection> _open = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    private long _refusedForCapacity;

    public int Count => _open.Count;

    /// <summary>Connections refused because the process-wide cap was reached (observability).</summary>
    public long RefusedForCapacity => Interlocked.Read(ref _refusedForCapacity);

    public bool TryAdd(HubCallerContext context, int maxPerPrincipal, out string reason)
    {
        reason = "unverifiable_session";
        if (!GhostLapSession.TryRead(context.User, out var session))
        {
            return false;
        }
        lock (_gate)
        {
            // Task 10.5: process-wide cap, checked before the per-principal count. A refused connection never reaches a room, so it cannot
            // leave a partial room, receipt or controller behind.
            if (_open.Count >= options.Value.MaxTotalConnections)
            {
                Interlocked.Increment(ref _refusedForCapacity);
                reason = "server_busy";
                return false;
            }
            if (_open.Values.Count(c => c.Session.Subject == session.Subject) >= maxPerPrincipal)
            {
                reason = "too_many_connections";
                return false;
            }
            return _open.TryAdd(context.ConnectionId, new GhostLapConnection(context, session, new InputBudget(options.Value, time)));
        }
    }

    public bool TryGet(string connectionId, out GhostLapConnection connection) => _open.TryGetValue(connectionId, out connection!);

    public void Remove(string connectionId) => _open.TryRemove(connectionId, out _);

    public GhostLapConnection[] Snapshot() => [.. _open.Values];

    public void AbortAll()
    {
        foreach (var connection in _open.Values)
        {
            connection.Context.Abort();
        }
    }
}

/// <summary>Reads the guest session's current liveness from the identity database. Nothing is cached.</summary>
public sealed class GhostLapSessionAccess(IServiceScopeFactory scopes)
{
    /// <summary>True while live, false when revoked/expired/banned, null when it cannot be verified right now (callers fail closed).</summary>
    public async Task<bool?> IsLiveAsync(GhostLapSession session, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<GuestSessionService>().IsSessionLiveAsync(session.SessionId, session.GuestId, ct);
        }
        catch (Exception e) when (e is NpgsqlException or TimeoutException)
        {
            return null;
        }
    }
}

/// <summary>
/// SignalR authenticates once. Like the shared hub's revalidator (which cannot see this hub), this closes any Ghost Lap connection
/// whose guest session was revoked, expired or banned, on the same <see cref="MarrowRealtimeOptions.AccessRevalidationSeconds"/>
/// cadence. A check that cannot finish within <see cref="CheckTimeoutSeconds"/> closes the connection (never assume still valid).
/// </summary>
public sealed class GhostLapAccessRevalidator(
    GhostLapConnections connections,
    GhostLapSessionAccess access,
    IOptions<MarrowRealtimeOptions> options,
    GhostLapLog log) : BackgroundService
{
    public const int CheckTimeoutSeconds = 5;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.AccessRevalidationSeconds));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await Parallel.ForEachAsync(
                    connections.Snapshot(),
                    new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = stoppingToken },
                    CheckAsync);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async ValueTask CheckAsync(GhostLapConnection connection, CancellationToken stoppingToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, connection.Context.ConnectionAborted);
        cts.CancelAfter(TimeSpan.FromSeconds(CheckTimeoutSeconds));
        try
        {
            if (await access.IsLiveAsync(connection.Session, cts.Token) is not true)
            {
                log.ConnectionClosedForAccess(connection.Context.ConnectionId, "session_inactive");
                connection.Context.Abort();
            }
        }
        catch (Exception) when (stoppingToken.IsCancellationRequested || connection.Context.ConnectionAborted.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            log.ConnectionClosedForAccess(connection.Context.ConnectionId, "revalidation_failed");
            connection.Context.Abort();
        }
    }
}
