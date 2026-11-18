extern alias realtime;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Channels;
using GhostLap.Contracts.Multiplayer;
using GhostLap.Contracts.Solo;
using Integration.Tests.Support;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using realtime::GhostLapHost;
using Xunit;

namespace Integration.Tests.GhostLap;

/// <summary>
/// Task 11.1 quick host smoke over the REAL Hosts.Realtime composition, a real guest cookie from the real Hosts.Api, and the real
/// <c>sands_realtime_solo</c> login (only <c>marrow_timeline_runtime</c> + <c>ghostlap_runtime</c>). It plays a few seconds of a run and
/// disconnects: it proves the composition, the grants for an interrupted run, and that an interrupted run is never listed or readable
/// as an original. It does NOT play a 90-second run to Saved over the host, and it is not the owner/foreign-user or cold-reload
/// verification; those are delegated to Codex.
/// </summary>
[Collection(IdentityCollection.Name)]
[Trait("Category", "Integration")]
public sealed class GhostLapSoloHostSmokeTests(IdentityDatabase db)
{
    [Fact]
    public async Task Same_active_key_with_a_different_mascot_conflicts()
    {
        var secret = IdentityApiFactory.NewMasterSecret();
        await using var api = new IdentityApiFactory(db, secret);
        await using var hub = new RealtimeHostFactory(db, secret, new Dictionary<string, string?>
        {
            ["ConnectionStrings:GhostLapSolo"] = db.RealtimeSoloConnectionString,
        });
        var browser = api.NewBrowser();
        await browser.PostAsync("/auth/guest/start");
        await using var connection = hub.ConnectTo(GhostLapSoloWire.HubPath, guestCookie: browser.Cookies[Flows.GuestCookie]);
        await connection.StartAsync();

        var started = await connection.InvokeAsync<GhostLapReply<SoloRunStarted>>(GhostLapSoloWire.StartRunMethod, new StartSoloRequest("conflict", 1));
        Assert.True(started.Ok, started.Code);
        var conflict = await connection.InvokeAsync<GhostLapReply<SoloRunStarted>>(GhostLapSoloWire.StartRunMethod, new StartSoloRequest("conflict", 2));
        Assert.False(conflict.Ok);
        Assert.Equal(SoloFailure.Conflict, conflict.Code);
    }

