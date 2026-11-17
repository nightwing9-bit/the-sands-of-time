extern alias realtime;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using GhostLap.Contracts.Multiplayer;
using GhostLap.Multiplayer;
using Integration.Tests.Support;
using Marrow.Infrastructure.Identity;
using Marrow.Kernel.Realtime;
using Marrow.Kernel.Security;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using realtime::GhostLapHost;
using Xunit;

namespace Integration.Tests.GhostLap;

/// <summary>
/// R10.01, R10.02, R10.03, R10.17, R10.20 (task 10.1) over the REAL Hosts.Realtime composition: real Kestrel-style WebSocket
/// connections, the real shared-hub rate filter, guest cookies issued by the real Hosts.Api against the real identity database.
/// The only substitutions are the two Task 10.2 seams (start-admission port, gameplay sink), which do not exist yet.
/// </summary>
[Collection(IdentityCollection.Name)]
[Trait("Category", "Integration")]
public sealed class GhostLapHubTests(IdentityDatabase db)
{
    internal sealed class CountingSink : IGameplayInputSink
    {
        public ConcurrentQueue<AdmittedInput> Seen { get; } = new();

        public string? Submit(in AdmittedInput input)
        {
            Seen.Enqueue(input);
            return null;
        }
    }

    private sealed class AdmittingPort : IMatchStartAdmission
    {
        public ValueTask<StartAdmissionOutcome> AdmitAsync(SealedRoundConfig config, CancellationToken ct) =>
            ValueTask.FromResult(StartAdmissionOutcome.Admitted);
    }

    internal sealed class Stack : IAsyncDisposable
    {
        public required IdentityApiFactory Api { get; init; }

        public required RealtimeHostFactory Hub { get; init; }

        public async ValueTask DisposeAsync()
        {
            await Hub.DisposeAsync();
            await Api.DisposeAsync();
        }
    }

    internal Stack NewStack(Dictionary<string, string?>? settings = null, Action<IServiceCollection>? customize = null)
    {
        var secret = IdentityApiFactory.NewMasterSecret();
        return new Stack
        {
            Api = new IdentityApiFactory(db, secret),
            Hub = new RealtimeHostFactory(db, secret, settings, customize: customize),
        };
    }

    internal sealed record GuestLogin(TestBrowser Browser, string Cookie, string Subject);

    internal static async Task<GuestLogin> GuestAsync(Stack stack)
    {
        var browser = stack.Api.NewBrowser();
        var start = await browser.PostAsync("/auth/guest/start");
        return new GuestLogin(browser, browser.Cookies[Flows.GuestCookie], start.Str("subject"));
    }

    /// <summary>A typed Ghost Lap client over one real hub connection (one browser tab).</summary>
    internal sealed class Tab : IAsyncDisposable
    {
        private int _req;

        public Tab(HubConnection connection)
        {
            Connection = connection;
            connection.On<InputRejection>(GhostLapWire.InputRejectedClientMethod, r => Rejections.Enqueue(r));
            connection.On(GhostLapWire.ControllerSupersededClientMethod, () => Interlocked.Increment(ref Superseded));
            connection.On(nameof(IGhostLapClient.RoomClosed), () => Interlocked.Increment(ref Closed));
        }

        public HubConnection Connection { get; }

        public ConcurrentQueue<InputRejection> Rejections { get; } = new();

        public int Superseded;

        public int Closed;

        public string Req() => "req-" + Interlocked.Increment(ref _req);

        public Task<GhostLapReply<JoinedRoom>> CreateAsync() =>
            Connection.InvokeAsync<GhostLapReply<JoinedRoom>>(GhostLapWire.CreateRoomMethod, new CreateRoomRequest(Req()));

        public Task<GhostLapReply<JoinedRoom>> JoinAsync(string code, bool spectator = false) =>
            Connection.InvokeAsync<GhostLapReply<JoinedRoom>>(GhostLapWire.JoinRoomMethod, new JoinRoomRequest(Req(), code, spectator));

        public Task<GhostLapReply<RoomCommandResult>> ReadyAsync(string roomId, bool ready = true) =>
            Connection.InvokeAsync<GhostLapReply<RoomCommandResult>>(GhostLapWire.SetReadyMethod, new SetReadyRequest(Req(), roomId, ready));

        public Task<GhostLapReply<RoomCommandResult>> StartAsync(string roomId) =>
            Connection.InvokeAsync<GhostLapReply<RoomCommandResult>>(GhostLapWire.StartCountdownMethod, new RoomCommand(Req(), roomId));

