namespace GhostLap.Contracts.Multiplayer;

/// <summary>
/// Wire constants of the Ghost Lap multiplayer lobby/command hub (Spec 10 task 10.1). Commands enter the single Realtime
/// process only; there is deliberately no API-host room command route. Read-only room state is the shared realtime
/// stream <see cref="RoomStreamKind"/> on <c>/hubs/realtime</c>, authorized by room membership.
/// </summary>
public static class GhostLapWire
{
    public const string HubPath = "/hubs/ghostlap";

    /// <summary>Stream kind on the shared hub: <c>ghostlap-room:{roomId:N}</c>. Sequence = room lifecycle version.</summary>
    public const string RoomStreamKind = "ghostlap-room";

    public const string CreateRoomMethod = "CreateRoom";
    public const string JoinRoomMethod = "JoinRoom";
    public const string LeaveRoomMethod = "LeaveRoom";
    public const string SetReadyMethod = "SetReady";
    public const string StartCountdownMethod = "StartCountdown";
    public const string CancelCountdownMethod = "CancelCountdown";
    public const string TransferHostMethod = "TransferHost";
    public const string GetRoomMethod = "GetRoom";

    /// <summary>
    /// Gameplay ingress: ONE client-to-server stream invocation per connection. The shared hub's per-invocation rate
    /// filter counts the invocation once, not every item; item rate is bounded by Ghost Lap itself.
    /// </summary>
    public const string StreamInputsMethod = "StreamInputs";

    /// <summary>Server-to-client: a gameplay item was refused (<see cref="InputRejection"/>).</summary>
    public const string InputRejectedClientMethod = "InputRejected";

    /// <summary>Server-to-client: this connection is no longer the controlling connection of its competitor.</summary>
    public const string ControllerSupersededClientMethod = "ControllerSuperseded";

    /// <summary>
    /// Server-to-client (task 10.2): the controlling connection's inputs up to <see cref="InputAck.LastAppliedSeq"/> were applied by the
    /// authoritative tick <see cref="InputAck.Tick"/>. Cumulative, so a newer acknowledgement supersedes an older one.
    /// </summary>
    public const string InputsAppliedClientMethod = "InputsApplied";

    /// <summary>Server-to-client (task 10.3): an authoritative <see cref="SnapshotFrame"/>. Latest-wins per recipient; never queued behind a stale one.</summary>
    public const string SnapshotClientMethod = "Snapshot";

    /// <summary>Server-to-client (task 10.3): reliable <see cref="MatchControlNotice"/> feedback.</summary>
    public const string MatchControlClientMethod = "MatchControl";

    /// <summary>Client-to-server (task 10.3): returns a consistent <see cref="SnapshotFrame"/> (<c>IsResync</c>) with the retained event tail.</summary>
    public const string ResyncMethod = "Resync";

    /// <summary>
    /// Client-to-server (task 10.4): after a match has a terminal outcome (finalized, aborted or unverified), return the room to its lobby. The next
    /// start goes through the normal ready/start policy and seals a fresh round (next ordinal: fresh match id, seed and state).
    /// </summary>
    public const string RematchMethod = "Rematch";

    /// <summary>Client-to-server (task 10.4): the durable outcome of a match (<see cref="MatchOutcomeView"/>), read from PostgreSQL when not in memory.</summary>
    public const string GetMatchResultMethod = "GetMatchResult";

    public const int MaxRequestIdLength = 64;
    public const int MaxRoomCodeLength = 16;

    public static string RoomStreamId(Guid roomId) => $"{RoomStreamKind}:{roomId:N}";
}

/// <summary>Typed failure codes. A missing, expired, foreign or closed room is always <see cref="RoomUnavailable"/> (no existence oracle).</summary>
public static class GhostLapFailure
{
    public const string InvalidRequest = "invalid_request";
    public const string RoomUnavailable = "room_unavailable";
    public const string AlreadyInRoom = "already_in_room";
    public const string RoomFull = "room_full";
    public const string SpectatorsFull = "spectators_full";
    public const string RoomsFull = "rooms_full";
    public const string JoinClosed = "join_closed";
    public const string RoleConflict = "role_conflict";
    public const string TooManyConnections = "too_many_connections";
    public const string LookupLimited = "lookup_limited";
    public const string NotController = "not_controller";
    public const string SpectatorForbidden = "spectator_forbidden";
    public const string NotHost = "not_host";
    public const string WrongPhase = "wrong_phase";
    public const string RosterInvalid = "roster_invalid";
    public const string NotReady = "not_ready";
    public const string NotConnected = "not_connected";
    public const string HostTargetInvalid = "host_target_invalid";
    public const string StaleVersion = "stale_version";
    public const string StartCommitting = "start_committing";
    public const string Superseded = "controller_superseded";
    public const string NoRunningMatch = "no_running_match";
    public const string WrongMatch = "wrong_match";
    public const string RateLimited = "rate_limited";
    public const string Forbidden = "forbidden";
    public const string Unavailable = "unavailable";
    public const string StreamAlreadyOpen = "stream_already_open";

