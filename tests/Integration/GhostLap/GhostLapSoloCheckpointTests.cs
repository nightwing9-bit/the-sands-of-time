extern alias realtime;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using GhostLap.Contracts.Multiplayer;
using GhostLap.Contracts.Solo;
using GhostLap.Rules;
using GhostLap.Simulation;
using GhostLap.Timeline;
using Integration.Tests.Support;
using Marrow.Infrastructure.Timeline;
using Marrow.Timeline;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using Xunit.Abstractions;
using realtime::GhostLapHost;

namespace Integration.Tests.GhostLap;

[Collection(IdentityCollection.Name)]
[Trait("Category", "Integration")]
public sealed class GhostLapSoloCheckpointTests(IdentityDatabase db, ITestOutputHelper output)
{
    [Fact]
    public async Task Full_solo_original_survives_lost_reply_and_a_cold_Kestrel_restart()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = timeout.Token;
        var secret = IdentityApiFactory.NewMasterSecret();
        await using var api = new IdentityApiFactory(db, secret);
        var owner = api.NewBrowser();
        var subject = (await owner.PostAsync("/auth/guest/start")).Str("subject");
        var cookie = owner.Cookies[Flows.GuestCookie];
        var foreign = api.NewBrowser();
        Assert.NotEqual(subject, (await foreign.PostAsync("/auth/guest/start")).Str("subject"));
        var otherCookie = foreign.Cookies[Flows.GuestCookie];
        var key = "checkpoint-" + Guid.NewGuid().ToString("N");
        var soloConnection = new NpgsqlConnectionStringBuilder(db.RealtimeSoloConnectionString)
        {
            Timeout = 2,
        }.ConnectionString;

