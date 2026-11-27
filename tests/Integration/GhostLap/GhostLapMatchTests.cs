extern alias realtime;
using System.Collections.Concurrent;
using System.Threading.Channels;
using GhostLap.Contracts.Multiplayer;
using GhostLap.Multiplayer;
using GhostLap.Rules;
using Integration.Tests.Support;
using Marrow.Infrastructure.Realtime;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using realtime::GhostLapHost;
using Xunit;
using Tab = Integration.Tests.GhostLap.GhostLapHubTests.Tab;

namespace Integration.Tests.GhostLap;

/// <summary>
/// Task 10.2 over the REAL Hosts.Realtime composition, real WebSocket clients with guest cookies issued by the real Hosts.Api, real
/// PostgreSQL: receipt before running, one authoritative world per room driven by 2/3/4 sockets, applied-input acknowledgements,
/// spectator and superseded-controller refusal, disconnect neutral, and the authority fences when the guard connection is lost.
/// Nothing is substituted. Clients read the current server tick from the registry to tag inputs because snapshots are task 10.3.
/// </summary>
[Collection(IdentityCollection.Name)]
[Trait("Category", "Integration")]
public sealed class GhostLapMatchTests(IdentityDatabase db)
{
    private readonly GhostLapHubTests _kit = new(db);

    private static readonly Dictionary<string, string?> Fast = new() { ["GhostLap:Multiplayer:CountdownMilliseconds"] = "1000" };

    private sealed class Player(Tab tab, JoinedRoom joined, GhostLapHubTests.GuestLogin login) : IAsyncDisposable
    {
        public Tab Tab { get; } = tab;

        public GhostLapHubTests.GuestLogin Login { get; } = login;

        public JoinedRoom Joined { get; set; } = joined;

        public ConcurrentQueue<InputAck> Acks { get; } = new();

        public Channel<GameplayInput> Inputs { get; } = Channel.CreateUnbounded<GameplayInput>();

        public Task<GhostLapReply<InputStreamSummary>>? Stream { get; set; }

        public int Seq;

        public ValueTask DisposeAsync() => Tab.DisposeAsync();
    }

    private sealed record Started(GhostLapHubTests.Stack Stack, Player[] Players, RoomView View, MatchRuntime Runtime, string RoomCode);

    private static Player Track(Tab tab, JoinedRoom joined, GhostLapHubTests.GuestLogin login)
    {
        var player = new Player(tab, joined, login);
        tab.Connection.On<InputAck>(GhostLapWire.InputsAppliedClientMethod, a => player.Acks.Enqueue(a));
        return player;
    }

