using GhostLap.Contracts.Multiplayer;
using GhostLap.Multiplayer;
using GhostLap.Rules;
using Integration.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Integration.Tests.GhostLap;

public sealed partial class GhostLapDemoGateTests
{
    // Each leg holds its direction for the specified number of ticks. These routes pick up real candles on the
    // pinned seed. Both slots are Overlit and within Burst range at tick 257. From tick 353 they wait in range.
    private static readonly (int Ticks, sbyte X, sbyte Y)[] LeftRoute =
    [
        (17, 1, -1), (15, 1, 0), (3, 1, 1), (20, 0, 1), (13, 1, -1), (3, 1, 0), (14, 1, 1), (1, 0, 1),
        (21, 1, 0), (10, 1, -1), (2, 0, -1), (13, 1, 1), (3, 1, 0), (3, 1, 1), (6, 1, 0),
        (19, -1, -1), (6, -1, 0), (43, -1, 1), (13, -1, -1), (7, -1, 0), (15, -1, -1), (9, 1, -1),
        (1, 0, 0), (7, 0, -1), (6, -1, -1), (15, -1, 0), (3, -1, 1), (24, 0, 1), (29, 1, 1), (11, 1, 0), (1, 0, 0),
    ];

    private static readonly (int Ticks, sbyte X, sbyte Y)[] RightRoute =
    [
        (6, -1, -1), (8, 0, -1), (21, -1, 0), (22, -1, -1), (11, 1, -1), (1, 0, -1), (12, 1, 1), (4, 0, 1),
        (32, 1, 1), (27, 0, 1), (25, -1, -1), (32, -1, 1), (11, -1, 0), (15, -1, -1), (5, -1, 0), (24, -1, -1),
        (1, 0, 0), (15, -1, -1), (13, -1, 0), (17, -1, 1), (10, 0, 1), (40, 1, 1), (1, 0, 0),
    ];

    private sealed class EventRoomRandom : IRoomRandom
    {
        public void Fill(Span<byte> bytes)
        {
            if (bytes.Length == 32)
            {
                bytes.Clear();
                bytes[0] = 2;
            }
            else
            {
                new CryptoRoomRandom().Fill(bytes);
            }
        }
    }

