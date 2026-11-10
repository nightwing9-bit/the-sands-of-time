using System.Text.Json;
using Marrow.Infrastructure.Identity;
using Marrow.Kernel.Realtime;
using Marrow.Kernel.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Marrow.Infrastructure.Realtime;

/// <summary>Typed server-to-client contract. The only client method; the TypeScript twin is <c>RealtimeHubClient</c>.</summary>
public interface IRealtimeClient
{
    Task Receive(RealtimeEnvelope<JsonElement> envelope);
}

/// <summary>Registered stream sources by kind. A kind without a source is indistinguishable from a forbidden stream.</summary>
public sealed class RealtimeStreamRegistry
{
    private readonly Dictionary<string, IRealtimeStreamSource> _sources = new(StringComparer.Ordinal);

    public RealtimeStreamRegistry(IEnumerable<IRealtimeStreamSource> sources)
    {
        foreach (var source in sources)
        {
            if (!RealtimeWire.TryParseStreamId(source.StreamKind + ":x", out _, out _))
            {
                throw new InvalidOperationException($"Realtime stream kind '{source.StreamKind}' is not a valid kind.");
            }
            if (!_sources.TryAdd(source.StreamKind, source))
            {
                throw new InvalidOperationException($"Realtime stream kind '{source.StreamKind}' is registered twice.");
            }
        }
    }

    public IRealtimeStreamSource? Find(string kind) => _sources.GetValueOrDefault(kind);
}

