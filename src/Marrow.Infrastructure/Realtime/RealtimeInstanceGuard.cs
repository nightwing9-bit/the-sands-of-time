using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Marrow.Infrastructure.Realtime;

/// <summary>
/// Runtime half of the one-Realtime-instance invariant (R03.07, ADR-P1-02, ADR-P1-11). At startup, before the web
/// server accepts connections, the host takes a PostgreSQL session advisory lock on a dedicated non-pooled connection.
/// If another process already holds it (for example <c>docker compose up --scale realtime=2</c>), the host refuses to
/// start. While running, the lock connection is probed; if it is lost, a second instance could take the lock, so this
/// host stops itself with a non-zero exit code.
/// <para>
/// This is a startup guard with loss detection, not an owner lease or fencing token: between a lost connection and the
/// next probe two processes can briefly overlap. Multi-replica Realtime stays forbidden until an owner lease/fence ADR
/// exists. See <c>docs/architecture/07-realtime-room-authority.md</c>.
/// </para>
/// </summary>
public sealed class RealtimeInstanceGuard(
    IConfiguration configuration,
    IOptions<MarrowRealtimeOptions> options,
    IHostApplicationLifetime lifetime,
    RealtimeConnections connections,
    RealtimeLog log) : IHostedService, IAsyncDisposable
{
    /// <summary>Advisory lock key, the ASCII bytes of "MARROWRT". Advisory locks are scoped to the current database.</summary>
    public const long LockKey = 0x4D41_5252_4F57_5254;

    public const string ApplicationName = "marrow-realtime-instance-lock";

    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);

    private readonly CancellationTokenSource _stopping = new();
    private NpgsqlConnection? _connection;
    private Task? _monitor;
    private volatile bool _held;
    private volatile bool _lost;
    private int _disposed;
    private readonly object _stopGate = new();
    private Task? _stop;

    /// <summary>True while this process holds the room-authority lock.</summary>
    public bool IsHeld => _held;

    /// <summary>True once the held lock connection was lost; the host is then stopping.</summary>
    public bool AuthorityLost => _lost;

    /// <summary>
    /// Process exit code for the host: 1 if authority was lost while running, else 0. Resolve the guard before
    /// <c>app.Run()</c>, which disposes the service provider when it returns.
    /// </summary>
    public int ExitCode => _lost ? 1 : 0;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var deadline = DateTime.UtcNow.AddSeconds(settings.InstanceLockAcquireSeconds);
        var heldElsewhere = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await TryAcquireAsync(cancellationToken))
                {
                    _held = true;
                    log.InstanceLockAcquired(LockKey);
                    _monitor = MonitorAsync(TimeSpan.FromSeconds(settings.InstanceLockCheckSeconds), _stopping.Token);
                    return;
                }
                heldElsewhere = true;
            }
            catch (Exception e) when (e is NpgsqlException or TimeoutException or InvalidOperationException)
            {
                // Unreachable or refused. Never log the exception: Npgsql messages can echo host and user names.
                heldElsewhere = false;
                await CloseAsync();
            }
            if (DateTime.UtcNow >= deadline)
            {
                break;
            }
            await Task.Delay(RetryDelay, cancellationToken);
        }

        log.InstanceLockRefused(heldElsewhere ? "held-by-another-instance" : "postgres-unreachable");
        await CloseAsync();
        throw new RealtimeInstanceRefusedException(heldElsewhere
            ? "Realtime refused to start: another Realtime instance already holds the room-authority lock "
              + $"(PostgreSQL advisory lock {LockKey}). Exactly one Realtime replica is supported until an owner lease/fence ADR "
              + "exists. Do not scale the realtime service; see docs/architecture/07-realtime-room-authority.md."
            : "Realtime refused to start: the single-instance room-authority lock could not be verified because PostgreSQL "
              + "was unreachable. See docs/architecture/07-realtime-room-authority.md.");
    }

    // The host can call StopAsync more than once, concurrently (e.g. the test host and app.Run both stop it). One
    // NpgsqlConnection cannot run two commands at once, so every caller shares a single stop.
    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_stopGate)
        {
            return _stop ??= StopOnceAsync(cancellationToken);
        }
    }

    private async Task StopOnceAsync(CancellationToken cancellationToken)
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
        if (_held && !_lost && _connection is not null)
        {
            try
            {
                await using var unlock = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", _connection);
                unlock.Parameters.AddWithValue("key", LockKey);
                await unlock.ExecuteScalarAsync(cancellationToken);
            }
            catch (Exception e) when (e is NpgsqlException or TimeoutException or InvalidOperationException or OperationCanceledException)
            {
                // Closing the session below releases the lock as well.
            }
        }
        _held = false;
        await CloseAsync();
    }

    // The container can dispose this twice (singleton and hosted-service registration share the instance).
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }
        await _stopping.CancelAsync();
        Task? stop;
        lock (_stopGate)
        {
            stop = _stop;
        }
        if (stop is not null)
        {
            try
            {
                await stop;
            }
            catch (OperationCanceledException)
            {
            }
        }
        if (_monitor is not null)
        {
            await Task.WhenAny(_monitor);
        }
        await CloseAsync();
        _stopping.Dispose();
    }

    private async Task<bool> TryAcquireAsync(CancellationToken ct)
    {
        if (_connection is null)
        {
            var connectionString = configuration.GetConnectionString("Postgres")
                ?? throw new InvalidOperationException("Configuration 'ConnectionStrings:Postgres' is required.");
            // Not pooled: returning a pooled connection resets the session and would silently drop the lock.
            var builder = new NpgsqlConnectionStringBuilder(connectionString)
            {
                Pooling = false,
                ApplicationName = ApplicationName,
                KeepAlive = 5,
            };
            _connection = new NpgsqlConnection(builder.ConnectionString);
            await _connection.OpenAsync(ct);
        }
        await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", _connection);
        command.Parameters.AddWithValue("key", LockKey);
        return await command.ExecuteScalarAsync(ct) is true;
    }

    private async Task MonitorAsync(TimeSpan interval, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                // A session advisory lock lives exactly as long as its session, so a live session still holds it.
                await using var probe = new NpgsqlCommand("SELECT 1", _connection);
                probe.CommandTimeout = Math.Max(1, (int)interval.TotalSeconds);
                await probe.ExecuteScalarAsync(ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception e) when (e is NpgsqlException or TimeoutException or InvalidOperationException or OperationCanceledException)
        {
            _lost = true;
            _held = false;
            log.InstanceLockLost();
            // Fail closed now rather than after graceful shutdown: drop every client so this process stops serving
            // streams while a new instance may already hold the lock. Kestrel refuses new connections once stopping.
            connections.AbortAll();
            lifetime.StopApplication();
        }
    }

    private async Task CloseAsync()
    {
        var connection = Interlocked.Exchange(ref _connection, null);
        if (connection is not null)
        {
            try
            {
                await connection.DisposeAsync();
            }
            catch (Exception e) when (e is NpgsqlException or InvalidOperationException)
            {
            }
        }
    }
}

/// <summary>The Realtime host refused to start because it could not take the single-instance room-authority lock.</summary>
public sealed class RealtimeInstanceRefusedException(string message) : InvalidOperationException(message)
{
    /// <summary>Exit code of a refused start (distinct from 1, authority lost while running).</summary>
    public const int ExitCode = 2;
}
