using System.Text.Json;
using GhostLap.Contracts.Multiplayer;
using GhostLap.Multiplayer;
using Xunit;

namespace GhostLap.Tests.Multiplayer;

/// <summary>
/// R10.01, R10.02, R10.17 (task 10.1): lobby, roster, ready, host, countdown and start-admission transitions against the real
/// <see cref="ActiveRoomOwner"/> with a controllable monotonic clock. The only test double is the start-admission port, which
/// is the 10.2 seam: here it stands in for the receipt commit that does not exist yet.
/// </summary>
public sealed class LobbyStateMachineTests
{
    [Fact]
    public void Creating_a_room_makes_the_creator_the_connected_host_competitor_with_epoch_one()
    {
        var rig = new Rig();
        var alice = new Guest("alice");
        var room = rig.Create(alice);
        Assert.Equal(RoomPhases.Lobby, room.View.Phase);
        Assert.Equal(RoomRoles.Competitor, room.Role);
        Assert.Equal(1, room.ControllerEpoch);
        Assert.Equal(8, room.RoomCode.Length);
        var member = Assert.Single(room.View.Members);
        Assert.True(member.IsHost && member.Connected && !member.Ready);
        Assert.Null(member.Slot);
    }

    [Fact]
    public void Competitors_are_capped_at_four_and_spectators_are_bounded_separately()
    {
        var rig = new Rig(o => o.MaxSpectators = 2);
        var host = new Guest("host");
        var room = rig.Create(host);
        for (var i = 0; i < 3; i++)
        {
            rig.Join(new Guest("c" + i), room);
        }
        var fifth = rig.Owner.JoinRoom(new Guest("c5").On(), new JoinRoomRequest(rig.Req(), room.RoomCode, false));
        Assert.Equal(GhostLapFailure.RoomFull, fifth.Code);
        rig.Join(new Guest("s1"), room, spectator: true);
        rig.Join(new Guest("s2"), room, spectator: true);
        var third = rig.Owner.JoinRoom(new Guest("s3").On(), new JoinRoomRequest(rig.Req(), room.RoomCode, true));
        Assert.Equal(GhostLapFailure.SpectatorsFull, third.Code);
        var view = rig.View(host, room);
        Assert.Equal(4, view.Members.Count(m => m.Role == RoomRoles.Competitor));
        Assert.Equal(2, view.Members.Count(m => m.Role == RoomRoles.Spectator));
    }

    [Fact]
    public void A_configured_maximum_of_three_competitors_is_enforced()
    {
        var rig = new Rig(o => o.MaxCompetitors = 3);
        var room = rig.Create(new Guest("h"));
        rig.Join(new Guest("a"), room);
        rig.Join(new Guest("b"), room);
        Assert.Equal(GhostLapFailure.RoomFull, rig.Owner.JoinRoom(new Guest("c").On(), new JoinRoomRequest(rig.Req(), room.RoomCode, false)).Code);
    }

    [Fact]
    public void Unknown_malformed_and_expired_codes_all_look_like_an_unavailable_room_and_a_member_can_still_reattach()
    {
        var rig = new Rig(o => o.RoomCodeSeconds = 60);
        var host = new Guest("host");
        var room = rig.Create(host);
        var outsider = new Guest("outsider");
        Assert.Equal(GhostLapFailure.RoomUnavailable, rig.Owner.JoinRoom(outsider.On(), new JoinRoomRequest(rig.Req(), "ZZZZZZZZ", false)).Code);
        Assert.Equal(GhostLapFailure.InvalidRequest, rig.Owner.JoinRoom(outsider.On(), new JoinRoomRequest(rig.Req(), "nope", false)).Code);
        Assert.Equal(GhostLapFailure.InvalidRequest, rig.Owner.JoinRoom(outsider.On(), new JoinRoomRequest("", room.RoomCode, false)).Code);
        rig.Time.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(GhostLapFailure.RoomUnavailable, rig.Owner.JoinRoom(outsider.On(), new JoinRoomRequest(rig.Req(), room.RoomCode, false)).Code);
        // The creator's membership is the credential: a refresh after the code expired re-attaches.
        var again = rig.Owner.JoinRoom(host.Reconnect(), new JoinRoomRequest(rig.Req(), room.RoomCode, false));
        Assert.True(again.Ok);
        Assert.Equal(2, again.Value!.ControllerEpoch);
    }

    [Fact]
    public void Code_entry_is_case_and_separator_tolerant_but_not_a_credential_for_other_rooms()
    {
        var rig = new Rig();
        var room = rig.Create(new Guest("host"));
        var spaced = room.RoomCode[..4].ToLowerInvariant() + "-" + room.RoomCode[4..].ToLowerInvariant();
        Assert.True(rig.Owner.JoinRoom(new Guest("g").On(), new JoinRoomRequest(rig.Req(), spaced, false)).Ok);
    }

    [Fact]
    public void Code_lookups_are_rate_limited_per_principal_and_recover_with_time()
    {
        var rig = new Rig(o => o.CodeLookupsPerMinute = 5);
        var guesser = new Guest("guesser");
        var codes = Enumerable.Range(0, 5).Select(i => rig.Owner.JoinRoom(guesser.On(), new JoinRoomRequest(rig.Req(), "AAAAAAA" + "BCDEF"[i], false)).Code).ToArray();
        Assert.All(codes, c => Assert.Equal(GhostLapFailure.RoomUnavailable, c));
        Assert.Equal(GhostLapFailure.LookupLimited, rig.Owner.JoinRoom(guesser.On(), new JoinRoomRequest(rig.Req(), "AAAAAAAG", false)).Code);
        rig.Time.Advance(TimeSpan.FromSeconds(13));
        Assert.Equal(GhostLapFailure.RoomUnavailable, rig.Owner.JoinRoom(guesser.On(), new JoinRoomRequest(rig.Req(), "AAAAAAAH", false)).Code);
    }

    [Fact]
    public void A_principal_holds_one_room_and_a_retried_create_returns_the_same_room()
    {
        var rig = new Rig();
        var alice = new Guest("alice");
        var firstRequest = rig.Req();
        var first = Rig.Ok(rig.Owner.CreateRoom(alice.On(), new CreateRoomRequest(firstRequest)));
        var retry = Rig.Ok(rig.Owner.CreateRoom(alice.On(), new CreateRoomRequest(firstRequest)));
        Assert.Equal(first.View.RoomId, retry.View.RoomId);
        Assert.Equal(first.RoomCode, retry.RoomCode);
        Assert.Equal(1, rig.Owner.RoomCount);
        Assert.Equal(GhostLapFailure.AlreadyInRoom, rig.Owner.CreateRoom(alice.On(), new CreateRoomRequest(rig.Req())).Code);
        var other = rig.Create(new Guest("bob"));
        Assert.Equal(GhostLapFailure.AlreadyInRoom, rig.Owner.JoinRoom(alice.On(), new JoinRoomRequest(rig.Req(), other.RoomCode, false)).Code);
    }

