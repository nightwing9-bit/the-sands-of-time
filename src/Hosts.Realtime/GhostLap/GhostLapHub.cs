using GhostLap.Contracts.Multiplayer;
using GhostLap.Multiplayer;
using Marrow.Infrastructure.Realtime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace GhostLapHost;

/// <summary>Typed server-to-client contract of the Ghost Lap hub.</summary>
public interface IGhostLapClient
{
    Task InputRejected(InputRejection rejection);

    /// <summary>The authoritative tick that applied this controller's inputs up to a sequence (cumulative, task 10.2).</summary>
    Task InputsApplied(InputAck ack);

    /// <summary>This connection lost control to another connection of the same competitor. It may keep viewing.</summary>
    Task ControllerSuperseded();

    /// <summary>The room closed (expired or emptied). Commands now return <c>room_unavailable</c>.</summary>
    Task RoomClosed();

    /// <summary>An authoritative snapshot frame (task 10.3). Latest-wins: a frame is never queued behind a stale one.</summary>
    Task Snapshot(SnapshotFrame frame);

    /// <summary>Reliable control feedback that is neither a rejection nor state: a forfeit of this slot, a dropped-rejection report.</summary>
    Task MatchControl(MatchControlNotice notice);
}

public static class GhostLapPolicies
{
    /// <summary>A live guest session, never staff. Ghost Lap players are guests.</summary>
    public const string Connect = "ghostlap.realtime.connect";
}