        public Task<GhostLapReply<RoomCommandResult>> CancelAsync(string roomId) =>
            Connection.InvokeAsync<GhostLapReply<RoomCommandResult>>(GhostLapWire.CancelCountdownMethod, new RoomCommand(Req(), roomId));

        public Task<GhostLapReply<RoomCommandResult>> LeaveAsync(string roomId) =>
            Connection.InvokeAsync<GhostLapReply<RoomCommandResult>>(GhostLapWire.LeaveRoomMethod, new RoomCommand(Req(), roomId));

        public Task<GhostLapReply<RoomView>> GetAsync(string roomId) =>
            Connection.InvokeAsync<GhostLapReply<RoomView>>(GhostLapWire.GetRoomMethod, roomId);

        public Task<GhostLapReply<InputStreamSummary>> StreamAsync(ChannelReader<GameplayInput> items) =>
            Connection.InvokeAsync<GhostLapReply<InputStreamSummary>>(GhostLapWire.StreamInputsMethod, items);

        public ValueTask DisposeAsync() => Connection.DisposeAsync();
    }

    internal static async Task<Tab> OpenAsync(
        Stack stack,
        GuestLogin guest,
        string? origin = "http://localhost",
        Func<System.Net.WebSockets.WebSocket, System.Net.WebSockets.WebSocket>? wrapSocket = null,
        Action<HubConnection>? configure = null)
    {
        var tab = new Tab(stack.Hub.ConnectTo(GhostLapWire.HubPath, guestCookie: guest.Cookie, origin: origin, wrapSocket: wrapSocket));
        configure?.Invoke(tab.Connection);
        await tab.Connection.StartAsync();
        return tab;
    }

