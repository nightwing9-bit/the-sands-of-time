using GhostLap.Contracts.Multiplayer;

namespace GhostLap.Multiplayer;

/// <summary>The verified caller of a room command: subject from the server-side session, connection id from the transport. Never request data.</summary>
public readonly record struct RoomCaller(string Subject, string ConnectionId);

public readonly record struct OwnerResult<T>(string? Code, T? Value)
{
    public bool Ok => Code is null;

    public static OwnerResult<T> Success(T value) => new(null, value);

    public static OwnerResult<T> Fail(string code) => new(code, default);
}

/// <summary>Source of randomness for room secrets and invite codes. Production uses the OS CSPRNG.</summary>
public interface IRoomRandom
{
    void Fill(Span<byte> bytes);
}

public sealed class CryptoRoomRandom : IRoomRandom
{
    public void Fill(Span<byte> bytes) => System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
}

public enum StartAdmissionOutcome
{
    /// <summary>The handoff accepted the sealed round. Task 10.2's port commits the start receipt before it returns this.</summary>
    Admitted,

    /// <summary>No receipt was committed. The room returns to the lobby and the host may retry the same sealed round.</summary>
    Unavailable,

    /// <summary>
    /// The sealed match id already has a durable terminal decision (for example an orphaned receipt that was aborted). It can never
    /// run; the room returns to the lobby and the next start seals a fresh round (next ordinal, fresh match id and seed, R10.16).
    /// </summary>
    Closed,
}

/// <summary>
/// The start-admission handoff, called when a countdown elapses. The production implementation (<see cref="MatchRegistry"/>,
/// task 10.2) commits the durable start receipt and returns <see cref="StartAdmissionOutcome.Admitted"/> only after that commit.
/// <see cref="UnavailableStartAdmission"/> remains for compositions without a match database: no match is ever claimed to run.
/// </summary>
public interface IMatchStartAdmission
{
    ValueTask<StartAdmissionOutcome> AdmitAsync(SealedRoundConfig config, CancellationToken ct);
}

public sealed class UnavailableStartAdmission : IMatchStartAdmission
{
    public ValueTask<StartAdmissionOutcome> AdmitAsync(SealedRoundConfig config, CancellationToken ct) =>
        ValueTask.FromResult(StartAdmissionOutcome.Unavailable);
}

/// <summary>A gameplay command that passed identity, role, controlling-connection and epoch checks at ingress.</summary>
public readonly record struct AdmittedInput(
    Guid RoomId, string MemberId, string Subject, long ControllerEpoch, GameplayInput Input, string ConnectionId = "");

/// <summary>
/// Where an admitted gameplay command goes: the per-room serial mailbox (<see cref="MatchRegistry"/>). It must not block (the caller
/// is the socket reader). Returns null when queued, otherwise a typed failure code. Sequence and tick window are decided later, by the
/// match loop, when it applies the input; the controller epoch is checked again at that moment.
/// </summary>
public interface IGameplayInputSink
{
    string? Submit(in AdmittedInput input);
}

public sealed class NoRunningMatchSink : IGameplayInputSink
{
    public string? Submit(in AdmittedInput input) => GhostLapFailure.NoRunningMatch;
}

/// <summary>The controller of one slot as the room owner last set it. <see cref="ConnectionId"/> null = disconnected.</summary>
public readonly record struct SlotControl(int Slot, long Epoch, string? ConnectionId);

/// <summary>
/// The room owner tells the match about its competitors' controlling connections (task 10.2). Every call is made under the owner's
/// lock and must not block: implementations only publish an immutable value the match loop reads when it applies inputs.
/// </summary>
public interface IMatchPresence
{
    /// <summary>
    /// The room accepted an admitted match: set every slot's controller, register every open connection of every member as a recipient
    /// (<paramref name="viewers"/>, task 10.3) and start the tick loop.
    /// </summary>
    void Activate(Guid matchId, IReadOnlyList<SlotControl> controllers, IReadOnlyList<MatchViewer>? viewers = null);

    /// <summary>A connection became a recipient of the match, or its role changed (a superseded controller becomes a plain viewer).</summary>
    void ViewerAttached(Guid matchId, MatchViewer viewer);

    /// <summary>A recipient's connection closed or its member left.</summary>
    void ViewerDetached(Guid matchId, string connectionId);

    /// <summary>A slot's controlling connection changed (takeover: new epoch) or closed (connection null).</summary>
    void ControllerChanged(Guid matchId, SlotControl control);

    /// <summary>The room no longer matches an admitted match (closed or changed while admission was in flight).</summary>
    void Orphaned(Guid matchId);

    /// <summary>The current <see cref="MatchStatuses"/> value, or null when the match is unknown.</summary>
    string? StatusOf(Guid matchId);
}

public sealed class NoMatchPresence : IMatchPresence
{
    public void Activate(Guid matchId, IReadOnlyList<SlotControl> controllers, IReadOnlyList<MatchViewer>? viewers = null)
    {
    }

    public void ViewerAttached(Guid matchId, MatchViewer viewer)
    {
    }

    public void ViewerDetached(Guid matchId, string connectionId)
    {
    }

    public void ControllerChanged(Guid matchId, SlotControl control)
    {
    }

