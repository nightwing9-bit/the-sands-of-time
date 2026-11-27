using System.Diagnostics;
using System.Net.WebSockets;
using System.Threading.Channels;

namespace Integration.Tests.GhostLap;

/// <summary>
/// Network conditions applied to ONE client connection, in both directions, by <see cref="ImpairedWebSocket"/>. Delay is per direction (RTT is twice
/// <see cref="OneWayMs"/>); order is always preserved, like TCP. <see cref="SpikeProbability"/>/<see cref="SpikeMs"/> model head-of-line blocking from a
/// retransmission (a lost segment on TCP shows up as a delay spike, never as a missing message). <see cref="Blackhole"/> models a half-open
/// connection or a suspended tab: nothing leaves the client and the client reads nothing. <see cref="StopReading"/> models a client that does not read.
/// </summary>
internal sealed class NetworkConditions
{
    public volatile int OneWayMs;
    public volatile int JitterMs;
    public double SpikeProbability;
    public volatile int SpikeMs;
    public volatile bool Blackhole;
    public volatile bool StopReading;
    public long SentMessages;
    public long ReceivedMessages;
    public long Spikes;
}

/// <summary>
/// A real <see cref="WebSocket"/> around the in-memory test server's socket that delays, jitters, stalls and blackholes traffic. The server side is
/// untouched production code; this only changes what the client's transport observes. It is not a packet-loss model (TCP has none above the socket).
/// </summary>
internal sealed class ImpairedWebSocket : WebSocket
{
    private readonly WebSocket _inner;
    private readonly NetworkConditions _net;
    private readonly Random _random;
    private readonly Channel<Outgoing> _out = Channel.CreateUnbounded<Outgoing>();
    private readonly Channel<Incoming> _in = Channel.CreateUnbounded<Incoming>();
    private readonly CancellationTokenSource _cts = new();
    private long _lastOut;
    private long _lastIn;
    private Incoming? _current;
    private int _offset;

    private sealed record Outgoing(byte[] Data, WebSocketMessageType Type, bool EndOfMessage, long ReleaseAt);

    private sealed record Incoming(byte[] Data, WebSocketMessageType Type, long ReleaseAt, WebSocketCloseStatus? Status, string? Description);

    public ImpairedWebSocket(WebSocket inner, NetworkConditions net, int seed)
    {
        _inner = inner;
        _net = net;
        _random = new Random(seed);
        _ = Task.Run(SendPumpAsync);
        _ = Task.Run(ReceivePumpAsync);
    }

    public override WebSocketCloseStatus? CloseStatus => _inner.CloseStatus;

    public override string? CloseStatusDescription => _inner.CloseStatusDescription;

    public override string? SubProtocol => _inner.SubProtocol;

    public override WebSocketState State => _inner.State;

    private long ReleaseTime(ref long last)
    {
        double delay = _net.OneWayMs;
        lock (_random)
        {
            if (_net.JitterMs > 0)
            {
                delay += (_random.NextDouble() * 2 - 1) * _net.JitterMs;
            }
            if (_net.SpikeProbability > 0 && _random.NextDouble() < _net.SpikeProbability)
            {
                delay += _net.SpikeMs;
                Interlocked.Increment(ref _net.Spikes);
            }
        }
        var now = Stopwatch.GetTimestamp();
        var at = now + (long)(Math.Max(0, delay) * Stopwatch.Frequency / 1000);
        lock (_random)
        {
            at = Math.Max(at, last); // a later message never overtakes an earlier one
            last = at;
        }
        return at;
    }

    private static async Task WaitUntilAsync(long at, Func<bool> hold, CancellationToken ct)
    {
        while (true)
        {
            var remaining = at - Stopwatch.GetTimestamp();
            if (remaining <= 0 && !hold())
            {
                return;
            }
            await Task.Delay(remaining > 0 ? (int)Math.Clamp(remaining * 1000 / Stopwatch.Frequency, 1, 20) : 5, ct);
        }
    }

    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        var copy = buffer.ToArray();
        Interlocked.Increment(ref _net.SentMessages);
        _out.Writer.TryWrite(new Outgoing(copy, messageType, endOfMessage, ReleaseTime(ref _lastOut)));
        return Task.CompletedTask;
    }

    private async Task SendPumpAsync()
    {
        try
        {
            await foreach (var message in _out.Reader.ReadAllAsync(_cts.Token))
            {
                await WaitUntilAsync(message.ReleaseAt, () => _net.Blackhole, _cts.Token);
                await _inner.SendAsync(message.Data, message.Type, message.EndOfMessage, _cts.Token);
            }
        }
        catch (Exception)
        {
            // The connection is gone: the client's receive loop reports it.
        }
    }

    private async Task ReceivePumpAsync()
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                while (_net.StopReading || _net.Blackhole)
                {
                    await Task.Delay(5, _cts.Token);
                }
                var result = await _inner.ReceiveAsync(buffer, _cts.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _in.Writer.TryWrite(new Incoming([], WebSocketMessageType.Close, ReleaseTime(ref _lastIn), result.CloseStatus, result.CloseStatusDescription));
                    return;
                }
                message.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                {
                    Interlocked.Increment(ref _net.ReceivedMessages);
                    _in.Writer.TryWrite(new Incoming(message.ToArray(), result.MessageType, ReleaseTime(ref _lastIn), null, null));
                    message.SetLength(0);
                }
            }
        }
        catch (Exception)
        {
            _in.Writer.TryComplete();
        }
    }

    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        if (_current is null)
        {
            Incoming next;
            try
            {
                next = await _in.Reader.ReadAsync(cancellationToken);
            }
            catch (ChannelClosedException)
            {
                throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely);
            }
            await WaitUntilAsync(next.ReleaseAt, () => _net.Blackhole, cancellationToken);
            if (next.Type == WebSocketMessageType.Close)
            {
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true, next.Status, next.Description);
            }
            _current = next;
            _offset = 0;
        }
        var count = Math.Min(buffer.Count, _current.Data.Length - _offset);
        Array.Copy(_current.Data, _offset, buffer.Array!, buffer.Offset, count);
        _offset += count;
        var done = _offset >= _current.Data.Length;
        var type = _current.Type;
        if (done)
        {
            _current = null;
        }
        return new WebSocketReceiveResult(count, type, done);
    }

    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
        _inner.CloseAsync(closeStatus, statusDescription, cancellationToken);

    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
        _inner.CloseOutputAsync(closeStatus, statusDescription, cancellationToken);

    public override void Abort()
    {
        _cts.Cancel();
        _inner.Abort();
    }

    public override void Dispose()
    {
        _cts.Cancel();
        _inner.Dispose();
    }
}
