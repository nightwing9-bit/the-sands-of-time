using FsCheck;
using FsCheck.Fluent;
using GhostLap.Multiplayer;
using GhostLap.Rules;
using Xunit;

namespace GhostLap.Tests.Multiplayer;

/// <summary>R10.02, R10.12 (task 10.1): the sealed round config and the slot-assignment fairness properties.</summary>
public sealed class RoundSealingTests
{
    private static readonly MultiplayerOptions Options = new();

    private static byte[] Secret(int n)
    {
        var bytes = new byte[32];
        new Random(n).NextBytes(bytes);
        return bytes;
    }

    private static RosterEntry[] Roster(int players) =>
        [.. Enumerable.Range(0, players).Select(i => new RosterEntry("m" + i, "guest:00000000-0000-0000-0000-00000000000" + i))];

    /// <summary>**Validates: Requirements 10.02, 10.12** Sealing is a pure function of (secret, room, ordinal, roster set): order of input never matters.</summary>
    [Fact]
    public void Sealing_is_deterministic_and_independent_of_roster_input_order()
    {
        var gen = from seed in Gen.Choose(0, 10_000)
                  from players in Gen.Choose(2, 4)
                  from order in Gen.Shuffle(Enumerable.Range(0, players).ToArray())
                  select (seed, players, order);
        Prop.ForAll(gen.ToArbitrary(), t =>
        {
            var roster = Roster(t.players);
            var shuffled = t.order.Select(i => roster[i]).ToArray();
            var room = Guid.Parse("11111111-1111-1111-1111-111111111111");
            var generation = Guid.Parse("22222222-2222-2222-2222-222222222222");
            var a = RoundSealing.Seal(Secret(t.seed), room, 1, roster, Options, generation);
            var b = RoundSealing.Seal(Secret(t.seed), room, 1, shuffled, Options, generation);
            return a.ConfigDigest == b.ConfigDigest && a.MatchId == b.MatchId && a.Slots.SequenceEqual(b.Slots);
        }).Check(Config.QuickThrowOnFailure.WithMaxTest(200));
    }

    /// <summary>
    /// **Validates: Requirements 10.02, 10.12** The relative slot order of two principals is the same in every roster containing both,
    /// so leaving, rejoining or changing who else is in the room cannot reorder two players against each other.
    /// </summary>
    [Fact]
    public void Relative_slot_order_of_two_players_is_the_same_in_every_roster_that_contains_both()
    {
        var gen = from seed in Gen.Choose(0, 10_000)
                  from extra in Gen.Choose(0, 2)
                  select (seed, extra);
        Prop.ForAll(gen.ToArbitrary(), t =>
        {
            var all = Roster(4);
            var room = Guid.NewGuid();
            var pair = RoundSealing.Seal(Secret(t.seed), room, 1, [all[0], all[1]], Options, Guid.Empty);
            var larger = RoundSealing.Seal(Secret(t.seed), room, 1, [.. all.Take(2 + t.extra)], Options, Guid.Empty);
            var four = RoundSealing.Seal(Secret(t.seed), room, 1, all, Options, Guid.Empty);
            bool FirstBefore(SealedRoundConfig c) => c.Slots.Single(s => s.MemberId == "m0").Slot < c.Slots.Single(s => s.MemberId == "m1").Slot;
            return FirstBefore(pair) == FirstBefore(larger) && FirstBefore(pair) == FirstBefore(four);
        }).Check(Config.QuickThrowOnFailure.WithMaxTest(200));
    }

