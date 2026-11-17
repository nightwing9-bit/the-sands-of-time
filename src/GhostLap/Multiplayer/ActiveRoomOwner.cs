using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Threading.Channels;
using GhostLap.Contracts.Multiplayer;

namespace GhostLap.Multiplayer;

/// <summary>
/// Lobby, roster, controller-epoch and start-admission authority for every Ghost Lap room of this (single) Realtime process.
/// Task 10.1 scope: Lobby, Countdown and the start-admission handoff. Task 10.2: after the handoff committed the start receipt the
/// room activates the match in <see cref="IMatchPresence"/> and keeps it informed of every controller change; the match loop, not
/// this class, owns the world.
/// <para>
/// Concurrency: every mutating command and the sweep run under one lock (rooms are tiny and commands are infrequent), so commands
/// never interleave and every transition sees a consistent room. Start admission, which may take a database round trip in 10.2,
/// runs OUTSIDE the lock; the room is marked in flight meanwhile and the result is applied only if the room still matches.
/// The gameplay ingress path reads an immutable per-connection binding and takes no lock.
/// </para>
/// </summary>
public sealed class ActiveRoomOwner : IActiveRoomOwner
{
    private const string CodeAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const int MaxSubjectLength = 64;
    private const int PublicationCapacity = 2048;
    private const int NoticeCapacity = 512;

    private readonly object _gate = new();
    private readonly Dictionary<Guid, Room> _rooms = [];
    private readonly Dictionary<string, Guid> _codes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Guid> _roomBySubject = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LookupBucket> _lookups = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConnectionBinding> _bindings = new(StringComparer.Ordinal);
    private readonly Channel<RoomPublication> _publications;
    private readonly Channel<ConnectionNotice> _notices;
    private readonly MultiplayerOptions _options;
    private readonly TimeProvider _time;
    private readonly IMatchStartAdmission _admission;
    private readonly IGameplayInputSink _sink;
    private readonly IRoomRandom _random;
    private readonly Guid _generation;
    private readonly IMatchPresence _presence;