        SoloFrame final;
        SoloRunStarted started;
        SoloOriginalView committed;
        int originalPid;
        await using (var host = await Kestrel.StartAsync(db, secret, soloConnection, ct))
        {
            originalPid = host.Pid;
            await using var hub = await host.ConnectAsync(cookie, ct);
            var frames = Channel.CreateUnbounded<(SoloFrame Frame, TimeSpan At)>();
            var ended = new TaskCompletionSource<SoloRunEnded>(TaskCreationOptions.RunContinuationsAsynchronously);
            var watch = Stopwatch.StartNew();
            hub.On<SoloFrame>(GhostLapSoloWire.FrameClientMethod, frame => { frames.Writer.TryWrite((frame, watch.Elapsed)); });
            hub.On<SoloRunEnded>(GhostLapSoloWire.RunEndedClientMethod, end => { ended.TrySetResult(end); });
            var start = await hub.InvokeAsync<GhostLapReply<SoloRunStarted>>(GhostLapSoloWire.StartRunMethod, new StartSoloRequest(key, 2), ct);
            started = Value(start);
            Assert.Equal((30, 2700), (started.TickRateHz, started.RoundTicks));

            var inputs = Channel.CreateUnbounded<SoloInputItem>();
            var stream = hub.InvokeAsync<GhostLapReply<InputStreamSummary>>(GhostLapSoloWire.StreamInputsMethod, inputs.Reader, ct);
            var plan = CandlePlan.Create(started.Seed);
            var visited = new HashSet<string>();
            var sent = 0;
            var detour = 0;
            var wasMoving = false;
            var maxGap = TimeSpan.Zero;
            var first = TimeSpan.Zero;
            var last = TimeSpan.Zero;
            var previous = new SoloFrame(0, 0, 0, 0, 0, 0, false);
            var frameCount = 0;
            while (true)
            {
                var (frame, at) = await frames.Reader.ReadAsync(ct);
                Assert.True(frame.Tick > previous.Tick);
                if (++frameCount == 1)
                {
                    first = at;
                }
                else
                {
                    maxGap = TimeSpan.FromTicks(Math.Max(maxGap.Ticks, (at - last).Ticks));
                }

                last = at;
                if (frame.Final)
                {
                    final = frame;
                    break;
                }

                var active = plan.ActiveAt(frame.Tick).ToArray();
                if (frame.Charge > previous.Charge)
                {
                    var collected = active.OrderBy(c => Distance(c, frame)).First();
                    visited.Add(collected.Id);
                }

                var target = active.Where(c => !visited.Contains(c.Id)).OrderBy(c => Distance(c, frame)).FirstOrDefault();
                if (wasMoving && frame.XFixed == previous.XFixed && frame.YFixed == previous.YFixed && detour == 0)
                {
                    detour = 18;
                }

                var dx = target is null ? 0 : target.Site.X - frame.XFixed;
                var dy = target is null ? 0 : target.Site.Y - frame.YFixed;
                if (detour > 0)
                {
                    detour--;
                    (dx, dy) = (frame.Tick / 23 % 2 == 0 ? 1 : -1, frame.Tick / 41 % 2 == 0 ? 1 : -1);
                }

                wasMoving = dx != 0 || dy != 0;
                await inputs.Writer.WriteAsync(new SoloInputItem(++sent, frame.Tick + 1, (sbyte)Math.Sign(dx), (sbyte)Math.Sign(dy),
                    frame.Charge == 4 && frame.Tick % 97 == 0), ct);
                previous = frame;
            }

            inputs.Writer.Complete();
            var ingress = Value(await stream);
            Assert.Equal(sent, ingress.Received);
            Assert.True(ingress.Received > ingress.Rejected);
            Assert.Equal("final", (await ended.Task.WaitAsync(ct)).Reason);
            Assert.Equal(2700, final.Tick);
            Assert.InRange((last - first).TotalSeconds, 88, 125);
            output.WriteLine($"Realtime PID {originalPid}, WebSockets, frames {frameCount}, duration {(last - first).TotalSeconds:F3}s, max frame gap {maxGap.TotalMilliseconds:F1}ms, inputs {ingress.Received}, rejected {ingress.Rejected}");

            var id = Guid.Parse(started.RunId);
            Assert.Equal("sands_realtime_solo", await db.ScalarAsync<string>(
                "SELECT DISTINCT usename::text FROM pg_stat_activity WHERE application_name = @app",
                ("app", GhostLapSoloDataSource.ApplicationName)));
            for (var attempt = 0; ; attempt++)
            {
                if (await db.ScalarAsync<string>("SELECT status FROM ghostlap.solo_originals WHERE timeline_id = @id", ("id", id)) == "Complete")
                {
                    break;
                }

                Assert.True(attempt < 120, "The automatic save did not commit within 30 seconds.");
                await Task.Delay(250, ct);
            }
            await host.WaitForSavedLogAsync(started.RunId, ct);

            // The injector consumes the wire completion and drops the connection before exposing any result to a caller.
            await host.DropFinishReplyAsync(cookie, key, ct);

            using var item = await host.GetAsync(Item(started.RunId), cookie, ct);
            Assert.Equal(HttpStatusCode.OK, item.StatusCode);
            committed = (await item.Content.ReadFromJsonAsync<SoloOriginalView>(ct))!;
            Assert.Equal((started.RunId, started.Seed, final.Tick, final.TotalScore),
                (committed.Id, committed.Seed, committed.FinalTick, committed.Score!.Value));
            Assert.Equal((started.MascotId, started.RulesVersion, started.LayoutVersion),
                (committed.MascotId, committed.RulesVersion, committed.LayoutVersion));
            var versions = GhostLapTimelineVersions.V1;
            Assert.Equal((versions.DomainId, versions.ReducerVersion, versions.InputSchemaVersion, versions.StateSchemaVersion, versions.ContextSchemaVersion),
                (committed.DomainId, committed.ReducerVersion, committed.InputSchemaVersion, committed.StateSchemaVersion, committed.ContextSchemaVersion));

            host.AssertLogHygiene(db.RuntimePasswords.Append(secret).Append(cookie).Append(otherCookie));
        }

        var resumed = await owner.PostAsync("/auth/guest/start", csrfCookie: Flows.GuestCsrf);
        Assert.True(resumed.Json.GetProperty("resumed").GetBoolean());
        Assert.Equal(subject, resumed.Str("subject"));

        var unreachable = new NpgsqlConnectionStringBuilder(soloConnection) { Port = 1 }.ConnectionString;
        await using (var failing = await Kestrel.StartAsync(db, secret, unreachable, ct))
        {
            await using var hub = await failing.ConnectAsync(cookie, ct);
            Assert.Equal(new SoloSaveView(SoloSaveStatuses.SavePending, null, SoloFailure.Unavailable), await FinishAsync(hub, key, ct));
            failing.AssertLogHygiene(db.RuntimePasswords.Append(secret).Append(cookie));
        }