    [Fact]
    public void Slot_one_is_not_systematically_given_to_any_player_across_secrets()
    {
        var firstCounts = new int[4];
        for (var i = 0; i < 1600; i++)
        {
            var c = RoundSealing.Seal(Secret(i), Guid.NewGuid(), 1, Roster(4), Options, Guid.Empty);
            firstCounts[int.Parse(c.Slots[0].MemberId[1..], System.Globalization.CultureInfo.InvariantCulture)]++;
        }
        // Expected 400 each, sd about 17. A 5-sigma window is wide enough to be stable and still catches any fixed or join-order bias.
        Assert.All(firstCounts, n => Assert.InRange(n, 310, 490));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Every_slot_gets_the_arena_spawn_socket_for_its_index_and_the_config_pins_identity(int players)
    {
        var c = RoundSealing.Seal(Secret(7), Guid.NewGuid(), 1, Roster(players), Options, Guid.NewGuid());
        var sockets = ArenaV1.SpawnSockets(players);
        for (var i = 0; i < players; i++)
        {
            Assert.Equal((sockets[i].X, sockets[i].Y), (c.Slots[i].SpawnXFixed, c.Slots[i].SpawnYFixed));
        }
        Assert.Equal(("ghost-mp-v1", "arena-v1", "ghost-mp-v1+arena-v1", 2700, "elo-pair-v1"), (c.RulesId, c.LayoutId, c.RulesHash, c.TargetTicks, c.RatingAlgorithmId));
        Assert.Matches("^[0-9a-f]{64}$", c.ConfigDigest);
        Assert.Matches("^[0-9a-f]{64}$", c.LayoutFingerprint);
        Assert.True(c.Seed >= 0);
    }

    [Fact]
    public void Changing_any_sealed_input_changes_the_digest_and_match_id()
    {
        var secret = Secret(1);
        var room = Guid.NewGuid();
        var generation = Guid.NewGuid();
        var baseline = RoundSealing.Seal(secret, room, 1, Roster(3), Options, generation);
        var variants = new[]
        {
            RoundSealing.Seal(secret, room, 2, Roster(3), Options, generation),
            RoundSealing.Seal(secret, room, 1, Roster(4), Options, generation),
            RoundSealing.Seal(secret, room, 1, Roster(3), new MultiplayerOptions { RatingAlgorithmId = "other-algo" }, generation),
            RoundSealing.Seal(secret, room, 1, Roster(3), new MultiplayerOptions { CountdownMilliseconds = 4000 }, generation),
            RoundSealing.Seal(secret, room, 1, Roster(3), Options, Guid.NewGuid()),
            RoundSealing.Seal(secret, Guid.NewGuid(), 1, Roster(3), Options, generation),
        };
        Assert.All(variants, v =>
        {
            Assert.NotEqual(baseline.ConfigDigest, v.ConfigDigest);
            Assert.NotEqual(baseline.MatchId, v.MatchId);
        });
    }

    [Fact]
    public void Illegal_rosters_and_weak_secrets_are_refused_by_the_sealer()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RoundSealing.Seal(Secret(1), Guid.NewGuid(), 1, Roster(1), Options, Guid.Empty));
        Assert.Throws<ArgumentOutOfRangeException>(() => RoundSealing.Seal(Secret(1), Guid.NewGuid(), 1, [.. Roster(4), new RosterEntry("m9", "guest:9")], Options, Guid.Empty));
        Assert.Throws<ArgumentException>(() => RoundSealing.Seal(Secret(1), Guid.NewGuid(), 1, [new RosterEntry("a", "guest:1"), new RosterEntry("b", "guest:1")], Options, Guid.Empty));
        Assert.Throws<ArgumentException>(() => RoundSealing.Seal(new byte[8], Guid.NewGuid(), 1, Roster(2), Options, Guid.Empty));
    }

    // ---- startup validation ----------------------------------------------------------

    [Fact]
    public void Default_options_and_the_arena_layout_pass_startup_validation()
    {
        Assert.Empty(MultiplayerStartup.Validate(new MultiplayerOptions()));
        var errors = new List<string>();
        MultiplayerStartup.ValidateRulesAndLayout(errors);
        Assert.Empty(errors);
    }

    [Fact]
    public void Out_of_range_or_unsafe_options_fail_startup_validation_with_the_key_named()
    {
        var bad = new MultiplayerOptions
        {
            MaxCompetitors = 5,
            MaxSpectators = -1,
            RoomCodeLength = 3,
            InputsPerSecond = 5,
            InputBurst = 0,
            RatingAlgorithmId = "Has Spaces",
            AllowedOrigins = ["https://ok.example", "not a url", "https://x.example/path"],
        };
        var errors = string.Join("\n", MultiplayerStartup.Validate(bad));
        foreach (var key in new[] { "MaxCompetitors", "MaxSpectators", "RoomCodeLength", "InputsPerSecond", "InputBurst", "RatingAlgorithmId", "AllowedOrigins" })
        {
            Assert.Contains(key, errors, StringComparison.Ordinal);
        }
        Assert.Throws<InvalidOperationException>(() => new ActiveRoomOwner(
            bad, TimeProvider.System, new UnavailableStartAdmission(), new NoRunningMatchSink(), new CryptoRoomRandom(), Guid.NewGuid()));
    }
}
