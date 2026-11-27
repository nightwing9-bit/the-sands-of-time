using GhostLap.Contracts.Multiplayer;
using GhostLap.Multiplayer;
using Marrow.Infrastructure.Realtime;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;

namespace GhostLapHost;

/// <summary>
/// The durable process generation of this Realtime process (task 10.2). The id is chosen at composition (so the room owner seals it
/// into every round) and registered durably by <see cref="GhostLapGenerationService"/> before the host serves requests.
/// </summary>
public sealed record GhostLapAuthorityGeneration(Guid Id);

/// <summary>
/// Ghost Lap's own pooled connections to its schema (<c>ConnectionStrings:Postgres</c>, the Realtime login, whose only Ghost Lap role
/// is <c>ghostlap_mp_runtime</c>). Separate from the guard's dedicated non-pooled lock connection, which this code never touches.
/// </summary>
public sealed class GhostLapMatchDataSource(NpgsqlDataSource dataSource) : IAsyncDisposable
{
    public const string ApplicationName = "ghostlap-realtime-match";

    public NpgsqlDataSource DataSource { get; } = dataSource;

    public static GhostLapMatchDataSource Create(IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("Configuration 'ConnectionStrings:Postgres' is required for Ghost Lap multiplayer.");
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { ApplicationName = ApplicationName };
        if (builder.CommandTimeout is 0 or > 10)
        {
            builder.CommandTimeout = 10; // a start or abort never waits unboundedly; the tick loop never waits on this at all
        }
        return new GhostLapMatchDataSource(NpgsqlDataSource.Create(builder.ConnectionString));
    }

    public ValueTask DisposeAsync() => DataSource.DisposeAsync();
}

/// <summary>
/// <see cref="IRealtimeAuthority"/> for this host: the existing session advisory guard (<see cref="RealtimeInstanceGuard"/>, reused, not
/// duplicated) is held and not lost, AND the durable generation is registered and has not been seen retired. Volatile reads only.
/// </summary>
public sealed class GhostLapRealtimeAuthority(RealtimeInstanceGuard guard, GhostLapAuthorityGeneration generation) : IRealtimeAuthority
{
    private volatile bool _registered;
    private volatile bool _retired;

    public Guid GenerationId => generation.Id;

    public bool IsValid => _registered && !_retired && guard.IsHeld && !guard.AuthorityLost;

    public bool Registered => _registered;

    public bool Retired => _retired;

    internal void MarkRegistered() => _registered = true;

    internal void MarkRetired() => _retired = true;
}

/// <summary>
/// Startup: after the guard holds the advisory lock, registers this process's generation and, in the same transaction, retires every
/// predecessor and aborts its unfinished start receipts (a committed decision is never touched). Refuses to start otherwise.
/// Running: re-reads the generation every <see cref="MultiplayerOptions.GenerationCheckMilliseconds"/>; when a successor retired it,
/// authority is withdrawn at once (every match loop stops at its next tick boundary, no further gameplay delivery), Ghost Lap
/// connections are closed and the host stops. A failed read is not treated as retirement (the guard owns database-loss detection).
/// Stop: cancels every match loop and waits, bounded by the host's shutdown timeout, for their fenced abort writes.
/// </summary>
public sealed class GhostLapGenerationService(
    RealtimeInstanceGuard guard,
    GhostLapRealtimeAuthority authority,
    MatchAuthorityStore store,
    MatchRegistry registry,
    GhostLapConnections connections,
    IOptions<MultiplayerOptions> options,
    IHostApplicationLifetime lifetime,
    GhostLapLog log) : IHostedService
{
    private readonly CancellationTokenSource _stopping = new();
    private Task? _monitor;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!guard.IsHeld)
        {
            throw new InvalidOperationException(
                "Ghost Lap multiplayer refused to start: the Realtime instance guard does not hold the room-authority lock.");
        }
        GenerationRegistration registration;
        try
        {
            registration = await store.RegisterGenerationAsync(authority.GenerationId, cancellationToken);
        }
        catch (Exception e) when (e is NpgsqlException or TimeoutException or InvalidOperationException)
        {
            // Never log the driver exception (it can echo host and user names).
            throw new InvalidOperationException(
                "Ghost Lap multiplayer refused to start: its durable process generation could not be registered "
                + "(is migration ghostlap/0003 applied and ghostlap_mp_runtime granted to the Realtime login?).");
        }
        authority.MarkRegistered();
        log.GenerationRegistered(registration.GenerationId.ToString("N"), registration.RetiredGenerations, registration.AbortedReceipts);
        _monitor = MonitorAsync(_stopping.Token);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync();
        if (_monitor is not null)
        {
            try
            {
                await _monitor.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
        }
        await registry.StopAllAsync(cancellationToken);
    }

    private async Task MonitorAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(options.Value.GenerationCheckMilliseconds));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                string? status;
                try
                {
                    status = await store.GenerationStatusAsync(authority.GenerationId, ct);
                }
                catch (Exception e) when (e is NpgsqlException or TimeoutException or InvalidOperationException)
                {
                    continue;
                }
                if (status != "Active")
                {
                    authority.MarkRetired();
                    log.GenerationRetired(authority.GenerationId.ToString("N"));
                    connections.AbortAll();
                    lifetime.StopApplication();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }
}

/// <summary>
/// The SignalR edge of the per-recipient fan-out (task 10.3). Every call is awaited by ONE recipient's sender task, so a connection that does not
/// read can only hold its own sender; that sender gives up after <see cref="MultiplayerOptions.SendTimeoutMilliseconds"/> and aborts the connection.
/// Replaces the single serial delivery pump of task 10.2. Untyped hub context: it is the overload that accepts a cancellation token.
/// </summary>
public sealed class GhostLapSignalRTransport(IHubContext<GhostLapHub> hub, GhostLapConnections connections, IServiceProvider services) : IMatchTransport
{
    // Resolve after construction: MatchRegistry itself owns this transport. Read only its immutable plan here.
    public ValueTask SendSnapshotAsync(string connectionId, SnapshotFrame frame, CancellationToken ct) =>
        new(hub.Clients.Client(connectionId).SendAsync(GhostLapWire.SnapshotClientMethod, GhostLapPresentation.Project(frame,
            services.GetRequiredService<MatchRegistry>().Find(Guid.Parse(frame.Snapshot.MatchId))?.Engine.Context.Plan), ct));

    public ValueTask SendAckAsync(string connectionId, InputAck ack, CancellationToken ct) =>
        new(hub.Clients.Client(connectionId).SendAsync(GhostLapWire.InputsAppliedClientMethod, ack, ct));

    public ValueTask SendRejectionAsync(string connectionId, InputRejection rejection, CancellationToken ct) =>
        new(hub.Clients.Client(connectionId).SendAsync(GhostLapWire.InputRejectedClientMethod, rejection, ct));

    public ValueTask SendControlAsync(string connectionId, MatchControlNotice notice, CancellationToken ct) =>
        new(hub.Clients.Client(connectionId).SendAsync(GhostLapWire.MatchControlClientMethod, notice, ct));

    public void Abort(string connectionId)
    {
        if (connections.TryGet(connectionId, out var connection))
        {
            connection.Context.Abort();
        }
    }
}