    /// <summary>Polls GetRoom slowly enough to stay inside the shared hub filter's 10/s budget (a throttled reply is simply retried).</summary>
    private static async Task<RoomView> WaitForPhaseAsync(Tab tab, string roomId, string phase, int timeoutMs = 15_000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < until)
        {
            var reply = await tab.GetAsync(roomId);
            if (reply.Ok && reply.Value!.Phase == phase)
            {
                return reply.Value;
            }
            await Task.Delay(250);
        }
        throw new TimeoutException("Timed out waiting for phase " + phase);
    }

    private static GameplayInput Item(string matchId, long epoch, int seq, sbyte x = 1, sbyte y = 0, bool burst = false) =>
        new(matchId, epoch, seq, seq, x, y, burst);

    // ---- gameplay ingress protocol proof (the BLOCKED-or-not question) ------------------------------------

    [Fact]
    public async Task Gameplay_stream_carries_30Hz_rapid_turns_burst_and_keepalives_without_any_throttling_by_the_shared_filter()
    {
        var sink = new CountingSink();
        await using var stack = NewStack(customize: s => s.Replace(ServiceDescriptor.Singleton<IGameplayInputSink>(sink)));
        var host = await GuestAsync(stack);
        await using var tab = await OpenAsync(stack, host);
        var created = (await tab.CreateAsync()).Value!;

        var channel = Channel.CreateUnbounded<GameplayInput>();
        var matchId = Guid.NewGuid().ToString("N");
        var stream = tab.StreamAsync(channel.Reader);
        var sent = 0;
        var seq = 0;
        var lobbyReplies = new List<GhostLapReply<RoomView>>();
        var clock = Stopwatch.StartNew();

        // Phase 1 (6 s): 30 Hz movement state, a keepalive of the current state every 4th tick, a Burst edge once a second.
        // Phase 2 (3 s): rapid legal direction changes, two per tick (60 items/s), alternating through all eight directions.
        for (var tick = 0; tick < 270; tick++)
        {
            var due = TimeSpan.FromSeconds(tick / 30.0) - clock.Elapsed;
            if (due > TimeSpan.Zero)
            {
                await Task.Delay(due);
            }
            var dir = tick < 180 ? (sbyte)1 : (sbyte)(tick % 3 - 1);
            await channel.Writer.WriteAsync(Item(matchId, created.ControllerEpoch, ++seq, dir, 0, burst: tick % 30 == 0));
            sent++;
            if (tick < 180 && tick % 4 == 0)
            {
                await channel.Writer.WriteAsync(Item(matchId, created.ControllerEpoch, ++seq, dir, 0));
                sent++;
            }
            if (tick >= 180)
            {
                await channel.Writer.WriteAsync(Item(matchId, created.ControllerEpoch, ++seq, (sbyte)(-dir), (sbyte)(tick % 2)));
                sent++;
            }
            if (tick % 8 == 0)
            {
                // Lobby invocations on the same connection keep working while the stream is open.
                lobbyReplies.Add(await tab.GetAsync(created.View.RoomId));
            }
        }
        channel.Writer.Complete();
        var summary = await stream;
        var elapsed = clock.Elapsed.TotalSeconds;

        Assert.True(summary.Ok, summary.Code);
        Assert.Equal(sent, summary.Value!.Received);
        Assert.Equal(0, summary.Value.Rejected);
        Assert.Equal("completed", summary.Value.EndReason);
        Assert.Equal(sent, sink.Seen.Count);
        Assert.Empty(tab.Rejections);
        Assert.All(lobbyReplies, r => Assert.True(r.Ok, r.Code));
        Assert.Equal(Enumerable.Range(1, sent), sink.Seen.Select(i => i.Input.Seq)); // in order, none lost
        Assert.All(sink.Seen, i => Assert.Equal((created.MemberId, created.ControllerEpoch), (i.MemberId, i.ControllerEpoch)));
        Assert.True(sent / elapsed > 40, $"sustained {sent / elapsed:F1} items/s");
        // Evidence for the log: sustained rate, well above the shared filter's 10/s.
        Console.WriteLine($"INGRESS-PROOF sent={sent} seconds={elapsed:F2} rate={sent / elapsed:F1}/s throttled=0");
    }

    [Fact]
    public async Task The_same_traffic_as_per_item_invocations_is_throttled_by_the_shared_filter_with_a_parseable_reply()
    {
        await using var stack = NewStack();
        var host = await GuestAsync(stack);
        await using var tab = await OpenAsync(stack, host);
        var created = (await tab.CreateAsync()).Value!;
        var codes = new List<string?>();
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < 90; i++) // 30 invocations per second for 3 s
        {
            var due = TimeSpan.FromSeconds(i / 30.0) - clock.Elapsed;
            if (due > TimeSpan.Zero)
            {
                await Task.Delay(due);
            }
            codes.Add((await tab.GetAsync(created.View.RoomId)).Code);
        }
        // The pre-existing global filter (10/s, burst 20) applies to every hub invocation, including this hub's: that is why
        // gameplay cannot be an invocation per item. Its reply deserializes into the Ghost Lap reply type (ok/code are shared).
        Assert.Contains(GhostLapFailure.RateLimited, codes);
        Assert.Equal(20, codes.Take(20).Count(c => c is null)); // the burst allowance passes
        Assert.InRange(codes.Count(c => c is null), 20, 60);
    }

    [Fact]
    public async Task A_flood_on_the_gameplay_stream_is_bounded_by_the_Ghost_Lap_limit_and_then_cut()
    {
        var sink = new CountingSink();
        await using var stack = NewStack(customize: s => s.Replace(ServiceDescriptor.Singleton<IGameplayInputSink>(sink)));
        var host = await GuestAsync(stack);
        await using var tab = await OpenAsync(stack, host);
        var created = (await tab.CreateAsync()).Value!;
        var channel = Channel.CreateUnbounded<GameplayInput>();
        var stream = tab.StreamAsync(channel.Reader);
        var matchId = Guid.NewGuid().ToString("N");
        var clock = Stopwatch.StartNew();
        for (var i = 1; i <= 5000; i++)
        {
            await channel.Writer.WriteAsync(Item(matchId, created.ControllerEpoch, i));
        }
        channel.Writer.Complete();
        InputStreamSummary? summary = null;
        try
        {
            summary = (await stream).Value;
        }
        catch (Exception)
        {
            // The server closed the connection; that is the other acceptable end.
        }
        var seconds = clock.Elapsed.TotalSeconds;
        var options = new MultiplayerOptions();
        Assert.True(sink.Seen.Count <= options.InputBurst + (options.InputsPerSecond * seconds) + 20, $"{sink.Seen.Count} items reached the sink in {seconds:F2}s");
        Assert.True(sink.Seen.Count < 5000);
        Assert.True(summary is null || summary.EndReason == "rate_limit_exceeded", summary?.EndReason);
        await Eventually.TrueAsync(() => tab.Connection.State != HubConnectionState.Connected, "the flooding connection is closed");
    }

    [Fact]
    public async Task Revenant_and_shared_hub_rate_limits_are_untouched_by_the_Ghost_Lap_registration()
    {
        await using var stack = NewStack();
        var options = stack.Hub.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Marrow.Infrastructure.Realtime.MarrowRealtimeOptions>>().Value;
        Assert.Equal((10d, 20, 8L * 1024), (options.InvocationsPerSecond, options.InvocationBurst, options.MaxInboundMessageBytes));
        // The shared hub still throttles per invocation exactly as before (see also RealtimeHubTests.Invocation_rate_...).
        var host = await GuestAsync(stack);
        stack.Hub.Domain.AddMember(host.Subject, PrincipalKind.Guest, "r");
        stack.Hub.Streams.Create("probe:r");
        await using var shared = stack.Hub.Connect(guestCookie: host.Cookie);
        await shared.StartAsync();
        var codes = new List<string?>();
        for (var i = 0; i < 40; i++)
        {
            codes.Add((await shared.InvokeAsync<RealtimeReply>(RealtimeWire.GetSnapshotMethod, "probe:r")).Code);
        }
        Assert.Contains(RealtimeFailure.RateLimited, codes);
    }

    // ---- controller epoch / two tabs --------------------------------------------------------------------

    [Fact]
    public async Task A_second_tab_takes_over_and_the_old_controller_cannot_submit_gameplay_or_lobby_commands()
    {
        var sink = new CountingSink();
        await using var stack = NewStack(customize: s => s.Replace(ServiceDescriptor.Singleton<IGameplayInputSink>(sink)));
        var host = await GuestAsync(stack);
        await using var tab1 = await OpenAsync(stack, host);
        await using var tab2 = await OpenAsync(stack, host);
        var created = (await tab1.CreateAsync()).Value!;
        var matchId = Guid.NewGuid().ToString("N");

        var channel1 = Channel.CreateUnbounded<GameplayInput>();
        var stream1 = tab1.StreamAsync(channel1.Reader);
        await channel1.Writer.WriteAsync(Item(matchId, created.ControllerEpoch, 1));
        await Eventually.TrueAsync(() => sink.Seen.Count == 1, "the first controller's input is admitted");

        var takeover = (await tab2.JoinAsync(created.RoomCode)).Value!;
        Assert.Equal(created.ControllerEpoch + 1, takeover.ControllerEpoch);
        await Eventually.TrueAsync(() => Volatile.Read(ref tab1.Superseded) == 1, "the old tab is told it lost control");

        // The old controller's in-flight and later items never reach the sink, whatever epoch they claim.
        await channel1.Writer.WriteAsync(Item(matchId, created.ControllerEpoch, 2));
        await channel1.Writer.WriteAsync(Item(matchId, takeover.ControllerEpoch, 3));
        await Eventually.TrueAsync(() => tab1.Rejections.Count == 2, "both old-controller items are rejected");
        Assert.All(tab1.Rejections, r => Assert.Equal(GhostLapFailure.Superseded, r.Code));
        Assert.Single(sink.Seen);
        Assert.Equal(GhostLapFailure.NotController, (await tab1.ReadyAsync(created.View.RoomId)).Code);
        Assert.Equal(GhostLapFailure.NotController, (await tab1.StartAsync(created.View.RoomId)).Code);
        Assert.True((await tab1.GetAsync(created.View.RoomId)).Ok); // viewing is allowed

        var channel2 = Channel.CreateUnbounded<GameplayInput>();
        var stream2 = tab2.StreamAsync(channel2.Reader);
        await channel2.Writer.WriteAsync(Item(matchId, takeover.ControllerEpoch, 4));
        await Eventually.TrueAsync(() => sink.Seen.Count == 2, "the new controller's input is admitted");
        Assert.Equal(takeover.ControllerEpoch, sink.Seen.Last().ControllerEpoch);
        Assert.True((await tab2.ReadyAsync(created.View.RoomId)).Ok);
        channel1.Writer.Complete();
        channel2.Writer.Complete();
        Assert.Equal(2, (await stream1).Value!.Rejected);
        Assert.Equal(0, (await stream2).Value!.Rejected);
    }

    [Fact]
    public async Task Without_a_running_match_gameplay_is_typed_no_running_match_and_a_second_stream_per_connection_is_refused()
    {
        await using var stack = NewStack();
        var host = await GuestAsync(stack);
        await using var tab = await OpenAsync(stack, host);
        var created = (await tab.CreateAsync()).Value!;
        var channel = Channel.CreateUnbounded<GameplayInput>();
        var stream = tab.StreamAsync(channel.Reader);
        var second = Channel.CreateUnbounded<GameplayInput>();
        Assert.Equal(GhostLapFailure.StreamAlreadyOpen, (await tab.StreamAsync(second.Reader)).Code);
        await channel.Writer.WriteAsync(Item(Guid.NewGuid().ToString("N"), created.ControllerEpoch, 1));
        await Eventually.TrueAsync(() => tab.Rejections.Count == 1, "typed rejection arrives");
        Assert.Equal(GhostLapFailure.NoRunningMatch, tab.Rejections.Single().Code);
        channel.Writer.Complete();
        second.Writer.Complete();
        Assert.Equal(1, (await stream).Value!.Rejected);
    }

    // ---- identity, roles, Origin ----------------------------------------------------------------------------

    [Fact]
    public async Task Foreign_identities_spectators_staff_and_missing_credentials_cannot_act_over_the_real_hub()
    {
        await using var stack = NewStack();
        var host = await GuestAsync(stack);
        var outsider = await GuestAsync(stack);
        var watcher = await GuestAsync(stack);
        await using var hostTab = await OpenAsync(stack, host);
        await using var outsiderTab = await OpenAsync(stack, outsider);
        await using var watcherTab = await OpenAsync(stack, watcher);
        var created = (await hostTab.CreateAsync()).Value!;
        var spectating = (await watcherTab.JoinAsync(created.RoomCode, spectator: true)).Value!;
        Assert.Equal(RoomRoles.Spectator, spectating.Role);

        Assert.Equal(GhostLapFailure.RoomUnavailable, (await outsiderTab.StartAsync(created.View.RoomId)).Code);
        Assert.Equal(GhostLapFailure.RoomUnavailable, (await outsiderTab.GetAsync(created.View.RoomId)).Code);
        Assert.Equal(GhostLapFailure.SpectatorForbidden, (await watcherTab.ReadyAsync(created.View.RoomId)).Code);
        Assert.Equal(GhostLapFailure.SpectatorForbidden, (await watcherTab.StartAsync(created.View.RoomId)).Code);
        Assert.True((await watcherTab.GetAsync(created.View.RoomId)).Ok);

        // A spectator's gameplay stream is rejected at the transport boundary with the typed reason.
        var channel = Channel.CreateUnbounded<GameplayInput>();
        var stream = watcherTab.StreamAsync(channel.Reader);
        await channel.Writer.WriteAsync(Item(Guid.NewGuid().ToString("N"), 0, 1));
        await Eventually.TrueAsync(() => watcherTab.Rejections.Count == 1, "spectator input rejected");
        Assert.Equal(GhostLapFailure.SpectatorForbidden, watcherTab.Rejections.Single().Code);
        channel.Writer.Complete();
        await stream;

        // Staff tokens, no credentials and a cookie signed by another deployment cannot even connect.
        var staff = await stack.Api.CreateStaffAsync(IdentityRoles.Operator);
        var (_, access, _) = await Flows.LoginAsync(stack.Api, staff);
        await Assert.ThrowsAnyAsync<Exception>(() => stack.Hub.ConnectTo(GhostLapWire.HubPath, accessToken: access, origin: null).StartAsync());
        await Assert.ThrowsAnyAsync<Exception>(() => stack.Hub.ConnectTo(GhostLapWire.HubPath).StartAsync());
        await Assert.ThrowsAnyAsync<Exception>(() => stack.Hub.ConnectTo(GhostLapWire.HubPath, guestCookie: host.Cookie[..^4] + "AAAA").StartAsync());
    }

    [Fact]
    public async Task The_Ghost_Lap_hub_checks_Origin_separately_from_the_shared_hub()
    {
        await using var stack = NewStack(new() { ["GhostLap:Multiplayer:AllowedOrigins:0"] = "https://game.sands.test" });
        var guest = await GuestAsync(stack);
        Assert.Equal("https://play.sands.test", RealtimeHostFactory.AllowedExtraOrigin);

        // Allowed for the identity/shared hub (Identity:AllowedOrigins) but NOT for the game hub.
        await using (var shared = stack.Hub.Connect(guestCookie: guest.Cookie, origin: RealtimeHostFactory.AllowedExtraOrigin))
        {
            await shared.StartAsync();
        }
        await Assert.ThrowsAnyAsync<Exception>(() =>
            stack.Hub.ConnectTo(GhostLapWire.HubPath, guestCookie: guest.Cookie, origin: RealtimeHostFactory.AllowedExtraOrigin).StartAsync());
        await Assert.ThrowsAnyAsync<Exception>(() => stack.Hub.ConnectTo(GhostLapWire.HubPath, guestCookie: guest.Cookie, origin: "https://evil.example").StartAsync());
        await Assert.ThrowsAnyAsync<Exception>(() => stack.Hub.ConnectTo(GhostLapWire.HubPath, guestCookie: guest.Cookie, origin: null).StartAsync());
        await Assert.ThrowsAnyAsync<Exception>(() => stack.Hub.ConnectTo(GhostLapWire.HubPath, guestCookie: guest.Cookie, origin: "null").StartAsync());

        await using var own = await OpenAsync(stack, guest); // the request's own origin
        await using var configured = new Tab(stack.Hub.ConnectTo(GhostLapWire.HubPath, guestCookie: guest.Cookie, origin: "https://game.sands.test"));
        await configured.Connection.StartAsync();
        Assert.Contains(stack.Hub.Logs.Lines, l => l.EventId == 4104);
    }

    [Fact]
    public async Task One_principal_cannot_hold_more_hub_connections_than_the_configured_cap()
    {
        await using var stack = NewStack(new() { ["GhostLap:Multiplayer:MaxConnectionsPerPrincipal"] = "2" });
        var guest = await GuestAsync(stack);
        await using var a = await OpenAsync(stack, guest);
        await using var b = await OpenAsync(stack, guest);
        await using var c = new Tab(stack.Hub.ConnectTo(GhostLapWire.HubPath, guestCookie: guest.Cookie));
        await c.Connection.StartAsync();
        await Eventually.TrueAsync(() => c.Connection.State != HubConnectionState.Connected, "the third connection is closed");
        Assert.Equal(HubConnectionState.Connected, a.Connection.State);
    }

    // ---- revocation -----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_revoked_guest_session_is_denied_at_the_next_command_and_closed_by_the_revalidator_and_leaves_the_room_stream()
    {
        const int intervalSeconds = 1;
        await using var stack = NewStack(new() { ["Realtime:AccessRevalidationSeconds"] = intervalSeconds.ToString() });
        var host = await GuestAsync(stack);
        var idle = await GuestAsync(stack);
        var control = await GuestAsync(stack);
        await using var hostTab = await OpenAsync(stack, host);
        await using var idleTab = await OpenAsync(stack, idle);
        await using var controlTab = await OpenAsync(stack, control);
        var created = (await hostTab.CreateAsync()).Value!;
        Assert.True((await idleTab.JoinAsync(created.RoomCode)).Ok);
        Assert.True((await controlTab.JoinAsync(created.RoomCode, spectator: true)).Ok);

        // The same guest's room stream on the shared hub.
        var streamId = GhostLapWire.RoomStreamId(Guid.Parse(created.View.RoomId));
        var received = new ConcurrentQueue<JsonElement>();
        await using var sharedIdle = stack.Hub.Connect(guestCookie: idle.Cookie);
        sharedIdle.On<JsonElement>(RealtimeWire.ReceiveMethod, e => received.Enqueue(e.Clone()));
        await sharedIdle.StartAsync();
        Assert.True((await sharedIdle.InvokeAsync<RealtimeReply>(RealtimeWire.SubscribeMethod, streamId)).Ok);

        // The host signs out on the API host. The very next lobby command on the open connection is refused and closes it.
        Assert.Equal(System.Net.HttpStatusCode.NoContent, (await host.Browser.PostAsync("/auth/guest/signout", csrfCookie: Flows.GuestCsrf)).Status);
        // The server aborts the connection while answering, so the client sees either the typed refusal or the close itself; never success.
        try
        {
            Assert.Equal(GhostLapFailure.Forbidden, (await hostTab.ReadyAsync(created.View.RoomId)).Code);
        }
        catch (Exception e) when (e is TaskCanceledException or InvalidOperationException or OperationCanceledException or IOException)
        {
        }
        await Eventually.TrueAsync(() => hostTab.Connection.State != HubConnectionState.Connected, "the revoked connection is aborted");

        // An idle revoked connection is found by the periodic revalidation within its documented bound.
        var clock = Stopwatch.StartNew();
        Assert.Equal(System.Net.HttpStatusCode.NoContent, (await idle.Browser.PostAsync("/auth/guest/signout", csrfCookie: Flows.GuestCsrf)).Status);
        var boundMs = (intervalSeconds + GhostLapAccessRevalidator.CheckTimeoutSeconds) * 1000 + 2000;
        await Eventually.TrueAsync(() => idleTab.Connection.State != HubConnectionState.Connected, "the idle revoked connection is closed", boundMs);
        Assert.True(clock.ElapsedMilliseconds < boundMs, $"revocation took {clock.ElapsedMilliseconds} ms");
        await Eventually.TrueAsync(() => sharedIdle.State != HubConnectionState.Connected, "the shared-hub connection of the same session is closed", boundMs);
        Assert.Equal(HubConnectionState.Connected, controlTab.Connection.State);

        // The revoked credentials cannot open a new game connection.
        await Assert.ThrowsAnyAsync<Exception>(() => stack.Hub.ConnectTo(GhostLapWire.HubPath, guestCookie: host.Cookie).StartAsync());
        Assert.Contains(stack.Hub.Logs.Lines, l => l.EventId == 4102);
    }

    // ---- room stream permissions (read-only shared-hub stream) ----------------------------------------------

    [Fact]
    public async Task The_room_stream_is_readable_by_members_and_spectators_only_and_its_sequence_is_the_lifecycle_version()
    {
        await using var stack = NewStack(new() { ["Realtime:AccessRevalidationSeconds"] = "1" });
        var host = await GuestAsync(stack);
        var joiner = await GuestAsync(stack);
        var watcher = await GuestAsync(stack);
        var outsider = await GuestAsync(stack);
        await using var hostTab = await OpenAsync(stack, host);
        var created = (await hostTab.CreateAsync()).Value!;
        var streamId = GhostLapWire.RoomStreamId(Guid.Parse(created.View.RoomId));

        async Task<(HubConnection Connection, ConcurrentQueue<JsonElement> Events)> SharedAsync(GuestLogin guest)
        {
            var events = new ConcurrentQueue<JsonElement>();
            var connection = stack.Hub.Connect(guestCookie: guest.Cookie);
            connection.On<JsonElement>(RealtimeWire.ReceiveMethod, e => events.Enqueue(e.Clone()));
            await connection.StartAsync();
            return (connection, events);
        }

        var (hostShared, hostEvents) = await SharedAsync(host);
        var snapshot = await hostShared.InvokeAsync<RealtimeReply>(RealtimeWire.SubscribeMethod, streamId);
        Assert.True(snapshot.Ok, snapshot.Code);
        Assert.Equal(created.View.Version.ToString(System.Globalization.CultureInfo.InvariantCulture), snapshot.Snapshot!.Sequence);
        Assert.Equal(RoomPhases.Lobby, snapshot.Snapshot.Payload.GetProperty("phase").GetString());
        var json = snapshot.Snapshot.Payload.GetRawText();
        Assert.DoesNotContain("guest:", json, StringComparison.Ordinal);
        Assert.DoesNotContain(created.RoomCode, json, StringComparison.Ordinal);

        // Non-members (an authenticated guest who is not in the room) and staff are denied exactly like an unknown stream.
        var (outsiderShared, _) = await SharedAsync(outsider);
        Assert.Equal(RealtimeFailure.Forbidden, (await outsiderShared.InvokeAsync<RealtimeReply>(RealtimeWire.SubscribeMethod, streamId)).Code);
        Assert.Equal(RealtimeFailure.Forbidden, (await outsiderShared.InvokeAsync<RealtimeReply>(RealtimeWire.SubscribeMethod, "ghostlap-room:" + Guid.NewGuid().ToString("N"))).Code);
        Assert.Equal(RealtimeFailure.Forbidden, (await outsiderShared.InvokeAsync<RealtimeReply>(RealtimeWire.SubscribeMethod, "ghostlap-admin:" + created.View.RoomId)).Code);
        var staff = await stack.Api.CreateStaffAsync(IdentityRoles.Supervisor);
        var (_, access, _) = await Flows.LoginAsync(stack.Api, staff);
        await using var staffShared = stack.Hub.Connect(accessToken: access, origin: null);
        await staffShared.StartAsync();
        Assert.Equal(RealtimeFailure.Forbidden, (await staffShared.InvokeAsync<RealtimeReply>(RealtimeWire.SubscribeMethod, streamId)).Code);

        // Members and spectators receive the events, in version order, with no principal ids.
        await using var joinerTab = await OpenAsync(stack, joiner);
        await using var watcherTab = await OpenAsync(stack, watcher);
        Assert.True((await joinerTab.JoinAsync(created.RoomCode)).Ok);
        Assert.True((await watcherTab.JoinAsync(created.RoomCode, spectator: true)).Ok);
        var (watcherShared, watcherEvents) = await SharedAsync(watcher);
        Assert.True((await watcherShared.InvokeAsync<RealtimeReply>(RealtimeWire.SubscribeMethod, streamId)).Ok);
        Assert.True((await joinerTab.ReadyAsync(created.View.RoomId)).Ok);
        await Eventually.TrueAsync(() => hostEvents.Count >= 3 && !watcherEvents.IsEmpty, "events are delivered");
        var sequences = hostEvents.Select(e => long.Parse(e.GetProperty("sequence").GetString()!, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        Assert.Equal(sequences.Order(), sequences);
        Assert.Equal(sequences.Distinct(), sequences);
        Assert.All(hostEvents.Concat(watcherEvents), e => Assert.DoesNotContain("guest:", e.GetRawText(), StringComparison.Ordinal));

        // A member who leaves stops receiving the stream within the revalidation bound.
        await joinerTab.LeaveAsync(created.View.RoomId);
        var (joinerShared, joinerEvents) = await SharedAsync(joiner);
        Assert.Equal(RealtimeFailure.Forbidden, (await joinerShared.InvokeAsync<RealtimeReply>(RealtimeWire.SubscribeMethod, streamId)).Code);
        Assert.Empty(joinerEvents);
        await hostShared.DisposeAsync();
        await watcherShared.DisposeAsync();
        await outsiderShared.DisposeAsync();
        await joinerShared.DisposeAsync();
    }

    // ---- the full lobby flow over real sockets (2/3/4 sockets, countdown, handoff) ---------------------------

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task A_lobby_of_n_real_clients_counts_down_and_when_no_receipt_can_be_committed_returns_to_a_retryable_lobby(int players)
    {
        // Since task 10.2 production commits a receipt; this test pins the "no receipt" path with the honest unavailable port.
        await using var stack = NewStack(
            new() { ["GhostLap:Multiplayer:CountdownMilliseconds"] = "1000" },
            s => s.Replace(ServiceDescriptor.Singleton<IMatchStartAdmission>(new UnavailableStartAdmission())));
        var logins = new List<GuestLogin>();
        var tabs = new List<Tab>();
        for (var i = 0; i < players; i++)
        {
            var guest = await GuestAsync(stack);
            logins.Add(guest);
            tabs.Add(await OpenAsync(stack, guest));
        }
        try
        {
            var created = (await tabs[0].CreateAsync()).Value!;
            for (var i = 1; i < players; i++)
            {
                Assert.True((await tabs[i].JoinAsync(created.RoomCode)).Ok);
            }
            foreach (var tab in tabs)
            {
                Assert.True((await tab.ReadyAsync(created.View.RoomId)).Ok);
            }
            var started = await tabs[0].StartAsync(created.View.RoomId);
            Assert.True(started.Ok, started.Code);
            var round = started.Value!.View.Round!;
            Assert.Equal(players, round.Slots.Count);
            Assert.Equal(players, round.Slots.Select(s => s.MemberId).Distinct().Count());

            // No further competitor can be admitted; a spectator can watch; duplicate starts return the same sealed round.
            var late = await GuestAsync(stack);
            await using var lateTab = await OpenAsync(stack, late);
            Assert.Equal(GhostLapFailure.JoinClosed, (await lateTab.JoinAsync(created.RoomCode)).Code);
            Assert.True((await lateTab.JoinAsync(created.RoomCode, spectator: true)).Ok);
            var again = await tabs[0].StartAsync(created.View.RoomId);
            Assert.Equal(round.ConfigDigest, again.Value!.View.Round!.ConfigDigest);

            // No receipt was committed: the room must not claim a running match.
            var back = await WaitForPhaseAsync(tabs[0], created.View.RoomId, RoomPhases.Lobby);
            Assert.Equal(RoomNotices.StartUnavailable, back.Notice);
            Assert.All(back.Members.Where(m => m.Role == RoomRoles.Competitor), m => Assert.True(m.Ready));
            var restart = (await tabs[0].StartAsync(created.View.RoomId)).Value!.View.Round!;
            Assert.Equal(round.ConfigDigest, restart.ConfigDigest); // restart re-seals the identical round
        }
        finally
        {
            foreach (var tab in tabs)
            {
                await tab.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task With_a_start_admission_port_the_countdown_ends_in_the_handoff_phase_never_a_running_match()
    {
        await using var stack = NewStack(
            new() { ["GhostLap:Multiplayer:CountdownMilliseconds"] = "1000" },
            s => s.Replace(ServiceDescriptor.Singleton<IMatchStartAdmission>(new AdmittingPort())));
        var a = await GuestAsync(stack);
        var b = await GuestAsync(stack);
        await using var tabA = await OpenAsync(stack, a);
        await using var tabB = await OpenAsync(stack, b);
        var created = (await tabA.CreateAsync()).Value!;
        await tabB.JoinAsync(created.RoomCode);
        await tabA.ReadyAsync(created.View.RoomId);
        await tabB.ReadyAsync(created.View.RoomId);
        Assert.True((await tabA.StartAsync(created.View.RoomId)).Ok);
        var view = await WaitForPhaseAsync(tabB, created.View.RoomId, RoomPhases.StartAdmitted);
        Assert.Equal(RoomNotices.StartAdmitted, view.Notice);
        Assert.DoesNotContain("running", view.Phase, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(GhostLapFailure.JoinClosed, (await (await OpenAsync(stack, await GuestAsync(stack))).JoinAsync(created.RoomCode)).Code);
    }
}
