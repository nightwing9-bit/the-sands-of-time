extern alias realtime;

using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using Marrow.Infrastructure.Identity.Policy;
using Marrow.Infrastructure.Realtime;
using Marrow.Kernel.Realtime;
using Marrow.Kernel.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Integration.Tests.Support;

/// <summary>
/// Test-owned domain facts: which subject is a member of which room, and who owns which history. Stands in for a
/// domain schema; the hub only ever sees it through the Kernel lookup ports.
/// </summary>
public sealed class ProbeDomain
{
    public ConcurrentDictionary<(string Subject, PrincipalKind Kind, string Room), bool> Members { get; } = new();
    public ConcurrentDictionary<string, ResourceOwnership> Histories { get; } = new();

    public void AddMember(string subject, PrincipalKind kind, string room) => Members[(subject, kind, room)] = true;

    public void SetOwner(string history, string subject, PrincipalKind kind) =>
        Histories[history] = new ResourceOwnership(new ResourceRef("probe-history", history), subject, kind, null);
}

public sealed class ProbeMembershipLookup(ProbeDomain domain) : IMembershipLookup
{
    public ValueTask<bool> IsMemberAsync(string subjectId, PrincipalKind kind, MembershipScope scope, CancellationToken ct) =>
        ValueTask.FromResult(scope.Kind == "probe-room" && domain.Members.ContainsKey((subjectId, kind, scope.Id)));
}

public sealed class ProbeOwnershipLookup(ProbeDomain domain) : IResourceOwnershipLookup
{
    public ValueTask<ResourceOwnership?> FindOwnershipAsync(ResourceRef resource, CancellationToken ct) =>
        ValueTask.FromResult(resource.Kind == "probe-history" ? domain.Histories.GetValueOrDefault(resource.Id) : null);
}

/// <summary>
/// A test-owned authoritative stream: state is a running sum, each event adds its sequence number. It can advance
/// without publishing, which simulates a message lost in transport.
/// </summary>
public sealed class ProbeStreams(IRealtimePublisher publisher)
{
    private readonly ConcurrentDictionary<string, Stream> _streams = new(StringComparer.Ordinal);

    public void Create(string streamId) => _streams.TryAdd(streamId, new Stream());

    public (long Sequence, long Value) Current(string streamId)
    {
        var s = _streams[streamId];
        lock (s)
        {
            return (s.Sequence, s.Value);
        }
    }

    public RealtimeSourceSnapshot? Snapshot(string streamId)
    {
        if (!_streams.TryGetValue(streamId, out var s))
        {
            return null;
        }
        lock (s)
        {
            return new RealtimeSourceSnapshot(s.Sequence, new ProbeState(s.Value));
        }
    }

    public async Task<long> AdvanceAsync(string streamId, bool publish = true)
    {
        var s = _streams[streamId];
        await s.Gate.WaitAsync();
        try
        {
            long sequence;
            lock (s)
            {
                sequence = ++s.Sequence;
                s.Value += sequence;
            }
            if (publish)
            {
                var result = await publisher.PublishAsync(streamId, sequence, "probe.advanced", new ProbeEvent(sequence), CancellationToken.None);
                if (result != RealtimePublishResult.Published)
                {
                    throw new InvalidOperationException("publish failed: " + result);
                }
            }
            return sequence;
        }
        finally
        {
            s.Gate.Release();
        }
    }

    private sealed class Stream
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public long Sequence { get; set; }
        public long Value { get; set; }
    }
}

public sealed record ProbeState(long Value);

public sealed record ProbeEvent(long Add);

/// <summary>Room streams (<c>probe:{room}</c>) require membership; history streams (<c>probe-history:{id}</c>) require read access.</summary>
public sealed class ProbeRoomSource(ProbeStreams streams) : IRealtimeStreamSource
{
    public string StreamKind => "probe";

    public RealtimeStreamAccess ResolveAccess(string streamKey) => RealtimeStreamAccess.RequireMembership(new MembershipScope("probe-room", streamKey));

    public ValueTask<RealtimeSourceSnapshot?> GetSnapshotAsync(string streamId, CancellationToken ct) => ValueTask.FromResult(streams.Snapshot(streamId));
}

public sealed class ProbeHistorySource(ProbeStreams streams) : IRealtimeStreamSource
{
    public string StreamKind => "probe-history";

