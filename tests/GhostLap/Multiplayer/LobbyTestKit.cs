using GhostLap.Contracts.Multiplayer;
using GhostLap.Multiplayer;
using Xunit;

namespace GhostLap.Tests.Multiplayer;

/// <summary>Manually advanced monotonic clock. The owner only uses GetTimestamp, so tests control every deadline exactly.</summary>
internal sealed class ManualTime : TimeProvider
{
    private long _ticks;

    public override long TimestampFrequency => 1000;

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, (long)by.TotalMilliseconds);
}

internal sealed class RecordingAdmission(StartAdmissionOutcome outcome) : IMatchStartAdmission
{
    private int _calls;
    private readonly List<SealedRoundConfig> _configs = [];

    public StartAdmissionOutcome Outcome { get; set; } = outcome;

    public int Calls => Volatile.Read(ref _calls);

    public IReadOnlyList<SealedRoundConfig> Configs
    {
        get
        {
            lock (_configs)
            {
                return [.. _configs];
            }
        }
    }

    public ValueTask<StartAdmissionOutcome> AdmitAsync(SealedRoundConfig config, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        lock (_configs)
        {
            _configs.Add(config);
        }
        return ValueTask.FromResult(Outcome);
    }
}

internal sealed class CapturingSink : IGameplayInputSink
{
    public List<AdmittedInput> Seen { get; } = [];

    public string? Submit(in AdmittedInput input)
    {
        lock (Seen)
        {
            Seen.Add(input);
        }
        return null;
    }
}

/// <summary>One guest: a verified subject and a connection id, as the hub would supply them.</summary>
internal sealed class Guest(string name)
{
    private int _conn;

    public string Subject { get; } = "guest:" + Guid.NewGuid().ToString("D");

    public string Name { get; } = name;

    public RoomCaller On(int tab = 1) => new(Subject, $"{Name}-conn-{tab}-{_conn}");

    /// <summary>A connection that reconnects gets a fresh transport id.</summary>
    public RoomCaller Reconnect(int tab = 1)
    {
        _conn++;
        return On(tab);
    }
}

internal sealed class Rig
{
    public Rig(Action<MultiplayerOptions>? configure = null, StartAdmissionOutcome admission = StartAdmissionOutcome.Admitted, IGameplayInputSink? sink = null)
    {
        Options = new MultiplayerOptions();
        configure?.Invoke(Options);
        Admission = new RecordingAdmission(admission);
        Owner = new ActiveRoomOwner(Options, Time, Admission, sink ?? new NoRunningMatchSink(), new CryptoRoomRandom(), Guid.NewGuid());
    }

    public ManualTime Time { get; } = new();

    public MultiplayerOptions Options { get; }

    public RecordingAdmission Admission { get; }

    public ActiveRoomOwner Owner { get; }

    private int _req;

    public string Req() => "r" + Interlocked.Increment(ref _req);

    public JoinedRoom Create(Guest host, int tab = 1) => Ok(Owner.CreateRoom(host.On(tab), new CreateRoomRequest(Req())));

    public JoinedRoom Join(Guest g, JoinedRoom room, bool spectator = false, int tab = 1) =>
        Ok(Owner.JoinRoom(g.On(tab), new JoinRoomRequest(Req(), room.RoomCode, spectator)));

    public OwnerResult<RoomCommandResult> Ready(Guest g, JoinedRoom room, bool ready = true, int tab = 1) =>
        Owner.SetReady(g.On(tab), new SetReadyRequest(Req(), room.View.RoomId, ready));

    public OwnerResult<RoomCommandResult> Start(Guest g, JoinedRoom room, long? expected = null, int tab = 1) =>
        Owner.StartCountdown(g.On(tab), new RoomCommand(Req(), room.View.RoomId, expected));

    public OwnerResult<RoomCommandResult> Cancel(Guest g, JoinedRoom room, int tab = 1) =>
        Owner.CancelCountdown(g.On(tab), new RoomCommand(Req(), room.View.RoomId));

    public OwnerResult<RoomCommandResult> Leave(Guest g, JoinedRoom room, int tab = 1) =>
        Owner.LeaveRoom(g.On(tab), new RoomCommand(Req(), room.View.RoomId));

    public RoomView View(Guest g, JoinedRoom room, int tab = 1) => Ok(Owner.GetRoom(g.On(tab), room.View.RoomId));

    /// <summary>A lobby with <paramref name="competitors"/> connected, ready players. The first guest is the host.</summary>
    public (JoinedRoom Room, Guest[] Guests) ReadyRoom(int competitors)
    {
        var guests = Enumerable.Range(0, competitors).Select(i => new Guest("p" + i)).ToArray();
        var room = Create(guests[0]);
        for (var i = 1; i < competitors; i++)
        {
            Join(guests[i], room);
        }
        foreach (var g in guests)
        {
            Assert.True(Ready(g, room).Ok);
        }
        return (room, guests);
    }

    public async Task ElapseCountdownAsync()
    {
        Time.Advance(TimeSpan.FromMilliseconds(Options.CountdownMilliseconds + 1));
        await Owner.SweepAsync(CancellationToken.None);
    }

    public static T Ok<T>(OwnerResult<T> result)
    {
        Assert.True(result.Ok, "expected success but got " + result.Code);
        return result.Value!;
    }
}