    [Fact]
    public void A_room_cap_bounds_how_many_rooms_exist()
    {
        var rig = new Rig(o => o.MaxRooms = 2);
        rig.Create(new Guest("a"));
        rig.Create(new Guest("b"));
        Assert.Equal(GhostLapFailure.RoomsFull, rig.Owner.CreateRoom(new Guest("c").On(), new CreateRoomRequest(rig.Req())).Code);
    }

    [Fact]
    public void A_roster_change_clears_everyones_ready_flag()
    {
        var rig = new Rig();
        var host = new Guest("host");
        var bob = new Guest("bob");
        var room = rig.Create(host);
        rig.Join(bob, room);
        Assert.True(rig.Ready(host, room).Ok);
        Assert.True(rig.Ready(bob, room).Ok);
        rig.Join(new Guest("carol"), room);
        Assert.All(rig.View(host, room).Members, m => Assert.False(m.Ready));
    }

    // ---- start validation matrix -------------------------------------------------

    [Fact]
    public void Illegal_rosters_cannot_start_and_leave_no_countdown_or_admission()
    {
        var rig = new Rig();
        var host = new Guest("host");
        var bob = new Guest("bob");
        var spectator = new Guest("spec");
        var room = rig.Create(host);

        // One competitor only.
        rig.Ready(host, room);
        Assert.Equal(GhostLapFailure.RosterInvalid, rig.Start(host, room).Code);

        // Two, but one not ready.
        rig.Join(bob, room);
        rig.Ready(host, room);
        Assert.Equal(GhostLapFailure.NotReady, rig.Start(host, room).Code);
        Assert.True(rig.Ready(bob, room).Ok);

        // Non-host and spectator cannot start.
        Assert.Equal(GhostLapFailure.NotHost, rig.Start(bob, room).Code);
        rig.Join(spectator, room, spectator: true);
        Assert.Equal(GhostLapFailure.SpectatorForbidden, rig.Start(spectator, room).Code);

        // A disconnected competitor blocks the start.
        rig.Owner.ConnectionClosed(bob.On().ConnectionId);
        Assert.Equal(GhostLapFailure.NotConnected, rig.Start(host, room).Code);
        rig.Owner.JoinRoom(bob.Reconnect(), new JoinRoomRequest(rig.Req(), room.RoomCode, false));
        rig.Ready(bob, room);
        rig.Ready(host, room);

        // Stale expectation.
        Assert.Equal(GhostLapFailure.StaleVersion, rig.Start(host, room, expected: 1).Code);

        Assert.Equal(RoomPhases.Lobby, rig.View(host, room).Phase);
        Assert.Equal(0, rig.Admission.Calls);
        Assert.True(rig.Start(host, room, expected: rig.View(host, room).Version).Ok);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Two_three_and_four_competitor_rosters_seal_distinct_valid_sockets(int players)
    {
        var rig = new Rig();
        var (room, guests) = rig.ReadyRoom(players);
        var started = Rig.Ok(rig.Start(guests[0], room));
        var round = started.View.Round!;
        Assert.Equal(RoomPhases.Countdown, started.View.Phase);
        Assert.Equal(players, round.Slots.Count);
        Assert.Equal(Enumerable.Range(0, players), round.Slots.Select(s => s.Slot).Order());
        Assert.Equal(players, round.Slots.Select(s => (s.SpawnXUnits, s.SpawnYUnits)).Distinct().Count());
        Assert.Equal(players, round.Slots.Select(s => s.MemberId).Distinct().Count());
        Assert.Equal(MpRulesProfile.CurrentRulesId, round.RulesId);
        Assert.Equal("arena-v1", round.LayoutId);
        Assert.Equal("elo-pair-v1", round.RatingAlgorithmId);
        Assert.Equal(2700, round.TargetTicks);
        Assert.Equal(3000, round.CountdownMs);
    }

    [Fact]
    public void The_sealed_rating_algorithm_id_is_opaque_configuration_carried_unchanged()
    {
        var rig = new Rig(o => o.RatingAlgorithmId = "future-algo.v2");
        var (room, guests) = rig.ReadyRoom(2);
        Assert.Equal("future-algo.v2", Rig.Ok(rig.Start(guests[0], room)).View.Round!.RatingAlgorithmId);
    }

    // ---- countdown and admission -------------------------------------------------

    [Fact]
    public async Task Duplicate_start_requests_seal_one_round_and_admit_it_exactly_once()
    {
        var rig = new Rig();
        var (room, guests) = rig.ReadyRoom(3);
        var first = Rig.Ok(rig.Start(guests[0], room));
        var second = Rig.Ok(rig.Start(guests[0], room));
        var third = Rig.Ok(rig.Owner.StartCountdown(guests[0].On(), new RoomCommand("r1", room.View.RoomId)));
        Assert.Equal(first.View.Round!.ConfigDigest, second.View.Round!.ConfigDigest);
        Assert.Equal(first.View.Round.MatchId, third.View.Round!.MatchId);
        Assert.Equal(first.View.Version, second.View.Version);
        await rig.Owner.SweepAsync(CancellationToken.None);
        Assert.Equal(0, rig.Admission.Calls); // not elapsed yet
        await rig.ElapseCountdownAsync();
        await rig.Owner.SweepAsync(CancellationToken.None);
        Assert.Equal(1, rig.Admission.Calls);
        Assert.Equal(first.View.Round.ConfigDigest, rig.Admission.Configs.Single().ConfigDigest);
        var after = rig.View(guests[0], room);
        Assert.Equal(RoomPhases.StartAdmitted, after.Phase);
        Assert.Equal(RoomNotices.StartAdmitted, after.Notice);
        // A start after admission is still the same single round.
        Assert.Equal("already_started", Rig.Ok(rig.Start(guests[0], room)).Detail);
        Assert.Equal(1, rig.Admission.Calls);
    }

    [Fact]
    public async Task Concurrent_duplicate_starts_create_one_countdown_and_one_admission()
    {
        var rig = new Rig();
        var (room, guests) = rig.ReadyRoom(4);
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => rig.Start(guests[0], room))));
        Assert.All(results, r => Assert.True(r.Ok));
        Assert.Single(results.Select(r => r.Value!.View.Round!.ConfigDigest).Distinct());
        Assert.Equal(1, results.Count(r => r.Value!.Detail is null));
        await rig.ElapseCountdownAsync();
        Assert.Equal(1, rig.Admission.Calls);
    }

    [Fact]
    public async Task Start_admission_unavailable_returns_to_a_retryable_lobby_with_the_same_sealed_round()
    {
        var rig = new Rig(admission: StartAdmissionOutcome.Unavailable);
        var (room, guests) = rig.ReadyRoom(2);
        var sealedFirst = Rig.Ok(rig.Start(guests[0], room)).View.Round!;
        await rig.ElapseCountdownAsync();
        var lobby = rig.View(guests[0], room);
        Assert.Equal(RoomPhases.Lobby, lobby.Phase);
        Assert.Equal(RoomNotices.StartUnavailable, lobby.Notice);
        Assert.Null(lobby.Round);
        Assert.All(lobby.Members, m => Assert.True(m.Ready)); // recoverable without re-readying
        var retry = Rig.Ok(rig.Start(guests[0], room)).View.Round!;
        Assert.Equal(sealedFirst.ConfigDigest, retry.ConfigDigest);
        Assert.Equal(sealedFirst.MatchId, retry.MatchId);
        rig.Admission.Outcome = StartAdmissionOutcome.Admitted;
        await rig.ElapseCountdownAsync();
        Assert.Equal(RoomPhases.StartAdmitted, rig.View(guests[0], room).Phase);
    }

    [Fact]
    public async Task A_failing_or_hanging_admission_port_never_claims_a_start()
    {
        var rig = new Rig(o => o.AdmissionTimeoutMilliseconds = 500);
        var hanging = new HangingAdmission();
        var owner = new ActiveRoomOwner(rig.Options, rig.Time, hanging, new NoRunningMatchSink(), new CryptoRoomRandom(), Guid.NewGuid());
        var (a, b) = (new Guest("a"), new Guest("b"));
        var room = Rig.Ok(owner.CreateRoom(a.On(), new CreateRoomRequest("c1")));
        Rig.Ok(owner.JoinRoom(b.On(), new JoinRoomRequest("j1", room.RoomCode, false)));
        owner.SetReady(a.On(), new SetReadyRequest("x1", room.View.RoomId, true));
        owner.SetReady(b.On(), new SetReadyRequest("x2", room.View.RoomId, true));
        Rig.Ok(owner.StartCountdown(a.On(), new RoomCommand("s1", room.View.RoomId)));
        rig.Time.Advance(TimeSpan.FromSeconds(4));
        await owner.SweepAsync(CancellationToken.None); // the port never answers; the timeout must end it
        Assert.Equal(RoomPhases.Lobby, Rig.Ok(owner.GetRoom(a.On(), room.View.RoomId)).Phase);

        var thrower = new Rig(admission: StartAdmissionOutcome.Admitted);
        var throwingOwner = new ActiveRoomOwner(thrower.Options, thrower.Time, new ThrowingAdmission(), new NoRunningMatchSink(), new CryptoRoomRandom(), Guid.NewGuid());
        var (c, d) = (new Guest("c"), new Guest("d"));
        var room2 = Rig.Ok(throwingOwner.CreateRoom(c.On(), new CreateRoomRequest("c2")));
        Rig.Ok(throwingOwner.JoinRoom(d.On(), new JoinRoomRequest("j2", room2.RoomCode, false)));
        throwingOwner.SetReady(c.On(), new SetReadyRequest("y1", room2.View.RoomId, true));
        throwingOwner.SetReady(d.On(), new SetReadyRequest("y2", room2.View.RoomId, true));
        Rig.Ok(throwingOwner.StartCountdown(c.On(), new RoomCommand("s2", room2.View.RoomId)));
        thrower.Time.Advance(TimeSpan.FromSeconds(4));
        await throwingOwner.SweepAsync(CancellationToken.None);
        Assert.Equal(RoomPhases.Lobby, Rig.Ok(throwingOwner.GetRoom(c.On(), room2.View.RoomId)).Phase);
    }

    [Fact]
    public async Task While_the_start_receipt_is_being_committed_the_roster_is_frozen_and_cancel_is_a_typed_conflict()
    {
        var rig = new Rig();
        var gate = new GatedAdmission();
        var owner = new ActiveRoomOwner(rig.Options, rig.Time, gate, new NoRunningMatchSink(), new CryptoRoomRandom(), Guid.NewGuid());
        var (a, b) = (new Guest("a"), new Guest("b"));
        var room = Rig.Ok(owner.CreateRoom(a.On(), new CreateRoomRequest("c1")));
        Rig.Ok(owner.JoinRoom(b.On(), new JoinRoomRequest("j1", room.RoomCode, false)));
        owner.SetReady(a.On(), new SetReadyRequest("x1", room.View.RoomId, true));
        owner.SetReady(b.On(), new SetReadyRequest("x2", room.View.RoomId, true));
        Rig.Ok(owner.StartCountdown(a.On(), new RoomCommand("s1", room.View.RoomId)));
        rig.Time.Advance(TimeSpan.FromSeconds(4));
        var sweep = owner.SweepAsync(CancellationToken.None).AsTask();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(GhostLapFailure.StartCommitting, owner.CancelCountdown(a.On(), new RoomCommand("c9", room.View.RoomId)).Code);
        Assert.Equal(GhostLapFailure.StartCommitting, owner.SetReady(b.On(), new SetReadyRequest("u1", room.View.RoomId, false)).Code);
        Assert.Equal(GhostLapFailure.StartCommitting, owner.LeaveRoom(b.On(), new RoomCommand("l1", room.View.RoomId)).Code);
        Assert.Equal(GhostLapFailure.JoinClosed, owner.JoinRoom(new Guest("late").On(), new JoinRoomRequest("j9", room.RoomCode, false)).Code);
        owner.ConnectionClosed(b.On().ConnectionId); // a dropout now is a match-time disconnect (10.2/10.3), not a countdown cancel
        gate.Release.SetResult(StartAdmissionOutcome.Admitted);
        await sweep;
        Assert.Equal(RoomPhases.StartAdmitted, Rig.Ok(owner.GetRoom(a.On(), room.View.RoomId)).Phase);
    }

    [Fact]
    public async Task No_competitor_can_join_during_countdown_or_after_admission_but_spectators_can_watch()
    {
        var rig = new Rig();
        var (room, guests) = rig.ReadyRoom(2);
        Rig.Ok(rig.Start(guests[0], room));
        var late = new Guest("late");
        Assert.Equal(GhostLapFailure.JoinClosed, rig.Owner.JoinRoom(late.On(), new JoinRoomRequest(rig.Req(), room.RoomCode, false)).Code);
        var watcher = new Guest("watcher");
        Assert.Equal(RoomRoles.Spectator, rig.Join(watcher, room, spectator: true).Role);
        await rig.ElapseCountdownAsync();
        Assert.Equal(RoomPhases.StartAdmitted, rig.View(guests[0], room).Phase);
        Assert.Equal(GhostLapFailure.JoinClosed, rig.Owner.JoinRoom(late.On(), new JoinRoomRequest(rig.Req(), room.RoomCode, false)).Code);
        Assert.Equal(2, rig.View(guests[0], room).Members.Count(m => m.Role == RoomRoles.Competitor));
    }

    [Fact]
    public void Cancel_by_host_unready_or_dropout_returns_to_lobby_and_each_has_its_own_reason()
    {
        var rig = new Rig();
        var (room, guests) = rig.ReadyRoom(3);

        Rig.Ok(rig.Start(guests[0], room));
        Assert.Equal(GhostLapFailure.NotHost, rig.Cancel(guests[1], room).Code);
        Rig.Ok(rig.Cancel(guests[0], room));
        var byHost = rig.View(guests[0], room);
        Assert.Equal((RoomPhases.Lobby, RoomNotices.CountdownCancelledByHost), (byHost.Phase, byHost.Notice));
        Assert.All(byHost.Members, m => Assert.True(m.Ready));
        Assert.Equal("already_lobby", Rig.Ok(rig.Cancel(guests[0], room)).Detail);

        Rig.Ok(rig.Start(guests[0], room));
        Rig.Ok(rig.Ready(guests[2], room, false));
        var unready = rig.View(guests[0], room);
        Assert.Equal((RoomPhases.Lobby, RoomNotices.CountdownCancelledUnready), (unready.Phase, unready.Notice));
        Rig.Ok(rig.Ready(guests[2], room));

        Rig.Ok(rig.Start(guests[0], room));
        rig.Owner.ConnectionClosed(guests[1].On().ConnectionId);
        var dropped = rig.View(guests[0], room);
        Assert.Equal((RoomPhases.Lobby, RoomNotices.CountdownCancelledParticipantLost), (dropped.Phase, dropped.Notice));
        Assert.False(dropped.Members.Single(m => m.Role == RoomRoles.Competitor && !m.IsHost && !m.Connected).Connected);
    }

    [Fact]
    public void Leaving_during_countdown_cancels_it_and_clears_ready_for_the_remaining_roster()
    {
        var rig = new Rig();
        var (room, guests) = rig.ReadyRoom(3);
        Rig.Ok(rig.Start(guests[0], room));
        Rig.Ok(rig.Leave(guests[2], room));
        var view = rig.View(guests[0], room);
        Assert.Equal(RoomPhases.Lobby, view.Phase);
        Assert.Equal(RoomNotices.CountdownCancelledParticipantLost, view.Notice);
        Assert.Equal(2, view.Members.Count);
        Assert.All(view.Members, m => Assert.False(m.Ready));
        Assert.Equal(GhostLapFailure.RoomUnavailable, rig.Owner.GetRoom(guests[2].On(), room.View.RoomId).Code);
    }

    [Fact]
    public void Repeated_and_cancelled_countdowns_cannot_reroll_the_seed_or_slot_assignment()
    {
        var rig = new Rig();
        var (room, guests) = rig.ReadyRoom(4);
        var baseline = Rig.Ok(rig.Start(guests[0], room)).View.Round!;
        for (var cycle = 0; cycle < 6; cycle++)
        {
            // Mix every available way of cancelling and restarting, plus reconnects and different request ids.
            if (cycle % 3 == 0)
            {
                Rig.Ok(rig.Cancel(guests[0], room));
            }
            else if (cycle % 3 == 1)
            {
                Rig.Ok(rig.Ready(guests[3], room, false));
                Rig.Ok(rig.Ready(guests[3], room));
            }
            else
            {
                rig.Owner.ConnectionClosed(guests[2].On().ConnectionId);
                rig.Owner.JoinRoom(guests[2].Reconnect(), new JoinRoomRequest(rig.Req(), room.RoomCode, false));
                Rig.Ok(rig.Ready(guests[2], room));
            }
            var again = Rig.Ok(rig.Start(guests[0], room)).View.Round!;
            Assert.Equal(baseline.ConfigDigest, again.ConfigDigest);
            Assert.Equal(baseline.Seed, again.Seed);
            Assert.Equal(baseline.MatchId, again.MatchId);
            Assert.Equal(baseline.Slots, again.Slots);
        }
    }

    [Fact]
    public void Slot_assignment_does_not_depend_on_join_order_or_who_hosts()
    {
        // Two rooms with the same secret cannot exist through the API, so compare the sealing function the rooms use.
        var secret = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(secret);
        var room = Guid.NewGuid();
        var roster = Enumerable.Range(0, 4).Select(i => new RosterEntry("m" + i, "guest:" + i)).ToArray();
        var options = new MultiplayerOptions();
        var generation = Guid.NewGuid();
        var baseline = RoundSealing.Seal(secret, room, 1, roster, options, generation);
        foreach (var shuffled in new IEnumerable<RosterEntry>[] { roster.AsEnumerable().Reverse(), roster.OrderByDescending(r => r.Subject), roster.Skip(2).Concat(roster.Take(2)) })
        {
            var again = RoundSealing.Seal(secret, room, 1, shuffled.ToArray(), options, generation);
            Assert.Equal(baseline, again, new SealedConfigComparer());
        }
        // A different round ordinal is a different seed and match (fresh rematch identity is 10.4).
        Assert.NotEqual(baseline.Seed, RoundSealing.Seal(secret, room, 2, roster, options, generation).Seed);
    }

    // ---- host transfer -----------------------------------------------------------

    [Fact]
    public void Host_leaving_hands_over_to_the_earliest_joined_connected_competitor()
    {
        var rig = new Rig();
        var (host, b, c) = (new Guest("host"), new Guest("b"), new Guest("c"));
        var room = rig.Create(host);
        rig.Join(b, room);
        rig.Join(c, room);
        rig.Owner.ConnectionClosed(b.On().ConnectionId); // b is earlier but disconnected
        Rig.Ok(rig.Leave(host, room));
        var view = rig.View(c, room);
        Assert.Equal(RoomNotices.HostChanged, view.Notice);
        var newHost = Assert.Single(view.Members, m => m.IsHost);
        Assert.True(newHost.Connected);
        Assert.Equal(2, view.Members.Count); // host removed; b kept in grace; c present
    }

    [Fact]
    public void A_disconnected_host_hands_over_in_the_lobby_and_does_not_get_the_role_back_on_return()
    {
        var rig = new Rig();
        var (host, b) = (new Guest("host"), new Guest("b"));
        var room = rig.Create(host);
        var joined = rig.Join(b, room);
        rig.Owner.ConnectionClosed(host.On().ConnectionId);
        Assert.Equal(joined.MemberId, Assert.Single(rig.View(b, room).Members, m => m.IsHost).MemberId);
        rig.Owner.JoinRoom(host.Reconnect(), new JoinRoomRequest(rig.Req(), room.RoomCode, false));
        Assert.Equal(joined.MemberId, Assert.Single(rig.View(b, room).Members, m => m.IsHost).MemberId);
    }

    [Fact]
    public void Explicit_host_transfer_is_host_only_lobby_only_and_to_a_connected_other_competitor()
    {
        var rig = new Rig();
        var (host, b, spectator) = (new Guest("host"), new Guest("b"), new Guest("s"));
        var room = rig.Create(host);
        var bJoined = rig.Join(b, room);
        var sJoined = rig.Join(spectator, room, spectator: true);
        TransferHostRequest To(string member, long? expected = null) => new(rig.Req(), room.View.RoomId, member, expected);
        Assert.Equal(GhostLapFailure.NotHost, rig.Owner.TransferHost(b.On(), To(bJoined.MemberId)).Code);
        Assert.Equal(GhostLapFailure.HostTargetInvalid, rig.Owner.TransferHost(host.On(), To(room.MemberId)).Code);
        Assert.Equal(GhostLapFailure.HostTargetInvalid, rig.Owner.TransferHost(host.On(), To(sJoined.MemberId)).Code);
        Assert.Equal(GhostLapFailure.HostTargetInvalid, rig.Owner.TransferHost(host.On(), To("unknown")).Code);
        Assert.Equal(GhostLapFailure.StaleVersion, rig.Owner.TransferHost(host.On(), To(bJoined.MemberId, 1)).Code);
        Assert.True(rig.Owner.TransferHost(host.On(), To(bJoined.MemberId)).Ok);
        Assert.Equal(GhostLapFailure.NotHost, rig.Owner.StartCountdown(host.On(), new RoomCommand(rig.Req(), room.View.RoomId)).Code);
        rig.Ready(host, room);
        rig.Ready(b, room);
        Rig.Ok(rig.Start(b, room));
        Assert.Equal(GhostLapFailure.WrongPhase, rig.Owner.TransferHost(b.On(), To(room.MemberId)).Code);
    }

    [Fact]
    public async Task Concurrent_leave_transfer_and_start_never_leave_zero_or_two_hosts()
    {
        for (var round = 0; round < 25; round++)
        {
            var rig = new Rig();
            var guests = Enumerable.Range(0, 4).Select(i => new Guest("g" + i)).ToArray();
            var room = rig.Create(guests[0]);
            var joined = guests.Skip(1).Select(g => rig.Join(g, room)).ToArray();
            foreach (var g in guests)
            {
                rig.Ready(g, room);
            }
            await Task.WhenAll(
                Task.Run(() => rig.Leave(guests[0], room)),
                Task.Run(() => rig.Owner.TransferHost(guests[0].On(), new TransferHostRequest(rig.Req(), room.View.RoomId, joined[0].MemberId))),
                Task.Run(() => rig.Leave(guests[1], room)),
                Task.Run(() => rig.Start(guests[0], room)),
                Task.Run(() => rig.Owner.ConnectionClosed(guests[2].On().ConnectionId)));
            var snapshot = rig.Owner.PublicSnapshot(Guid.Parse(room.View.RoomId));
            Assert.NotNull(snapshot);
            LobbyInvariants.Check(snapshot.Value.View, rig.Options);
        }
    }

    // ---- two tabs, takeover, foreign identity --------------------------------------

    [Fact]
    public void A_second_tab_takes_control_with_a_new_epoch_and_the_old_tab_loses_every_control_command_but_may_view()
    {
        var rig = new Rig();
        var (host, b) = (new Guest("host"), new Guest("b"));
        var room = rig.Create(host, tab: 1);
        rig.Join(b, room);
        var second = Rig.Ok(rig.Owner.JoinRoom(host.On(2), new JoinRoomRequest(rig.Req(), room.RoomCode, false)));
        Assert.Equal(2, second.ControllerEpoch);
        Assert.Equal(GhostLapFailure.NotController, rig.Ready(host, room, tab: 1).Code);
        Assert.Equal(GhostLapFailure.NotController, rig.Start(host, room, tab: 1).Code);
        Assert.Equal(GhostLapFailure.NotController, rig.Leave(host, room, tab: 1).Code);
        Assert.Equal(GhostLapFailure.NotController, rig.Owner.TransferHost(host.On(1), new TransferHostRequest(rig.Req(), room.View.RoomId, "x")).Code);
        Assert.NotNull(rig.View(host, room, tab: 1)); // permitted to view
        Assert.True(rig.Ready(host, room, tab: 2).Ok);
        // Re-attaching the same tab is idempotent and does not bump the epoch.
        Assert.Equal(2, Rig.Ok(rig.Owner.JoinRoom(host.On(2), new JoinRoomRequest(rig.Req(), room.RoomCode, false))).ControllerEpoch);
        // The viewer tab must claim control explicitly to get it back.
        Assert.Equal(3, Rig.Ok(rig.Owner.JoinRoom(host.On(1), new JoinRoomRequest(rig.Req(), room.RoomCode, false))).ControllerEpoch);
        Assert.Equal(GhostLapFailure.NotController, rig.Ready(host, room, tab: 2).Code);
    }

    [Fact]
    public void Closing_the_controlling_tab_disconnects_the_competitor_even_when_a_viewer_tab_is_open()
    {
        var rig = new Rig();
        var (host, b) = (new Guest("host"), new Guest("b"));
        var room = rig.Create(b);
        rig.Join(host, room, tab: 1);
        rig.Owner.JoinRoom(host.On(2), new JoinRoomRequest(rig.Req(), room.RoomCode, false));
        rig.Owner.ConnectionClosed(host.On(1).ConnectionId); // viewer tab: nothing changes
        Assert.True(rig.View(b, room).Members.Single(m => !m.IsHost).Connected);
        rig.Owner.ConnectionClosed(host.On(2).ConnectionId); // controlling tab
        Assert.False(rig.View(b, room).Members.Single(m => !m.IsHost).Connected);
    }

    [Fact]
    public void A_member_cannot_hold_more_than_the_configured_number_of_connections()
    {
        var rig = new Rig(o => o.MaxConnectionsPerMember = 2);
        var host = new Guest("host");
        var room = rig.Create(host, tab: 1);
        Assert.True(rig.Owner.JoinRoom(host.On(2), new JoinRoomRequest(rig.Req(), room.RoomCode, false)).Ok);
        Assert.Equal(GhostLapFailure.TooManyConnections, rig.Owner.JoinRoom(host.On(3), new JoinRoomRequest(rig.Req(), room.RoomCode, false)).Code);
    }

    [Fact]
    public void Foreign_identities_and_forged_connections_cannot_act_on_a_room()
    {
        var rig = new Rig();
        var (host, b) = (new Guest("host"), new Guest("b"));
        var room = rig.Create(host);
        rig.Join(b, room);
        var mallory = new Guest("mallory");
        var forgedRoom = new RoomCommand(rig.Req(), room.View.RoomId);
        Assert.Equal(GhostLapFailure.RoomUnavailable, rig.Owner.StartCountdown(mallory.On(), forgedRoom).Code);
        Assert.Equal(GhostLapFailure.RoomUnavailable, rig.Owner.SetReady(mallory.On(), new SetReadyRequest(rig.Req(), room.View.RoomId, true)).Code);
        Assert.Equal(GhostLapFailure.RoomUnavailable, rig.Owner.LeaveRoom(mallory.On(), forgedRoom).Code);
        Assert.Equal(GhostLapFailure.RoomUnavailable, rig.Owner.GetRoom(mallory.On(), room.View.RoomId).Code);
        Assert.Equal(GhostLapFailure.InvalidRequest, rig.Owner.GetRoom(mallory.On(), "not-a-guid").Code);
        // A connection already bound to b's identity cannot be reused under another subject.
        var hijack = new RoomCaller(mallory.Subject, b.On().ConnectionId);
        Assert.Equal(GhostLapFailure.Forbidden, rig.Owner.CreateRoom(hijack, new CreateRoomRequest(rig.Req())).Code);
        Assert.Equal(GhostLapFailure.Forbidden, rig.Owner.JoinRoom(hijack, new JoinRoomRequest(rig.Req(), room.RoomCode, false)).Code);
        Assert.False(rig.Owner.IsMember(mallory.Subject, Guid.Parse(room.View.RoomId), competitorOnly: false));
    }

    [Fact]
    public void Spectators_have_read_only_access_and_every_competitor_command_is_denied_to_them()
    {
        var rig = new Rig();
        var (host, b, watcher) = (new Guest("host"), new Guest("b"), new Guest("watcher"));
        var room = rig.Create(host);
        rig.Join(b, room);
        rig.Join(watcher, room, spectator: true);
        Assert.NotNull(rig.View(watcher, room));
        Assert.Equal(GhostLapFailure.SpectatorForbidden, rig.Ready(watcher, room).Code);
        Assert.Equal(GhostLapFailure.SpectatorForbidden, rig.Start(watcher, room).Code);
        Assert.Equal(GhostLapFailure.SpectatorForbidden, rig.Cancel(watcher, room).Code);
        Assert.Equal(GhostLapFailure.SpectatorForbidden, rig.Owner.TransferHost(watcher.On(), new TransferHostRequest(rig.Req(), room.View.RoomId, "x")).Code);
        Assert.True(rig.Owner.IsMember(watcher.Subject, Guid.Parse(room.View.RoomId), competitorOnly: false));
        Assert.False(rig.Owner.IsMember(watcher.Subject, Guid.Parse(room.View.RoomId), competitorOnly: true));
        // A spectator may leave, and a closed tab frees the seat.
        rig.Owner.ConnectionClosed(watcher.On().ConnectionId);
        Assert.DoesNotContain(rig.View(host, room).Members, m => m.Role == RoomRoles.Spectator);
        Assert.Equal(GhostLapFailure.RoleConflict, rig.Owner.JoinRoom(host.On(2), new JoinRoomRequest(rig.Req(), room.RoomCode, true)).Code);
    }

    [Fact]
    public void Snapshots_expose_no_principal_ids_connection_ids_or_epochs()
    {
        var rig = new Rig();
        var (room, guests) = rig.ReadyRoom(3);
        var json = JsonSerializer.Serialize(Rig.Ok(rig.Start(guests[0], room)).View);
        Assert.DoesNotContain("guest:", json, StringComparison.Ordinal);
        Assert.DoesNotContain("-conn-", json, StringComparison.Ordinal);
        Assert.DoesNotContain("epoch", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(room.RoomCode, json, StringComparison.Ordinal); // the invite code is a per-recipient reply field, not broadcast state
        var snapshot = rig.Owner.PublicSnapshot(Guid.Parse(room.View.RoomId))!.Value;
        Assert.Equal(snapshot.Version, snapshot.View.Version);
        Assert.Null(rig.Owner.PublicSnapshot(Guid.NewGuid()));
    }

    // ---- expiry ------------------------------------------------------------------

    [Fact]
    public async Task Idle_rooms_expire_and_their_members_and_connections_are_released()
    {
        var rig = new Rig(o => o.LobbyIdleSeconds = 120);
        var host = new Guest("host");
        var room = rig.Create(host);
        rig.Time.Advance(TimeSpan.FromSeconds(121));
        await rig.Owner.SweepAsync(CancellationToken.None);
        Assert.Equal(0, rig.Owner.RoomCount);
        Assert.Equal(GhostLapFailure.RoomUnavailable, rig.Owner.GetRoom(host.On(), room.View.RoomId).Code);
        Assert.Equal(GhostLapFailure.Forbidden, rig.Owner.AdmitInput(host.On().ConnectionId, host.Subject, Input(room, 1)));
        Assert.True(rig.Owner.CreateRoom(host.On(), new CreateRoomRequest(rig.Req())).Ok); // the principal is free again
        var final = await DrainAsync(rig.Owner);
        Assert.Contains(final, p => p.View.Phase == RoomPhases.Closed && p.View.Notice == RoomNotices.RoomExpired);
    }

    [Fact]
    public async Task A_disconnected_competitor_is_removed_after_the_grace_and_an_empty_room_expires()
    {
        var rig = new Rig(o => { o.DisconnectGraceSeconds = 10; o.EmptyRoomSeconds = 30; });
        var (host, b) = (new Guest("host"), new Guest("b"));
        var room = rig.Create(host);
        rig.Join(b, room);
        rig.Owner.ConnectionClosed(b.On().ConnectionId);
        rig.Time.Advance(TimeSpan.FromSeconds(5));
        await rig.Owner.SweepAsync(CancellationToken.None);
        Assert.Equal(2, rig.View(host, room).Members.Count); // still in grace
        rig.Time.Advance(TimeSpan.FromSeconds(6));
        await rig.Owner.SweepAsync(CancellationToken.None);
        Assert.Single(rig.View(host, room).Members);
        rig.Owner.ConnectionClosed(host.On().ConnectionId);
        rig.Time.Advance(TimeSpan.FromSeconds(11));
        await rig.Owner.SweepAsync(CancellationToken.None); // grace over: the last member is removed and the room closes
        Assert.Equal(0, rig.Owner.RoomCount);
    }

    // ---- gameplay ingress identity checks ------------------------------------------

    [Fact]
    public void Gameplay_ingress_requires_the_current_controlling_competitor_epoch_and_reports_no_running_match_in_10_1()
    {
        var rig = new Rig();
        var (host, b, watcher) = (new Guest("host"), new Guest("b"), new Guest("watcher"));
        var room = rig.Create(host);
        rig.Join(b, room);
        rig.Join(watcher, room, spectator: true);
        var tab1 = host.On(1).ConnectionId;
        Assert.Equal(GhostLapFailure.NoRunningMatch, rig.Owner.AdmitInput(tab1, host.Subject, Input(room, room.ControllerEpoch)));
        Assert.Equal(GhostLapFailure.SpectatorForbidden, rig.Owner.AdmitInput(watcher.On().ConnectionId, watcher.Subject, Input(room, 0)));
        Assert.Equal(GhostLapFailure.Forbidden, rig.Owner.AdmitInput("unbound", host.Subject, Input(room, 1)));
        Assert.Equal(GhostLapFailure.Forbidden, rig.Owner.AdmitInput(tab1, b.Subject, Input(room, 1)));
        Assert.Equal(GhostLapFailure.Superseded, rig.Owner.AdmitInput(tab1, host.Subject, Input(room, room.ControllerEpoch + 5)));
        Assert.Equal(GhostLapFailure.InvalidRequest, rig.Owner.AdmitInput(tab1, host.Subject, Input(room, room.ControllerEpoch) with { MoveX = 2 }));
        Assert.Equal(GhostLapFailure.InvalidRequest, rig.Owner.AdmitInput(tab1, host.Subject, Input(room, room.ControllerEpoch) with { Seq = -1 }));
        Assert.Equal(GhostLapFailure.InvalidRequest, rig.Owner.AdmitInput(tab1, host.Subject, Input(room, room.ControllerEpoch) with { MatchId = "x" }));
    }

    [Fact]
    public void After_a_takeover_the_old_controller_cannot_submit_gameplay_but_the_new_one_reaches_the_sink()
    {
        var sink = new CapturingSink();
        var rig = new Rig(sink: sink);
        var (host, b) = (new Guest("host"), new Guest("b"));
        var room = rig.Create(host, tab: 1);
        rig.Join(b, room);
        var oldTab = host.On(1).ConnectionId;
        Assert.Null(rig.Owner.AdmitInput(oldTab, host.Subject, Input(room, 1)));
        var takeover = Rig.Ok(rig.Owner.JoinRoom(host.On(2), new JoinRoomRequest(rig.Req(), room.RoomCode, false)));
        // Same epoch the old tab last knew, and the new epoch: both refused from the old connection.
        Assert.Equal(GhostLapFailure.Superseded, rig.Owner.AdmitInput(oldTab, host.Subject, Input(room, 1)));
        Assert.Equal(GhostLapFailure.Superseded, rig.Owner.AdmitInput(oldTab, host.Subject, Input(room, takeover.ControllerEpoch)));
        // The new connection with a stale epoch is also refused.
        Assert.Equal(GhostLapFailure.Superseded, rig.Owner.AdmitInput(host.On(2).ConnectionId, host.Subject, Input(room, 1)));
        Assert.Null(rig.Owner.AdmitInput(host.On(2).ConnectionId, host.Subject, Input(room, takeover.ControllerEpoch)));
        Assert.Equal([1L, 2L], sink.Seen.Select(i => i.ControllerEpoch));
        Assert.All(sink.Seen, i => Assert.Equal(room.MemberId, i.MemberId));
    }

    [Fact]
    public async Task A_takeover_is_announced_to_the_old_connection_through_the_notice_queue()
    {
        var rig = new Rig();
        var host = new Guest("host");
        var room = rig.Create(host, tab: 1);
        rig.Owner.JoinRoom(host.On(2), new JoinRoomRequest(rig.Req(), room.RoomCode, false));
        var notice = await rig.Owner.Channels.Notices.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(new ConnectionNotice(host.On(1).ConnectionId, GhostLapFailure.Superseded), notice);
    }

    [Fact]
    public void Gameplay_input_budget_allows_30_hz_plus_rapid_direction_changes_and_bounds_a_flood()
    {
        var options = new MultiplayerOptions();
        var time = new ManualTime();
        var budget = new InputBudget(options, time);
        // 30 Hz movement plus an extra direction change every tick (60 items/s) and Burst edges for 20 seconds: never refused.
        for (var i = 0; i < 20 * 60; i++)
        {
            Assert.True(budget.TryTake(), "refused at item " + i);
            time.Advance(TimeSpan.FromMilliseconds(1000.0 / 60));
        }
        // A flood of 1000 items inside one tick is bounded by the burst, then refused, then the stream is cut.
        var accepted = Enumerable.Range(0, 1000).Count(_ => budget.TryTake());
        Assert.InRange(accepted, options.InputBurst - 5, options.InputBurst + 5);
        Assert.True(budget.ShouldDisconnect); // ~940 consecutive over-limit items exceed the cut-off
    }

    // ---- stress: concurrent joins across rooms -------------------------------------

    [Fact]
    public async Task Concurrent_joins_leaves_and_disconnects_keep_every_room_and_the_one_room_per_principal_rule_consistent()
    {
        var rig = new Rig(o => { o.MaxSpectators = 3; o.CodeLookupsPerMinute = 600; o.MaxRooms = 64; });
        var guests = Enumerable.Range(0, 40).Select(i => new Guest("g" + i)).ToArray();
        var hosts = guests.Take(6).ToArray();
        var rooms = hosts.Select(h => rig.Create(h)).ToArray();
        var rng = new Random(12345);
        var plans = guests.Skip(6).Select(g => (Guest: g, Room: rooms[rng.Next(rooms.Length)], Spectator: rng.Next(4) == 0)).ToArray();

        var joins = await Task.WhenAll(plans.Select(p => Task.Run(() => rig.Owner.JoinRoom(p.Guest.On(), new JoinRoomRequest(rig.Req(), p.Room.RoomCode, p.Spectator)))));
        Assert.All(joins, r => Assert.True(r.Ok || r.Code is GhostLapFailure.RoomFull or GhostLapFailure.SpectatorsFull, "unexpected " + r.Code));

        // Random concurrent churn, then check every invariant.
        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            var local = new Random(worker);
            for (var i = 0; i < 400; i++)
            {
                var g = guests[local.Next(guests.Length)];
                var room = rooms[local.Next(rooms.Length)];
                switch (local.Next(7))
                {
                    case 0: rig.Owner.LeaveRoom(g.On(), new RoomCommand(rig.Req(), room.View.RoomId)); break;
                    case 1: rig.Owner.ConnectionClosed(g.On().ConnectionId); break;
                    case 2: rig.Owner.JoinRoom(g.Reconnect(), new JoinRoomRequest(rig.Req(), room.RoomCode, local.Next(5) == 0)); break;
                    case 3: rig.Owner.SetReady(g.On(), new SetReadyRequest(rig.Req(), room.View.RoomId, local.Next(2) == 0)); break;
                    case 4: rig.Owner.StartCountdown(g.On(), new RoomCommand(rig.Req(), room.View.RoomId)); break;
                    case 5: rig.Owner.CancelCountdown(g.On(), new RoomCommand(rig.Req(), room.View.RoomId)); break;
                    default: rig.Owner.TransferHost(g.On(), new TransferHostRequest(rig.Req(), room.View.RoomId, "m")); break;
                }
            }
        })));

        var seenSubjects = new HashSet<string>();
        foreach (var room in rooms)
        {
            var snapshot = rig.Owner.PublicSnapshot(Guid.Parse(room.View.RoomId));
            if (snapshot is null)
            {
                continue; // closed by churn (everyone left)
            }
            LobbyInvariants.Check(snapshot.Value.View, rig.Options);
            foreach (var g in guests.Where(g => rig.Owner.IsMember(g.Subject, Guid.Parse(room.View.RoomId), false)))
            {
                Assert.True(seenSubjects.Add(g.Subject), g.Name + " is a member of two rooms");
            }
        }
        var versions = (await DrainAsync(rig.Owner)).GroupBy(p => p.RoomId);
        foreach (var perRoom in versions)
        {
            Assert.Equal(perRoom.Select(p => p.Version).Order(), perRoom.Select(p => p.Version)); // published in version order
            Assert.Equal(perRoom.Count(), perRoom.Select(p => p.Version).Distinct().Count());
        }
    }

    [Fact]
    public async Task Twelve_simultaneous_joins_into_a_three_seat_gap_admit_exactly_three()
    {
        var rig = new Rig(o => o.CodeLookupsPerMinute = 600);
        var room = rig.Create(new Guest("host"));
        var joiners = Enumerable.Range(0, 12).Select(i => new Guest("j" + i)).ToArray();
        var results = await Task.WhenAll(joiners.Select(j => Task.Run(() => rig.Owner.JoinRoom(j.On(), new JoinRoomRequest(rig.Req(), room.RoomCode, false)))));
        Assert.Equal(3, results.Count(r => r.Ok));
        Assert.Equal(9, results.Count(r => r.Code == GhostLapFailure.RoomFull));
        var view = rig.Owner.PublicSnapshot(Guid.Parse(room.View.RoomId))!.Value.View;
        Assert.Equal(4, view.Members.Count);
        Assert.Equal(4, view.Members.Count(m => m.Connected));
        LobbyInvariants.Check(view, rig.Options);
    }

    private static GameplayInput Input(JoinedRoom room, long epoch) =>
        new(Guid.NewGuid().ToString("N"), epoch, 0, 1, 1, 0, false);

    private static async Task<List<RoomPublication>> DrainAsync(ActiveRoomOwner owner)
    {
        var all = new List<RoomPublication>();
        while (owner.Channels.Publications.TryRead(out var p))
        {
            all.Add(p);
        }
        await Task.CompletedTask;
        return all;
    }

    private sealed class HangingAdmission : IMatchStartAdmission
    {
        public async ValueTask<StartAdmissionOutcome> AdmitAsync(SealedRoundConfig config, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return StartAdmissionOutcome.Admitted;
        }
    }

    private sealed class ThrowingAdmission : IMatchStartAdmission
    {
        public ValueTask<StartAdmissionOutcome> AdmitAsync(SealedRoundConfig config, CancellationToken ct) =>
            throw new InvalidOperationException("receipt store down");
    }

    private sealed class GatedAdmission : IMatchStartAdmission
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<StartAdmissionOutcome> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<StartAdmissionOutcome> AdmitAsync(SealedRoundConfig config, CancellationToken ct)
        {
            Entered.TrySetResult();
            return await Release.Task;
        }
    }

    private sealed class SealedConfigComparer : IEqualityComparer<SealedRoundConfig>
    {
        public bool Equals(SealedRoundConfig? x, SealedRoundConfig? y) =>
            x is not null && y is not null && x.ConfigDigest == y.ConfigDigest && x.MatchId == y.MatchId && x.Seed == y.Seed
            && x.Slots.SequenceEqual(y.Slots);

        public int GetHashCode(SealedRoundConfig obj) => obj.ConfigDigest.GetHashCode(StringComparison.Ordinal);
    }
}

