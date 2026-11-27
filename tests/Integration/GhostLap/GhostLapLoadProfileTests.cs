extern alias realtime;
using System.Diagnostics;
using System.Globalization;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using GhostLap.Contracts.Multiplayer;
using GhostLap.Multiplayer;
using GhostLap.Rules;
using GhostLap.Simulation;
using Integration.Tests.Support;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;
using Xunit.Abstractions;
using ProcessProbe = realtime::GhostLapHost.ProcessProbe;

namespace Integration.Tests.GhostLap;

public sealed class LoadProfileFactAttribute : FactAttribute
{
    public LoadProfileFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("GHOSTLAP_LOAD_PROFILE") != "1")
        {
            Skip = "Run scripts/checks/ghostlap-load-benchmark.sh to start the external-host profile.";
        }
    }
}

[Collection(IdentityCollection.Name)]
[Trait("Category", "LoadProfile")]
public sealed class GhostLapLoadProfileTests(IdentityDatabase db, ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private static int Env(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var n) ? n : fallback;

    private sealed record Room(string Id, List<GameClient> Players, List<GameClient> Spectators)
    {
        public IEnumerable<GameClient> Clients => Players.Concat(Spectators);
    }

    [LoadProfileFact]
    public async Task Concurrent_rooms_profile()
    {
        var roomCount = Env("GHOSTLAP_LOAD_ROOMS", 4);
        var players = Env("GHOSTLAP_LOAD_PLAYERS", 4);
        var spectators = Env("GHOSTLAP_LOAD_SPECTATORS_PER_ROOM", 2);
        var ticks = Env("GHOSTLAP_LOAD_TICKS", GameRulesV1.RoundTicks);
        const int workloadSeed = 1052026;
        var fullRound = ticks == GameRulesV1.RoundTicks;
        var dir = Environment.GetEnvironmentVariable("GHOSTLAP_LOAD_OUT")!;
        var profile = Path.Combine(dir, "host-profile.jsonl");
        File.Delete(profile);
        File.Delete(Path.Combine(dir, "summary.json"));

        var secret = IdentityApiFactory.NewMasterSecret();
        await using var api = new IdentityApiFactory(db, secret);
        await using var host = new BenchmarkRealtimeProcess(db, secret, roomCount, roomCount * (players + spectators), spectators, profile);
        var address = await host.WaitForAddressAsync();
        var clients = new List<GameClient>();
        var sockets = new Dictionary<GameClient, MeasuredWebSocket>();
        async Task<GameClient> OpenAsync()
        {
            var browser = api.NewBrowser();
            var login = await browser.PostAsync("/auth/guest/start");
            var guest = new GhostLapHubTests.GuestLogin(browser, browser.Cookies[Flows.GuestCookie], login.Str("subject"));
            MeasuredWebSocket? socket = null;
            var connection = new HubConnectionBuilder()
                .WithUrl(address + GhostLapWire.HubPath, options =>
                {
                    options.Transports = HttpTransportType.WebSockets;
                    options.SkipNegotiation = true;
                    options.WebSocketFactory = async (context, ct) =>
                    {
                        var wire = new ClientWebSocket();
                        try
                        {
                            wire.Options.SetRequestHeader("Cookie", Flows.GuestCookie + "=" + guest.Cookie);
                            wire.Options.SetRequestHeader("Origin", address);
                            await wire.ConnectAsync(context.Uri, ct);
                            return socket = new MeasuredWebSocket(wire);
                        }
                        catch
                        {
                            wire.Dispose();
                            throw;
                        }
                    };
                })
                .Build();
            connection.KeepAliveInterval = TimeSpan.FromSeconds(5);
            var client = new GameClient(new GhostLapHubTests.Tab(connection), new NetworkConditions(), guest, serverTick: null);
            clients.Add(client);
            await connection.StartAsync();
            sockets.Add(client, socket!);
            return client;
        }

        object Traffic(GameClient client) => new { sockets[client].SentBytes, sockets[client].ReceivedBytes };

        var rooms = new List<Room>();
        using var play = new CancellationTokenSource();
        var playTasks = new List<Task>();
        try
        {
            for (var r = 0; r < roomCount; r++)
            {
                var leader = await OpenAsync();
                leader.Joined = Require(await leader.Tab.CreateAsync());
                var room = new Room(leader.Joined.View.RoomId, [leader], []);
                rooms.Add(room);
                for (var j = 1; j < players + spectators; j++)
                {
                    var client = await OpenAsync();
                    var watch = j >= players;
                    client.Joined = Require(await client.Tab.JoinAsync(leader.Joined.RoomCode, watch));
                    (watch ? room.Spectators : room.Players).Add(client);
                }
                foreach (var player in room.Players)
                {
                    Require(await player.Tab.ReadyAsync(room.Id));
                }
            }

            var probe = new ProcessProbe();
            var clock = Stopwatch.StartNew();
            var samples = new List<realtime::GhostLapHost.ProcessLoad> { probe.Capture() };
            var start = await Task.WhenAll(rooms.Select(r => r.Players[0].Tab.StartAsync(r.Id)));
            foreach (var reply in start)
            {
                Require(reply);
            }
            var rounds = await Task.WhenAll(rooms.Select(async room =>
            {
                var wait = Stopwatch.StartNew();
                while (wait.Elapsed < TimeSpan.FromSeconds(15))
                {
                    var view = Require(await room.Players[0].Tab.GetAsync(room.Id));
                    if (view.MatchStatus == MatchStatuses.Running)
                    {
                        return view.Round!;
                    }
                    await Task.Delay(150);
                }
                throw new TimeoutException("Room did not reach Running: " + room.Id);
            }));
            Assert.Equal(roomCount, rounds.Select(r => r.MatchId).Distinct().Count());
            Assert.Equal(roomCount, rounds.Select(r => r.Seed).Distinct().Count());

            for (var r = 0; r < rooms.Count; r++)
            {
                var room = rooms[r];
                var round = rounds[r];
                var plan = CandlePlan.Create(long.Parse(round.Seed, CultureInfo.InvariantCulture));
                foreach (var client in room.Clients)
                {
                    client.MatchId = round.MatchId;
                }
                foreach (var player in room.Players)
                {
                    player.Slot = round.Slots.Single(s => s.MemberId == player.Joined.MemberId).Slot;
                    player.OpenStream();
                    if (roomCount > 1 && player == room.Players[0])
                    {
                        // Probe the wrong-match guard with an otherwise valid stream/epoch.
                        var other = rounds[(r + 1) % roomCount].MatchId;
                        player.Inputs.Writer.TryWrite(new GameplayInput(other, player.Joined.ControllerEpoch, 1, int.MaxValue, 1, 0, true));
                    }
                    playTasks.Add(player.PlayAsync(new CandleBrain(plan), play.Token, workloadSeed));
                }
            }

            var deadline = TimeSpan.FromSeconds(ticks / 30.0 + 20);
            while (clients.Any(c => c.Last?.Tick < ticks || c.Last is null) && clock.Elapsed < deadline)
            {
                samples.Add(probe.Capture());
                await Task.Delay(1000);
            }
            var elapsed = clock.Elapsed.TotalSeconds;
            samples.Add(probe.Capture());
            await play.CancelAsync();
            await Task.WhenAll(playTasks);
            foreach (var player in rooms.SelectMany(r => r.Players))
            {
                player.Inputs.Writer.Complete();
            }

            var streamReplies = await Task.WhenAll(rooms.SelectMany(r => r.Players)
                .Select(async p => Require(await p.Stream!.WaitAsync(TimeSpan.FromSeconds(10)))));
            var streamIndex = 0;
            var evidence = new List<object>();
            var outcomes = new List<(MatchOutcomeView?, MatchResultDocument?)>();
            for (var r = 0; r < rooms.Count; r++)
            {
                var room = rooms[r];
                var round = rounds[r];
                MatchOutcomeView? outcome = null;
                MatchResultDocument? document = null;
                long outbox = 0;
                if (fullRound)
                {
                    var settle = Stopwatch.StartNew();
                    do
                    {
                        var reply = await room.Players[0].Tab.Connection.InvokeAsync<GhostLapReply<MatchOutcomeView>>(
                            GhostLapWire.GetMatchResultMethod, round.MatchId).WaitAsync(TimeSpan.FromSeconds(5));
                        outcome = Require(reply);
                        if (outcome.Decision != MatchOutcomeDecisions.Pending)
                        {
                            break;
                        }
                        await Task.Delay(250);
                    } while (settle.Elapsed < TimeSpan.FromSeconds(10));

                    var id = Guid.Parse(round.MatchId);
                    var stored = await db.ScalarAsync<string>("select result_document from ghostlap.match_result where match_id = @id", ("id", id));
                    document = stored is null ? null : JsonSerializer.Deserialize<MatchResultDocument>(stored, Json);
                    outbox = await db.ScalarAsync<long>(
                        "select count(*) from ghostlap.outbox where aggregate_id = @id and event_type = 'ghostlap.MatchFinalizedV1'",
                        ("id", id.ToString()));
                }
                outcomes.Add((outcome, document));
                var events = room.Spectators.FirstOrDefault()?.AppliedEvents.ToArray() ?? room.Players[0].AppliedEvents.ToArray();
                evidence.Add(new
                {
                    round,
                    outcome,
                    document,
                    outbox,
                    eventCounts = events.GroupBy(e => e.Kind).ToDictionary(g => g.Key, g => g.Count()),
                    players = room.Players.Select(p => new { p.Slot, net = p.Summary(), p.Rejections, traffic = Traffic(p), stream = streamReplies[streamIndex++] }).ToArray(),
                    spectators = room.Spectators.Select(s => new { frames = s.Frames.Count, events = s.AppliedEvents.Count, traffic = Traffic(s) }).ToArray(),
                });
            }
            // Let the host reporter take a post-terminal sample with all recipients still attached.
            await Task.Delay(1300);
            await File.WriteAllTextAsync(Path.Combine(dir, "summary.json"), JsonSerializer.Serialize(new
            {
                kind = "ghostlap-load-external/1",
                hostPid = host.Id,
                driverPid = Environment.ProcessId,
                roomCount, players, spectators, ticks, workloadSeed, elapsedSeconds = elapsed,
                driverSamples = samples,
                rooms = evidence,
            }, new JsonSerializerOptions(Json) { WriteIndented = true }));
            output.WriteLine($"Host PID {host.Id}, driver PID {Environment.ProcessId}, observed {roomCount} rooms for {elapsed:F2}s.");

            if (fullRound)
            {
                var lines = await File.ReadAllLinesAsync(profile);
                var complete = lines.Where(l => l.EndsWith('}'))
                    .Select(l => JsonSerializer.Deserialize<realtime::GhostLapHost.GhostLapProfileLine>(l, Json)!)
                    .Last(l => l.Load.Matches.Count == roomCount && l.Load.Matches.All(m => m.Tick == ticks));
                Assert.Equal(roomCount * (players + spectators), complete.OpenConnections);
                Assert.All(complete.Load.Matches, m =>
                {
                    var roomIndex = Array.FindIndex(rounds, r => r.MatchId == m.MatchId);
                    Assert.InRange(roomIndex, 0, roomCount - 1);
                    var streams = streamReplies.Skip(roomIndex * players).Take(players).ToArray();
                    Assert.All(streams, stream => Assert.Equal("completed", stream.EndReason));
                    Assert.Equal(streams.Sum(s => s.Received),
                        streams.Sum(s => s.Rejected) + m.TotalAcceptedInputs + m.TotalRejectedInputs);
                    Assert.Equal(0, m.PendingInputs);
                    Assert.Equal(ticks, m.TickExecutionMicros.Count);
                    Assert.Equal(ticks, m.StartLatenessMicros.Count);
                    Assert.Equal(players + spectators, m.Recipients.Recipients);
                    Assert.Equal(MatchStatuses.Finalized, m.Status);
                    Assert.Equal(0, m.Recipients.Failures);
                    Assert.Equal(0, m.Recipients.Stalled);
                    if (Env("GHOSTLAP_LOAD_ENFORCE", 0) == 1)
                    {
                        Assert.True(m.TickExecutionMicros.P99 < 33_333);
                        Assert.True(m.StartLatenessMicros.P95 < 33_333);
                        Assert.True(m.StartLatenessMicros.P99 < 66_666);
                        Assert.True(m.BacklogTicks.P99 <= 2);
                    }
                });
            }
            for (var r = 0; r < roomCount; r++)
            {
                var round = rounds[r];
                var room = rooms[r];
                Assert.Equal(MpRulesProfile.CurrentRulesId, round.RulesId);
                var events = room.Players[0].AppliedEvents.Select(e => e.Key).ToArray();
                if (roomCount > 1)
                {
                    Assert.Equal(1, room.Players[0].RejectionCount(GhostLapFailure.WrongMatch));
                }
                foreach (var client in room.Clients)
                {
                    Assert.True(client.Last?.Tick >= ticks, $"Room {r}, slot {client.Slot}, last tick {client.Last?.Tick}.");
                    var frames = client.Frames.ToArray();
                    Assert.All(frames, f =>
                    {
                        Assert.Equal(round.MatchId, f.Snapshot.MatchId);
                        Assert.Equal(round.RulesId, f.Snapshot.RulesId);
                        Assert.Equal(round.AuthorityGeneration, f.Snapshot.GenerationId);
                    });
                    Assert.True(frames.Zip(frames.Skip(1)).All(p => p.First.Snapshot.Tick <= p.Second.Snapshot.Tick));
                    var keys = client.AppliedEvents.Select(e => e.Key).ToArray();
                    Assert.True(keys.Zip(keys.Skip(1)).All(p => p.First < p.Second));
                    if (fullRound)
                    {
                        Assert.Equal(events, keys);
                        Assert.True(client.Last!.IsFinal);
                        Assert.Single(client.AppliedEvents, e => e.Kind == MatchEventKinds.RoundFinished);
                    }
                    Assert.All(client.Acks, a =>
                    {
                        Assert.Equal(round.MatchId, a.MatchId);
                        Assert.True(client.Sent.ContainsKey(a.LastAppliedSeq));
                    });
                }
                if (fullRound)
                {
                    var (outcome, document) = outcomes[r];
                    Assert.Equal(MatchOutcomeDecisions.Finalized, outcome?.Decision);
                    Assert.NotNull(document);
                    Assert.Equal(round.ConfigDigest, document.ConfigDigest);
                    Assert.Equal(round.Seed, document.Seed);
                    Assert.Equal(round.RulesId, document.RulesId);
                    Assert.Equal(round.AuthorityGeneration, document.GenerationId);
                    Assert.Equal(outcome!.ResultDigest, MatchResults.Digest(document));
                    Assert.Equal(room.Players[0].Last!.Players.Select(p => p.TotalScore), document.Players.Select(p => p.TotalScore));
                    var id = Guid.Parse(round.MatchId);
                    Assert.Equal("ResultCommitted", await db.ScalarAsync<string>(
                        "select terminal_decision from ghostlap.match_session where match_id = @id", ("id", id)));
                    Assert.Equal(1, await db.ScalarAsync<long>(
                        """
                        select count(*) from ghostlap.match_result r
                        join ghostlap.outbox o on o.aggregate_id = r.match_id::text
                          and o.event_type = 'ghostlap.MatchFinalizedV1'
                        where r.match_id = @id and r.final_tick = 2700 and o.payload::jsonb ->> 'resultDigest' = r.result_digest
                        """, ("id", id)));
                }
            }
        }
        finally
        {
            await play.CancelAsync();
            await Task.WhenAll(playTasks);
            await Task.WhenAll(clients.Select(c => c.DisposeAsync().AsTask()));
        }
    }

    private static T Require<T>(GhostLapReply<T> reply)
    {
        Assert.True(reply.Ok, reply.Code);
        return reply.Value!;
    }

    private sealed class MeasuredWebSocket(ClientWebSocket wire) : WebSocket
    {
        private long _received;
        private long _sent;

        public long ReceivedBytes => Interlocked.Read(ref _received);
        public long SentBytes => Interlocked.Read(ref _sent);
        public override WebSocketCloseStatus? CloseStatus => wire.CloseStatus;
        public override string? CloseStatusDescription => wire.CloseStatusDescription;
        public override string? SubProtocol => wire.SubProtocol;
        public override WebSocketState State => wire.State;
        public override void Abort() => wire.Abort();
        public override void Dispose() => wire.Dispose();
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) =>
            wire.CloseAsync(status, description, ct);
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) =>
            wire.CloseOutputAsync(status, description, ct);

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
        {
            var result = await wire.ReceiveAsync(buffer, ct);
            Interlocked.Add(ref _received, result.Count);
            return result;
        }

        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken ct)
        {
            await wire.SendAsync(buffer, type, end, ct);
            Interlocked.Add(ref _sent, buffer.Count);
        }
    }
}