    // Task 10.2: rejections decided by the authoritative match loop when it applies an input (the Spec 06 admission policy plus
    // the multiplayer controller/participation checks). Each is final for that input; none is retried by the server.
    public const string StaleSequence = "stale_sequence";
    public const string LateTick = "late_tick";
    public const string FutureTick = "future_tick";
    public const string RoundOver = "round_over";
    public const string Forfeited = "forfeited";
    public const string TickInputLimit = "tick_input_limit";
    public const string MailboxFull = "mailbox_full";

    // Task 10.4.
    /// <summary>The round ended but its result is not durably decided yet; a rematch must wait for a terminal outcome.</summary>
    public const string FinalizationPending = "finalization_pending";

    /// <summary>No outcome the caller may read: unknown match id, or the caller was neither in its roster nor in its room.</summary>
    public const string ResultNotFound = "result_not_found";
}

/// <summary>
/// Status of the authoritative match of a room (task 10.2), carried on <see cref="RoomView.MatchStatus"/>. Only <see cref="Finalized"/> and
/// <see cref="Aborted"/> are durable terminal decisions (task 10.4: both are reported only after this process read or wrote the committed receipt);
/// every other value is the live or provisional view of this process.
/// </summary>
public static class MatchStatuses
{
    /// <summary>The start receipt is committed and the tick loop runs.</summary>
    public const string Running = "running";

    /// <summary>
    /// Tick 2700 was resolved and the result commit is being attempted (bounded retries). Provisional: no result, winner or rating exists yet, and
    /// it is never a durable abort either.
    /// </summary>
    public const string PendingFinalization = "pending_finalization";

    /// <summary>The receipt's durable terminal decision is ResultCommitted: one immutable result and one <c>MatchFinalizedV1</c> outbox row exist.</summary>
    public const string Finalized = "finalized";

    /// <summary>The loop stopped (overload, authority loss, host stop) and a fenced abort is being written.</summary>
    public const string Aborting = "aborting";

    /// <summary>The receipt's durable terminal decision is Aborted (unrated, no winner).</summary>
    public const string Aborted = "aborted";

    /// <summary>
    /// The loop stopped, or the round ended, and this process could not verify a durable decision (database unreachable or authority lost).
    /// Provisional and unrated. The durable truth is the receipt: a committed result stays committed, an uncommitted one is aborted by the next
    /// generation's retirement. Read it with <see cref="GhostLapWire.GetMatchResultMethod"/>.
    /// </summary>
    public const string Unverified = "unverified";

    /// <summary>True for the statuses after which a room may return to its lobby (task 10.4 rematch).</summary>
    public static bool IsSettled(string? status) => status is Finalized or Aborted or Unverified;
}

public static class RoomPhases
{
    public const string Lobby = "lobby";
    public const string Countdown = "countdown";

    /// <summary>
    /// The start-admission handoff accepted the sealed round: since task 10.2 that means the durable start receipt is committed and
    /// the authoritative match exists. Whether it is running, pending finalization or aborted is <see cref="RoomView.MatchStatus"/>.
    /// </summary>
    public const string StartAdmitted = "start_admitted";

    public const string Closed = "closed";
}

public static class RoomRoles
{
    public const string Competitor = "competitor";
    public const string Spectator = "spectator";
}

/// <summary>Machine-readable reasons the room view carries for its last transition (shown to all members).</summary>
public static class RoomNotices
{
    public const string CountdownStarted = "countdown_started";
    public const string CountdownCancelledByHost = "countdown_cancelled_by_host";
    public const string CountdownCancelledUnready = "countdown_cancelled_unready";
    public const string CountdownCancelledParticipantLost = "countdown_cancelled_participant_lost";
    public const string CountdownCancelledRosterChanged = "countdown_cancelled_roster_changed";
    public const string StartAdmitted = "start_admitted";
    public const string StartUnavailable = "start_unavailable";
    public const string HostChanged = "host_changed";
    public const string RoomExpired = "room_expired";

