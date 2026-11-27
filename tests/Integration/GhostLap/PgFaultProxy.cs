using System.Net;
using System.Net.Sockets;
using Npgsql;

namespace Integration.Tests.GhostLap;

/// <summary>
/// A transparent TCP relay in front of the real PostgreSQL container, for task 10.4 fault injection. It changes no SQL and no bytes; it can only
/// refuse connections (database unreachable) or cut ONE connection around its next transaction COMMIT:
/// <list type="bullet">
/// <item><see cref="CommitFault.DropBeforeCommit"/>: the COMMIT message is never forwarded and the connection is closed, so the server rolls back.</item>
/// <item><see cref="CommitFault.DropAfterCommit"/>: the COMMIT is forwarded, the server's reply (proof that it processed the COMMIT) is swallowed
/// and the connection is closed: the server committed, the client never learns it (a genuinely lost acknowledgement).</item>
/// </list>
/// The COMMIT is recognised as the exact frontend Query message <c>'Q', int32 11, "COMMIT\0"</c> that Npgsql sends for a transaction commit.
/// Use with <c>Pooling=false</c> so every open is a new relayed connection.
/// </summary>
internal sealed class PgFaultProxy : IAsyncDisposable
{
    public enum CommitFault
    {
        None,
        DropBeforeCommit,
        DropAfterCommit,
    }

    private static readonly byte[] CommitMessage = [(byte)'Q', 0, 0, 0, 11, (byte)'C', (byte)'O', (byte)'M', (byte)'M', (byte)'I', (byte)'T', 0];

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly string _targetHost;
    private readonly int _targetPort;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<TcpClient> _open = [];
    private readonly Task _accept;
    private int _fault;
    private volatile bool _down;

    public PgFaultProxy(string connectionString)
    {
        var target = new NpgsqlConnectionStringBuilder(connectionString);
        _targetHost = target.Host!;
        _targetPort = target.Port;
        _listener.Start();
        _accept = AcceptAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public int CommitsDroppedBefore;

    public int CommitsDroppedAfter;

    /// <summary>With <see cref="CommitFault.DropAfterCommit"/>: the database also becomes unreachable at that instant (the process can no longer even look the commit up).</summary>
    public volatile bool DownAfterDroppedCommit;

    /// <summary>Refuse every new connection and cut the open ones (the database looks unreachable). False restores normal relaying.</summary>
    public bool Down
    {
        get => _down;
        set
        {
            _down = value;
            if (value)
            {
                lock (_open)
                {
                    foreach (var c in _open)
                    {
                        c.Close();
                    }
                    _open.Clear();
                }
            }
        }
    }

    /// <summary>Arms a one-shot fault for the next COMMIT that passes through.</summary>
    public void Arm(CommitFault fault) => Interlocked.Exchange(ref _fault, (int)fault);

    public string ConnectionString(string baseConnectionString, string applicationName) =>
        new NpgsqlConnectionStringBuilder(baseConnectionString)
        {
            Host = "127.0.0.1",
            Port = Port,
            Pooling = false,
            ApplicationName = applicationName,
            Timeout = 3,
            CommandTimeout = 5,
        }.ConnectionString;

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                if (_down)
                {
                    client.Close();
                    continue;
                }
                _ = RelayAsync(client);
            }
        }
        catch (Exception) when (_stop.IsCancellationRequested)
        {
        }
    }

    private async Task RelayAsync(TcpClient client)
    {
        var server = new TcpClient();
        try
        {
            await server.ConnectAsync(_targetHost, _targetPort, _stop.Token);
            lock (_open)
            {
                _open.Add(client);
                _open.Add(server);
            }
            var swallow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cut = new CancellationTokenSource();
            var up = PumpUpAsync(client.GetStream(), server.GetStream(), swallow, cut);
            var down = PumpDownAsync(server.GetStream(), client.GetStream(), swallow, cut);
            await Task.WhenAny(up, down);
            await cut.CancelAsync();
        }
        catch (Exception)
        {
        }
        finally
        {
            client.Close();
            server.Close();
            lock (_open)
            {
                _open.Remove(client);
                _open.Remove(server);
            }
        }
    }

    // client -> server
    private async Task PumpUpAsync(NetworkStream from, NetworkStream to, TaskCompletionSource swallow, CancellationTokenSource cut)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (true)
            {
                var n = await from.ReadAsync(buffer, cut.Token);
                if (n == 0)
                {
                    return;
                }
                if (buffer.AsSpan(0, n).IndexOf(CommitMessage) >= 0)
                {
                    var fault = (CommitFault)Interlocked.Exchange(ref _fault, (int)CommitFault.None);
                    if (fault == CommitFault.DropBeforeCommit)
                    {
                        Interlocked.Increment(ref CommitsDroppedBefore);
                        return; // never forwarded: the server sees the connection end inside the transaction and rolls back
                    }
                    if (fault == CommitFault.DropAfterCommit)
                    {
                        swallow.TrySetResult(); // from now on the server's replies are not forwarded
                        await to.WriteAsync(buffer.AsMemory(0, n), cut.Token);
                        await Task.Delay(Timeout.Infinite, cut.Token); // the down pump ends the relay once the server answered
                        return;
                    }
                }
                await to.WriteAsync(buffer.AsMemory(0, n), cut.Token);
            }
        }
        catch (Exception)
        {
        }
    }

    // server -> client
    private async Task PumpDownAsync(NetworkStream from, NetworkStream to, TaskCompletionSource swallow, CancellationTokenSource cut)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (true)
            {
                var n = await from.ReadAsync(buffer, cut.Token);
                if (n == 0)
                {
                    return;
                }
                if (swallow.Task.IsCompleted)
                {
                    Interlocked.Increment(ref CommitsDroppedAfter);
                    if (DownAfterDroppedCommit)
                    {
                        Down = true;
                    }
                    return; // the server answered the COMMIT (CommandComplete/ReadyForQuery): it committed; the client never sees it
                }
                await to.WriteAsync(buffer.AsMemory(0, n), cut.Token);
            }
        }
        catch (Exception)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        Down = true;
        try
        {
            await _accept;
        }
        catch (Exception)
        {
        }
    }
}