    [Fact]
    public async Task Real_hub_delivers_Overlit_cancellation_auto_Burst_and_forfeit_once_with_resync()
    {
        const int manual = 257;
        const int rearmed = 352;
        const int auto = rearmed + 240;
        var clock = new GateClock();
        clock.Hold();
        var useClock = UseClock(clock);
        var settings = new Dictionary<string, string?>(Fast) { ["GhostLap:Multiplayer:SnapshotIntervalTicks"] = "1" };
        var stack = _kit.NewStack(settings, services =>
        {
            useClock(services);
            services.Replace(ServiceDescriptor.Singleton<IRoomRandom, EventRoomRandom>());
        });
        await using var h = await OpenRoomAsync(stack, 2, 1);
        var round = await StartRoundAsync(h, null);
        Assert.Equal("2078438126824520019", round.Seed);
        await clock.Parked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await AttachAsync(h, round, rematch: false);
        var (left, right) = (h.Players.Single(p => p.Slot == 0), h.Players.Single(p => p.Slot == 1));
        var watcher = h.Spectators.Single();
        var engine = h.Runtime!.Engine;

        watcher.OpenStream();
        watcher.Send(1, 0, true, aim: 1);
        await Eventually(() => watcher.RejectionCount(GhostLapFailure.SpectatorForbidden) == 1, "spectator input refused");
        Assert.Equal(0, engine.QueuedInputs);

        var changes = Inputs(LeftRoute, left).Concat(Inputs(RightRoute, right)).ToLookup(i => i.Tick);
        var checkpoints = changes.Select(g => g.Key).Concat([manual, manual + 1, rearmed, rearmed + 1, auto]).Distinct().Order();
        var charges = new HashSet<int> { 0 };
        var followerPenalty = 0;
        long cancelledDeadline = 0;
        foreach (var tick in checkpoints)
        {
            if (engine.State.Tick < tick - 1)
            {
                await clock.AdvanceToAsync(tick - 1);
            }
            var before = engine.State.World.Players;
            if (tick == manual)
            {
                Assert.All(before, p => Assert.Equal(4, p.Charge));
                cancelledDeadline = before[1].OverlitDeadlineTick;
                followerPenalty = before[1].PenaltyPoints;
                Assert.True(cancelledDeadline > manual);
            }

            var commands = changes[tick].ToArray();
            foreach (var (client, _, x, y) in commands)
            {
                client.Send(x, y, tick == manual && client == left, aim: tick);
            }
            if (commands.Length > 0)
            {
                await Eventually(() => engine.QueuedInputs == commands.Length, $"input at tick {tick}");
            }
            await clock.AdvanceToAsync(tick);
            Assert.Equal(tick, engine.State.Tick);
            var after = engine.State.World.Players;
            charges.UnionWith(after.Select(p => p.Charge));
            if (tick == manual)
            {
                Assert.Equal((0, 0L, 2, 0L), (after[0].Charge, after[0].OverlitDeadlineTick, after[1].Charge, after[1].OverlitDeadlineTick));
                Assert.Equal(followerPenalty + 90, after[1].PenaltyPoints);
            }
            if (tick == rearmed)
            {
                Assert.Equal((4, (long)auto), (after[0].Charge, after[0].OverlitDeadlineTick));
            }
            if (tick == auto)
            {
                Assert.Equal((4, (long)auto, 1), (before[0].Charge, before[0].OverlitDeadlineTick, before[0].Bursts));
                Assert.Equal((0, 0L, 2), (after[0].Charge, after[0].OverlitDeadlineTick, after[0].Bursts));
                Assert.Equal(Math.Max(0, before[1].Charge - 2), after[1].Charge);
                Assert.Equal(before[1].PenaltyPoints + Math.Min(90, before[1].TotalScore), after[1].PenaltyPoints);
            }
        }

        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, charges.Order());
        await Eventually(() => h.All.All(c => c.Last?.Tick == auto), "all recipients see auto Burst");
        var manualEvent = Assert.Single(watcher.AppliedEvents, e => e.Kind == MatchEventKinds.BurstManual);
        var cancelEvent = Assert.Single(watcher.AppliedEvents, e => e.Kind == MatchEventKinds.OverlitCancelled);
        var autoEvent = Assert.Single(watcher.AppliedEvents, e => e.Kind == MatchEventKinds.BurstAuto);
        Assert.Equal((manual, 0, 1), (manualEvent.Tick, manualEvent.Slot, manualEvent.Value));
        Assert.Equal((manual, 1), (cancelEvent.Tick, cancelEvent.Slot));
        Assert.Equal((auto, 0, 1), (autoEvent.Tick, autoEvent.Slot, autoEvent.Value));
        foreach (var c in h.Players)
        {
            Assert.Contains(manualEvent, c.AppliedEvents);
            Assert.Contains(cancelEvent, c.AppliedEvents);
            Assert.Contains(autoEvent, c.AppliedEvents);
        }

        // The same authenticated principal takes over. Resync returns the current state and event cursor,
        // and re-offering that actual frame does not replay its cues.
        var takeoverTab = await GhostLapHubTests.OpenAsync(stack, left.Login);
        await using var takeover = new GameClient(takeoverTab, left.Net, left.Login, h.ServerTick)
        {
            Joined = RequireDemo(await takeoverTab.JoinAsync(h.RoomCode)), MatchId = round.MatchId, Slot = 0,
        };
        await Eventually(() => left.Tab.Superseded == 1, "previous controller is superseded");
        var synced = await ResyncAsync(takeover);
        Assert.Equal((round.MatchId, (long)auto, 2, true), (synced.Snapshot.MatchId, synced.Snapshot.Tick, synced.Snapshot.Players[0].Bursts, synced.You.CanControl));
        Assert.Contains(autoEvent, synced.Events.Items);
        Assert.Equal(synced.Events.Items.Max(e => e.Key), takeover.Filter.EventHighWaterKey);
        Assert.Empty(takeover.OfferSnapshot(synced).NewEvents);
        Assert.Single(takeover.AppliedEvents, e => e == autoEvent);