/// <summary>
/// The single realtime hub. It only relays: it holds no game or finance state, computes no score and accepts no
/// client-published events. Every subscribe and snapshot request is authorized with the verified
/// <see cref="IActorContext"/> through the domain's <see cref="IMembershipAuthorizer"/> or <see cref="IResourceAuthorizer"/>
/// (deny by default). Clients can name a stream but never an owner or principal.
/// <para>
/// Authorization is not taken from the connect-time principal alone: every call re-reads the session (and staff roles)
/// from the identity database, and <see cref="RealtimeAccessRevalidator"/> re-checks idle connections periodically, so a
/// revoked session or removed membership stops delivery within the documented bound.
/// </para>
/// </summary>
[Authorize(Policy = RealtimePolicies.Connect)]
public sealed class MarrowRealtimeHub(
    RealtimeConnections connections,
    IServiceProvider services,
    IMembershipAuthorizer membership,
    IResourceAuthorizer resources,
    IOptions<MarrowRealtimeOptions> options,
    RealtimeLog log) : Hub<IRealtimeClient>
{
    internal static string GroupName(string streamId) => "stream:" + streamId;

    public override Task OnConnectedAsync()
    {
        if (connections.Add(Context) is null)
        {
            log.ConnectionClosedForAccess(Context.ConnectionId, "unverifiable_session");
            Context.Abort();
        }
        return Task.CompletedTask;
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        connections.Remove(Context.ConnectionId);
        return Task.CompletedTask;
    }

    public async Task<RealtimeReply> Subscribe(string streamId)
    {
        if (connections.Find(Context.ConnectionId) is not { } connection)
        {
            return RealtimeReply.Failure(RealtimeFailure.Forbidden);
        }
        await connection.Gate.WaitAsync(Context.ConnectionAborted);
        try
        {
            var (failure, source) = await AuthorizeAsync(connection, streamId);
            if (failure is not null)
            {
                return failure;
            }
            if (!connection.Subscriptions.Contains(streamId) && connection.Subscriptions.Count >= options.Value.MaxSubscriptionsPerConnection)
            {
                log.SubscriptionLimit(Context.ConnectionId);
                return RealtimeReply.Failure(RealtimeFailure.SubscriptionLimit);
            }
            connection.Subscriptions.Add(streamId);
            // Join first, then read the snapshot: an event published in between is either in the snapshot or delivered
            // with a higher sequence, so nothing falls between the two. Duplicates are discarded by the client cursor.
            await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(streamId), Context.ConnectionAborted);
            var reply = await SnapshotAsync(source!, streamId);
            if (!reply.Ok)
            {
                await LeaveAsync(connection, streamId);
            }
            return reply;
        }
        finally
        {
            connection.Gate.Release();
        }
    }

    public async Task<RealtimeReply> Unsubscribe(string streamId)
    {
        if (!RealtimeWire.TryParseStreamId(streamId, out _, out _))
        {
            return RealtimeReply.Failure(RealtimeFailure.InvalidStream);
        }
        if (connections.Find(Context.ConnectionId) is { } connection)
        {
            await connection.Gate.WaitAsync(Context.ConnectionAborted);
            try
            {
                await LeaveAsync(connection, streamId);
            }
            finally
            {
                connection.Gate.Release();
            }
        }
        return RealtimeReply.Success();
    }

    /// <summary>Refetch after a detected gap. Only for a stream this connection is subscribed to; authorization is re-checked.</summary>
    public async Task<RealtimeReply> GetSnapshot(string streamId)
    {
        if (connections.Find(Context.ConnectionId) is not { } connection)
        {
            return RealtimeReply.Failure(RealtimeFailure.Forbidden);
        }
        await connection.Gate.WaitAsync(Context.ConnectionAborted);
        try
        {
            var (failure, source) = await AuthorizeAsync(connection, streamId);
            if (failure is not null)
            {
                if (failure.Code == RealtimeFailure.Forbidden)
                {
                    await LeaveAsync(connection, streamId);
                }
                return failure;
            }
            if (!connection.Subscriptions.Contains(streamId))
            {
                return RealtimeReply.Failure(RealtimeFailure.Forbidden);
            }
            return await SnapshotAsync(source!, streamId);
        }
        finally
        {
            connection.Gate.Release();
        }
    }

    private async Task<(RealtimeReply? Failure, IRealtimeStreamSource? Source)> AuthorizeAsync(RealtimeConnection connection, string streamId)
    {
        if (!RealtimeWire.TryParseStreamId(streamId, out var kind, out var key))
        {
            return (RealtimeReply.Failure(RealtimeFailure.InvalidStream), null);
        }
        var access = services.GetRequiredService<RealtimeAccess>();
        ClaimsActorContext? actor;
        try
        {
            actor = await access.CurrentActorAsync(services, connection.Session, Context.ConnectionAborted);
        }
        catch (Exception e) when (e is NpgsqlException or TimeoutException)
        {
            // The session cannot be verified right now: refuse (never assume it is still live). No exception text is logged.
            return (RealtimeReply.Failure(RealtimeFailure.Unavailable), null);
        }
        if (actor is null)
        {
            log.ConnectionClosedForAccess(Context.ConnectionId, "session_inactive");
            Context.Abort();
            return (RealtimeReply.Failure(RealtimeFailure.Forbidden), null);
        }
        var (decision, source) = await access.AuthorizeAsync(membership, resources, actor, kind, key, Context.ConnectionAborted);
        if (!decision.IsAllowed)
        {
            log.StreamDenied(Context.ConnectionId, kind, decision.Denial);
            return (RealtimeReply.Failure(RealtimeFailure.Forbidden), null);
        }
        return (null, source);
    }

    private async Task<RealtimeReply> SnapshotAsync(IRealtimeStreamSource source, string streamId)
    {
        var snapshot = await source.GetSnapshotAsync(streamId, Context.ConnectionAborted);
        if (snapshot is null)
        {
            // Unknown stream: same answer as forbidden, so the reply is no existence oracle.
            log.StreamDenied(Context.ConnectionId, source.StreamKind, AccessDenial.UnknownResource);
            return RealtimeReply.Failure(RealtimeFailure.Forbidden);
        }
        if (snapshot.Sequence < 0 || !RealtimePayload.TryEncode(snapshot.Payload, options.Value.MaxPayloadBytes, out var payload))
        {
            log.SnapshotRejected(source.StreamKind);
            return RealtimeReply.Failure(RealtimeFailure.Unavailable);
        }
        return RealtimeReply.Success(new RealtimeSnapshot<JsonElement>(
            RealtimeEnvelope<JsonElement>.CurrentSchemaVersion, streamId, RealtimeWire.FormatSequence(snapshot.Sequence), payload));
    }

    // Caller holds connection.Gate.
    private async Task LeaveAsync(RealtimeConnection connection, string streamId)
    {
        connection.Subscriptions.Remove(streamId);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(streamId), CancellationToken.None);
    }
}

public static class RealtimePolicies
{
    /// <summary>A verified staff access token or a live guest session, never a mix of both.</summary>
    public const string Connect = "marrow.realtime.connect";
}

internal static class RealtimePayload
{
    public static bool TryEncode(object? value, int maxBytes, out JsonElement element)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, value?.GetType() ?? typeof(object), JsonSerializerOptions.Web);
        if (bytes.Length > maxBytes)
        {
            element = default;
            return false;
        }
        using var document = JsonDocument.Parse(bytes);
        element = document.RootElement.Clone();
        return true;
    }
}
