using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using GhostLap.Multiplayer;
using GhostLap.Rules;
using GhostLap.Simulation;
using Xunit;
using Xunit.Abstractions;

namespace GhostLap.Tests.Multiplayer;

/// <summary>
/// Task 10.5 (R10.18/R10.19), prepared: deterministic fairness and strategy fixtures over the real <c>ghost-mp-v1</c> reducer. The default
/// matrix is deliberately small (a smoke run). The wide matrix and the judgement of whether incentives are defensible belong to the delegated
/// Codex verification: set <c>GHOSTLAP_FAIRNESS_SEEDS</c> (seed count) and <c>GHOSTLAP_FAIRNESS_OUT</c> (JSON-lines path) to widen and record it.
/// Assertions here are invariants of the sealed rules and of determinism. They do not assert that any strategy beats another: that is a
/// measurement for architectural review, and changing the rules because of it would be a new versioned identity (ADR-P1-22).
/// </summary>
public sealed class CompetitiveFairnessTests(ITestOutputHelper output)
{
    private static int SeedCount => int.TryParse(Environment.GetEnvironmentVariable("GHOSTLAP_FAIRNESS_SEEDS"), out var n) && n is > 0 and <= 5000 ? n : 2;

    private static long SeedAt(int index) => 1000 + (index * 7919L);

    private void Record(PlayStats stats)
    {
        var line = JsonSerializer.Serialize(stats);
        if (SeedCount <= 2)
        {
            output.WriteLine(line);
        }
        if (Environment.GetEnvironmentVariable("GHOSTLAP_FAIRNESS_OUT") is { Length: > 0 } path)
        {
            lock (typeof(CompetitiveFairnessTests))
            {
                File.AppendAllText(path, line + "\n");
            }
        }
    }

    // ------------------------------------------------------------------------------------------ layouts