    [Fact]
    public async Task Revoked_guest_cannot_open_a_new_input_stream_on_an_existing_connection()
    {
        var secret = IdentityApiFactory.NewMasterSecret();
        await using var api = new IdentityApiFactory(db, secret);
        await using var hub = new RealtimeHostFactory(db, secret, new Dictionary<string, string?>
        {
            ["ConnectionStrings:GhostLapSolo"] = db.RealtimeSoloConnectionString,
        });
        var browser = api.NewBrowser();
        await browser.PostAsync("/auth/guest/start");
        await using var connection = hub.ConnectTo(GhostLapSoloWire.HubPath, guestCookie: browser.Cookies[Flows.GuestCookie]);
        await connection.StartAsync();
        var started = await connection.InvokeAsync<GhostLapReply<SoloRunStarted>>(GhostLapSoloWire.StartRunMethod, new StartSoloRequest("revoked-stream", 1));
        Assert.True(started.Ok, started.Code);
        Assert.Equal(HttpStatusCode.NoContent, (await browser.PostAsync("/auth/guest/signout", csrfCookie: Flows.GuestCsrf)).Status);

        var channel = Channel.CreateUnbounded<SoloInputItem>();
        channel.Writer.Complete();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Closed += _ => { closed.TrySetResult(); return Task.CompletedTask; };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var result = await connection.InvokeAsync<GhostLapReply<InputStreamSummary>>(GhostLapSoloWire.StreamInputsMethod, channel.Reader, timeout.Token);
            Assert.False(result.Ok);
            Assert.Equal(SoloFailure.Forbidden, result.Code);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException)
        {
            await closed.Task.WaitAsync(timeout.Token);
        }
    }

    [Fact]
    public async Task Solo_hub_streams_frames_and_a_disconnected_run_is_interrupted_unlisted_and_not_an_original()
    {
        var secret = IdentityApiFactory.NewMasterSecret();
        await using var api = new IdentityApiFactory(db, secret);
        await using var hub = new RealtimeHostFactory(db, secret, new Dictionary<string, string?>
        {
            ["ConnectionStrings:GhostLapSolo"] = db.RealtimeSoloConnectionString,
        });
        var browser = api.NewBrowser();
        var start = await browser.PostAsync("/auth/guest/start");
        var cookie = browser.Cookies[Flows.GuestCookie];
        var runs = hub.Services.GetRequiredService<SoloRunRegistry>();

        var frames = new List<SoloFrame>();
        var connection = hub.ConnectTo(GhostLapSoloWire.HubPath, guestCookie: cookie);
        connection.On<SoloFrame>(GhostLapSoloWire.FrameClientMethod, f => { lock (frames) { frames.Add(f); } });
        await connection.StartAsync();

        var started = await connection.InvokeAsync<GhostLapReply<SoloRunStarted>>(GhostLapSoloWire.StartRunMethod, new StartSoloRequest("smoke-run", 1));
        Assert.True(started.Ok, started.Code);
        Assert.Equal(GhostLapSoloWire.TickRateHz, started.Value!.TickRateHz);
        Assert.Equal(2700, started.Value.RoundTicks);
        Assert.Equal(1, runs.ActiveRuns);

        // The same start again is the same run; a different key while it is live is refused.
        var again = await connection.InvokeAsync<GhostLapReply<SoloRunStarted>>(GhostLapSoloWire.StartRunMethod, new StartSoloRequest("smoke-run", 1));
        Assert.Equal(started.Value.RunId, again.Value!.RunId);
        var other = await connection.InvokeAsync<GhostLapReply<SoloRunStarted>>(GhostLapSoloWire.StartRunMethod, new StartSoloRequest("smoke-other", 1));
        Assert.Equal(SoloFailure.RunActive, other.Code);

        // Input on the one client-to-server stream, for about two seconds of ticks.
        var channel = Channel.CreateUnbounded<SoloInputItem>();
        var summary = connection.InvokeAsync<GhostLapReply<InputStreamSummary>>(GhostLapSoloWire.StreamInputsMethod, channel.Reader);
        for (var seq = 1; seq <= 40; seq++)
        {
            await channel.Writer.WriteAsync(new SoloInputItem(seq, seq, 1, 0, false));
            await Task.Delay(50);
        }

        channel.Writer.Complete();
        var streamed = await summary;
        Assert.True(streamed.Ok, streamed.Code);
        Assert.Equal(40, streamed.Value!.Received);

        SoloFrame[] seen;
        lock (frames)
        {
            seen = [.. frames];
        }

        Assert.True(seen.Length >= 20, $"expected live frames, got {seen.Length}");
        Assert.Equal(seen.Select(f => f.Tick).OrderBy(t => t), seen.Select(f => f.Tick));
        Assert.All(seen, f => Assert.False(f.Final));
        Assert.Contains(seen, f => f.XFixed != seen[0].XFixed); // the admitted inputs moved the server-owned player

        // Disconnect mid-run: the run is interrupted (no live run, a Recording row) and is never an original.
        await connection.DisposeAsync();
        await WaitAsync(() => runs.ActiveRuns == 0);
        Assert.Equal("Recording", await db.ScalarAsync<string>(
            "SELECT status FROM ghostlap.solo_originals WHERE operation_key = @k AND owner_subject_id = @o",
            ("k", "smoke-run"), ("o", start.Str("subject"))));

        using var http = hub.CreateClient();
        var list = await Get(http, cookie, GhostLapSoloWire.OriginalsRoute);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal("[]", (await list.Content.ReadAsStringAsync()).Trim());
        var item = await Get(http, cookie, $"{GhostLapSoloWire.OriginalsRoute}/{started.Value.RunId}");
        Assert.Equal(HttpStatusCode.NotFound, item.StatusCode);
        var anonymous = await http.GetAsync(GhostLapSoloWire.OriginalsRoute);
        Assert.NotEqual(HttpStatusCode.OK, anonymous.StatusCode);

        // A new connection after the interruption: FinishRun is honest (Failed/not_complete) and the key is not silently resumed.
        await using var next = hub.ConnectTo(GhostLapSoloWire.HubPath, guestCookie: cookie);
        await next.StartAsync();
        var finish = await next.InvokeAsync<GhostLapReply<SoloSaveView>>(GhostLapSoloWire.FinishRunMethod, "smoke-run");
        Assert.True(finish.Ok, finish.Code);
        Assert.Equal(SoloSaveStatuses.Failed, finish.Value!.Status);
        Assert.Equal("not_complete", finish.Value.Code);
        Assert.Null(finish.Value.Original);
        var restart = await next.InvokeAsync<GhostLapReply<SoloRunStarted>>(GhostLapSoloWire.StartRunMethod, new StartSoloRequest("smoke-run", 1));
        Assert.Equal(SoloFailure.Interrupted, restart.Code);
    }

    /// <summary>
    /// Task 11.3 composition smoke (no 90-second round): the real host wires the challenge registry and hub methods, a guest cannot
    /// prepare an unknown or malformed original, cannot start without a Ready trace, and has nothing to cancel or finish.
    /// </summary>
    [Fact]
    public async Task Challenge_methods_are_composed_and_refuse_unprepared_unknown_and_malformed_requests()
    {
        var secret = IdentityApiFactory.NewMasterSecret();
        await using var api = new IdentityApiFactory(db, secret);
        await using var hub = new RealtimeHostFactory(db, secret, new Dictionary<string, string?>
        {
            ["ConnectionStrings:GhostLapSolo"] = db.RealtimeSoloConnectionString,
        });
        var browser = api.NewBrowser();
        await browser.PostAsync("/auth/guest/start");
        var challenges = hub.Services.GetRequiredService<SoloChallengeRegistry>();
        await using var connection = hub.ConnectTo(GhostLapSoloWire.HubPath, guestCookie: browser.Cookies[Flows.GuestCookie]);
        await connection.StartAsync();

        var malformed = await connection.InvokeAsync<GhostLapReply<ChallengePrepared>>(
            GhostLapChallengeWire.PrepareChallengeMethod, new PrepareChallengeRequest("not-a-guid"));
        Assert.Equal((false, SoloFailure.InvalidRequest), (malformed.Ok, malformed.Code));
        var unknown = await connection.InvokeAsync<GhostLapReply<ChallengePrepared>>(
            GhostLapChallengeWire.PrepareChallengeMethod, new PrepareChallengeRequest(Guid.NewGuid().ToString("D")));
        Assert.Equal((false, SoloFailure.NotFound), (unknown.Ok, unknown.Code));
        Assert.Equal(0, challenges.Active);

        var start = await connection.InvokeAsync<GhostLapReply<ChallengeStarted>>(
            GhostLapChallengeWire.StartChallengeMethod, new StartChallengeRequest("composition"));
        Assert.Equal((false, ChallengeFailure.NotReady), (start.Ok, start.Code));
        var cancel = await connection.InvokeAsync<GhostLapReply<bool>>(GhostLapChallengeWire.CancelChallengeMethod);
        Assert.True(cancel.Ok);
        Assert.False(cancel.Value);
        var finish = await connection.InvokeAsync<GhostLapReply<ChallengeFinishView>>(GhostLapChallengeWire.FinishChallengeMethod, "composition");
        Assert.True(finish.Ok, finish.Code);
        Assert.Equal((ChallengeFinishStatuses.Failed, SoloFailure.NotFound), (finish.Value!.Status, finish.Value.Code));
        Assert.Null(finish.Value.Result);
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM ghostlap.solo_branches WHERE operation_key = 'challenge.composition'"));
    }

    private static Task<HttpResponseMessage> Get(HttpClient http, string cookie, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation("Cookie", $"{Flows.GuestCookie}={cookie}");
        return http.SendAsync(request);
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "condition not reached within 10 s");
            await Task.Delay(50);
        }
    }
}