    public RealtimeStreamAccess ResolveAccess(string streamKey) => RealtimeStreamAccess.RequireRead(new ResourceRef("probe-history", streamKey));

    public ValueTask<RealtimeSourceSnapshot?> GetSnapshotAsync(string streamId, CancellationToken ct) => ValueTask.FromResult(streams.Snapshot(streamId));
}

/// <summary>The real <c>Hosts.Realtime</c> composition (identity, hub, publisher) against the real identity database.</summary>
public sealed class RealtimeHostFactory(
    IdentityDatabase database,
    string masterSecret,
    Dictionary<string, string?>? extraSettings = null,
    bool registerDomainPolicies = true,
    bool registerProbes = true) : WebApplicationFactory<realtime::Program>
{
    public const string AllowedExtraOrigin = "https://play.sands.test";

    public LogCapture Logs { get; } = new();
    public ProbeDomain Domain { get; } = new();

    public ProbeStreams Streams => Services.GetRequiredService<ProbeStreams>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = database.RealtimeConnectionString,
            ["ConnectionStrings:Redis"] = "localhost:6399,abortConnect=false",
            ["Identity:MasterSecret"] = masterSecret,
            ["Identity:AllowedOrigins:0"] = AllowedExtraOrigin,
        };
        foreach (var (key, value) in extraSettings ?? [])
        {
            settings[key] = value;
        }
        foreach (var (key, value) in settings)
        {
            builder.UseSetting(key, value);
        }
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<ILoggerProvider>(Logs);
            services.AddSingleton(Domain);
            services.AddSingleton<ProbeStreams>();
            if (registerProbes)
            {
                services.AddMarrowRealtimeStream<ProbeRoomSource>();
                services.AddMarrowRealtimeStream<ProbeHistorySource>();
            }
            if (registerDomainPolicies)
            {
                services.AddMarrowMembership<ProbeMembershipLookup>();
                services.AddMarrowResourceOwnership<ProbeOwnershipLookup>();
            }
        });
    }

    /// <summary>
    /// A real SignalR <see cref="HubConnection"/> over the in-memory server's WebSocket, the way a browser connects:
    /// the staff token as the <c>access_token</c> query parameter, the guest session as a cookie, plus an Origin header.
    /// </summary>
    public HubConnection Connect(
        string? accessToken = null,
        string? guestCookie = null,
        string? origin = "http://localhost",
        bool autoReconnect = false,
        ConcurrentQueue<WebSocket>? sockets = null)
    {
        var server = Server;
        var builder = new HubConnectionBuilder().WithUrl(new Uri(server.BaseAddress, RealtimeWire.HubPath.TrimStart('/')), o =>
        {
            o.Transports = HttpTransportType.WebSockets;
            o.SkipNegotiation = true;
            o.WebSocketFactory = async (context, ct) =>
            {
                var client = server.CreateWebSocketClient();
                client.ConfigureRequest = request =>
                {
                    if (guestCookie is not null)
                    {
                        request.Headers.Cookie = $"{Flows.GuestCookie}={guestCookie}";
                    }
                    if (origin is not null)
                    {
                        request.Headers.Origin = origin;
                    }
                };
                var uri = context.Uri;
                if (accessToken is not null)
                {
                    uri = new UriBuilder(uri) { Query = (uri.Query.Length > 1 ? uri.Query[1..] + "&" : "") + "access_token=" + Uri.EscapeDataString(accessToken) }.Uri;
                }
                var socket = await client.ConnectAsync(uri, ct);
                sockets?.Enqueue(socket);
                return socket;
            };
        });
        if (autoReconnect)
        {
            // First retry after 500 ms so a test can publish while the client is provably disconnected.
            builder.WithAutomaticReconnect([TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1)]);
        }
        return builder.Build();
    }
}

/// <summary>
/// The typed, loss-tolerant C# subscriber used by the tests: strict envelope read, Kernel <see cref="RealtimeStreamCursor{T}"/>,
/// snapshot refetch on a gap and resubscribe after a reconnect. Same rules as the TypeScript <c>RealtimeStreamClient</c>.
/// </summary>
public sealed class ProbeSubscriber : IAsyncDisposable
{
    private readonly HubConnection _connection;
    private readonly ConcurrentDictionary<string, RealtimeStreamCursor<RealtimeEnvelope<JsonElement>>> _cursors = new();
    private readonly object _gate = new();