        await using (var cold = await Kestrel.StartAsync(db, secret, soloConnection, ct))
        {
            Assert.NotEqual(originalPid, cold.Pid);
            using var list = await cold.GetAsync(GhostLapSoloWire.OriginalsRoute, cookie, ct);
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            Assert.Equal(committed with { Score = null }, Assert.Single((await list.Content.ReadFromJsonAsync<SoloOriginalView[]>(ct))!));
            using var item = await cold.GetAsync(Item(committed.Id), cookie, ct);
            Assert.Equal(HttpStatusCode.OK, item.StatusCode);
            Assert.Equal(committed, await item.Content.ReadFromJsonAsync<SoloOriginalView>(ct));

            await using var hub = await cold.ConnectAsync(cookie, ct);
            Assert.Equal(new SoloSaveView(SoloSaveStatuses.Saved, committed, null), await FinishAsync(hub, key, ct));
            Assert.Equal(new SoloSaveView(SoloSaveStatuses.Saved, committed, null), await FinishAsync(hub, key, ct));
            var conflict = await hub.InvokeAsync<GhostLapReply<SoloRunStarted>>(GhostLapSoloWire.StartRunMethod, new StartSoloRequest(key, 3), ct);
            Assert.False(conflict.Ok);
            Assert.Equal(SoloFailure.Conflict, conflict.Code);

            using var foreignList = await cold.GetAsync(GhostLapSoloWire.OriginalsRoute, otherCookie, ct);
            Assert.Equal(HttpStatusCode.OK, foreignList.StatusCode);
            Assert.Empty((await foreignList.Content.ReadFromJsonAsync<SoloOriginalView[]>(ct))!);
            using var foreignItem = await cold.GetAsync(Item(committed.Id), otherCookie, ct);
            using var unknown = await cold.GetAsync(Item(Guid.NewGuid().ToString("D")), otherCookie, ct);
            using var malformed = await cold.GetAsync(Item("not-a-guid"), otherCookie, ct);
            foreach (var response in new[] { foreignItem, unknown, malformed })
            {
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                Assert.Equal("{\"code\":\"not_found\"}", await response.Content.ReadAsStringAsync(ct));
            }

            await using var otherHub = await cold.ConnectAsync(otherCookie, ct);
            Assert.Equal(new SoloSaveView(SoloSaveStatuses.Failed, null, SoloFailure.NotFound), await FinishAsync(otherHub, key, ct));
            foreach (var invalid in new[] { null, "invalid" })
            {
                using var unauthorized = await cold.GetAsync(Item(committed.Id), invalid, ct);
                Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
            }

            await VerifyReplayAsync(committed, final, subject, ct);
            output.WriteLine($"Cold Realtime PID {cold.Pid}, original {committed.Id}, operation {key[..19]}…, seed {committed.Seed}, {committed.RulesVersion}/{committed.LayoutVersion} {committed.InputSchemaVersion}/{committed.StateSchemaVersion}/{committed.ContextSchemaVersion}, final tick {final.Tick}, score {final.TotalScore}, state hash {committed.FinalStateHash}, record hash {committed.LastRecordHash}");

            await using var revokedStart = await cold.ConnectAsync(cookie, ct);
            Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync("/auth/guest/signout", csrfCookie: Flows.GuestCsrf)).Status);
            using var revoked = await cold.GetAsync(Item(committed.Id), cookie, ct);
            Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
            await AssertRevokedAsync(hub, () => hub.InvokeAsync<GhostLapReply<SoloSaveView>>(GhostLapSoloWire.FinishRunMethod, key, ct), ct);
            await AssertRevokedAsync(revokedStart, () => revokedStart.InvokeAsync<GhostLapReply<SoloRunStarted>>(
                GhostLapSoloWire.StartRunMethod, new StartSoloRequest("revoked-start", 2), ct), ct);
            Assert.Equal(0, await db.ScalarAsync<long>("SELECT count(*) FROM ghostlap.solo_originals WHERE operation_key = 'revoked-start'"));
            cold.AssertLogHygiene(db.RuntimePasswords.Append(secret).Append(cookie).Append(otherCookie));
        }

        await using var disabled = await Kestrel.StartAsync(db, secret, null, ct);
        using var missing = await disabled.GetAsync(GhostLapSoloWire.OriginalsRoute, otherCookie, ct);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var missingHub = await disabled.GetAsync(GhostLapSoloWire.HubPath, null, ct);
        Assert.Equal(HttpStatusCode.NotFound, missingHub.StatusCode);
        await using var multiplayer = await disabled.ConnectAsync(otherCookie, ct, GhostLapWire.HubPath);
        Assert.Equal(HubConnectionState.Connected, multiplayer.State);
    }

    private async Task VerifyReplayAsync(SoloOriginalView saved, SoloFrame live, string owner, CancellationToken ct)
    {
        var id = Guid.Parse(saved.Id);
        Assert.Equal(2700, await db.ScalarAsync<long>("SELECT count(*) FROM marrow_timeline.steps WHERE timeline_id = @id AND position BETWEEN 1 AND 2700", ("id", id)));
        Assert.Equal(46, await db.ScalarAsync<long>("SELECT count(*) FROM marrow_timeline.checkpoints WHERE timeline_id = @id", ("id", id)));
        var inputs = await db.ScalarAsync<long>("SELECT count(*) FROM marrow_timeline.step_inputs WHERE timeline_id = @id", ("id", id));
        Assert.True(inputs > 0);
        Assert.Equal(1, await db.ScalarAsync<long>("SELECT count(*) FROM ghostlap.solo_originals WHERE owner_subject_id = @owner AND operation_key = @key",
            ("owner", owner), ("key", saved.OperationKey)));

        var services = new ServiceCollection();
        services.AddSingleton(_ => NpgsqlDataSource.Create(db.RealtimeSoloConnectionString));
        services.AddMarrowTimeline(GhostLapTimelineVersions.All, GhostLapTimelineVersions.CheckpointInterval);
        await using var provider = services.BuildServiceProvider();
        var engine = provider.GetRequiredService<ITimelineEngine<GameState, GameInput, GameContext>>();
        var result = await engine.ReplayAsync(new TimelineId(id), ct);
        Assert.True(result.IsSuccess, result.ToString());
        var replay = result.Value;
        Assert.Equal(HistoryStatus.Verified, replay.Status);
        Assert.Equal((saved.FinalTick, saved.FinalStateHash, saved.LastRecordHash), (replay.FinalPosition, replay.FinalStateHash, replay.LastRecordHash));
        var player = Assert.Single(replay.FinalState.Players);
        Assert.Equal((live.XFixed, live.YFixed, live.Charge, live.OverlitDeadlineTick, live.TotalScore),
            (player.XFixed, player.YFixed, player.Charge, player.OverlitDeadlineTick, player.TotalScore));
        Assert.True(player.DistanceFixed > 0);
        Assert.True(player.Pickups > 0);
        output.WriteLine($"Independent replay: {inputs} canonical input rows, 46 checkpoints, {player.DistanceFixed} fixed distance, {player.Pickups} pickups, {player.Bursts} Bursts");
    }

    private static async Task AssertRevokedAsync<T>(HubConnection connection, Func<Task<GhostLapReply<T>>> invoke, CancellationToken ct)
    {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Closed += _ => { closed.TrySetResult(); return Task.CompletedTask; };
        try
        {
            var reply = await invoke();
            Assert.False(reply.Ok);
            Assert.Equal(SoloFailure.Forbidden, reply.Code);
        }
        catch (Exception e) when (e is OperationCanceledException or InvalidOperationException or IOException)
        {
            await closed.Task.WaitAsync(ct);
        }
    }

    private static long Distance(CandleSpec candle, SoloFrame frame) =>
        Math.Abs((long)candle.Site.X - frame.XFixed) + Math.Abs((long)candle.Site.Y - frame.YFixed);

    private static string Item(string id) => $"{GhostLapSoloWire.OriginalsRoute}/{id}";

    private static T Value<T>(GhostLapReply<T> reply)
    {
        Assert.True(reply.Ok, reply.Code);
        return reply.Value!;
    }

    private static async Task<SoloSaveView> FinishAsync(HubConnection hub, string key, CancellationToken ct) =>
        Value(await hub.InvokeAsync<GhostLapReply<SoloSaveView>>(GhostLapSoloWire.FinishRunMethod, key, ct));

    private sealed class Kestrel(Process process, Uri origin, ConcurrentQueue<string> logs) : IAsyncDisposable
    {
        private readonly HttpClient _http = new() { BaseAddress = origin };
        public int Pid => process.Id;

        public static async Task<Kestrel> StartAsync(IdentityDatabase db, string secret, string? soloConnection, CancellationToken ct)
        {
            var settings = new ProcessStartInfo("dotnet", "Hosts.Realtime.dll")
            {
                WorkingDirectory = AppContext.BaseDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            settings.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
            settings.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
            settings.Environment["DOTNET_ENVIRONMENT"] = "Production";
            settings.Environment["ConnectionStrings__Postgres"] = db.RealtimeConnectionString;
            settings.Environment["ConnectionStrings__Redis"] = "localhost:6399,abortConnect=false";
            settings.Environment["ConnectionStrings__RevenantLedger"] = "";
            settings.Environment["ConnectionStrings__GhostLapSolo"] = soloConnection ?? "";
            settings.Environment["Identity__MasterSecret"] = secret;
            settings.Environment["Logging__LogLevel__Microsoft.Hosting.Lifetime"] = "Information";
            var listening = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
            var logs = new ConcurrentQueue<string>();
            using var started = CancellationTokenSource.CreateLinkedTokenSource(ct);
            started.CancelAfter(TimeSpan.FromSeconds(30));
            var process = new Process { StartInfo = settings, EnableRaisingEvents = true };
            process.Exited += (_, _) => listening.TrySetException(new InvalidOperationException($"Realtime exited with code {process.ExitCode}."));
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is { } line)
                {
                    logs.Enqueue(line);
                    const string prefix = "Now listening on: ";
                    if (line.StartsWith('{') && line.Contains(prefix, StringComparison.Ordinal))
                    {
                        using var json = JsonDocument.Parse(line);
                        var message = json.RootElement.GetProperty("Message").GetString();
                        if (message is not null && message.StartsWith(prefix, StringComparison.Ordinal)
                            && Uri.TryCreate(message[prefix.Length..], UriKind.Absolute, out var address))
                        {
                            listening.TrySetResult(address);
                        }
                    }
                }
            };
            process.ErrorDataReceived += (_, e) => { if (e.Data is { } line) logs.Enqueue(line); };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            try
            {
                return new Kestrel(process, await listening.Task.WaitAsync(started.Token), logs);
            }
            catch
            {
                await StopAsync(process);
                throw;
            }
        }

        public async Task<HubConnection> ConnectAsync(string cookie, CancellationToken ct, string path = GhostLapSoloWire.HubPath)
        {
            var hub = new HubConnectionBuilder().WithUrl(new Uri(origin, path), options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.SkipNegotiation = true;
                options.Headers["Cookie"] = $"{Flows.GuestCookie}={cookie}";
                options.Headers["Origin"] = origin.GetLeftPart(UriPartial.Authority);
            }).Build();
            try
            {
                await hub.StartAsync(ct);
                return hub;
            }
            catch
            {
                await hub.DisposeAsync();
                throw;
            }
        }

        public async Task DropFinishReplyAsync(string cookie, string key, CancellationToken ct)
        {
            using var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Cookie", $"{Flows.GuestCookie}={cookie}");
            socket.Options.SetRequestHeader("Origin", origin.GetLeftPart(UriPartial.Authority));
            var ws = new UriBuilder(origin) { Scheme = "ws", Path = GhostLapSoloWire.HubPath };
            await socket.ConnectAsync(ws.Uri, ct);
            await SendAsync("{\"protocol\":\"json\",\"version\":1}");
            await SendAsync(JsonSerializer.Serialize(new { type = 1, invocationId = "lost", target = GhostLapSoloWire.FinishRunMethod, arguments = new[] { key } }));
            using var reader = new StreamReader(WebSocketStream.Create(socket, WebSocketMessageType.Text, ownsWebSocket: false));
            var ch = new char[1];
            while (true)
            {
                var message = new StringBuilder();
                while (true)
                {
                    Assert.Equal(1, await reader.ReadAsync(ch.AsMemory(), ct));
                    if (ch[0] == '\u001e') break;
                    message.Append(ch[0]);
                }
                using var document = JsonDocument.Parse(message.ToString());
                if (document.RootElement.TryGetProperty("invocationId", out var invocation) && invocation.GetString() == "lost")
                {
                    Assert.Equal(3, document.RootElement.GetProperty("type").GetInt32());
                    var reply = document.RootElement.GetProperty("result");
                    Assert.Equal(SoloSaveStatuses.Saved, reply.GetProperty("value").GetProperty("status").GetString());
                    socket.Abort();
                    return;
                }
            }

            async Task SendAsync(string message) =>
                await socket.SendAsync(Encoding.UTF8.GetBytes(message + '\u001e'), WebSocketMessageType.Text, true, ct);
        }

        public async Task<HttpResponseMessage> GetAsync(string route, string? cookie, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, route);
            if (cookie is not null)
            {
                request.Headers.Add("Cookie", $"{Flows.GuestCookie}={cookie}");
            }

            return await _http.SendAsync(request, ct);
        }

        public async Task WaitForSavedLogAsync(string id, CancellationToken ct)
        {
            for (var attempt = 0; ; attempt++)
            {
                if (logs.Any(line => line.Contains($"run {id}, status Saved", StringComparison.Ordinal))) return;
                Assert.True(attempt < 120, "The host did not report a verified Saved outcome.");
                await Task.Delay(250, ct);
            }
        }

        public void AssertLogHygiene(IEnumerable<string> secrets) =>
            Assert.True(!secrets.Any(secret => logs.Any(line => line.Contains(secret, StringComparison.Ordinal))), "Realtime output contains a credential.");

        public async ValueTask DisposeAsync()
        {
            _http.Dispose();
            await StopAsync(process);
        }

        private static async Task StopAsync(Process child)
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync();
            child.Dispose();
        }
    }
}