    private static async Task<RoomView> WaitForMatchStatusAsync(Tab tab, string roomId, string status, int timeoutMs = 15_000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < until)
        {
            var reply = await tab.GetAsync(roomId);
            if (reply.Ok && reply.Value!.MatchStatus == status)
            {
                return reply.Value;
            }
            await Task.Delay(250);
        }
        throw new TimeoutException("Timed out waiting for match status " + status);
    }

    private async Task<Started> StartAsync(GhostLapHubTests.Stack stack, int players)
    {
        var list = new List<Player>();
        for (var i = 0; i < players; i++)
        {
            var guest = await GhostLapHubTests.GuestAsync(stack);
            var tab = await GhostLapHubTests.OpenAsync(stack, guest);
            list.Add(Track(tab, null!, guest));
        }
        var created = (await list[0].Tab.CreateAsync()).Value!;
        list[0].Joined = created;
        for (var i = 1; i < players; i++)
        {
            var joined = await list[i].Tab.JoinAsync(created.RoomCode);
            Assert.True(joined.Ok, joined.Code);
            list[i].Joined = joined.Value!;
        }
        foreach (var p in list)
        {
            Assert.True((await p.Tab.ReadyAsync(created.View.RoomId)).Ok);
        }
        Assert.True((await list[0].Tab.StartAsync(created.View.RoomId)).Ok);
        var view = await WaitForMatchStatusAsync(list[0].Tab, created.View.RoomId, MatchStatuses.Running);
        Assert.Equal(RoomPhases.StartAdmitted, view.Phase);
        var runtime = stack.Hub.Services.GetRequiredService<MatchRegistry>().Find(Guid.Parse(view.Round!.MatchId))!;
        Assert.NotNull(runtime);
        foreach (var p in list)
        {
            p.Stream = p.Tab.StreamAsync(p.Inputs.Reader);
        }
        return new Started(stack, [.. list], view, runtime, created.RoomCode);
    }

    private static int SlotOf(Started m, Player p) => m.View.Round!.Slots.Single(s => s.MemberId == p.Joined.MemberId).Slot;

    /// <summary>Points from the slot's spawn towards the arena centre, so every slot really moves.</summary>
    private static (sbyte X, sbyte Y) Inward(Started m, int slot)
    {
        var p = m.Runtime.Engine.State.World.Players[slot];
        return ((sbyte)Math.Sign((GameRulesV1.WorldWidthFixed / 2) - p.XFixed), (sbyte)Math.Sign((GameRulesV1.WorldHeightFixed / 2) - p.YFixed));
    }

    private static GameplayInput Next(Started m, Player p, sbyte x, sbyte y, bool burst = false, long? epoch = null) =>
        new(m.View.Round!.MatchId, epoch ?? p.Joined.ControllerEpoch, m.Runtime.Engine.State.Tick + 1, Interlocked.Increment(ref p.Seq), x, y, burst);

    private static async Task Eventually(Func<bool> condition, string what, int timeoutMs = 10_000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > until)
            {
                throw new TimeoutException(what);
            }
            await Task.Delay(50);
        }
    }

    /// <summary>
    /// **Validates: Requirements 10.02, 10.03, 10.04, 10.08, 10.17** n real sockets: the room reports a running match only after its
    /// durable receipt (Started, this process's generation, sealed roster) exists; every controller's inputs move its own slot in the one
    /// authoritative world and come back as applied-input acknowledgements; a spectator's gameplay items are refused.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Real_clients_start_after_the_receipt_commits_and_drive_one_authoritative_world(int players)
    {
        await using var stack = _kit.NewStack(Fast);
        var m = await StartAsync(stack, players);
        try
        {
            var authority = stack.Hub.Services.GetRequiredService<GhostLapRealtimeAuthority>();
            Assert.Equal("Started", await db.ScalarAsync<string>(
                "SELECT terminal_decision FROM ghostlap.match_session WHERE match_id = @m AND generation_id = @g",
                ("m", m.Runtime.Config.MatchId), ("g", authority.GenerationId)));
            Assert.Equal(players, await db.ScalarAsync<int>(
                "SELECT cardinality(roster_subjects) FROM ghostlap.match_session WHERE match_id = @m", ("m", m.Runtime.Config.MatchId)));
            Assert.Equal(authority.GenerationId.ToString("N"), m.View.Round!.AuthorityGeneration);

            var startTick = m.Runtime.Engine.State.Tick;
            var directions = m.Players.Select(p => Inward(m, SlotOf(m, p))).ToArray();
            for (var i = 0; i < 45; i++) // ~1.5 s at 30 Hz, from every controller
            {
                for (var k = 0; k < m.Players.Length; k++)
                {
                    await m.Players[k].Inputs.Writer.WriteAsync(Next(m, m.Players[k], directions[k].X, directions[k].Y));
                }
                await Task.Delay(33);
            }
            foreach (var p in m.Players)
            {
                await Eventually(() => p.Acks.Any(a => a.LastAppliedSeq == p.Seq), $"slot {SlotOf(m, p)} acknowledged up to {p.Seq}");
            }
            var world = m.Runtime.Engine.State.World;
            Assert.True(m.Runtime.Engine.State.Tick > startTick + 30);
            foreach (var p in m.Players)
            {
                var slot = SlotOf(m, p);
                // Movement has no source other than this controller's applied inputs (walls may stop it early; any distance proves it).
                Assert.True(world.Players[slot].DistanceFixed > 0, $"slot {slot} did not move");
                Assert.Empty(p.Tab.Rejections);
                var ticks = p.Acks.Select(a => a.Tick).ToArray();
                Assert.Equal(ticks.Order(), ticks);                                   // monotonically identifiable
                Assert.All(p.Acks, a => Assert.Equal(m.View.Round.MatchId, a.MatchId));
                Assert.All(p.Acks, a => Assert.Equal(p.Joined.ControllerEpoch, a.ControllerEpoch));
            }

            // A spectator's gameplay items never reach the world.
            var watcher = await GhostLapHubTests.GuestAsync(stack);
            await using var watcherTab = await GhostLapHubTests.OpenAsync(stack, watcher);
            Assert.True((await watcherTab.JoinAsync(m.RoomCode, spectator: true)).Ok);
            var items = Channel.CreateUnbounded<GameplayInput>();
            var stream = watcherTab.StreamAsync(items.Reader);
            for (var i = 1; i <= 3; i++)
            {
                await items.Writer.WriteAsync(new GameplayInput(m.View.Round.MatchId, 0, m.Runtime.Engine.State.Tick + 1, i, 1, 0, false));
            }
            items.Writer.Complete();
            Assert.Equal(3, (await stream).Value!.Rejected);
            await Eventually(() => watcherTab.Rejections.Count == 3, "spectator rejections delivered");
            Assert.All(watcherTab.Rejections, r => Assert.Equal(GhostLapFailure.SpectatorForbidden, r.Code));
        }
        finally
        {
            foreach (var p in m.Players)
            {
                p.Inputs.Writer.TryComplete();
                await p.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// **Validates: Requirements 10.03, 10.06** A second tab takes control mid-match: the old tab's items are refused and the new epoch is
    /// acknowledged. Closing a controller's only tab neutralizes its slot on the next tick (no six-tick hold).
    /// </summary>
    [Fact]
    public async Task A_takeover_mid_match_supersedes_the_old_tab_and_a_closed_controller_is_neutralized_at_once()
    {
        await using var stack = _kit.NewStack(Fast);
        var m = await StartAsync(stack, 2);
        try
        {
            var a = m.Players[0];
            var b = m.Players[1];
            var slotA = SlotOf(m, a);
            var slotB = SlotOf(m, b);
            var dirB = Inward(m, slotB);
            await a.Inputs.Writer.WriteAsync(Next(m, a, 1, 0));
            await Eventually(() => !a.Acks.IsEmpty, "first tab acknowledged");

            // Same guest, second tab: a new controller epoch.
            var guestA = await TakeoverTabAsync(stack, m, a);
            Assert.Equal(a.Joined.ControllerEpoch + 1, guestA.Joined.ControllerEpoch);
            await Eventually(() => Volatile.Read(ref a.Tab.Superseded) == 1, "old tab told it was superseded");
            await Eventually(() => m.Runtime.Engine.ControllerOf(slotA).Epoch == guestA.Joined.ControllerEpoch, "match saw the takeover");
            await a.Inputs.Writer.WriteAsync(Next(m, a, -1, 0));
            await Eventually(() => a.Tab.Rejections.Any(r => r.Code == GhostLapFailure.Superseded), "old tab refused");
            guestA.Seq = a.Seq;
            await guestA.Inputs.Writer.WriteAsync(Next(m, guestA, 0, 1));
            await Eventually(() => guestA.Acks.Any(x => x.ControllerEpoch == guestA.Joined.ControllerEpoch && x.LastAppliedSeq == guestA.Seq), "new tab acknowledged");
            Assert.Equal(1, m.Runtime.Engine.State.World.Players[slotA].HeldMoveY);

            // B moves, then its only tab closes: the next tick applies the server neutral.
            await b.Inputs.Writer.WriteAsync(Next(m, b, dirB.X, dirB.Y));
            await Eventually(() => !b.Acks.IsEmpty, "B acknowledged");
            b.Inputs.Writer.Complete();
            await b.DisposeAsync();
            await Eventually(() => m.Runtime.Engine.ControllerOf(slotB).ConnectionId is null, "match saw the disconnect");
            var seen = m.Runtime.Engine.State.Tick;
            await Eventually(() => m.Runtime.Engine.State.Tick >= seen + 2, "two more ticks");
            var pb = m.Runtime.Engine.State.World.Players[slotB];
            Assert.Equal((0, 0, 0L, false), ((int)pb.HeldMoveX, (int)pb.HeldMoveY, pb.HoldUntilTick, pb.BurstLatch));
            await guestA.DisposeAsync();
        }
        finally
        {
            foreach (var p in m.Players)
            {
                p.Inputs.Writer.TryComplete();
                await p.DisposeAsync();
            }
        }
    }

    /// <summary>The same guest opens a second tab (same session cookie) and re-attaches: the server makes it the controller.</summary>
    private static async Task<Player> TakeoverTabAsync(GhostLapHubTests.Stack stack, Started m, Player original)
    {
        var tab = await GhostLapHubTests.OpenAsync(stack, original.Login);
        var joined = await tab.JoinAsync(m.RoomCode);
        Assert.True(joined.Ok, joined.Code);
        var player = Track(tab, joined.Value!, original.Login);
        player.Stream = tab.StreamAsync(player.Inputs.Reader);
        return player;
    }

    /// <summary>Keeps every controller streaming inward-pointing inputs at about 30 Hz until cancelled.</summary>
    private static Task PlayAsync(Started m, CancellationToken ct) => Task.Run(async () =>
    {
        var directions = m.Players.Select(p => Inward(m, SlotOf(m, p))).ToArray();
        while (!ct.IsCancellationRequested)
        {
            for (var k = 0; k < m.Players.Length; k++)
            {
                m.Players[k].Inputs.Writer.TryWrite(Next(m, m.Players[k], directions[k].X, directions[k].Y));
            }
            try
            {
                await Task.Delay(33, ct);
            }
            catch (OperationCanceledException)
            {
            }
        }
    });

    private static Task<long> TerminateGuardSessionAsync(IdentityDatabase db) => db.ScalarAsync<long>(
        "SELECT count(*) FROM (SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE application_name = @name) t",
        ("name", RealtimeInstanceGuard.ApplicationName));

    /// <summary>
    /// **Validates: Requirements 10.05, 10.15** The guard-loss detection interval. Process A's dedicated guard session dies silently while A
    /// keeps its ordinary pool and keeps ticking (its probe interval is 60 s here). A successor B takes the lock and registers its generation:
    /// in the same transaction A's generation is retired and A's running receipt is aborted. During the interval A can still tick, but it
    /// cannot start a match or decide one; as soon as A's generation monitor sees the retirement, A executes and delivers nothing more.
    /// </summary>
    [Fact]
    public async Task A_stale_process_after_silent_guard_loss_cannot_start_or_decide_and_goes_quiet_once_it_sees_retirement()
    {
        var secret = IdentityApiFactory.NewMasterSecret();
        var stackA = new GhostLapHubTests.Stack
        {
            Api = new IdentityApiFactory(db, secret),
            Hub = new RealtimeHostFactory(db, secret, new()
            {
                ["Realtime:InstanceLockCheckSeconds"] = "60",
                ["GhostLap:Multiplayer:GenerationCheckMilliseconds"] = "250",
                ["GhostLap:Multiplayer:CountdownMilliseconds"] = "1000",
            }),
        };
        await using var disposeA = stackA;
        var m = await StartAsync(stackA, 2);
        using var stop = new CancellationTokenSource();
        var play = PlayAsync(m, stop.Token);
        try
        {
            var guardA = stackA.Hub.Services.GetRequiredService<RealtimeInstanceGuard>();
            var authorityA = stackA.Hub.Services.GetRequiredService<GhostLapRealtimeAuthority>();
            var storeA = stackA.Hub.Services.GetRequiredService<MatchAuthorityStore>();
            var lifetimeA = stackA.Hub.Services.GetRequiredService<IHostApplicationLifetime>(); // A disposes its provider once it stops
            await Eventually(() => m.Players.All(p => !p.Acks.IsEmpty), "both controllers acknowledged");

            Assert.Equal(1, await TerminateGuardSessionAsync(db));
            await Task.Delay(300);
            // The documented overlap: A has not noticed (no probe yet) and still runs its match.
            Assert.True(guardA.IsHeld);
            Assert.True(authorityA.IsValid);
            var tickBefore = m.Runtime.Engine.State.Tick;
            await Eventually(() => m.Runtime.Engine.State.Tick > tickBefore + 3, "A still ticking during the interval");

            await using var hostB = new RealtimeHostFactory(db, secret, new() { ["Realtime:InstanceLockAcquireSeconds"] = "10" });
            _ = hostB.Server;
            var authorityB = hostB.Services.GetRequiredService<GhostLapRealtimeAuthority>();
            Assert.True(authorityB.IsValid);
            Assert.Equal("Retired", await db.ScalarAsync<string>(
                "SELECT status FROM ghostlap.realtime_generation WHERE generation_id = @g", ("g", authorityA.GenerationId)));
            Assert.Equal("Aborted:generation_retired", await db.ScalarAsync<string>(
                "SELECT terminal_decision || ':' || abort_reason FROM ghostlap.match_session WHERE match_id = @m", ("m", m.Runtime.Config.MatchId)));

            // Still inside A's interval or not: the database refuses A a new start and keeps the successor's decision.
            var stale = RoundSealing.Seal(new byte[32], Guid.CreateVersion7(), 1,
                [new RosterEntry("a", "guest:" + Guid.NewGuid()), new RosterEntry("b", "guest:" + Guid.NewGuid())], new MultiplayerOptions(), authorityA.GenerationId);
            Assert.Equal(StartReceiptOutcome.GenerationRetired, (await storeA.CommitStartReceiptAsync(stale, authorityA.GenerationId, default)).Outcome);
            Assert.Equal(AbortOutcome.AlreadyTerminal, (await storeA.AbortAsync(m.Runtime.Config.MatchId, authorityA.GenerationId, AbortReasons.Overload, default)).Outcome);

            // A's monitor sees the retirement: authority withdrawn, loop stopped, host stopping, status is the durable abort.
            await Eventually(() => !authorityA.IsValid, "A sees its retirement", 5_000);
            Assert.True(authorityA.Retired);
            await Eventually(() => m.Runtime.Status == MatchStatuses.Aborted, "A reports the durable abort", 5_000);
            Assert.Equal(MatchStopReason.AuthorityLost, m.Runtime.StopReason);
            Assert.True(lifetimeA.ApplicationStopping.IsCancellationRequested);
            await Task.Delay(200); // let anything already on the wire arrive
            var frozenTick = m.Runtime.Engine.State.Tick;
            var acks = m.Players.Sum(p => p.Acks.Count);
            await Task.Delay(700);
            Assert.Equal(frozenTick, m.Runtime.Engine.State.Tick);   // no tick executed after the loop stopped
            Assert.Equal(acks, m.Players.Sum(p => p.Acks.Count));      // no gameplay feedback delivered by A
            Assert.Equal("Aborted:generation_retired", await db.ScalarAsync<string>(
                "SELECT terminal_decision || ':' || abort_reason FROM ghostlap.match_session WHERE match_id = @m", ("m", m.Runtime.Config.MatchId)));
            Console.WriteLine($"STALE-INTERVAL A ticked to {frozenTick}; decision written by successor; A acks frozen at {acks}");
        }
        finally
        {
            await stop.CancelAsync();
            await play;
            foreach (var p in m.Players)
            {
                p.Inputs.Writer.TryComplete();
                await p.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// **Validates: Requirements 10.05, 10.09, 10.15** When the dedicated guard connection is lost and detected (1 s probe), the running
    /// match loop is cancelled and its receipt gets a fenced Aborted decision through the ordinary pool (no successor exists to retire it).
    /// Nothing is reported as finished or rated.
    /// </summary>
    [Fact]
    public async Task Detected_guard_loss_cancels_the_running_match_and_aborts_its_receipt()
    {
        await using var stack = _kit.NewStack(new()
        {
            ["Realtime:InstanceLockCheckSeconds"] = "1",
            ["GhostLap:Multiplayer:CountdownMilliseconds"] = "1000",
        });
        var m = await StartAsync(stack, 2);
        using var stop = new CancellationTokenSource();
        var play = PlayAsync(m, stop.Token);
        try
        {
            await Eventually(() => m.Players.All(p => !p.Acks.IsEmpty), "both controllers acknowledged");
            Assert.Equal(1, await TerminateGuardSessionAsync(db));
            var guard = stack.Hub.Services.GetRequiredService<RealtimeInstanceGuard>();
            await Eventually(() => guard.AuthorityLost, "guard detects the loss", 5_000);
            await Eventually(() => m.Runtime.Status is MatchStatuses.Aborted or MatchStatuses.Unverified, "loop stopped and abort written", 10_000);
            Assert.Equal(MatchStatuses.Aborted, m.Runtime.Status);
            Assert.Contains(m.Runtime.StopReason, new MatchStopReason?[] { MatchStopReason.AuthorityLost, MatchStopReason.Cancelled });
            Assert.True(m.Runtime.Engine.State.Tick < GameRulesV1.RoundTicks);
            var decision = await db.ScalarAsync<string>(
                "SELECT terminal_decision || ':' || abort_reason FROM ghostlap.match_session WHERE match_id = @m", ("m", m.Runtime.Config.MatchId));
            Assert.Contains(decision, new[] { "Aborted:authority_lost", "Aborted:host_stopping" });
            Console.WriteLine($"GUARD-LOSS stop={m.Runtime.StopReason} tick={m.Runtime.Engine.State.Tick} decision={decision}");
        }
        finally
        {
            await stop.CancelAsync();
            await play;
            foreach (var p in m.Players)
            {
                p.Inputs.Writer.TryComplete();
                await p.DisposeAsync();
            }
        }
    }
}
