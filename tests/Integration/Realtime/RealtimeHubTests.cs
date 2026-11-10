using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using Integration.Tests.Support;
using Marrow.Infrastructure.Identity;
using Marrow.Infrastructure.Realtime;
using Marrow.Kernel.Realtime;
using Marrow.Kernel.Security;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Integration.Tests.Realtime;

/// <summary>
/// R03.06, R03.08, R03.09, R03.10: the real <c>Hosts.Realtime</c> hub over real SignalR WebSocket connections, with staff
/// tokens and guest cookies issued by the real <c>Hosts.Api</c> against the real identity database.
/// </summary>
[Collection(IdentityCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RealtimeHubTests(IdentityDatabase db)
{
    private sealed class Stack : IAsyncDisposable
    {
        public required IdentityApiFactory Api { get; init; }
        public required RealtimeHostFactory Hub { get; init; }

        public async ValueTask DisposeAsync()
        {
            await Hub.DisposeAsync();
            await Api.DisposeAsync();
        }
    }

    private Stack NewStack(Dictionary<string, string?>? realtimeSettings = null, bool domainPolicies = true)
    {
        var secret = IdentityApiFactory.NewMasterSecret();
        return new Stack
        {
            Api = new IdentityApiFactory(db, secret),
            Hub = new RealtimeHostFactory(db, secret, realtimeSettings, domainPolicies),
        };
    }

    private static async Task<(string Cookie, string Subject)> GuestAsync(Stack stack)
    {
        var browser = stack.Api.NewBrowser();
        var start = await browser.PostAsync("/auth/guest/start");
        return (browser.Cookies[Flows.GuestCookie], start.Str("subject"));
    }

    private static async Task<(string Access, string Subject)> StaffAsync(Stack stack, string role = IdentityRoles.Supervisor)
    {
        var staff = await stack.Api.CreateStaffAsync(role);
        var (_, access, _) = await Flows.LoginAsync(stack.Api, staff);
        return (access, "staff:" + staff.Id.ToString("D"));
    }

    private static async Task<ProbeSubscriber> MemberAsync(Stack stack, string room, bool autoReconnect = false, ConcurrentQueue<WebSocket>? sockets = null)
    {
        var (cookie, subject) = await GuestAsync(stack);
        stack.Hub.Domain.AddMember(subject, PrincipalKind.Guest, room);
        var subscriber = new ProbeSubscriber(stack.Hub.Connect(guestCookie: cookie, autoReconnect: autoReconnect, sockets: sockets));
        await subscriber.Connection.StartAsync();
        return subscriber;
    }

    [Fact]
    public async Task Server_envelope_JSON_has_exactly_the_contract_fields_and_the_TypeScript_contract_parses_it()
    {
        await using var stack = NewStack();
        stack.Hub.Streams.Create("probe:wire");
        await using var subscriber = await MemberAsync(stack, "wire");
        var reply = await subscriber.Connection.InvokeAsync<JsonElement>(RealtimeWire.SubscribeMethod, "probe:wire");
        await subscriber.SubscribeAsync("probe:wire");
        await stack.Hub.Streams.AdvanceAsync("probe:wire");
        await Eventually.TrueAsync(() => !subscriber.Raw.IsEmpty, "an envelope arrives");
        var raw = subscriber.Raw.First();

        Assert.Equal(RealtimeWire.EnvelopeFields, raw.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(JsonValueKind.String, raw.GetProperty("sequence").ValueKind);
        Assert.Equal("1", raw.GetProperty("sequence").GetString());
        Assert.Equal(1, raw.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(["ok", "code", "snapshot"], reply.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(["schemaVersion", "streamId", "sequence", "payload"], reply.GetProperty("snapshot").EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(JsonValueKind.String, reply.GetProperty("snapshot").GetProperty("sequence").ValueKind);

        var ts = await RunTypeScriptContractAsync(raw.GetRawText());
        Assert.Equal(RealtimeWire.EnvelopeFields, ts.GetProperty("fields").EnumerateArray().Select(f => f.GetString()!).ToArray());
        Assert.True(ts.GetProperty("parsed").GetBoolean(), "the TypeScript parser rejected the server's envelope");
        Assert.True(ts.GetProperty("aliasRejected").GetBoolean());
        Assert.Equal(RealtimeWire.HubPath, ts.GetProperty("hubPath").GetString());
        Assert.Equal(RealtimeWire.ReceiveMethod, ts.GetProperty("receive").GetString());
        Assert.Equal(RealtimeWire.SubscribeMethod, ts.GetProperty("subscribe").GetString());
        Assert.Equal(RealtimeWire.UnsubscribeMethod, ts.GetProperty("unsubscribe").GetString());
        Assert.Equal(RealtimeWire.GetSnapshotMethod, ts.GetProperty("getSnapshot").GetString());
    }

    [Fact]
    public async Task Events_arrive_in_stream_order_and_the_client_state_matches_the_source()
    {
        await using var stack = NewStack();
        stack.Hub.Streams.Create("probe:order");
        await using var subscriber = await MemberAsync(stack, "order");
        Assert.True((await subscriber.SubscribeAsync("probe:order")).Ok);
        for (var i = 0; i < 50; i++)
        {
            await stack.Hub.Streams.AdvanceAsync("probe:order");
        }
        await Eventually.TrueAsync(() => subscriber.SequenceOf("probe:order") == 50, "all 50 events applied");
        Assert.Equal(Enumerable.Range(1, 50).Select(i => (long)i), subscriber.AppliedSequences);
        Assert.Equal(stack.Hub.Streams.Current("probe:order").Value, subscriber.Values["probe:order"]);
        Assert.Equal(0, subscriber.Gaps);
        Assert.Equal(0, subscriber.Rejected);
    }

    [Fact]
    public async Task A_source_sequence_gap_is_detected_and_recovered_by_a_snapshot_refetch()
    {
        await using var stack = NewStack();
        stack.Hub.Streams.Create("probe:gap");
        await using var subscriber = await MemberAsync(stack, "gap");
        await subscriber.SubscribeAsync("probe:gap");
        for (var i = 0; i < 3; i++)
        {
            await stack.Hub.Streams.AdvanceAsync("probe:gap");
        }
        await stack.Hub.Streams.AdvanceAsync("probe:gap", publish: false); // sequence 4 lost in transport
        await stack.Hub.Streams.AdvanceAsync("probe:gap"); // 5 arrives: gap
        await Eventually.TrueAsync(() => subscriber.SequenceOf("probe:gap") == 5, "refetched snapshot applied");
        Assert.Equal(1, subscriber.Gaps);
        Assert.Equal(1, subscriber.Refetches);
        Assert.Equal(stack.Hub.Streams.Current("probe:gap").Value, subscriber.Values["probe:gap"]);

        await stack.Hub.Streams.AdvanceAsync("probe:gap");
        await Eventually.TrueAsync(() => subscriber.SequenceOf("probe:gap") == 6, "resumed after the snapshot");
        Assert.Equal([1L, 2L, 3L, 6L], subscriber.AppliedSequences);
        Assert.Equal(stack.Hub.Streams.Current("probe:gap").Value, subscriber.Values["probe:gap"]);
    }

    [Fact]
    public async Task After_a_dropped_connection_the_client_reconnects_resubscribes_and_converges_without_the_lost_events()
    {
        await using var stack = NewStack();
        stack.Hub.Streams.Create("probe:recon");
        var sockets = new ConcurrentQueue<WebSocket>();
        await using var subscriber = await MemberAsync(stack, "recon", autoReconnect: true, sockets);
        await subscriber.SubscribeAsync("probe:recon");
        await stack.Hub.Streams.AdvanceAsync("probe:recon");
        await stack.Hub.Streams.AdvanceAsync("probe:recon");
        await Eventually.TrueAsync(() => subscriber.SequenceOf("probe:recon") == 2, "first events applied");
        var receivedBefore = subscriber.Raw.Count;

        var closed = new TaskCompletionSource();
        subscriber.Connection.Reconnecting += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };
        // Simulated network drop: the socket dies under the client without a close handshake.
        Assert.Single(sockets).Abort();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // Published while the client is disconnected: these envelopes can never reach it.
        for (var i = 0; i < 3; i++)
        {
            await stack.Hub.Streams.AdvanceAsync("probe:recon");
        }
        await Eventually.TrueAsync(() => subscriber.Reconnects == 1 && subscriber.SequenceOf("probe:recon") == 5, "reconnected and resynchronized");
        Assert.Equal(receivedBefore, subscriber.Raw.Count);
        Assert.Equal(stack.Hub.Streams.Current("probe:recon").Value, subscriber.Values["probe:recon"]);

        await stack.Hub.Streams.AdvanceAsync("probe:recon");
        await Eventually.TrueAsync(() => subscriber.SequenceOf("probe:recon") == 6, "live again after reconnect");
        Assert.Equal([1L, 2L, 6L], subscriber.AppliedSequences);
        Assert.Equal(stack.Hub.Streams.Current("probe:recon").Value, subscriber.Values["probe:recon"]);
    }

    [Fact]
    public async Task Forged_spectators_and_non_owners_are_denied_and_receive_nothing()
    {
        await using var stack = NewStack();
        stack.Hub.Streams.Create("probe:room1");
        stack.Hub.Streams.Create("probe-history:h1");
        await using var member = await MemberAsync(stack, "room1");
        var (intruderCookie, intruderSubject) = await GuestAsync(stack);
        var memberSubject = stack.Hub.Domain.Members.Keys.Single().Subject;
        stack.Hub.Domain.SetOwner("h1", memberSubject, PrincipalKind.Guest);

        await using var intruder = new ProbeSubscriber(stack.Hub.Connect(guestCookie: intruderCookie));
        await intruder.Connection.StartAsync();
        Assert.Equal(RealtimeFailure.Forbidden, (await intruder.SubscribeAsync("probe:room1")).Code);
        Assert.Equal(RealtimeFailure.Forbidden, (await intruder.Connection.InvokeAsync<RealtimeReply>(RealtimeWire.GetSnapshotMethod, "probe:room1")).Code);
        Assert.Equal(RealtimeFailure.Forbidden, (await intruder.SubscribeAsync("probe-history:h1")).Code);
        // An unknown stream kind and an unknown stream look exactly like a forbidden one.
        Assert.Equal(RealtimeFailure.Forbidden, (await intruder.SubscribeAsync("nope:x")).Code);
        stack.Hub.Domain.AddMember(intruderSubject, PrincipalKind.Guest, "ghost-room");
        Assert.Equal(RealtimeFailure.Forbidden, (await intruder.SubscribeAsync("probe:ghost-room")).Code);
        Assert.Equal(RealtimeFailure.InvalidStream, (await intruder.SubscribeAsync("probe:../room1")).Code);
        // A client cannot name an owner, publish, or reach a group directly: there is no such hub method or parameter.
        await Assert.ThrowsAsync<HubException>(() => intruder.Connection.InvokeAsync<RealtimeReply>(RealtimeWire.SubscribeMethod, "probe:room1", memberSubject));
        await Assert.ThrowsAsync<HubException>(() => intruder.Connection.InvokeAsync("Publish", "probe:room1", "probe.advanced", new { add = 1000 }));
        await Assert.ThrowsAsync<HubException>(() => intruder.Connection.InvokeAsync("SendToGroup", "stream:probe:room1", new { add = 1000 }));

        // Staff with the supervisor role gets no bypass into a guest room or a guest's history.
        var (access, _) = await StaffAsync(stack);
        await using var supervisor = new ProbeSubscriber(stack.Hub.Connect(accessToken: access, origin: null));
        await supervisor.Connection.StartAsync();
        Assert.Equal(RealtimeFailure.Forbidden, (await supervisor.SubscribeAsync("probe:room1")).Code);
        Assert.Equal(RealtimeFailure.Forbidden, (await supervisor.SubscribeAsync("probe-history:h1")).Code);

        Assert.True((await member.SubscribeAsync("probe:room1")).Ok);
        Assert.True((await member.SubscribeAsync("probe-history:h1")).Ok);
        await stack.Hub.Streams.AdvanceAsync("probe:room1");
        await stack.Hub.Streams.AdvanceAsync("probe-history:h1");
        await Eventually.TrueAsync(() => member.Raw.Count == 2, "the member receives both events");
        await Task.Delay(200);
        Assert.Empty(intruder.Raw);
        Assert.Empty(supervisor.Raw);
    }

    /// <summary>
    /// Revocation committed on the API host reaches already-open hub connections on the Realtime host (shared identity
    /// database) within the documented bound: revoked sessions are closed, a removed membership loses its group.
    /// </summary>
    [Fact]
    public async Task Revoked_sessions_bans_and_removed_memberships_stop_delivery_to_live_connections()
    {
        const int intervalSeconds = 1;
        await using var stack = NewStack(new() { ["Realtime:AccessRevalidationSeconds"] = intervalSeconds.ToString() });
        stack.Hub.Streams.Create("probe:live");
        var closed = new ConcurrentDictionary<string, bool>();

        async Task<(ProbeSubscriber Subscriber, TestBrowser Browser, string Cookie, string Subject)> GuestMemberAsync(string name)
        {
            var browser = stack.Api.NewBrowser();
            var subject = (await browser.PostAsync("/auth/guest/start")).Str("subject");
            stack.Hub.Domain.AddMember(subject, PrincipalKind.Guest, "live");
            var cookie = browser.Cookies[Flows.GuestCookie];
            var subscriber = new ProbeSubscriber(stack.Hub.Connect(guestCookie: cookie));
            subscriber.Connection.Closed += _ => { closed[name] = true; return Task.CompletedTask; };
            await subscriber.Connection.StartAsync();
            return (subscriber, browser, cookie, subject);
        }

        var signedOut = await GuestMemberAsync("signed-out");
        var banned = await GuestMemberAsync("banned");
        var removed = await GuestMemberAsync("membership-removed");
        var control = await GuestMemberAsync("control");
        var staff = await stack.Api.CreateStaffAsync(IdentityRoles.Operator);
        var (_, staffAccess, _) = await Flows.LoginAsync(stack.Api, staff);
        var staffSubject = "staff:" + staff.Id.ToString("D");
        stack.Hub.Domain.AddMember(staffSubject, PrincipalKind.Staff, "live");
        var revokedStaff = new ProbeSubscriber(stack.Hub.Connect(accessToken: staffAccess, origin: null));
        revokedStaff.Connection.Closed += _ => { closed["staff-revoked"] = true; return Task.CompletedTask; };
        await revokedStaff.Connection.StartAsync();
        ProbeSubscriber[] all = [signedOut.Subscriber, banned.Subscriber, removed.Subscriber, control.Subscriber, revokedStaff];
        try
        {
            foreach (var subscriber in all)
            {
                Assert.True((await subscriber.SubscribeAsync("probe:live")).Ok);
            }
            await stack.Hub.Streams.AdvanceAsync("probe:live");
            await Eventually.TrueAsync(() => all.All(s => s.Raw.Count == 1), "every authorized subscriber receives the first event");

            // Revocations happen on the API host (and in the domain's membership facts), never on the hub connection.
            var (admin, adminAccess, _, _) = await Flows.SteppedUpSupervisorAsync(stack.Api);
            var clock = Stopwatch.StartNew();
            Assert.Equal(System.Net.HttpStatusCode.NoContent, (await signedOut.Browser.PostAsync("/auth/guest/signout", csrfCookie: Flows.GuestCsrf)).Status);
            Assert.Equal(System.Net.HttpStatusCode.NoContent, (await admin.PostAsync($"/auth/admin/guests/{banned.Subject["guest:".Length..]}/ban", bearer: adminAccess)).Status);
            Assert.Equal(System.Net.HttpStatusCode.NoContent, (await admin.PostAsync($"/auth/admin/staff-sessions/{Flows.SessionId(staffAccess)}/revoke", bearer: adminAccess)).Status);
            Assert.True(stack.Hub.Domain.Members.TryRemove((removed.Subject, PrincipalKind.Guest, "live"), out _));

            // Bound: one revalidation interval plus the per-connection check timeout, with scheduling slack.
            var boundMs = (intervalSeconds + RealtimeAccessRevalidator.CheckTimeoutSeconds) * 1000 + 2000;
            await Eventually.TrueAsync(() => closed.ContainsKey("signed-out") && closed.ContainsKey("banned") && closed.ContainsKey("staff-revoked"),
                "revoked and banned sessions are closed", boundMs);
            await Eventually.TrueAsync(() => stack.Hub.Logs.Lines.Any(l => l.EventId == 3211), "the removed membership loses its subscription", boundMs);
            Assert.True(clock.ElapsedMilliseconds < boundMs, $"revocation took {clock.ElapsedMilliseconds} ms");
            Assert.False(closed.ContainsKey("control"));
            Assert.False(closed.ContainsKey("membership-removed"));

            await stack.Hub.Streams.AdvanceAsync("probe:live");
            await Eventually.TrueAsync(() => control.Subscriber.Raw.Count == 2, "the still-authorized subscriber keeps receiving");
            await Task.Delay(300);
            foreach (var revoked in new[] { signedOut.Subscriber, banned.Subscriber, removed.Subscriber, revokedStaff })
            {
                Assert.Single(revoked.Raw);
            }
            Assert.Equal(HubConnectionState.Connected, removed.Subscriber.Connection.State);
            Assert.Equal(RealtimeFailure.Forbidden, (await removed.Subscriber.Connection.InvokeAsync<RealtimeReply>(RealtimeWire.GetSnapshotMethod, "probe:live")).Code);
            Assert.Equal(RealtimeFailure.Forbidden, (await removed.Subscriber.Connection.InvokeAsync<RealtimeReply>(RealtimeWire.SubscribeMethod, "probe:live")).Code);

            // The revoked credentials cannot open a new connection either.
            await Assert.ThrowsAnyAsync<Exception>(() => stack.Hub.Connect(guestCookie: signedOut.Cookie).StartAsync());
            await Assert.ThrowsAnyAsync<Exception>(() => stack.Hub.Connect(guestCookie: banned.Cookie).StartAsync());
            await Assert.ThrowsAnyAsync<Exception>(() => stack.Hub.Connect(accessToken: staffAccess, origin: null).StartAsync());
        }
        finally
        {
            foreach (var subscriber in all)
            {
                await subscriber.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task Unauthenticated_forged_and_mixed_connections_are_refused()
    {
        await using var stack = NewStack();
        await Assert.ThrowsAnyAsync<Exception>(() => stack.Hub.Connect().StartAsync());
        var (cookie, _) = await GuestAsync(stack);
        var tampered = cookie[..^2] + (cookie[^1] == 'A' ? "BB" : "AA");
        await Assert.ThrowsAnyAsync<Exception>(() => stack.Hub.Connect(guestCookie: tampered).StartAsync());
        await Assert.ThrowsAnyAsync<Exception>(() => stack.Hub.Connect(accessToken: "not-a-jwt", origin: null).StartAsync());
        // A token minted under another master secret.
        await using var otherApi = new IdentityApiFactory(db);
        var otherStaff = await otherApi.CreateStaffAsync();
        var (_, foreignAccess, _) = await Flows.LoginAsync(otherApi, otherStaff);
        await Assert.ThrowsAnyAsync<Exception>(() => stack.Hub.Connect(accessToken: foreignAccess, origin: null).StartAsync());
        // Staff token plus a guest cookie: two kinds in one principal fail closed.
        var (access, _) = await StaffAsync(stack);
        await Assert.ThrowsAnyAsync<Exception>(() => stack.Hub.Connect(accessToken: access, guestCookie: cookie).StartAsync());
    }

    [Fact]
    public async Task Without_a_domain_policy_registration_every_stream_is_denied()
    {
        await using var stack = NewStack(domainPolicies: false);
        stack.Hub.Streams.Create("probe:room1");
        await using var member = await MemberAsync(stack, "room1");
        Assert.Equal(RealtimeFailure.Forbidden, (await member.SubscribeAsync("probe:room1")).Code);
    }

    [Fact]
    public async Task Cookie_authenticated_hub_connections_need_an_allowed_origin()
    {
        await using var stack = NewStack();
        var (cookie, _) = await GuestAsync(stack);
        await Assert.ThrowsAnyAsync<Exception>(() => stack.Hub.Connect(guestCookie: cookie, origin: "https://evil.example").StartAsync());
        await Assert.ThrowsAnyAsync<Exception>(() => stack.Hub.Connect(guestCookie: cookie, origin: null).StartAsync());
        await using var allowed = stack.Hub.Connect(guestCookie: cookie, origin: RealtimeHostFactory.AllowedExtraOrigin);
        await allowed.StartAsync();
        await using var own = stack.Hub.Connect(guestCookie: cookie);
        await own.StartAsync();
        var (access, _) = await StaffAsync(stack);
        await using var staff = stack.Hub.Connect(accessToken: access, origin: null);
        await staff.StartAsync();
        Assert.Contains(stack.Hub.Logs.Lines, l => l.EventId == 3206);
    }

    [Fact]
    public async Task Invocation_rate_subscription_count_and_message_sizes_are_bounded()
    {
        await using (var limited = NewStack(new() { ["Realtime:InvocationBurst"] = "3", ["Realtime:InvocationsPerSecond"] = "0.01" }))
        {
            limited.Hub.Streams.Create("probe:rate");
            await using var subscriber = await MemberAsync(limited, "rate");
            var codes = new List<string?>();
            for (var i = 0; i < 6; i++)
            {
                codes.Add((await subscriber.Connection.InvokeAsync<RealtimeReply>(RealtimeWire.SubscribeMethod, "probe:rate")).Code);
            }
            Assert.Equal([null, null, null, RealtimeFailure.RateLimited, RealtimeFailure.RateLimited, RealtimeFailure.RateLimited], codes);
            Assert.Contains(limited.Hub.Logs.Lines, l => l.EventId == 3202);
        }

        await using var stack = NewStack(new() { ["Realtime:MaxSubscriptionsPerConnection"] = "2", ["Realtime:MaxInboundMessageBytes"] = "1024", ["Realtime:MaxPayloadBytes"] = "2048" });
        foreach (var room in new[] { "a", "b", "c" })
        {
            stack.Hub.Streams.Create("probe:" + room);
        }
        var (cookie, subject) = await GuestAsync(stack);
        foreach (var room in new[] { "a", "b", "c" })
        {
            stack.Hub.Domain.AddMember(subject, PrincipalKind.Guest, room);
        }
        await using var client = new ProbeSubscriber(stack.Hub.Connect(guestCookie: cookie));
        await client.Connection.StartAsync();
        Assert.True((await client.SubscribeAsync("probe:a")).Ok);
        Assert.True((await client.SubscribeAsync("probe:b")).Ok);
        Assert.Equal(RealtimeFailure.SubscriptionLimit, (await client.SubscribeAsync("probe:c")).Code);
        Assert.True((await client.Connection.InvokeAsync<RealtimeReply>(RealtimeWire.UnsubscribeMethod, "probe:b")).Ok);
        Assert.True((await client.SubscribeAsync("probe:c")).Ok);

        var publisher = stack.Hub.Services.GetRequiredService<IRealtimePublisher>();
        Assert.Equal(RealtimePublishResult.PayloadTooLarge, await publisher.PublishAsync("probe:a", 1, "probe.big", new { blob = new string('x', 4096) }, default));
        Assert.Equal(RealtimePublishResult.Published, await publisher.PublishAsync("probe:a", 1, "probe.advanced", new ProbeEvent(1), default));
        Assert.Equal(RealtimePublishResult.SequenceNotIncreasing, await publisher.PublishAsync("probe:a", 1, "probe.advanced", new ProbeEvent(1), default));
        Assert.Equal(RealtimePublishResult.InvalidKind, await publisher.PublishAsync("probe:a", 2, "Bad Kind", new ProbeEvent(2), default));
        Assert.Equal(RealtimePublishResult.InvalidStream, await publisher.PublishAsync("probe:a b", 2, "probe.advanced", new ProbeEvent(2), default));

        // A client message above MaximumReceiveMessageSize closes that connection.
        var closed = new TaskCompletionSource();
        client.Connection.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };
        await Assert.ThrowsAnyAsync<Exception>(() => client.Connection.InvokeAsync<RealtimeReply>(RealtimeWire.SubscribeMethod, "probe:" + new string('a', 4000)));
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HubConnectionState.Disconnected, client.Connection.State);
    }

    [Fact]
    public async Task Realtime_logs_at_trace_level_contain_no_access_token_or_guest_cookie()
    {
        await using var stack = NewStack(new()
        {
            ["Logging:LogLevel:Default"] = "Trace",
            ["Logging:LogLevel:Microsoft"] = "Trace",
            ["Logging:LogLevel:Microsoft.AspNetCore"] = "Trace",
            ["Logging:LogLevel:Microsoft.AspNetCore.Hosting.Diagnostics"] = "Trace",
        });
        stack.Hub.Streams.Create("probe:logs");
        var (cookie, subject) = await GuestAsync(stack);
        stack.Hub.Domain.AddMember(subject, PrincipalKind.Guest, "logs");
        var (access, _) = await StaffAsync(stack);
        await using (var guest = new ProbeSubscriber(stack.Hub.Connect(guestCookie: cookie)))
        await using (var staff = new ProbeSubscriber(stack.Hub.Connect(accessToken: access, origin: null)))
        {
            await guest.Connection.StartAsync();
            await staff.Connection.StartAsync();
            Assert.True((await guest.SubscribeAsync("probe:logs")).Ok);
            Assert.Equal(RealtimeFailure.Forbidden, (await staff.SubscribeAsync("probe:logs")).Code);
            await stack.Hub.Streams.AdvanceAsync("probe:logs");
            await Eventually.TrueAsync(() => guest.SequenceOf("probe:logs") == 1, "event delivered");
        }
        await Assert.ThrowsAnyAsync<Exception>(() => stack.Hub.Connect(guestCookie: cookie, origin: "https://evil.example").StartAsync());
        await Task.Delay(200);

        var text = stack.Hub.Logs.AllText();
        Assert.True(stack.Hub.Logs.Lines.Count > 20, "trace logging was not enabled");
        Assert.Contains(stack.Hub.Logs.Lines, l => l.EventId == 3201);
        Assert.Contains(stack.Hub.Logs.Lines, l => l.EventId == 3206);
        Assert.DoesNotContain(access, text, StringComparison.Ordinal);
        foreach (var part in access.Split('.'))
        {
            Assert.DoesNotContain(part, text, StringComparison.Ordinal);
        }
        Assert.DoesNotContain(cookie, text, StringComparison.Ordinal);
        foreach (var part in cookie.Split('.').Where(p => p.Length >= 16))
        {
            Assert.DoesNotContain(part, text, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("access_token", text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Runs the real TypeScript contract module under Node (type stripping) against the server's JSON.</summary>
    private static async Task<JsonElement> RunTypeScriptContractAsync(string envelopeJson)
    {
        var module = Path.Combine(RepositoryRoot(), "web", "packages", "contracts", "src", "realtime.ts");
        var script = $$"""
            import * as c from {{JsonSerializer.Serialize(new Uri(module).AbsoluteUri)}};
            let input = '';
            for await (const chunk of process.stdin) input += chunk;
            const envelope = JSON.parse(input);
            const alias = { ...envelope, messageId: envelope.eventId };
            console.log(JSON.stringify({
              fields: c.REALTIME_ENVELOPE_FIELDS,
              parsed: c.parseRealtimeEnvelope(envelope) !== null,
              aliasRejected: c.parseRealtimeEnvelope(alias) === null,
              hubPath: c.REALTIME_HUB_PATH,
              receive: c.RealtimeClientMethods.receive,
              subscribe: c.RealtimeHubMethods.subscribe,
              unsubscribe: c.RealtimeHubMethods.unsubscribe,
              getSnapshot: c.RealtimeHubMethods.getSnapshot,
            }));
            """;
        var start = new ProcessStartInfo("node")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("--input-type=module");
        start.ArgumentList.Add("-e");
        start.ArgumentList.Add(script);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("node could not be started");
        await process.StandardInput.WriteAsync(envelopeJson);
        process.StandardInput.Close();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"node exited {process.ExitCode}: {stderr}");
        return JsonDocument.Parse(stdout.Trim().Split('\n').Last()).RootElement.Clone();
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "TheSandsOfTime.slnx")))
            {
                return dir.FullName;
            }
        }
        throw new InvalidOperationException("Repository root not found.");
    }
}