internal static class LobbyInvariants
{
    public static void Check(RoomView view, MultiplayerOptions options)
    {
        var competitors = view.Members.Where(m => m.Role == RoomRoles.Competitor).ToArray();
        var spectators = view.Members.Where(m => m.Role == RoomRoles.Spectator).ToArray();
        Assert.True(competitors.Length <= options.MaxCompetitors, "too many competitors");
        Assert.True(spectators.Length <= options.MaxSpectators, "too many spectators");
        Assert.Equal(view.Members.Count, view.Members.Select(m => m.MemberId).Distinct().Count());
        var hosts = view.Members.Where(m => m.IsHost).ToArray();
        if (competitors.Length > 0)
        {
            Assert.Single(hosts);
            Assert.Equal(RoomRoles.Competitor, hosts[0].Role);
        }
        Assert.All(spectators, s => Assert.False(s.Ready || s.IsHost || s.Slot is not null));
        if (view.Phase == RoomPhases.Countdown)
        {
            Assert.InRange(competitors.Length, 2, 4);
            Assert.NotNull(view.Round);
            Assert.Equal(competitors.Length, view.Round!.Slots.Count);
            Assert.All(competitors, c => Assert.True(c.Ready && c.Connected && c.Slot is not null));
        }
        else if (view.Phase == RoomPhases.Lobby)
        {
            Assert.Null(view.Round);
        }
    }
}