        await right.DisposeAsync();
        await Eventually(() => h.Runtime.Fanout.Recipients.Count == 3, "disconnected controller detached");
        var disconnect = auto + 1;
        var forfeit = disconnect + 450;
        await clock.AdvanceToAsync(disconnect);
        var grace = await FetchResyncAsync(watcher);
        Assert.Equal((SlotStatuses.Grace, (long)forfeit, 0, 0),
            (grace.Snapshot.Players[1].Status, grace.Snapshot.Players[1].GraceEndsTick, grace.Snapshot.Players[1].MoveX, grace.Snapshot.Players[1].MoveY));
        await clock.AdvanceToAsync(forfeit - 1);
        Assert.False(engine.State.IsForfeited(1));
        await clock.AdvanceToAsync(forfeit);
        await Eventually(() => watcher.AppliedEvents.Any(e => e.Kind == MatchEventKinds.Forfeited), "spectator sees forfeit");
        var forfeited = Assert.Single(watcher.AppliedEvents, e => e.Kind == MatchEventKinds.Forfeited);
        Assert.Equal((forfeit, 1), (forfeited.Tick, forfeited.Slot));
        Assert.Equal(forfeit, engine.State.ForfeitTicks[1]);
        var frozen = engine.State.World.Players[1];
        Assert.Equal(0, frozen.OverlitDeadlineTick);

        var returningTab = await GhostLapHubTests.OpenAsync(stack, right.Login);
        await using var returning = new GameClient(returningTab, right.Net, right.Login, h.ServerTick)
        {
            Joined = RequireDemo(await returningTab.JoinAsync(h.RoomCode)), MatchId = round.MatchId, Slot = 1,
        };
        var resync = await ResyncAsync(returning);
        Assert.False(resync.You.CanControl);
        Assert.Equal(SlotStatuses.Forfeited, resync.Snapshot.Players[1].Status);
        Assert.Contains(forfeited, resync.Events.Items);
        returning.OpenStream();
        returning.Send(1, 1, true, aim: forfeit + 1, seq: resync.You.AppliedSeq + 1);
        await Eventually(() => engine.QueuedInputs == 1, "the rejoined competitor's input reaches the tick mailbox");
        await clock.AdvanceToAsync(forfeit + 1);
        await Eventually(() => returning.RejectionCount(GhostLapFailure.Forfeited) == 1, "forfeited input refused by the match tick");
        await clock.AdvanceToAsync(forfeit + 60);
        Assert.Equal(frozen, engine.State.World.Players[1]);
        Assert.Equal(2, engine.State.World.Players[0].Bursts);
        await Eventually(() => watcher.Last?.Tick == forfeit + 60, "spectator sees post-forfeit state");
        Assert.DoesNotContain(watcher.AppliedEvents, e => e.Tick > forfeit &&
            (e.Slot == 1 || e.Kind is MatchEventKinds.BurstHit or MatchEventKinds.ContactDrain && e.OtherSlot == 1));
        Assert.All(h.All.Append(takeover).Append(returning), c =>
        {
            var keys = c.AppliedEvents.Select(e => e.Key).ToArray();
            Assert.Equal(keys.Distinct().Order(), keys);
        });
        output.WriteLine($"EVENTS seed={round.Seed} manual={manual} cancelledDeadline={cancelledDeadline} auto={auto} disconnect={disconnect} forfeit={forfeit}");
    }

    private static IEnumerable<(GameClient Client, int Tick, sbyte X, sbyte Y)> Inputs(
        (int Ticks, sbyte X, sbyte Y)[] route, GameClient client)
    {
        var tick = 1;
        foreach (var (ticks, x, y) in route)
        {
            var hold = x == 0 && y == 0 ? ticks : GameRulesV1.InputHoldTicks + 1;
            for (var elapsed = 0; elapsed < ticks; elapsed += hold)
            {
                yield return (client, tick + elapsed, x, y);
            }
            tick += ticks;
        }
    }

    [Fact]
    public async Task An_expired_guest_cookie_cannot_open_the_Ghost_Lap_hub()
    {
        await using var stack = _kit.NewStack();
        var guest = await GhostLapHubTests.GuestAsync(stack);
        await using (var valid = await GhostLapHubTests.OpenAsync(stack, guest))
        {
            Assert.True((await valid.CreateAsync()).Ok);
        }
        Assert.True(await db.ScalarAsync<bool>("""
            UPDATE marrow_identity.guest_sessions SET expires_at = now() - interval '1 second'
            WHERE guest_id = @id RETURNING true
            """, ("id", Guid.Parse(guest.Subject["guest:".Length..]))));
        await using var expired = stack.Hub.ConnectTo(GhostLapWire.HubPath, guestCookie: guest.Cookie);
        await Assert.ThrowsAnyAsync<Exception>(() => expired.StartAsync());
    }
}