    public void Orphaned(Guid matchId)
    {
    }

    public string? StatusOf(Guid matchId) => null;
}

/// <summary>
/// Whether this process may still act as the single room authority (task 10.2): it holds the session advisory guard (the existing
/// RealtimeInstanceGuard, never a second guard) AND its durable process generation is registered and not retired. Read on every
/// tick and before every publication, so it must be a cheap volatile read.
/// </summary>
public interface IRealtimeAuthority
{
    bool IsValid { get; }

    /// <summary>The durable process generation this process registered (sealed into every round and start receipt).</summary>
    Guid GenerationId { get; }
}

public sealed record RoomPublication(Guid RoomId, long Version, RoomView View);

public sealed record ConnectionNotice(string ConnectionId, string Code);

/// <summary>
/// The one in-process owner of every lobby/room (design section 1: "one long-lived IActiveRoomOwner per Realtime host"). Hubs are
/// ephemeral ingress and only call it. All commands are serialized by one lock, so two commands never interleave; the gameplay
/// ingress path is lock-free.
/// </summary>
public interface IActiveRoomOwner
{
    OwnerResult<JoinedRoom> CreateRoom(RoomCaller caller, CreateRoomRequest request);

    OwnerResult<JoinedRoom> JoinRoom(RoomCaller caller, JoinRoomRequest request);

    OwnerResult<RoomCommandResult> LeaveRoom(RoomCaller caller, RoomCommand command);

    OwnerResult<RoomCommandResult> SetReady(RoomCaller caller, SetReadyRequest request);

    OwnerResult<RoomCommandResult> StartCountdown(RoomCaller caller, RoomCommand command);

    OwnerResult<RoomCommandResult> CancelCountdown(RoomCaller caller, RoomCommand command);

    OwnerResult<RoomCommandResult> TransferHost(RoomCaller caller, TransferHostRequest request);

    OwnerResult<RoomView> GetRoom(RoomCaller caller, string roomId);

    /// <summary>
    /// Task 10.4: a competitor's controlling connection returns the room to its lobby once the match is settled (finalized, aborted or
    /// unverified). Ready flags clear and the round ordinal advances, so the next start seals a fresh match id, seed and state. Idempotent.
    /// </summary>
    OwnerResult<RoomCommandResult> Rematch(RoomCaller caller, RoomCommand command);

    /// <summary>A transport connection closed. Idempotent.</summary>
    void ConnectionClosed(string connectionId);

    /// <summary>The room of a connection that is a member's (competitor or spectator) bound connection. Lock-free; used by resync.</summary>
    OwnerResult<Guid> RoomOf(RoomCaller caller);

    /// <summary>Gameplay ingress. Returns null when the command reached the sink, otherwise a typed rejection code.</summary>
    string? AdmitInput(string connectionId, string subject, GameplayInput input);

    /// <summary>Timers: elapsed countdowns (start admission), disconnect grace, code/room expiry. Called by a hosted service.</summary>
    ValueTask SweepAsync(CancellationToken ct);

    bool IsMember(string subject, Guid roomId, bool competitorOnly);

    /// <summary>Public room projection and its version, for the read-only stream. The caller has already authorized the stream.</summary>
    (long Version, RoomView View)? PublicSnapshot(Guid roomId);

    ChannelReaderPair Channels { get; }

    int RoomCount { get; }
}

/// <summary>Outbound, bounded, drop-oldest queues drained by host pumps (publication never runs inside a command).</summary>
public sealed class ChannelReaderPair(
    System.Threading.Channels.ChannelReader<RoomPublication> publications,
    System.Threading.Channels.ChannelReader<ConnectionNotice> notices)
{
    public System.Threading.Channels.ChannelReader<RoomPublication> Publications { get; } = publications;

    public System.Threading.Channels.ChannelReader<ConnectionNotice> Notices { get; } = notices;
}

/// <summary>
/// Per-connection gameplay token bucket. Ghost Lap's own bound; independent of the shared hub's per-invocation filter.
/// Integer arithmetic only (thousandths of a token), like the rest of this assembly.
/// </summary>
public sealed class InputBudget(MultiplayerOptions options, TimeProvider time)
{
    private const long Milli = 1000;
    private const long MaxElapsedTicks = TimeSpan.TicksPerSecond * 60;
    private long _milliTokens = options.InputBurst * Milli;
    private long _stamp = time.GetTimestamp();

    public int ConsecutiveOverLimit { get; private set; }

    /// <summary>True when the item is within budget. Not thread-safe: one stream reader owns it.</summary>
    public bool TryTake()
    {
        var now = time.GetTimestamp();
        var elapsed = Math.Min(time.GetElapsedTime(_stamp, now).Ticks, MaxElapsedTicks);
        _stamp = now;
        _milliTokens = Math.Min(options.InputBurst * Milli, _milliTokens + (elapsed * options.InputsPerSecond * Milli / TimeSpan.TicksPerSecond));
        if (_milliTokens < Milli)
        {
            ConsecutiveOverLimit++;
            return false;
        }
        _milliTokens -= Milli;
        ConsecutiveOverLimit = 0;
        return true;
    }

    public bool ShouldDisconnect => ConsecutiveOverLimit >= options.InputOverLimitDisconnectAfter;
}
