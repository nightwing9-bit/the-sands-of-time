extern alias realtime;
using System.Diagnostics;
using System.Globalization;
using System.Net.WebSockets;
using System.Text.Json;
using GhostLap.Contracts.Multiplayer;
using GhostLap.Multiplayer;
using GhostLap.Rules;
using Integration.Tests.Support;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;
using ProfileLine = realtime::GhostLapHost.GhostLapProfileLine;

namespace Integration.Tests.GhostLap;

public sealed partial class GhostLapDemoGateTests
{
    private static readonly JsonSerializerOptions DemoJson = new(JsonSerializerDefaults.Web);

    private async Task RunKestrelRoundsAsync(int playerCount, bool rematch)
    {
        var root = Environment.GetEnvironmentVariable("GHOSTLAP_DEMO_OUT")
            ?? Path.Combine(RepositoryRoot(), "TestResults", "ghostlap-demo");
        var dir = Path.Combine(root, $"{playerCount}p");
        Directory.CreateDirectory(dir);
        var profile = Path.Combine(dir, "host-profile.jsonl");
        File.Delete(profile);
        var secret = IdentityApiFactory.NewMasterSecret();
        await using var api = new IdentityApiFactory(db, secret);
        var spectatorCount = rematch ? 2 : 1;
        var clients = new List<GameClient>();
        var players = new List<GameClient>();
        var rounds = new List<object>();
        await using var host = new BenchmarkRealtimeProcess(db, secret, 1, playerCount + spectatorCount, spectatorCount, profile, emptyRoomSeconds: 2);
        var address = await host.WaitForAddressAsync();
        output.WriteLine($"Kestrel pid={host.Id} driver={Environment.ProcessId} {address}");
        try
        {
            for (var i = 0; i < playerCount + spectatorCount; i++)
            {
                var guest = await GhostLapHubTests.GuestAsync(api);
                var client = NewKestrelClient(address, guest);
                clients.Add(client);
                await client.Tab.Connection.StartAsync();
                client.Joined = RequireDemo(i == 0 ? await client.Tab.CreateAsync()
                    : await client.Tab.JoinAsync(clients[0].Joined.RoomCode, i >= playerCount));
                if (i < playerCount)
                {
                    players.Add(client);
                }
            }

            var roomId = players[0].Joined.View.RoomId;
            var first = await PlayKestrelRoundAsync(profile, clients, players, roomId, ordinal: 1, previous: null, previousFrames: null, rounds);
            var last = first;
            if (rematch)
            {
                var previousFrames = clients.Select(c => c.Frames.Last()).ToArray();
                var lobby = RequireDemo(await RematchAsync(players[0], roomId)).View;
                Assert.Equal((RoomPhases.Lobby, RoomNotices.RematchLobby), (lobby.Phase, lobby.Notice));
                Assert.Equal(clients.Select(c => c.Joined.MemberId).Order(), lobby.Members.Select(m => m.MemberId).Order());
                Assert.False((await RematchAsync(clients[^1], roomId)).Ok);
                // Listen for the new round before starting it. Use a new stream and measurements on each existing connection.
                for (var i = 0; i < clients.Count; i++)
                {
                    var old = clients[i];
                    clients[i] = new GameClient(old.Tab, old.Net, old.Login, serverTick: null) { Joined = old.Joined };
                    if (i < playerCount)
                    {
                        players[i] = clients[i];
                    }
                }
                last = await PlayKestrelRoundAsync(profile, clients, players, roomId, ordinal: 2, previous: first.Round, previousFrames, rounds);
                Assert.NotEqual(first.Digest, last.Digest);
                Assert.Equal(first.Stored, await ReadStoredRoundAsync(first.Round.MatchId));
                Assert.NotEqual(first.Stored.EventId, last.Stored.EventId);
            }

            await Task.WhenAll(players.Select(p => p.DisposeAsync().AsTask()));
            var watcher = clients[^1];
            await WaitForRoomClosedAsync(watcher, roomId);
            await Task.WhenAll(clients.Skip(playerCount).Select(p => p.DisposeAsync().AsTask()));
            _ = await ReadProfileAsync(profile, line => line.OpenConnections == 0 && line.Load.Capacity.LiveMatches == 0
                && line.Load.Capacity.ReservedStarts == 0
                && line.Load.Matches.All(m => m.Recipients.Recipients == 0 && m.PendingInputs == 0));

            if (rematch)
            {
                var restartDir = Path.Combine(dir, "replacement");
                Directory.CreateDirectory(restartDir);
                var restartProfile = Path.Combine(restartDir, "host-profile.jsonl");
                File.Delete(restartProfile);
                await host.KillAsync();
                await using var replacement = new BenchmarkRealtimeProcess(db, secret, 1, playerCount, 0, restartProfile);
                var replacementAddress = await replacement.WaitForAddressAsync();
                await using var reader = NewKestrelClient(replacementAddress, players[0].Login);
                await reader.Tab.Connection.StartAsync();
                foreach (var result in new[] { first, last })
                {
                    Assert.Equal(result.Stored, await ReadStoredRoundAsync(result.Round.MatchId));
                    Assert.Equal(result.Digest, RequireDemo(await ReadResult(reader, result.Round.MatchId)).ResultDigest);
                }
                Assert.Equal(2, await db.ScalarAsync<int>(
                    """
                    SELECT count(*)::int FROM ghostlap.realtime_generation
                    WHERE generation_id = @old OR retired_at IS NULL
                    """, ("old", Guid.Parse(first.Round.AuthorityGeneration))));
                Assert.True(await db.ScalarAsync<bool>(
                    "SELECT retired_at IS NOT NULL FROM ghostlap.realtime_generation WHERE generation_id = @old",
                    ("old", Guid.Parse(first.Round.AuthorityGeneration))));
                output.WriteLine($"SIGKILL pid={host.Id} successor={replacement.Id} both digests and outbox rows unchanged");
            }

            Assert.Equal(0L, await MatchFinalizationTests.GlobalInvariantViolationsAsync(db));
        }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "rounds.json"), JsonSerializer.Serialize(rounds, DemoJson));
            await Task.WhenAll(clients.Select(c => c.DisposeAsync().AsTask()));
        }
    }

    private async Task<(SealedRoundView Round, string Digest, StoredRound Stored)> PlayKestrelRoundAsync(
        string profile, List<GameClient> clients, List<GameClient> players, string roomId, int ordinal, SealedRoundView? previous, SnapshotFrame[]? previousFrames, List<object> evidence)
    {
        foreach (var p in players)
        {
            RequireDemo(await p.Tab.ReadyAsync(roomId));
        }
        var round = RequireDemo(await players[0].Tab.StartAsync(roomId)).View.Round!;
        Assert.Equal(("ghost-mp-v2", "arena-v1", "ghost-mp-v2+arena-v1", 2700, "elo-pair-v1"),
            (round.RulesId, round.LayoutId, round.RulesHash, round.TargetTicks, round.RatingAlgorithmId));
        if (previous is not null)
        {
            Assert.NotEqual(previous.MatchId, round.MatchId);
            Assert.NotEqual(previous.Seed, round.Seed);
            Assert.NotEqual(previous.ConfigDigest, round.ConfigDigest);
            Assert.Equal(previous.Slots.Select(s => s.MemberId).Order(), round.Slots.Select(s => s.MemberId).Order());
        }

        foreach (var client in clients)
        {
            client.MatchId = round.MatchId;
        }
        foreach (var p in players)
        {
            p.Slot = round.Slots.Single(s => s.MemberId == p.Joined.MemberId).Slot;
        }
        await Eventually(() => clients.All(c => c.Frames.Any(f => f.Snapshot.MatchId == round.MatchId)), "initial broadcast");
        var plan = CandlePlan.Create(long.Parse(round.Seed, CultureInfo.InvariantCulture));
        foreach (var c in clients)
        {
            var initial = c.Frames.First(f => f.Snapshot.MatchId == round.MatchId);
            Assert.Equal(0, initial.Snapshot.Tick);
            Assert.Equal(plan.ActiveIdsAt(0).ToArray(), initial.Snapshot.ActiveCandleIds.ToArray());
            foreach (var slot in round.Slots)
            {
                var p = initial.Snapshot.Players[slot.Slot];
                Assert.Equal((slot.SpawnXUnits * GameRulesV1.FixedOne, slot.SpawnYUnits * GameRulesV1.FixedOne), (p.XFixed, p.YFixed));
                Assert.Equal((0, 0, 0, 0, 0L), (p.TotalScore, p.Charge, p.Pickups, p.Bursts, p.OverlitDeadlineTick));
            }
        }
        Assert.Equal($"Started|{ordinal}|{round.ConfigDigest}|{round.Seed}|{round.AuthorityGeneration}",
            await db.ScalarAsync<string>("""
                SELECT concat_ws('|', terminal_decision, round_ordinal, config_digest, seed, replace(generation_id::text, '-', ''))
                FROM ghostlap.match_session WHERE match_id = @m
                """, ("m", Guid.Parse(round.MatchId))));

        var clock = Stopwatch.StartNew();
        for (var i = 0; i < clients.Count; i++)
        {
            var c = clients[i];
            var frame = await FetchResyncAsync(c);
            if (previousFrames is not null)
            {
                var prior = previousFrames[i];
                var filter = new SnapshotFilter();
                Assert.True(filter.Offer(prior).Applied);
                var initial = c.Frames.First(f => f.Snapshot.MatchId == round.MatchId);
                Assert.True(initial.IsResync); // A fresh fan-out recipient gets a pushed baseline.
                Assert.Equal(SnapshotVerdict.ForeignMatch, filter.Offer(initial with { IsResync = false }).Verdict);
                Assert.True(filter.Offer(frame).Applied);
                Assert.Equal(SnapshotVerdict.ForeignMatch, filter.Offer(prior with { IsResync = false }).Verdict);
            }
            Assert.Equal((round.MatchId, round.AuthorityGeneration, true), (frame.Snapshot.MatchId, frame.Snapshot.GenerationId, frame.IsResync));
            Assert.Equal((players.Contains(c), c.Slot), (frame.You.CanControl, frame.You.Slot));
            if (players.Contains(c))
            {
                Assert.Equal(c.Joined.ControllerEpoch, frame.You.ControllerEpoch);
                c.Seq = frame.You.AppliedSeq;
                c.OpenStream();
            }
        }
        // The old match cannot accept an item from an otherwise valid controller on the new stream.
        if (previous is not null)
        {
            players[0].Inputs.Writer.TryWrite(new GameplayInput(previous.MatchId, players[0].Joined.ControllerEpoch, 1, int.MaxValue, 1, 0, true));
        }
        using var play = new CancellationTokenSource();
        var tasks = players.Select(c => c.PlayAsync(new CandleBrain(plan), play.Token)).ToArray();
        try
        {
            await Eventually(() => clients.All(c => c.Last is { IsFinal: true } s && s.MatchId == round.MatchId), "every client sees the final tick", 110_000);
        }
        finally
        {
            await play.CancelAsync();
            await Task.WhenAll(tasks);
            foreach (var p in players)
            {
                p.Inputs.Writer.TryComplete();
            }
        }
        var seconds = clock.Elapsed.TotalSeconds;
        Assert.InRange(seconds, 80, 100);
        var streams = await Task.WhenAll(players.Select(async p => RequireDemo(await p.Stream!.WaitAsync(TimeSpan.FromSeconds(10)))));
        var line = await ReadProfileAsync(profile, l => l.Load.Matches.Any(m => m.MatchId == round.MatchId && m.Status == MatchStatuses.Finalized));
        var load = line.Load.Matches.Single(m => m.MatchId == round.MatchId);
        var received = streams.Sum(s => s.Received);
        Assert.All(streams, s => Assert.Equal("completed", s.EndReason));
        Assert.Equal(received, streams.Sum(s => s.Rejected) + load.TotalAcceptedInputs + load.TotalRejectedInputs);
        Assert.Equal(0, load.PendingInputs);
        Assert.Equal(2700, load.Tick);
        Assert.Equal(2700, load.TickExecutionMicros.Count);
        Assert.True(load.MaxBacklogTicks <= 3 && load.BacklogTicks.P99 <= 2, $"Max backlog {load.MaxBacklogTicks}, p99 {load.BacklogTicks.P99}");
        Assert.Equal((0L, 0), (load.Recipients.Failures, load.Recipients.Stalled));

        var finalPlayers = players[0].Last!.Players;
        var keys = players[0].AppliedEvents.Select(e => e.Key).ToArray();
        foreach (var c in clients)
        {
            Assert.All(c.Frames, f => Assert.Equal((round.MatchId, round.AuthorityGeneration, round.RulesId, round.LayoutId),
                (f.Snapshot.MatchId, f.Snapshot.GenerationId, f.Snapshot.RulesId, f.Snapshot.LayoutId)));
            Assert.Equal(2700, c.Last!.Tick);
            Assert.Equal(finalPlayers.ToArray(), c.Last.Players.ToArray());
            Assert.Equal(keys, c.AppliedEvents.Select(e => e.Key));
            Assert.Single(c.AppliedEvents, e => e.Kind == MatchEventKinds.RoundFinished);
            if (players.Contains(c))
            {
                Assert.NotEmpty(c.Acks);
                Assert.All(c.Acks, a => Assert.True(a.MatchId == round.MatchId && c.Sent.ContainsKey(a.LastAppliedSeq)));
                Assert.True(c.Last.Players[c.Slot].MovementPoints > 0);
            }
        }
        Assert.Equal(keys.Distinct().Order(), keys);
        if (previous is not null)
        {
            Assert.Equal(1, players[0].RejectionCount(GhostLapFailure.WrongMatch));
        }

        var expected = RequireDemo(await ReadResult(players[0], round.MatchId));
        Assert.Equal((round.MatchId, MatchOutcomeDecisions.Finalized, "elo-pair-v1", true),
            (expected.MatchId, expected.Decision, expected.RatingAlgorithmId, expected.RatedEligible));
        Assert.Equal(players.Count, expected.Players.Count);
        foreach (var c in clients)
        {
            Assert.Equal(expected.ResultDigest, RequireDemo(await ReadResult(c, round.MatchId)).ResultDigest);
        }
        foreach (var p in players)
        {
            var slot = expected.Players.Single(s => s.MemberId == p.Joined.MemberId);
            var score = finalPlayers[p.Slot].TotalScore;
            Assert.Equal((p.Slot, score, 1 + finalPlayers.Count(s => s.TotalScore > score)),
                (slot.Slot, slot.TotalScore, slot.CompetitivePlacement));
        }

        var stored = await ReadStoredRoundAsync(round.MatchId);
        var doc = JsonSerializer.Deserialize<MatchResultDocument>(stored.Document, DemoJson)!;
        Assert.Equal((round.ConfigDigest, round.Seed, round.RulesId, round.LayoutId, round.AuthorityGeneration),
            (doc.ConfigDigest, doc.Seed, doc.RulesId, doc.LayoutId, doc.GenerationId));
        Assert.Equal(expected.ResultDigest, MatchResults.Digest(doc));
        var events = players[0].AppliedEvents.GroupBy(e => e.Kind).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.Count());
        evidence.Add(new { round, ordinal, seconds, load, streams, received, expected, stored.EventId, events });
        output.WriteLine($"REALTIME players={players.Count} round={ordinal} ticks={load.Tick} seconds={seconds:F1} backlogMax={load.MaxBacklogTicks} late={load.LateTicks} " +
            $"maxLatenessMs={load.StartLatenessMicros.Max / 1000.0:F2} digest={expected.ResultDigest} inputs={received} events[{string.Join(",", events.Select(e => $"{e.Key}={e.Value}"))}]");
        return (round, expected.ResultDigest!, stored);
    }

    private sealed record StoredRound(string Document, string EventId, string Payload);

    private async Task<StoredRound> ReadStoredRoundAsync(string matchId)
    {
        var row = await db.ScalarAsync<string>("""
            SELECT jsonb_build_array(r.result_document, o.event_id, o.payload)::text
            FROM ghostlap.match_result r
            JOIN ghostlap.match_session s USING (match_id)
            JOIN ghostlap.outbox o ON o.aggregate_id = r.match_id::text
            WHERE r.match_id = @m AND r.final_tick = 2700 AND s.terminal_decision = 'ResultCommitted'
              AND o.event_type = 'ghostlap.MatchFinalizedV1' AND o.payload::jsonb ->> 'resultDigest' = r.result_digest
              AND (SELECT count(*) FROM ghostlap.outbox WHERE aggregate_id = r.match_id::text) = 1
            """, ("m", Guid.Parse(matchId)));
        Assert.NotNull(row);
        using var parsed = JsonDocument.Parse(row);
        return new(parsed.RootElement[0].GetString()!, parsed.RootElement[1].GetString()!, parsed.RootElement[2].GetRawText());
    }

    private static async Task<ProfileLine> ReadProfileAsync(string path, Func<ProfileLine, bool> condition)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(15))
        {
            if (File.Exists(path))
            {
                foreach (var json in (await File.ReadAllLinesAsync(path)).Reverse().Where(l => l.EndsWith('}')))
                {
                    var line = JsonSerializer.Deserialize<ProfileLine>(json, DemoJson)!;
                    if (condition(line) && line.Kind != "start")
                    {
                        return line;
                    }
                }
            }
            await Task.Delay(200);
        }
        throw new TimeoutException($"Missing expected Realtime profile sample in {path}");
    }

    private static async Task WaitForRoomClosedAsync(GameClient watcher, string roomId)
    {
        await Eventually(() => Volatile.Read(ref watcher.Tab.Closed) > 0, $"RoomClosed for {roomId}");
        Assert.Equal(GhostLapFailure.RoomUnavailable, (await watcher.Tab.GetAsync(roomId)).Code);
    }

    private static Task<GhostLapReply<RoomCommandResult>> RematchAsync(GameClient c, string roomId) =>
        c.Tab.Connection.InvokeAsync<GhostLapReply<RoomCommandResult>>(GhostLapWire.RematchMethod, new RoomCommand(c.Tab.Req(), roomId));

    private static async Task<SnapshotFrame> FetchResyncAsync(GameClient c) =>
        RequireDemo(await c.Tab.Connection.InvokeAsync<GhostLapReply<SnapshotFrame>>(GhostLapWire.ResyncMethod));

    private static T RequireDemo<T>(GhostLapReply<T> reply)
    {
        Assert.True(reply.Ok, reply.Code);
        return reply.Value!;
    }

    private static GameClient NewKestrelClient(string address, GhostLapHubTests.GuestLogin guest)
    {
        var connection = new HubConnectionBuilder().WithUrl(address + GhostLapWire.HubPath, options =>
        {
            options.Transports = HttpTransportType.WebSockets;
            options.SkipNegotiation = true;
            options.WebSocketFactory = async (context, ct) =>
            {
                var socket = new ClientWebSocket();
                try
                {
                    socket.Options.SetRequestHeader("Cookie", Flows.GuestCookie + "=" + guest.Cookie);
                    socket.Options.SetRequestHeader("Origin", address);
                    await socket.ConnectAsync(context.Uri, ct);
                    return socket;
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            };
        }).Build();
        connection.KeepAliveInterval = TimeSpan.FromSeconds(5);
        return new GameClient(new GhostLapHubTests.Tab(connection), new NetworkConditions(), guest, serverTick: null);
    }
}