/// <summary>
/// Thin, ephemeral ingress to the single <see cref="IActiveRoomOwner"/>. It holds no room state and no game logic. The caller's
/// identity is the verified guest session of the connection, never a request field.
/// <para>
/// Lobby commands are ordinary invocations: they are rare, so the shared hub's per-invocation rate filter (10/s, burst 20) is
/// appropriate for them and they re-read the session on every call. Gameplay commands are NOT invocations: they travel on ONE
/// client-to-server stream (<see cref="StreamInputs"/>), which that filter counts once. The item rate is bounded here by
/// <see cref="InputBudget"/> (Ghost Lap's own limit), and a connection that keeps flooding is closed. Because a stream item is not
/// an invocation, session liveness for gameplay is enforced by the periodic revalidator (bound: Realtime:AccessRevalidationSeconds)
/// and by the controlling-connection check, not by a database read per item.
/// </para>
/// </summary>
[Authorize(Policy = GhostLapPolicies.Connect)]
public sealed class GhostLapHub(
    IActiveRoomOwner owner,
    IMatchResync resync,
    MatchRegistry matches,
    MatchAuthorityStore store,
    GhostLapConnections connections,
    GhostLapSessionAccess access,
    RealtimeInstanceGuard guard,
    IOptions<MultiplayerOptions> options,
    TimeProvider time,
    GhostLapLog log) : Hub<IGhostLapClient>
{
    public override Task OnConnectedAsync()
    {
        if (!connections.TryAdd(Context, options.Value.MaxConnectionsPerPrincipal, out var reason))
        {
            log.ConnectionRefused(Context.ConnectionId, reason);
            Context.Abort();
        }
        return Task.CompletedTask;
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        connections.Remove(Context.ConnectionId);
        owner.ConnectionClosed(Context.ConnectionId);
        return Task.CompletedTask;
    }

    public Task<GhostLapReply<JoinedRoom>> CreateRoom(CreateRoomRequest request) => RunAsync(c => owner.CreateRoom(c, request));

    public Task<GhostLapReply<JoinedRoom>> JoinRoom(JoinRoomRequest request) => RunAsync(c => owner.JoinRoom(c, request));

    public Task<GhostLapReply<RoomCommandResult>> LeaveRoom(RoomCommand command) => RunAsync(c => owner.LeaveRoom(c, command));

    public Task<GhostLapReply<RoomCommandResult>> SetReady(SetReadyRequest request) => RunAsync(c => owner.SetReady(c, request));

    public Task<GhostLapReply<RoomCommandResult>> StartCountdown(RoomCommand command) => RunAsync(c => owner.StartCountdown(c, command));

    public Task<GhostLapReply<RoomCommandResult>> CancelCountdown(RoomCommand command) => RunAsync(c => owner.CancelCountdown(c, command));

    public Task<GhostLapReply<RoomCommandResult>> TransferHost(TransferHostRequest request) => RunAsync(c => owner.TransferHost(c, request));

    public Task<GhostLapReply<RoomView>> GetRoom(string roomId) => RunAsync(c => owner.GetRoom(c, roomId));

    /// <summary>Task 10.4: back to the lobby after a settled match. The next round is fresh and starts through ready/start as usual.</summary>
    public Task<GhostLapReply<RoomCommandResult>> Rematch(RoomCommand command) => RunAsync(c => owner.Rematch(c, command));

    /// <summary>
    /// Task 10.4: the durable outcome of a match, for a member of its room or a principal of its sealed roster. A committed result in memory is
    /// answered directly; otherwise PostgreSQL is read (it is the authority, so this works after a Realtime restart). An unknown or foreign match is
    /// <c>result_not_found</c> (no existence oracle). A Started receipt is <c>pending</c>, never reported as aborted.
    /// </summary>
    public async Task<GhostLapReply<MatchOutcomeView>> GetMatchResult(string matchId)
    {
        var gate = await AuthorizeCallAsync();
        if (gate.Failure is not null)
        {
            return GhostLapReply<MatchOutcomeView>.Failure(gate.Failure);
        }
        var subject = gate.Subject!;
        if (!Guid.TryParse(matchId, out var id))
        {
            return GhostLapReply<MatchOutcomeView>.Failure(GhostLapFailure.InvalidRequest);
        }
        if (matches.Find(id) is { Result: { } committed } runtime && owner.IsMember(subject, runtime.Config.RoomId, competitorOnly: false))
        {
            return GhostLapReply<MatchOutcomeView>.Success(MatchResults.FinalizedView(committed.Document, committed.Digest));
        }
        MatchOutcomeRecord? outcome;
        try
        {
            outcome = await store.FindOutcomeAsync(id, Context.ConnectionAborted);
        }
        catch (Exception e) when (e is Npgsql.NpgsqlException or TimeoutException or InvalidOperationException)
        {
            return GhostLapReply<MatchOutcomeView>.Failure(GhostLapFailure.Unavailable);
        }
        if (outcome is null
            || !(outcome.RosterSubjects.Contains(subject, StringComparer.Ordinal) || owner.IsMember(subject, outcome.Receipt.RoomId, competitorOnly: false)))
        {
            return GhostLapReply<MatchOutcomeView>.Failure(GhostLapFailure.ResultNotFound);
        }
        var receipt = outcome.Receipt;
        var key = id.ToString("N");
        return GhostLapReply<MatchOutcomeView>.Success(receipt.TerminalDecision switch
        {
            TerminalDecisions.ResultCommitted when outcome.ResultDocumentJson is { } json && outcome.ResultDigest is { } digest =>
                MatchResults.FinalizedView(MatchResults.DocumentFromJson(json), digest),
            TerminalDecisions.Aborted => new MatchOutcomeView(key, MatchOutcomeDecisions.Aborted, receipt.AbortReason, null, false, null, null, []),
            _ => new MatchOutcomeView(key, MatchOutcomeDecisions.Pending, null, null, false, null, null, []),
        });
    }

    /// <summary>
    /// Task 10.3: a consistent authoritative frame (state and event boundary of the same tick, the retained event tail, this connection's own
    /// applied-sequence floor and epoch). Read-only, so spectators may call it. Replaces the client's baseline; the pushed stream continues from it.
    /// </summary>
    public Task<GhostLapReply<SnapshotFrame>> Resync() => RunAsync(c =>
    {
        var room = owner.RoomOf(c);
        if (!room.Ok)
        {
            return OwnerResult<SnapshotFrame>.Fail(room.Code!);
        }
        return resync.Resync(room.Value, c.ConnectionId) is { } frame
            ? OwnerResult<SnapshotFrame>.Success(frame)
            : OwnerResult<SnapshotFrame>.Fail(GhostLapFailure.NoRunningMatch);
    });

    /// <summary>
    /// Gameplay ingress. One stream per connection; each item is checked against the connection's current controller binding.
    /// Rejections are reported to the caller (bounded) and counted; the reply summarizes the stream when it completes.
    /// </summary>
    public async Task<GhostLapReply<InputStreamSummary>> StreamInputs(IAsyncEnumerable<GameplayInput> inputs)
    {
        if (!connections.TryGet(Context.ConnectionId, out var connection))
        {
            return GhostLapReply<InputStreamSummary>.Failure(GhostLapFailure.Forbidden);
        }
        if (Interlocked.CompareExchange(ref connection.StreamOpen, 1, 0) != 0)
        {
            return GhostLapReply<InputStreamSummary>.Failure(GhostLapFailure.StreamAlreadyOpen);
        }
        long received = 0;
        long rejected = 0;
        var end = "completed";
        try
        {
            if (await access.IsLiveAsync(connection.Session, Context.ConnectionAborted) is not true)
            {
                Context.Abort();
                return GhostLapReply<InputStreamSummary>.Failure(GhostLapFailure.Forbidden);
            }
            var notices = new InputBudget(new MultiplayerOptions { InputsPerSecond = 20, InputBurst = 20 }, time);
            await foreach (var item in inputs.WithCancellation(Context.ConnectionAborted))
            {
                received++;
                string? code;
                if (!connection.Budget.TryTake())
                {
                    code = GhostLapFailure.RateLimited;
                    if (connection.Budget.ShouldDisconnect)
                    {
                        end = "rate_limit_exceeded";
                        log.InputFloodClosed(Context.ConnectionId);
                        Context.Abort();
                        break;
                    }
                }
                else
                {
                    code = owner.AdmitInput(Context.ConnectionId, connection.Session.Subject, item);
                }
                if (code is not null)
                {
                    rejected++;
                    if (notices.TryTake())
                    {
                        var rejection = new InputRejection(item?.Seq ?? -1, code);
                        // A connection that is a recipient of a live match gets it through its own bounded sender (a client that does not read
                        // can then never stall this stream's reader). Before a match exists the lobby reply is the only path.
                        if (!(owner.RoomOf(new RoomCaller(connection.Session.Subject, Context.ConnectionId)) is { Ok: true } room
                              && matches.TryOfferIngressRejection(room.Value, Context.ConnectionId, rejection)))
                        {
                            await Clients.Caller.InputRejected(rejection).WaitAsync(TimeSpan.FromMilliseconds(options.Value.SendTimeoutMilliseconds), Context.ConnectionAborted);
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            end = "aborted";
        }
        finally
        {
            Interlocked.Exchange(ref connection.StreamOpen, 0);
        }
        return GhostLapReply<InputStreamSummary>.Success(new InputStreamSummary(received, rejected, end));
    }

    private async Task<GhostLapReply<T>> RunAsync<T>(Func<RoomCaller, OwnerResult<T>> command)
    {
        var gate = await AuthorizeCallAsync();
        if (gate.Failure is not null)
        {
            return GhostLapReply<T>.Failure(gate.Failure);
        }
        var result = command(new RoomCaller(gate.Subject!, Context.ConnectionId));
        return result.Ok ? GhostLapReply<T>.Success(result.Value!) : GhostLapReply<T>.Failure(result.Code!);
    }

    /// <summary>The checks every invocation passes: this process is the authority, the connection is known and its session is live (re-read now).</summary>
    private async Task<(string? Subject, string? Failure)> AuthorizeCallAsync()
    {
        if (guard.AuthorityLost)
        {
            // This process is no longer the room authority: admit no room commands (the host is stopping).
            return (null, GhostLapFailure.Unavailable);
        }
        if (!connections.TryGet(Context.ConnectionId, out var connection))
        {
            return (null, GhostLapFailure.Forbidden);
        }
        // Lobby commands are rare, so the session is re-read from the identity database on every one (revocation is immediate).
        var live = await access.IsLiveAsync(connection.Session, Context.ConnectionAborted);
        if (live is null)
        {
            return (null, GhostLapFailure.Unavailable);
        }
        if (!live.Value)
        {
            log.ConnectionClosedForAccess(Context.ConnectionId, "session_inactive");
            Context.Abort();
            return (null, GhostLapFailure.Forbidden);
        }
        return (connection.Session.Subject, null);
    }
}