    public ActiveRoomOwner(
        MultiplayerOptions options,
        TimeProvider time,
        IMatchStartAdmission admission,
        IGameplayInputSink sink,
        IRoomRandom random,
        Guid authorityGeneration,
        IMatchPresence? presence = null)
    {
        var errors = MultiplayerStartup.Validate(options);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException("Ghost Lap multiplayer configuration is invalid: " + string.Join(" ", errors));
        }
        _options = options;
        _time = time;
        _admission = admission;
        _sink = sink;
        _random = random;
        _generation = authorityGeneration;
        _presence = presence ?? new NoMatchPresence();
        // Bounded and drop-oldest: a stalled pump can never grow memory or block a command. A dropped publication is a sequence
        // gap that subscribers repair with a snapshot (the stream contract).
        _publications = Channel.CreateBounded<RoomPublication>(new BoundedChannelOptions(PublicationCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        _notices = Channel.CreateBounded<ConnectionNotice>(new BoundedChannelOptions(NoticeCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        Channels = new ChannelReaderPair(_publications.Reader, _notices.Reader);
    }

    public ChannelReaderPair Channels { get; }

    public int RoomCount
    {
        get
        {
            lock (_gate)
            {
                return _rooms.Count;
            }
        }
    }

    // ---------------------------------------------------------------- commands

    public OwnerResult<JoinedRoom> CreateRoom(RoomCaller caller, CreateRoomRequest request)
    {
        if (!ValidCaller(caller) || !ValidRequestId(request?.RequestId))
        {
            return OwnerResult<JoinedRoom>.Fail(GhostLapFailure.InvalidRequest);
        }
        lock (_gate)
        {
            if (ConnectionOwnedByOther(caller))
            {
                return OwnerResult<JoinedRoom>.Fail(GhostLapFailure.Forbidden);
            }
            if (_roomBySubject.TryGetValue(caller.Subject, out var existingId) && _rooms.TryGetValue(existingId, out var existing))
            {
                // A retried create (same request id) re-attaches to the room it already made; anything else is a typed conflict.
                return existing.CreatorSubject == caller.Subject && existing.CreateRequestId == request!.RequestId
                    ? Attach(existing, existing.MemberOf(caller.Subject)!, caller, isNew: false)
                    : OwnerResult<JoinedRoom>.Fail(GhostLapFailure.AlreadyInRoom);
            }
            if (_rooms.Count >= _options.MaxRooms)
            {
                return OwnerResult<JoinedRoom>.Fail(GhostLapFailure.RoomsFull);
            }
            var room = new Room(Guid.CreateVersion7(), _time.GetTimestamp(), caller.Subject, request!.RequestId);
            _random.Fill(room.Secret);
            room.Code = NewUniqueCode();
            var host = AddMember(room, caller.Subject, spectator: false);
            room.HostMemberId = host.MemberId;
            _rooms[room.Id] = room;
            _codes[room.Code] = room.Id;
            _roomBySubject[caller.Subject] = room.Id;
            return Attach(room, host, caller, isNew: true);
        }
    }

    public OwnerResult<JoinedRoom> JoinRoom(RoomCaller caller, JoinRoomRequest request)
    {
        if (!ValidCaller(caller) || !ValidRequestId(request?.RequestId) || !TryNormalizeCode(request!.Code, out var code))
        {
            return OwnerResult<JoinedRoom>.Fail(GhostLapFailure.InvalidRequest);
        }
        lock (_gate)
        {
            if (ConnectionOwnedByOther(caller))
            {
                return OwnerResult<JoinedRoom>.Fail(GhostLapFailure.Forbidden);
            }
            if (!TakeLookupToken(caller.Subject))
            {
                return OwnerResult<JoinedRoom>.Fail(GhostLapFailure.LookupLimited);
            }
            Room? room = _codes.TryGetValue(code, out var id) && _rooms.TryGetValue(id, out var found) ? found : null;
            if (_roomBySubject.TryGetValue(caller.Subject, out var ownId))
            {
                // Already a member somewhere: only the same room may be re-attached (a refresh or a second tab). Membership, not the
                // code, is the credential here, so an expired code does not lock a member out.
                if (room is null || room.Id != ownId)
                {
                    return OwnerResult<JoinedRoom>.Fail(GhostLapFailure.AlreadyInRoom);
                }
                var self = room.MemberOf(caller.Subject)!;
                return self.Spectator != request.AsSpectator
                    ? OwnerResult<JoinedRoom>.Fail(GhostLapFailure.RoleConflict)
                    : Attach(room, self, caller, isNew: false);
            }
            if (room is null || room.Phase == RoomPhases.Closed || CodeExpired(room))
            {
                return OwnerResult<JoinedRoom>.Fail(GhostLapFailure.RoomUnavailable);
            }
            if (request.AsSpectator)
            {
                if (room.Members.Count(m => m.Spectator) >= _options.MaxSpectators)
                {
                    return OwnerResult<JoinedRoom>.Fail(GhostLapFailure.SpectatorsFull);
                }
            }
            else
            {
                // No competitor admission once the countdown has started (the roster is sealed), only observers.
                if (room.Phase != RoomPhases.Lobby)
                {
                    return OwnerResult<JoinedRoom>.Fail(GhostLapFailure.JoinClosed);
                }
                if (room.Members.Count(m => !m.Spectator) >= _options.MaxCompetitors)
                {
                    return OwnerResult<JoinedRoom>.Fail(GhostLapFailure.RoomFull);
                }
            }
            var member = AddMember(room, caller.Subject, request.AsSpectator);
            _roomBySubject[caller.Subject] = room.Id;
            if (!request.AsSpectator)
            {
                ClearReady(room);
            }
            return Attach(room, member, caller, isNew: true);
        }
    }

    public OwnerResult<RoomCommandResult> LeaveRoom(RoomCaller caller, RoomCommand command)
    {
        lock (_gate)
        {
            var found = Resolve(caller, command?.RoomId, requireCompetitor: false, requireController: false);
            if (found.Failure is not null)
            {
                return OwnerResult<RoomCommandResult>.Fail(found.Failure);
            }
            var (room, member) = (found.Room!, found.Member!);
            if (!member.Spectator)
            {
                if (member.Controller != caller.ConnectionId)
                {
                    return OwnerResult<RoomCommandResult>.Fail(GhostLapFailure.NotController);
                }
                if (room.Phase == RoomPhases.StartAdmitted || room.AdmissionInFlight)
                {
                    // The roster is sealed. Leaving a started or starting round is a disconnect/forfeit question owned by tasks 10.2/10.3.
                    return OwnerResult<RoomCommandResult>.Fail(room.AdmissionInFlight ? GhostLapFailure.StartCommitting : GhostLapFailure.WrongPhase);
                }
            }
            RemoveMember(room, member, RoomNotices.CountdownCancelledParticipantLost);
            return OwnerResult<RoomCommandResult>.Success(new RoomCommandResult(BuildView(room), null));
        }
    }

    public OwnerResult<RoomCommandResult> SetReady(RoomCaller caller, SetReadyRequest request)
    {
        lock (_gate)
        {
            var found = Resolve(caller, request?.RoomId, requireCompetitor: true, requireController: true);
            if (found.Failure is not null)
            {
                return OwnerResult<RoomCommandResult>.Fail(found.Failure);
            }
            var (room, member) = (found.Room!, found.Member!);
            if (room.AdmissionInFlight)
            {
                return OwnerResult<RoomCommandResult>.Fail(GhostLapFailure.StartCommitting);
            }
            if (room.Phase is not (RoomPhases.Lobby or RoomPhases.Countdown))
            {
                return OwnerResult<RoomCommandResult>.Fail(GhostLapFailure.WrongPhase);
            }
            if (member.Ready == request!.Ready)
            {
                return OwnerResult<RoomCommandResult>.Success(new RoomCommandResult(BuildView(room), null));
            }
            member.Ready = request.Ready;
            Touch(room);
            if (room.Phase == RoomPhases.Countdown)
            {
                // Only un-ready can reach here (ready was already true for everyone). It withdraws consent and cancels.
                CancelCountdownCore(room, RoomNotices.CountdownCancelledUnready);
            }
            else
            {
                Bump(room, null);
            }
            return OwnerResult<RoomCommandResult>.Success(new RoomCommandResult(BuildView(room), null));
        }
    }

    public OwnerResult<RoomCommandResult> StartCountdown(RoomCaller caller, RoomCommand command)
    {
        lock (_gate)
        {
            var found = Resolve(caller, command?.RoomId, requireCompetitor: true, requireController: true);
            if (found.Failure is not null)
            {
                return OwnerResult<RoomCommandResult>.Fail(found.Failure);
            }
            var (room, member) = (found.Room!, found.Member!);
            if (!ValidRequestId(command!.RequestId))
            {
                return OwnerResult<RoomCommandResult>.Fail(GhostLapFailure.InvalidRequest);
            }
            if (room.HostMemberId != member.MemberId)
            {
                return OwnerResult<RoomCommandResult>.Fail(GhostLapFailure.NotHost);
            }
            if (room.Phase is RoomPhases.Countdown or RoomPhases.StartAdmitted)
            {
                // Duplicate start (retry, double click, second tab): the same sealed round, never a second one.
                return OwnerResult<RoomCommandResult>.Success(new RoomCommandResult(BuildView(room), "already_started"));
            }
            if (command.ExpectedVersion is { } expected && expected != room.Version)
            {
                return OwnerResult<RoomCommandResult>.Fail(GhostLapFailure.StaleVersion);
            }
            var competitors = room.Members.Where(m => !m.Spectator).ToArray();
            if (competitors.Length < MultiplayerOptions.MinCompetitorsFixed || competitors.Length > _options.MaxCompetitors)
            {
                return OwnerResult<RoomCommandResult>.Fail(GhostLapFailure.RosterInvalid);
            }
            if (competitors.Any(m => m.Controller is null))
            {
                return OwnerResult<RoomCommandResult>.Fail(GhostLapFailure.NotConnected);
            }
            if (competitors.Any(m => !m.Ready))
            {
                return OwnerResult<RoomCommandResult>.Fail(GhostLapFailure.NotReady);
            }
            room.Config = RoundSealing.Seal(
                room.Secret, room.Id, room.RoundOrdinal, [.. competitors.Select(m => new RosterEntry(m.MemberId, m.Subject))], _options, _generation);
            room.CountdownDeadline = _time.GetTimestamp() + MillisecondsToTimestamp(_options.CountdownMilliseconds);
            room.Phase = RoomPhases.Countdown;
            Touch(room);
            Bump(room, RoomNotices.CountdownStarted);
            return OwnerResult<RoomCommandResult>.Success(new RoomCommandResult(BuildView(room), null));
        }
    }

    public OwnerResult<RoomCommandResult> CancelCountdown(RoomCaller caller, RoomCommand command)
    {
        lock (_gate)
        {
            var found = Resolve(caller, command?.RoomId, requireCompetitor: true, requireController: true);
            if (found.Failure is not null)
            {
                return OwnerResult<RoomCommandResult>.Fail(found.Failure);
            }
            var (room, member) = (found.Room!, found.Member!);
            if (room.HostMemberId != member.MemberId)
            {
                return OwnerResult<RoomCommandResult>.Fail(GhostLapFailure.NotHost);
            }
            if (room.Phase == RoomPhases.Lobby)
            {
                return OwnerResult<RoomCommandResult>.Success(new RoomCommandResult(BuildView(room), "already_lobby"));
            }
            if (room.AdmissionInFlight)
            {
                return OwnerResult<RoomCommandResult>.Fail(GhostLapFailure.StartCommitting);
            }
            if (room.Phase != RoomPhases.Countdown)
            {
                return OwnerResult<RoomCommandResult>.Fail(GhostLapFailure.WrongPhase);
            }
            Touch(room);
            CancelCountdownCore(room, RoomNotices.CountdownCancelledByHost);
            return OwnerResult<RoomCommandResult>.Success(new RoomCommandResult(BuildView(room), null));
        }
    }

    public OwnerResult<RoomCommandResult> TransferHost(RoomCaller caller, TransferHostRequest request)
    {
        lock (_gate)
        {
            var found = Resolve(caller, request?.RoomId, requireCompetitor: true, requireController: true);
            if (found.Failure is not null)
            {
                return OwnerResult<RoomCommandResult>.Fail(found.Failure);
            }
            var (room, member) = (found.Room!, found.Member!);
            if (room.HostMemberId != member.MemberId)
            {
                return OwnerResult<RoomCommandResult>.Fail(GhostLapFailure.NotHost);
            }
            if (room.Phase != RoomPhases.Lobby)
            {
                return OwnerResult<RoomCommandResult>.Fail(GhostLapFailure.WrongPhase);
            }
            if (request!.ExpectedVersion is { } expected && expected != room.Version)
            {
                return OwnerResult<RoomCommandResult>.Fail(GhostLapFailure.StaleVersion);
            }
            var target = room.Members.FirstOrDefault(m => m.MemberId == request.TargetMemberId);
            if (target is null || target.Spectator || target.Controller is null || target == member)
            {
                return OwnerResult<RoomCommandResult>.Fail(GhostLapFailure.HostTargetInvalid);
            }
            room.HostMemberId = target.MemberId;
            Touch(room);
            Bump(room, RoomNotices.HostChanged);
            return OwnerResult<RoomCommandResult>.Success(new RoomCommandResult(BuildView(room), null));
        }
    }

    public OwnerResult<RoomCommandResult> Rematch(RoomCaller caller, RoomCommand command)
    {
        lock (_gate)
        {
            var found = Resolve(caller, command?.RoomId, requireCompetitor: true, requireController: true);
            if (found.Failure is not null)
            {
                return OwnerResult<RoomCommandResult>.Fail(found.Failure);
            }
            if (!ValidRequestId(command!.RequestId))
            {
                return OwnerResult<RoomCommandResult>.Fail(GhostLapFailure.InvalidRequest);
            }
            var room = found.Room!;
            if (room.Phase == RoomPhases.Lobby)
            {
                // A duplicate rematch (retry, second competitor, second tab) after the room already returned: no second ordinal step.
                return OwnerResult<RoomCommandResult>.Success(new RoomCommandResult(BuildView(room), "already_lobby"));
            }
            if (room.Phase != RoomPhases.StartAdmitted || room.MatchId is not { } matchId)
            {
                return OwnerResult<RoomCommandResult>.Fail(GhostLapFailure.WrongPhase);
            }
            // The registry forgets a match only after it ended, so an unknown status is a settled one.
            var status = _presence.StatusOf(matchId);
            if (status is not null && !MatchStatuses.IsSettled(status))
            {
                return OwnerResult<RoomCommandResult>.Fail(
                    status is MatchStatuses.PendingFinalization or MatchStatuses.Aborting ? GhostLapFailure.FinalizationPending : GhostLapFailure.WrongPhase);
            }
            foreach (var member in room.Members)
            {
                foreach (var connection in member.Connections)
                {
                    _presence.ViewerDetached(matchId, connection);
                }
            }
            // Fresh round: never the previous match id, seed, receipt, state or rating identity (R10.16). The next start seals ordinal + 1.
            room.Phase = RoomPhases.Lobby;
            room.MatchId = null;
            room.MatchStatus = null;
            room.Config = null;
            room.CountdownDeadline = 0;
            room.RoundOrdinal++;
            ClearReady(room);
            if (room.Members.FirstOrDefault(m => m.MemberId == room.HostMemberId) is not { Controller: not null })
            {
                room.HostMemberId = PickHost(room);
            }
            Touch(room);
            Bump(room, RoomNotices.RematchLobby);
            return OwnerResult<RoomCommandResult>.Success(new RoomCommandResult(BuildView(room), null));
        }
    }

    public OwnerResult<RoomView> GetRoom(RoomCaller caller, string roomId)
    {
        lock (_gate)
        {
            var found = Resolve(caller, roomId, requireCompetitor: false, requireController: false);
            return found.Failure is not null
                ? OwnerResult<RoomView>.Fail(found.Failure)
                : OwnerResult<RoomView>.Success(BuildView(found.Room!));
        }
    }

    public void ConnectionClosed(string connectionId)
    {
        lock (_gate)
        {
            if (!_bindings.TryRemove(connectionId, out var binding) || !_rooms.TryGetValue(binding.RoomId, out var room))
            {
                return;
            }
            if (room.MatchId is { } closedMatch)
            {
                _presence.ViewerDetached(closedMatch, connectionId);
            }
            var member = room.Members.FirstOrDefault(m => m.MemberId == binding.MemberId);
            if (member is null)
            {
                return;
            }
            member.Connections.Remove(connectionId);
            if (member.Spectator)
            {
                if (member.Connections.Count == 0)
                {
                    RemoveMember(room, member, null);
                }
                return;
            }
            if (member.Controller != connectionId)
            {
                return; // a superseded viewer tab closed: no change to the competitor
            }
            member.Controller = null;
            member.DisconnectedAt = _time.GetTimestamp();
            NotifyController(room, member); // a running match neutralizes this slot on its next tick
            if (room.HostMemberId == member.MemberId && room.Phase == RoomPhases.Lobby)
            {
                TransferHostFromDisconnected(room, member);
            }
            if (room.Phase == RoomPhases.Countdown && !room.AdmissionInFlight)
            {
                CancelCountdownCore(room, RoomNotices.CountdownCancelledParticipantLost);
            }
            else
            {
                Bump(room, null);
            }
        }
    }

    public OwnerResult<Guid> RoomOf(RoomCaller caller) =>
        ValidCaller(caller) && _bindings.TryGetValue(caller.ConnectionId, out var binding) && binding.Subject == caller.Subject
            ? OwnerResult<Guid>.Success(binding.RoomId)
            : OwnerResult<Guid>.Fail(GhostLapFailure.RoomUnavailable);

    // ---------------------------------------------------------------- gameplay ingress (lock-free)

    public string? AdmitInput(string connectionId, string subject, GameplayInput input)
    {
        if (input is null || !_bindings.TryGetValue(connectionId, out var binding) || binding.Subject != subject)
        {
            return GhostLapFailure.Forbidden;
        }
        if (binding.Spectator)
        {
            return GhostLapFailure.SpectatorForbidden;
        }
        // A takeover replaces the old connection's binding (Controlling = false) under the room lock before it returns, so any
        // command processed after the takeover is refused here even if the old socket still has items in flight.
        if (!binding.Controlling || input.ControllerEpoch != binding.Epoch)
        {
            return GhostLapFailure.Superseded;
        }
        if (input.MoveX is < -1 or > 1 || input.MoveY is < -1 or > 1 || input.Seq < 0 || input.ClientTick < 0
            || !Guid.TryParse(input.MatchId, out _))
        {
            return GhostLapFailure.InvalidRequest;
        }
        return _sink.Submit(new AdmittedInput(binding.RoomId, binding.MemberId, binding.Subject, binding.Epoch, input, connectionId));
    }

    // ---------------------------------------------------------------- timers

    public async ValueTask SweepAsync(CancellationToken ct)
    {
        List<(Guid RoomId, SealedRoundConfig Config)> due = [];
        lock (_gate)
        {
            Housekeeping();
            var now = _time.GetTimestamp();
            foreach (var room in _rooms.Values)
            {
                if (room.Phase == RoomPhases.Countdown && !room.AdmissionInFlight && now >= room.CountdownDeadline)
                {
                    room.AdmissionInFlight = true;
                    due.Add((room.Id, room.Config!));
                }
            }
        }
        foreach (var (roomId, config) in due)
        {
            var outcome = await AdmitSafelyAsync(config, ct).ConfigureAwait(false);
            if (outcome is null)
            {
                return; // host stopping
            }
            lock (_gate)
            {
                if (!_rooms.TryGetValue(roomId, out var room) || room.Phase != RoomPhases.Countdown || !room.AdmissionInFlight
                    || room.Config?.ConfigDigest != config.ConfigDigest)
                {
                    if (outcome == StartAdmissionOutcome.Admitted)
                    {
                        _presence.Orphaned(config.MatchId); // committed receipt, no room to run it: the match aborts it
                    }
                    continue;
                }
                room.AdmissionInFlight = false;
                if (outcome == StartAdmissionOutcome.Admitted)
                {
                    room.Phase = RoomPhases.StartAdmitted;
                    room.MatchId = config.MatchId;
                    // Controllers as they are NOW (a takeover or dropout during admission is included), then the loop starts.
                    _presence.Activate(
                        config.MatchId,
                        [.. config.Slots.Select(slot => Control(room, slot))],
                        [.. room.Members.SelectMany(m => m.Connections.Select(c => Viewer(room, m, c)))]);
                    room.MatchStatus = _presence.StatusOf(config.MatchId);
                    Bump(room, RoomNotices.StartAdmitted);
                }
                else if (outcome == StartAdmissionOutcome.Closed)
                {
                    // The sealed match id is terminal and can never run: the next start seals a fresh round (fresh id and seed).
                    room.RoundOrdinal++;
                    CancelCountdownCore(room, RoomNotices.StartUnavailable);
                }
                else
                {
                    // No receipt: back to a retryable lobby. Ready flags and the sealed identity are unchanged, so a retry re-seals the same round.
                    CancelCountdownCore(room, RoomNotices.StartUnavailable);
                }
            }
        }
    }

    private async ValueTask<StartAdmissionOutcome?> AdmitSafelyAsync(SealedRoundConfig config, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.AdmissionTimeoutMilliseconds);
        try
        {
            return await _admission.AdmitAsync(config, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception)
        {
            // Timeout or any port failure: no receipt is assumed. Never throws into the sweep loop.
            return StartAdmissionOutcome.Unavailable;
        }
    }

    private void Housekeeping()
    {
        var now = _time.GetTimestamp();
        foreach (var room in _rooms.Values.ToArray())
        {
            if (room.MatchId is { } matchId)
            {
                var status = _presence.StatusOf(matchId);
                if (status != room.MatchStatus)
                {
                    room.MatchStatus = status;
                    Bump(room, null);
                }
            }
            if (Elapsed(room.CreatedAt, now) > TimeSpan.FromSeconds(_options.MaxRoomAgeSeconds)
                || Elapsed(room.LastActivity, now) > TimeSpan.FromSeconds(_options.LobbyIdleSeconds))
            {
                if (!room.AdmissionInFlight)
                {
                    CloseRoom(room, RoomNotices.RoomExpired);
                }
                continue;
            }
            if (!room.AdmissionInFlight && room.Phase != RoomPhases.StartAdmitted)
            {
                foreach (var gone in room.Members
                             .Where(m => !m.Spectator && m.Controller is null && m.DisconnectedAt is { } at
                                         && Elapsed(at, now) > TimeSpan.FromSeconds(_options.DisconnectGraceSeconds))
                             .ToArray())
                {
                    RemoveMember(room, gone, RoomNotices.CountdownCancelledParticipantLost);
                    if (room.Phase == RoomPhases.Closed)
                    {
                        break;
                    }
                }
                if (room.Phase == RoomPhases.Closed)
                {
                    continue;
                }
            }
            var anyConnected = room.Members.Any(m => !m.Spectator && m.Controller is not null);
            if (anyConnected)
            {
                room.EmptySince = null;
            }
            else
            {
                room.EmptySince ??= now;
                // A running match is never closed under its players: the server keeps ticking it and forfeits an absent competitor after the 450-tick grace.
                if (!room.AdmissionInFlight && room.MatchStatus != MatchStatuses.Running
                    && Elapsed(room.EmptySince.Value, now) > TimeSpan.FromSeconds(_options.EmptyRoomSeconds))
                {
                    CloseRoom(room, RoomNotices.RoomExpired);
                }
            }
        }
        foreach (var key in _lookups.Where(kv => Elapsed(kv.Value.Stamp, now) > TimeSpan.FromMinutes(2)).Select(kv => kv.Key).ToArray())
        {
            _lookups.Remove(key);
        }
    }

    // ---------------------------------------------------------------- queries

    public bool IsMember(string subject, Guid roomId, bool competitorOnly)
    {
        lock (_gate)
        {
            return _rooms.TryGetValue(roomId, out var room)
                && room.MemberOf(subject) is { } member
                && (!competitorOnly || !member.Spectator);
        }
    }

    public (long Version, RoomView View)? PublicSnapshot(Guid roomId)
    {
        lock (_gate)
        {
            return _rooms.TryGetValue(roomId, out var room) ? (room.Version, BuildView(room)) : null;
        }
    }

    // ---------------------------------------------------------------- internals (all called under _gate)

    private (Room? Room, Member? Member, string? Failure) Resolve(RoomCaller caller, string? roomId, bool requireCompetitor, bool requireController)
    {
        if (!ValidCaller(caller) || !Guid.TryParse(roomId, out var id))
        {
            return (null, null, GhostLapFailure.InvalidRequest);
        }
        // A room the caller is not in (unknown, expired, someone else's) is indistinguishable from a closed one.
        if (!_roomBySubject.TryGetValue(caller.Subject, out var ownId) || ownId != id || !_rooms.TryGetValue(id, out var room))
        {
            return (null, null, GhostLapFailure.RoomUnavailable);
        }
        var member = room.MemberOf(caller.Subject);
        if (member is null)
        {
            return (null, null, GhostLapFailure.RoomUnavailable);
        }
        if (requireCompetitor && member.Spectator)
        {
            return (null, null, GhostLapFailure.SpectatorForbidden);
        }
        if (requireController && member.Controller != caller.ConnectionId)
        {
            return (null, null, GhostLapFailure.NotController);
        }
        return (room, member, null);
    }

    private OwnerResult<JoinedRoom> Attach(Room room, Member member, RoomCaller caller, bool isNew)
    {
        var changed = isNew;
        if (!member.Connections.Contains(caller.ConnectionId))
        {
            if (member.Connections.Count >= _options.MaxConnectionsPerMember)
            {
                if (isNew)
                {
                    // Cannot happen for a first connection, but never leave a half-created member behind.
                    RemoveMember(room, member, null);
                }
                return OwnerResult<JoinedRoom>.Fail(GhostLapFailure.TooManyConnections);
            }
            member.Connections.Add(caller.ConnectionId);
        }
        if (member.Spectator)
        {
            _bindings[caller.ConnectionId] = new ConnectionBinding(room.Id, member.MemberId, member.Subject, true, 0, false);
        }
        else
        {
            if (member.Controller != caller.ConnectionId)
            {
                var previous = member.Controller;
                member.Epoch++;
                member.Controller = caller.ConnectionId;
                member.DisconnectedAt = null;
                changed = true;
                if (previous is not null)
                {
                    // The old tab may keep viewing; it can no longer control. Replaced before this call returns.
                    _bindings[previous] = new ConnectionBinding(room.Id, member.MemberId, member.Subject, false, member.Epoch - 1, false);
                    _notices.Writer.TryWrite(new ConnectionNotice(previous, GhostLapFailure.Superseded));
                    NotifyViewer(room, member, previous);
                }
                NotifyController(room, member);
            }
            _bindings[caller.ConnectionId] = new ConnectionBinding(room.Id, member.MemberId, member.Subject, false, member.Epoch, true);
        }
        NotifyViewer(room, member, caller.ConnectionId);
        Touch(room);
        if (changed)
        {
            Bump(room, null);
        }
        return OwnerResult<JoinedRoom>.Success(new JoinedRoom(
            BuildView(room), member.MemberId, member.Spectator ? RoomRoles.Spectator : RoomRoles.Competitor, member.Epoch, room.Code));
    }

    private Member AddMember(Room room, string subject, bool spectator)
    {
        var id = new byte[6];
        string memberId;
        do
        {
            _random.Fill(id);
            memberId = Convert.ToHexStringLower(id);
        }
        while (room.Members.Any(m => m.MemberId == memberId));
        var member = new Member(memberId, subject, spectator, room.NextJoinSequence++);
        room.Members.Add(member);
        return member;
    }

    private void RemoveMember(Room room, Member member, string? cancelNotice)
    {
        var wasHost = room.HostMemberId == member.MemberId;
        foreach (var connection in member.Connections)
        {
            _bindings.TryRemove(connection, out _);
            if (room.MatchId is { } matchId)
            {
                _presence.ViewerDetached(matchId, connection);
            }
        }
        room.Members.Remove(member);
        _roomBySubject.Remove(member.Subject);
        if (room.Members.Count == 0)
        {
            CloseRoom(room, null);
            return;
        }
        string? notice = null;
        if (!member.Spectator)
        {
            ClearReady(room);
            if (room.Phase == RoomPhases.Countdown && !room.AdmissionInFlight)
            {
                CancelCountdownCoreNoBump(room);
                notice = cancelNotice ?? RoomNotices.CountdownCancelledRosterChanged;
            }
            if (wasHost)
            {
                room.HostMemberId = PickHost(room);
                notice ??= RoomNotices.HostChanged;
            }
        }
        Touch(room);
        Bump(room, notice);
    }

    private void TransferHostFromDisconnected(Room room, Member leaving)
    {
        var next = room.Members.Where(m => !m.Spectator && m != leaving && m.Controller is not null).OrderBy(m => m.JoinSequence).FirstOrDefault();
        if (next is not null)
        {
            room.HostMemberId = next.MemberId;
        }
    }

    /// <summary>Tells a running match the slot's current controller. No-op before a match exists.</summary>
    private void NotifyController(Room room, Member member)
    {
        if (room.MatchId is { } matchId && room.Config?.Slots.FirstOrDefault(s => s.MemberId == member.MemberId) is { } slot)
        {
            _presence.ControllerChanged(matchId, new SlotControl(slot.Slot, member.Epoch, member.Controller));
        }
    }

    /// <summary>The recipient description of one open connection of a member, as the match fan-out needs it.</summary>
    private static MatchViewer Viewer(Room room, Member member, string connectionId)
    {
        var slot = member.Spectator ? -1 : room.Config?.Slots.FirstOrDefault(s => s.MemberId == member.MemberId)?.Slot ?? -1;
        return new MatchViewer(connectionId, member.MemberId, member.Spectator || slot < 0, slot, member.Epoch, !member.Spectator && member.Controller == connectionId);
    }

    /// <summary>Tells a running match that a connection is (or changed role as) a recipient. No-op before a match exists.</summary>
    private void NotifyViewer(Room room, Member member, string connectionId)
    {
        if (room.MatchId is { } matchId && member.Connections.Contains(connectionId))
        {
            _presence.ViewerAttached(matchId, Viewer(room, member, connectionId));
        }
    }

    private static SlotControl Control(Room room, SealedSlot slot)
    {
        var member = room.Members.FirstOrDefault(m => m.MemberId == slot.MemberId);
        return new SlotControl(slot.Slot, member?.Epoch ?? -1, member?.Controller);
    }

    private static string PickHost(Room room) =>
        (room.Members.Where(m => !m.Spectator && m.Controller is not null).OrderBy(m => m.JoinSequence).FirstOrDefault()
         ?? room.Members.Where(m => !m.Spectator).OrderBy(m => m.JoinSequence).FirstOrDefault())?.MemberId
        ?? string.Empty;

    private static void ClearReady(Room room)
    {
        foreach (var member in room.Members)
        {
            member.Ready = false;
        }
    }

    private void CancelCountdownCore(Room room, string notice)
    {
        CancelCountdownCoreNoBump(room);
        Bump(room, notice);
    }

    private static void CancelCountdownCoreNoBump(Room room)
    {
        room.Phase = RoomPhases.Lobby;
        room.Config = null;
        room.AdmissionInFlight = false;
        room.CountdownDeadline = 0;
        // RoundOrdinal is deliberately unchanged: the next start re-seals the same seed for the same roster.
    }

    private void CloseRoom(Room room, string? notice)
    {
        room.Phase = RoomPhases.Closed;
        room.Config = null;
        room.AdmissionInFlight = false;
        foreach (var member in room.Members)
        {
            foreach (var connection in member.Connections)
            {
                _bindings.TryRemove(connection, out _);
                _notices.Writer.TryWrite(new ConnectionNotice(connection, GhostLapFailure.RoomUnavailable));
                if (room.MatchId is { } matchId)
                {
                    _presence.ViewerDetached(matchId, connection);
                }
            }
            _roomBySubject.Remove(member.Subject);
        }
        room.Members.Clear();
        _rooms.Remove(room.Id);
        _codes.Remove(room.Code);
        Bump(room, notice ?? RoomNotices.RoomExpired);
    }

    private void Touch(Room room) => room.LastActivity = _time.GetTimestamp();

    private void Bump(Room room, string? notice)
    {
        room.Version++;
        room.Notice = notice;
        _publications.Writer.TryWrite(new RoomPublication(room.Id, room.Version, BuildView(room)));
    }

    private RoomView BuildView(Room room)
    {
        var sealedSlots = room.Config?.Slots.ToDictionary(s => s.MemberId, s => s.Slot);
        var members = room.Members
            .OrderBy(m => m.JoinSequence)
            .Select(m => new RoomMemberView(
                m.MemberId,
                m.Spectator ? RoomRoles.Spectator : RoomRoles.Competitor,
                m.Ready,
                m.Spectator ? m.Connections.Count > 0 : m.Controller is not null,
                m.MemberId == room.HostMemberId,
                sealedSlots is not null && sealedSlots.TryGetValue(m.MemberId, out var slot) ? slot : null))
            .ToArray();
        var remaining = room.Phase == RoomPhases.Countdown && !room.AdmissionInFlight
            ? (int)Math.Max(0, Elapsed(_time.GetTimestamp(), room.CountdownDeadline).Ticks / TimeSpan.TicksPerMillisecond)
            : 0;
        return new RoomView(
            room.Id.ToString("N"), room.Version, room.Phase, _options.MaxCompetitors, _options.MaxSpectators, members, remaining,
            room.Config is null ? null : ToView(room.Config), room.Notice, room.MatchStatus);
    }

    private static SealedRoundView ToView(SealedRoundConfig c) => new(
        c.MatchId.ToString("N"), c.RulesId, c.LayoutId, c.RulesHash, c.LayoutFingerprint, c.Seed.ToString(CultureInfo.InvariantCulture),
        c.RatingAlgorithmId, c.TargetTicks, c.CountdownMs, c.AuthorityGeneration.ToString("N"), c.ConfigDigest,
        [.. c.Slots.Select(s => new SlotAssignmentView(s.Slot, s.MemberId, s.SpawnXFixed >> 10, s.SpawnYFixed >> 10))]);

    private bool TakeLookupToken(string subject)
    {
        // Thousandths of a token; refill is CodeLookupsPerMinute per 60 s, capped at one full bucket (elapsed is capped at 2 min).
        const long milli = 1000;
        var now = _time.GetTimestamp();
        var capacity = _options.CodeLookupsPerMinute * milli;
        if (!_lookups.TryGetValue(subject, out var bucket))
        {
            bucket = new LookupBucket { MilliTokens = capacity, Stamp = now };
            _lookups[subject] = bucket;
        }
        var elapsed = Math.Min(Elapsed(bucket.Stamp, now).Ticks, TimeSpan.TicksPerMinute * 2);
        bucket.MilliTokens = Math.Min(capacity, bucket.MilliTokens + (elapsed * _options.CodeLookupsPerMinute * milli / TimeSpan.TicksPerMinute));
        bucket.Stamp = now;
        if (bucket.MilliTokens < milli)
        {
            return false;
        }
        bucket.MilliTokens -= milli;
        return true;
    }

    private bool CodeExpired(Room room) =>
        Elapsed(room.LastActivity, _time.GetTimestamp()) > TimeSpan.FromSeconds(_options.RoomCodeSeconds);

    private string NewUniqueCode()
    {
        var bytes = new byte[_options.RoomCodeLength];
        for (var attempt = 0; attempt < 16; attempt++)
        {
            _random.Fill(bytes);
            var code = string.Create(bytes.Length, bytes, static (span, b) =>
            {
                for (var i = 0; i < span.Length; i++)
                {
                    span[i] = CodeAlphabet[b[i] & 31];
                }
            });
            if (!_codes.ContainsKey(code))
            {
                return code;
            }
        }
        throw new InvalidOperationException("Could not allocate a unique room code.");
    }

    private bool TryNormalizeCode(string? raw, out string code)
    {
        code = string.Empty;
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > MultiplayerLimits.MaxRoomCodeLength + 4)
        {
            return false;
        }
        var chars = new List<char>(raw.Length);
        foreach (var c in raw.ToUpperInvariant())
        {
            if (c is '-' or ' ')
            {
                continue;
            }
            chars.Add(c switch { 'O' => '0', 'I' or 'L' => '1', _ => c });
        }
        if (chars.Count != _options.RoomCodeLength || chars.Any(c => !CodeAlphabet.Contains(c)))
        {
            return false;
        }
        code = new string([.. chars]);
        return true;
    }

    private bool ConnectionOwnedByOther(RoomCaller caller) =>
        _bindings.TryGetValue(caller.ConnectionId, out var binding) && binding.Subject != caller.Subject;

    private static bool ValidCaller(RoomCaller caller) =>
        !string.IsNullOrEmpty(caller.Subject) && caller.Subject.Length <= MaxSubjectLength && !string.IsNullOrEmpty(caller.ConnectionId);

    private static bool ValidRequestId(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length <= MultiplayerLimits.MaxRequestIdLength && id.All(c => c is > ' ' and < (char)127);

    private long MillisecondsToTimestamp(int ms) => ms * _time.TimestampFrequency / 1000;

    private TimeSpan Elapsed(long from, long to) => _time.GetElapsedTime(from, to);

    private sealed record ConnectionBinding(Guid RoomId, string MemberId, string Subject, bool Spectator, long Epoch, bool Controlling);

    private sealed class LookupBucket
    {
        public long MilliTokens { get; set; }

        public long Stamp { get; set; }
    }

    private sealed class Member(string memberId, string subject, bool spectator, long joinSequence)
    {
        public string MemberId { get; } = memberId;

        public string Subject { get; } = subject;

        public bool Spectator { get; } = spectator;

        public long JoinSequence { get; } = joinSequence;

        public bool Ready { get; set; }

        public string? Controller { get; set; }

        public long Epoch { get; set; }

        public HashSet<string> Connections { get; } = new(StringComparer.Ordinal);

        public long? DisconnectedAt { get; set; }
    }

    private sealed class Room(Guid id, long createdAt, string creatorSubject, string createRequestId)
    {
        public Guid Id { get; } = id;

        public byte[] Secret { get; } = new byte[32];

        public string Code { get; set; } = string.Empty;

        public long Version { get; set; }

        public string Phase { get; set; } = RoomPhases.Lobby;

        public long CreatedAt { get; } = createdAt;

        public long LastActivity { get; set; } = createdAt;

        public long? EmptySince { get; set; }

        public string CreatorSubject { get; } = creatorSubject;

        public string CreateRequestId { get; } = createRequestId;

        public List<Member> Members { get; } = [];

        public long NextJoinSequence { get; set; }

        public string HostMemberId { get; set; } = string.Empty;

        /// <summary>Changes only when a sealed match id turned out terminal before running (and, in task 10.4, on rematch).</summary>
        public int RoundOrdinal { get; set; } = 1;

        /// <summary>The admitted match (receipt committed) of this room, if any.</summary>
        public Guid? MatchId { get; set; }

        public string? MatchStatus { get; set; }

        public SealedRoundConfig? Config { get; set; }

        public long CountdownDeadline { get; set; }

        public bool AdmissionInFlight { get; set; }

        public string? Notice { get; set; }

        public Member? MemberOf(string subject) => Members.FirstOrDefault(m => m.Subject == subject);
    }
}