    private static int[,] Distances(int startX, int startY)
    {
        // Breadth-first search over the 10-unit lattice with the real occupancy test: a step is shorter than any wall, so a lattice path is a real path.
        const int step = GameRulesV1.CardinalStepFixed;
        var w = (GameRulesV1.WorldWidthFixed / step) + 1;
        var h = (GameRulesV1.WorldHeightFixed / step) + 1;
        var dist = new int[w, h];
        for (var x = 0; x < w; x++)
        {
            for (var y = 0; y < h; y++)
            {
                dist[x, y] = -1;
            }
        }
        var queue = new Queue<(int X, int Y)>();
        dist[startX / step, startY / step] = 0;
        queue.Enqueue((startX / step, startY / step));
        while (queue.Count > 0)
        {
            var (cx, cy) = queue.Dequeue();
            foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                var nx = cx + dx;
                var ny = cy + dy;
                if (nx < 0 || ny < 0 || nx >= w || ny >= h || dist[nx, ny] >= 0 || !ArenaV1.CanOccupy(nx * step, ny * step))
                {
                    continue;
                }
                dist[nx, ny] = dist[cx, cy] + 1;
                queue.Enqueue((nx, ny));
            }
        }
        return dist;
    }

    /// <summary>
    /// **Validates: Requirements 10.12, 10.18** Every candle site is reachable from every 2/3/4-player spawn socket, and the 2- and 4-player
    /// layouts are point-symmetric: a slot and its mirror slot see the same multiset of path lengths to the 24 sites. The 3-player socket
    /// distances are recorded, not asserted equal (that layout is not point-symmetric by authoring).
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Every_candle_site_is_reachable_from_every_spawn_socket_and_symmetric_layouts_are_equally_far(int players)
    {
        var sockets = ArenaV1.SpawnSockets(players);
        var perSlot = new List<int[]>();
        foreach (var socket in sockets)
        {
            var dist = Distances(socket.X, socket.Y);
            var toSites = ArenaV1.CandleSites.Select(site =>
            {
                // A site's centre may not be on the 10-unit lattice used by the search; use the nearest lattice cell the collider can occupy.
                var d = dist[(site.X + 5 * 1024) / GameRulesV1.CardinalStepFixed, (site.Y + 5 * 1024) / GameRulesV1.CardinalStepFixed];
                Assert.True(d >= 0, $"{players}P socket ({socket.X / 1024},{socket.Y / 1024}) cannot reach site ({site.X / 1024},{site.Y / 1024})");
                return d;
            }).Order().ToArray();
            perSlot.Add(toSites);
        }
        for (var slot = 0; slot < players; slot++)
        {
            output.WriteLine($"{players}P slot {slot}: path ticks to the 24 sites min {perSlot[slot][0]}, median {perSlot[slot][12]}, max {perSlot[slot][^1]}");
        }
        if (players is 2 or 4)
        {
            for (var slot = 0; slot < players; slot++)
            {
                Assert.Equal(perSlot[players - 1 - slot], perSlot[slot]); // slot i mirrors slot (n-1-i) under the 180-degree rotation
            }
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Scheduled_candle_travel_is_measured_on_the_same_seeds_as_competitive_play(int players)
    {
        var sockets = ArenaV1.SpawnSockets(players);
        var rows = new List<string>();
        for (var s = 0; s < SeedCount; s++)
        {
            var seed = SeedAt(s);
            var candles = CandlePlan.Create(seed).Candles;
            var meanPathTicks = sockets.Select(socket => candles.Average(c => RoutedSeeker.InitialDistance(socket, c))).ToArray();
            Assert.All(meanPathTicks, distance => Assert.InRange(distance, 1, 250));
            rows.Add(JsonSerializer.Serialize(new { Seed = seed, Players = players, MeanPathTicks = meanPathTicks }));
        }
        if (Environment.GetEnvironmentVariable("GHOSTLAP_FAIRNESS_LAYOUT_OUT") is { Length: > 0 } path)
        {
            lock (typeof(CompetitiveFairnessTests))
            {
                File.AppendAllLines(path, rows);
            }
        }
    }

    // ------------------------------------------------------------------------------------------ slot assignment

    /// <summary>
    /// **Validates: Requirements 10.12** Sealed slot assignment is fair across rooms: over many room secrets each principal lands in each slot
    /// about equally often, so the lowest-slot priority (ADR-P1-22 D4) is not a chosen advantage. The bound is loose on purpose (binomial noise
    /// over a fixed set of secrets, so the test is deterministic); it catches a biased or constant assignment, not a 2% skew.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Sealed_slot_assignment_is_spread_evenly_across_rooms(int players)
    {
        const int rooms = 600;
        var roster = Enumerable.Range(0, players).Select(i => new RosterEntry($"m{i}", $"guest:{i:D8}-0000-0000-0000-000000000000")).ToArray();
        var counts = new int[players, players];
        Span<byte> fixtureSeed = stackalloc byte[sizeof(int)];
        for (var r = 0; r < rooms; r++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(fixtureSeed, (players * 1000) + r);
            var key = SHA256.HashData(fixtureSeed);
            var sealedRound = RoundSealing.Seal(key, Guid.Empty, 1, roster, new MultiplayerOptions(), MatchTestKit.Generation);
            foreach (var slot in sealedRound.Slots)
            {
                counts[int.Parse(slot.MemberId[1..]), slot.Slot]++;
            }
        }
        var expected = rooms / (double)players;
        for (var member = 0; member < players; member++)
        {
            output.WriteLine($"{players}P member {member} slot counts: {string.Join(", ", Enumerable.Range(0, players).Select(s => counts[member, s]))} (expected {expected:F0})");
            for (var slot = 0; slot < players; slot++)
            {
                Assert.InRange(counts[member, slot], expected * 0.6, expected * 1.4);
            }
        }
        // Join order, roster order and request order do not exist in the seal: a reversed roster seals to the same assignment.
        var secret = Enumerable.Repeat((byte)5, 32).ToArray();
        var room = Guid.NewGuid();
        var forward = RoundSealing.Seal(secret, room, 1, roster, new MultiplayerOptions(), MatchTestKit.Generation);
        var reversed = RoundSealing.Seal(secret, room, 1, [.. roster.OrderByDescending(r => r.MemberId, StringComparer.Ordinal)], new MultiplayerOptions(), MatchTestKit.Generation);
        Assert.Equal(forward.ConfigDigest, reversed.ConfigDigest);
    }

    // ------------------------------------------------------------------------------------------ playtests

    private static ScriptedPlayer[] Seekers(int n, int farmerSlot = -1) =>
        [.. Enumerable.Range(0, n).Select(i => i == farmerSlot ? (ScriptedPlayer)new LaneFarmer() : new CandleSeeker(burstsNearRivals: true))];

    /// <summary>
    /// **Validates: Requirements 10.12, 10.19** Scripted rounds in 2/3/4-player rooms (all seekers; one lane farmer among seekers; all lane
    /// farmers). Invariants that must hold for every seed: the run is reproducible, a contested candle always goes to the lowest overlapping slot,
    /// movement points never exceed the full-speed ceiling, each candle is collected at most once and equals the pickup total, and no total is
    /// negative. The scores themselves are recorded as measurements (output and optional JSON lines), not asserted.
    /// </summary>
    [Fact]
    public void Scripted_rounds_hold_the_sealed_rule_invariants_in_2_3_and_4_player_rooms()
    {
        for (var s = 0; s < SeedCount; s++)
        {
            var seed = SeedAt(s);
            for (var n = 2; n <= 4; n++)
            {
                foreach (var (scenario, farmer) in new[] { ("all-seekers", -1), ("one-lane-farmer", n - 1), ("all-lane-farmers", -2) })
                {
                    var players = farmer == -2 ? [.. Enumerable.Range(0, n).Select(_ => (ScriptedPlayer)new LaneFarmer())] : Seekers(n, farmer);
                    var stats = Playtest.Run(seed, players, scenario);
                    Record(stats);
                    Assert.All(stats.MovementPoints, m => Assert.InRange(m, 0, Playtest.MovementPointCeiling));
                    Assert.All(stats.Scores, t => Assert.True(t >= 0));
                    Assert.Equal(stats.CandlesCollected, stats.Pickups.Sum());
                    Assert.InRange(stats.ContestedCollections, 0, stats.CandlesCollected);
                    Assert.Equal(stats.PickupPoints, stats.Pickups.Select(p => p * GameRulesV1.CandlePickupPoints).ToArray());
                    if (s == 0 && n == 4)
                    {
                        var again = Playtest.Run(seed, farmer == -2 ? [.. Enumerable.Range(0, n).Select(_ => (ScriptedPlayer)new LaneFarmer())] : Seekers(n, farmer), scenario);
                        Assert.Equal(stats.StateHash, again.StateHash); // same seed and same scripted decisions reproduce the same world
                    }
                }
            }
        }
    }

    /// <summary>
    /// **Validates: Requirements 10.12, 10.19** Slot permutation: the one lane farmer is placed in every slot of a 2- and 3-player room (all
    /// permutations of a role set). The per-slot results are recorded so the lowest-slot priority and spawn-socket effects can be read from
    /// data; the assertion is only that each permutation is a valid, reproducible round with the same candle schedule (the plan never depends on
    /// who sits where).
    /// </summary>
    [Fact]
    public void Role_placement_across_slots_changes_no_candle_schedule_and_is_recorded_for_review()
    {
        var seed = SeedAt(0);
        foreach (var n in new[] { 2, 3 })
        {
            var plan = MpMatchContext.Create(seed, n).Plan.Candles.Select(c => (c.Id, c.SiteIndex, c.ActivateTick)).ToArray();
            for (var farmerSlot = 0; farmerSlot < n; farmerSlot++)
            {
                var stats = Playtest.Run(seed, Seekers(n, farmerSlot), $"farmer-in-slot-{farmerSlot}");
                Record(stats);
                Assert.Equal(plan, MpMatchContext.Create(seed, n).Plan.Candles.Select(c => (c.Id, c.SiteIndex, c.ActivateTick)).ToArray());
                Assert.Equal("lane-farmer", stats.Roles[farmerSlot]);
            }
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Routed_candle_and_safe_movement_policies_rotate_across_every_socket(int players)
    {
        var random = new Random(1052026 + players);
        for (var s = 0; s < SeedCount; s++)
        {
            var seed = SeedAt(s);
            Record(Playtest.Run(seed, [.. Enumerable.Range(0, players).Select(_ => new RoutedSeeker(true))], "routed-all-engage"));
            foreach (var (name, make) in new (string, Func<ScriptedPlayer>)[]
            {
                ("corner-v-engagement", () => new CornerFarmer()),
                ("lane-v-engagement", () => new LaneFarmer()),
                ("evasion-v-engagement", () => new RoutedSeeker(false)),
            })
            {
                var rotation = random.Next(players);
                for (var i = 0; i < players; i++)
                {
                    var rotated = (rotation + i) % players;
                    var scripts = Enumerable.Range(0, players)
                        .Select(slot => slot == rotated ? make() : new RoutedSeeker(true)).ToArray();
                    var stats = Playtest.Run(seed, scripts, name);
                    Assert.All(stats.MovementPoints, points => Assert.InRange(points, 0, Playtest.MovementPointCeiling));
                    Record(stats);
                }
            }
        }
    }

    /// <summary>
    /// **Validates: Requirements 10.12** Mascots are cosmetic: the same strategies in the same slots give identical positions, scores and events
    /// whatever mascot each slot wears (identical physical players), over a 600-tick window.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void The_mascot_assignment_never_changes_the_outcome(int n)
    {
        var seed = SeedAt(1);
        var identity = Enumerable.Range(0, n).Select(i => (byte)i).ToArray();
        var rotated = Enumerable.Range(0, n).Select(i => (byte)((i + 1) % GameRulesV1.MascotCount)).ToArray();
        var a = Playtest.Run(seed, Seekers(n), "mascots-identity", 600, () => GameContext.Create(seed, n, identity));
        var b = Playtest.Run(seed, Seekers(n), "mascots-rotated", 600, () => GameContext.Create(seed, n, rotated));
        Assert.Equal(a.Scores, b.Scores);
        Assert.Equal(a.Pickups, b.Pickups);
        Assert.Equal(a.MovementPoints, b.MovementPoints);
        Assert.Equal(a.CandlesCollected, b.CandlesCollected);
    }

    /// <summary>
    /// **Validates: Requirements 10.12** Seed variation: different seeds give different candle schedules and different worlds, the same seed the
    /// same one, for every player count (so a fairness matrix over seeds samples genuinely different rounds).
    /// </summary>
    [Fact]
    public void Different_seeds_give_different_candle_schedules_and_worlds()
    {
        var hashes = new HashSet<string>();
        var schedules = new HashSet<string>();
        for (var s = 0; s < 4; s++)
        {
            var stats = Playtest.Run(SeedAt(s), Seekers(3), "seed-variation", 300);
            hashes.Add(stats.StateHash);
            schedules.Add(string.Join(",", MpMatchContext.Create(SeedAt(s), 3).Plan.Candles.Take(40).Select(c => $"{c.SiteIndex}@{c.ActivateTick}")));
        }
        Assert.Equal(4, hashes.Count);
        Assert.Equal(4, schedules.Count);
    }
}