    /// <summary>Task 10.4: a competitor returned the room to its lobby after a settled match; ready flags are cleared and the next round is fresh.</summary>
    public const string RematchLobby = "rematch_lobby";
}

/// <summary>Reply of every lobby command. The first two fields are shaped like the shared hub's throttle reply, so a throttled call parses.</summary>
public sealed record GhostLapReply<T>(bool Ok, string? Code, T? Value)
{
    public static GhostLapReply<T> Success(T value) => new(true, null, value);

    public static GhostLapReply<T> Failure(string code) => new(false, code, default);
}

public sealed record CreateRoomRequest(string RequestId);

public sealed record JoinRoomRequest(string RequestId, string Code, bool AsSpectator);

/// <summary>Leave, start or cancel. <paramref name="ExpectedVersion"/> (optional) makes a start conditional on the room state the caller saw.</summary>
public sealed record RoomCommand(string RequestId, string RoomId, long? ExpectedVersion = null);

public sealed record SetReadyRequest(string RequestId, string RoomId, bool Ready);

public sealed record TransferHostRequest(string RequestId, string RoomId, string TargetMemberId, long? ExpectedVersion = null);

/// <summary>Public member projection. The guest principal id is never exposed; <paramref name="MemberId"/> is room-scoped and opaque.</summary>
public sealed record RoomMemberView(string MemberId, string Role, bool Ready, bool Connected, bool IsHost, int? Slot);

public sealed record SlotAssignmentView(int Slot, string MemberId, int SpawnXUnits, int SpawnYUnits);

/// <summary>The sealed round configuration shown to members. <paramref name="Seed"/> is a decimal string (it can exceed 2^53).</summary>
public sealed record SealedRoundView(
    string MatchId,
    string RulesId,
    string LayoutId,
    string RulesHash,
    string LayoutFingerprint,
    string Seed,
    string RatingAlgorithmId,
    int TargetTicks,
    int CountdownMs,
    string AuthorityGeneration,
    string ConfigDigest,
    IReadOnlyList<SlotAssignmentView> Slots);

/// <summary>Read-only room projection, identical for every member. Per-connection secrets (controller epoch) are never in it.</summary>
public sealed record RoomView(
    string RoomId,
    long Version,
    string Phase,
    int MaxCompetitors,
    int MaxSpectators,
    IReadOnlyList<RoomMemberView> Members,
    int CountdownRemainingMs,
    SealedRoundView? Round,
    string? Notice,
    string? MatchStatus = null);

/// <summary>Reply to create/join: the caller's room-scoped identity and the controller epoch of THIS connection.</summary>
public sealed record JoinedRoom(RoomView View, string MemberId, string Role, long ControllerEpoch, string RoomCode);

public sealed record RoomCommandResult(RoomView View, string? Detail);

/// <summary>
/// One gameplay command (conceptual envelope, design section 2). It carries no position, score, hit or time claim.
/// Identity, epoch and role are checked at ingress (task 10.1); sequence and tick window are checked by the match loop when the
/// input is applied (task 10.2). <paramref name="ClientTick"/> is only a freshness filter and never moves the match clock.
/// </summary>
public sealed record GameplayInput(string MatchId, long ControllerEpoch, long ClientTick, int Seq, sbyte MoveX, sbyte MoveY, bool RequestBurst);

/// <summary>
/// A refused input. <paramref name="SequenceFloor"/> (task 10.3, set by the match loop) is the highest sequence the loop has applied for the slot, so a
/// controller that lost its counter (new tab, reconnect) resumes above it. <paramref name="Tick"/> is the tick that refused it.
/// </summary>
public sealed record InputRejection(int Seq, string Code, int? SequenceFloor = null, long? Tick = null);

/// <summary>Applied-input acknowledgement (task 10.2): the authoritative tick that applied this controller's inputs up to the sequence.</summary>
public sealed record InputAck(string MatchId, long ControllerEpoch, long Tick, int LastAppliedSeq);

public sealed record InputStreamSummary(long Received, long Rejected, string EndReason);