    public ProbeSubscriber(HubConnection connection)
    {
        _connection = connection;
        connection.On<JsonElement>(RealtimeWire.ReceiveMethod, OnReceive);
        connection.Reconnected += async _ =>
        {
            Interlocked.Increment(ref _reconnects);
            foreach (var streamId in _cursors.Keys)
            {
                await ResubscribeAsync(streamId);
            }
        };
    }

    private int _reconnects;

    public int Reconnects => Volatile.Read(ref _reconnects);
    public ConcurrentQueue<JsonElement> Raw { get; } = new();
    public ConcurrentQueue<long> AppliedSequences { get; } = new();
    public ConcurrentDictionary<string, long> Values { get; } = new();
    public int Gaps;
    public int Refetches;
    public int Rejected;

    public HubConnection Connection => _connection;

    public async Task<RealtimeReply> SubscribeAsync(string streamId)
    {
        _cursors[streamId] = new RealtimeStreamCursor<RealtimeEnvelope<JsonElement>>(streamId);
        return await ResubscribeAsync(streamId);
    }

    public long? SequenceOf(string streamId) => _cursors.TryGetValue(streamId, out var c) ? c.Sequence : null;

    private async Task<RealtimeReply> ResubscribeAsync(string streamId)
    {
        lock (_gate)
        {
            _cursors[streamId].Reset();
        }
        var reply = await _connection.InvokeAsync<RealtimeReply>(RealtimeWire.SubscribeMethod, streamId);
        Apply(streamId, reply);
        return reply;
    }

    private async Task RefetchAsync(string streamId)
    {
        Interlocked.Increment(ref Refetches);
        var reply = await _connection.InvokeAsync<RealtimeReply>(RealtimeWire.GetSnapshotMethod, streamId);
        Apply(streamId, reply);
    }

    private void Apply(string streamId, RealtimeReply reply)
    {
        var refetch = false;
        lock (_gate)
        {
            if (!reply.Ok || reply.Snapshot is null || !RealtimeWire.TryParseSequence(reply.Snapshot.Sequence, out var sequence))
            {
                _cursors.TryRemove(streamId, out _);
                return;
            }
            var cursor = _cursors[streamId];
            var outcome = cursor.ApplySnapshot(sequence);
            if (!outcome.Accepted)
            {
                return;
            }
            Values[streamId] = reply.Snapshot.Payload.GetProperty("value").GetInt64();
            foreach (var envelope in outcome.Ready)
            {
                ApplyEvent(envelope);
            }
            refetch = outcome.GapRemains;
        }
        if (refetch)
        {
            _ = RefetchAsync(streamId);
        }
    }

    private void OnReceive(JsonElement raw)
    {
        Raw.Enqueue(raw.Clone());
        if (!RealtimeWire.TryReadEnvelope(raw, out var envelope, out var sequence))
        {
            Interlocked.Increment(ref Rejected);
            return;
        }
        var gap = false;
        lock (_gate)
        {
            if (!_cursors.TryGetValue(envelope!.StreamId, out var cursor))
            {
                return;
            }
            switch (cursor.Accept(sequence, envelope))
            {
                case RealtimeCursorAction.Apply:
                    ApplyEvent(envelope);
                    break;
                case RealtimeCursorAction.Gap:
                    Interlocked.Increment(ref Gaps);
                    gap = true;
                    break;
            }
        }
        if (gap)
        {
            _ = RefetchAsync(envelope.StreamId);
        }
    }

    private void ApplyEvent(RealtimeEnvelope<JsonElement> envelope)
    {
        RealtimeWire.TryParseSequence(envelope.Sequence, out var sequence);
        AppliedSequences.Enqueue(sequence);
        Values[envelope.StreamId] = Values.GetValueOrDefault(envelope.StreamId) + envelope.Payload.GetProperty("add").GetInt64();
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}

public static class Eventually
{
    public static async Task TrueAsync(Func<bool> condition, string because, int timeoutMs = 10_000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > until)
            {
                throw new TimeoutException("Timed out waiting: " + because);
            }
            await Task.Delay(20);
        }
    }
}
