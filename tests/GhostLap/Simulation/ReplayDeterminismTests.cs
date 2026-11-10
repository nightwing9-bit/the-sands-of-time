using System.Security.Cryptography;
using FsCheck;
using FsCheck.Fluent;
using GhostLap.Rules;
using GhostLap.Simulation;
using GhostLap.Timeline;
using Marrow.Kernel.Canonical;
using Marrow.Timeline;
using Xunit;

namespace GhostLap.Tests.Simulation;

public sealed class ReplayDeterminismTests
{
    /// <summary>Per tick, per slot: null means no input that tick, otherwise a move vector.</summary>
    private sealed record Script(long Seed, int Players, IReadOnlyList<(sbyte X, sbyte Y)?[]> Ticks);

    private static IReadOnlyList<OrderedInput<GameInput>> InputsAt(Script s, long tick)
    {
        var list = new List<OrderedInput<GameInput>>();
        var row = s.Ticks[(int)tick - 1];
        for (byte slot = 0; slot < s.Players; slot++)
        {
            if (row[slot] is { } m)
            {
                list.Add(new OrderedInput<GameInput>($"t{tick}-s{slot}", slot, new GameInput(tick, slot, (int)tick, m.X, m.Y, false)));
            }
        }
        return list;
    }

    private static GameState Run(GameContext context, Script s, GameState? from = null)
    {
        var state = from ?? GameState.Initial(context);
        for (var t = state.Tick + 1; t <= s.Ticks.Count; t++)
        {
            state = GhostLapTransition.Instance.Apply(state, t, InputsAt(s, t), context);
        }
        return state;
    }

    /// <summary>The Timeline state hash definition: SHA-256 over (stateSchemaVersion, canonical bytes).</summary>
    private static string Hash(GameState state) => Convert.ToHexString(SHA256.HashData(
        new CanonicalWriter().WriteString(GhostLapTimelineVersions.StateSchemaVersion)
            .WriteBytes(GameStateCodec.Instance.EncodeCanonical(state)).ToArray())).ToLowerInvariant();

    private static Gen<Script> ScriptGen(int maxTicks) =>
        from players in Gen.Choose(1, 4)
        from seed in Gen.Choose(0, 1_000_000).Select(i => (long)i)
        from length in Gen.Choose(0, maxTicks)
        from rows in Gen.ArrayOf(
            Gen.Frequency(
                (1, Gen.Constant(((sbyte X, sbyte Y)?)null)),
                (6, Gen.Choose(-1, 1).Zip(Gen.Choose(-1, 1)).Select(p => ((sbyte X, sbyte Y)?)((sbyte)p.Item1, (sbyte)p.Item2)))),
            4 * length)
        select new Script(seed, players,
            Enumerable.Range(0, length).Select(t => rows.Skip(t * 4).Take(4).ToArray()).ToList());

    [Fact]
    public void Same_context_and_inputs_give_the_same_state_hash_across_two_independent_runs()
    {
        Prop.ForAll(ScriptGen(300).ToArbitrary(), s =>
        {
            var a = Run(GameContext.Create(s.Seed, s.Players), s);
            var b = Run(GameContext.Create(s.Seed, s.Players), s);
            return Hash(a) == Hash(b) && a.Tick == s.Ticks.Count;
        }).Check(Config.QuickThrowOnFailure.WithMaxTest(100));
    }

    [Fact]
    public void Full_2700_tick_round_with_a_fixed_script_is_reproducible_and_the_hash_is_pinned()
    {
        var script = new Script(42, 2, Enumerable.Range(1, GameRulesV1.RoundTicks)
            .Select(t => new (sbyte, sbyte)?[]
            {
                ((sbyte)((t / 40) % 3 - 1), (sbyte)((t / 25) % 3 - 1)),
                t % 7 == 0 ? null : ((sbyte)(-((t / 33) % 3 - 1)), (sbyte)((t / 17) % 3 - 1)),
                null, null,
            }).ToList());
        var hash = Hash(Run(GameContext.Create(42, 2), script));
        Assert.Equal(hash, Hash(Run(GameContext.Create(42, 2), script)));
        // Regression pin produced by this implementation; it detects drift, it is not an independent reference.
        // Re-pinned in task 6.2 (was cc2dbc90...8b54): the state now carries charge, score, input latch, pickups and events.
        Assert.True("e76f1d50d3ebd41dadec6cd83dfdf9b3cd39d6aa947e524e85cb156238a50628" == hash, "ACTUAL=" + hash);
    }

    /// <summary>Checkpoint = canonical encode/decode of the state plus decode of the pinned context bytes, mid-run.</summary>
    [Fact]
    public void Restoring_a_checkpoint_mid_run_continues_to_the_same_hash_as_an_uninterrupted_run()
    {
        var gen = from s in ScriptGen(300) from cut in Gen.Choose(0, 300) select (s, cut);
        Prop.ForAll(gen.ToArbitrary(), t =>
        {
            var (s, cutRaw) = t;
            var context = GameContext.Create(s.Seed, s.Players);
            var uninterrupted = Run(context, s);
            var cut = Math.Min(cutRaw, s.Ticks.Count);
            var partial = Run(context, new Script(s.Seed, s.Players, s.Ticks.Take(cut).ToList()));
            var restoredState = GameStateCodec.Instance.Decode(GameStateCodec.Instance.EncodeCanonical(partial));
            var restoredContext = GameContextCodec.Instance.Decode(GameContextCodec.Instance.EncodeCanonical(context));
            var resumed = Run(restoredContext, s, restoredState);
            return Hash(resumed) == Hash(uninterrupted);
        }).Check(Config.QuickThrowOnFailure.WithMaxTest(100));
    }

    [Fact]
    public void Candle_activation_follows_the_plan_and_is_unaffected_by_player_inputs()
    {
        var context = GameContext.Create(42, 2);
        var idle = new Script(42, 2, Enumerable.Range(0, 600).Select(_ => new (sbyte, sbyte)?[] { null, null, null, null }).ToList());
        var busy = new Script(42, 2, Enumerable.Range(0, 600).Select(t => new (sbyte, sbyte)?[]
            { ((sbyte)1, (sbyte)(t % 3 - 1)), ((sbyte)-1, (sbyte)1), null, null }).ToList());
        var a = GameState.Initial(context);
        var b = GameState.Initial(context);
        var planBefore = context.Plan.Candles.ToList();
        for (var t = 1; t <= 600; t++)
        {
            a = GhostLapTransition.Instance.Apply(a, t, InputsAt(idle, t), context);
            b = GhostLapTransition.Instance.Apply(b, t, InputsAt(busy, t), context);
            // Each world sees the planned candles minus only its own pickups (task 6.2 added pickups).
            Assert.True(context.Plan.ActiveIdsAt(t).Except(a.Collected.Select(c => c.Id)).SequenceEqual(a.ActiveCandleIds));
            Assert.True(context.Plan.ActiveIdsAt(t).Except(b.Collected.Select(c => c.Id)).SequenceEqual(b.ActiveCandleIds));
        }
        Assert.Empty(a.Collected); // idle players stand on sockets, which are off every candle site
        Assert.True(planBefore.SequenceEqual(context.Plan.Candles));
        Assert.True(planBefore.SequenceEqual(GameContext.Create(42, 2).Plan.Candles));
        Assert.NotEqual(Hash(a), Hash(b));
    }

    [Fact]
    public void Empty_ticks_advance_the_tick_and_leave_players_in_place()
    {
        var context = GameContext.Create(7, 3);
        var s0 = GameState.Initial(context);
        var s1 = GhostLapTransition.Instance.Apply(s0, 1, [], context);
        Assert.Equal(1, s1.Tick);
        Assert.True(s0.Players.SequenceEqual(s1.Players));
    }

    [Fact]
    public void Initial_state_places_each_player_on_its_socket_with_the_context_mascot()
    {
        foreach (var players in new[] { 2, 3, 4 })
        {
            var context = GameContext.Create(1, players, new byte[] { 3, 2, 1, 0 }.Take(players));
            var state = GameState.Initial(context);
            var sockets = ArenaV1.SpawnSockets(players);
            for (var i = 0; i < players; i++)
            {
                Assert.Equal((sockets[i].X, sockets[i].Y), (state.Players[i].XFixed, state.Players[i].YFixed));
                Assert.Equal(3 - i, state.Players[i].MascotId);
            }
        }
    }

    [Fact]
    public void Reducer_rejects_wrong_ticks_missing_slots_bad_vectors_and_the_round_end()
    {
        var context = GameContext.Create(1, 2);
        var s0 = GameState.Initial(context);
        var t = GhostLapTransition.Instance;
        Assert.Throws<GhostLapTransitionException>(() => t.Apply(s0, 2, [], context));
        Assert.Throws<GhostLapTransitionException>(() => t.Apply(s0, 1,
            [new OrderedInput<GameInput>("a", 0, new GameInput(2, 0, 1, 0, 0, false))], context));
        Assert.Throws<GhostLapTransitionException>(() => t.Apply(s0, 1,
            [new OrderedInput<GameInput>("a", 0, new GameInput(1, 3, 1, 0, 0, false))], context));
        Assert.Throws<GhostLapTransitionException>(() => t.Apply(s0, 1,
            [new OrderedInput<GameInput>("a", 0, new GameInput(1, 0, 1, 2, 0, false))], context));
        var last = s0 with { Tick = GameRulesV1.RoundTicks };
        Assert.Throws<GhostLapTransitionException>(() => t.Apply(last, GameRulesV1.RoundTicks + 1, [], context));
    }

    [Fact]
    public void Burst_request_without_charge_moves_identically_and_only_sets_the_latch()
    {
        var context = GameContext.Create(1, 1);
        var s0 = GameState.Initial(context);
        var a = GhostLapTransition.Instance.Apply(s0, 1, [new OrderedInput<GameInput>("a", 0, new GameInput(1, 0, 1, 1, 0, true))], context);
        var b = GhostLapTransition.Instance.Apply(s0, 1, [new OrderedInput<GameInput>("a", 0, new GameInput(1, 0, 1, 1, 0, false))], context);
        Assert.Equal(b.Players[0] with { BurstLatch = true }, a.Players[0]);
        Assert.Empty(a.Events);
    }

    [Fact]
    public void Highest_sequence_wins_per_slot_regardless_of_canonical_order()
    {
        var context = GameContext.Create(1, 1);
        var s0 = GameState.Initial(context);
        var both = GhostLapTransition.Instance.Apply(s0, 1,
        [
            new OrderedInput<GameInput>("a", 0, new GameInput(1, 0, 2, 1, 0, false)),
            new OrderedInput<GameInput>("b", 1, new GameInput(1, 0, 1, -1, 0, false)),
        ], context);
        Assert.Equal(s0.Players[0].XFixed + GameRulesV1.CardinalStepFixed, both.Players[0].XFixed);
        Assert.Equal(2, both.Players[0].LastSequence);
    }

    [Fact]
    public void Codecs_round_trip_and_reject_malformed_bytes()
    {
        var context = GameContext.Create(99, 4, [0, 1, 2, 3]);
        var state = Run(context, new Script(99, 4, Enumerable.Range(0, 50).Select(_ => new (sbyte, sbyte)?[]
            { ((sbyte)1, (sbyte)1), ((sbyte)-1, (sbyte)1), ((sbyte)1, (sbyte)-1), ((sbyte)-1, (sbyte)-1) }).ToList()));
        var stateBytes = GameStateCodec.Instance.EncodeCanonical(state);
        Assert.Equal(stateBytes, GameStateCodec.Instance.EncodeCanonical(GameStateCodec.Instance.Decode(stateBytes)));
        Assert.Throws<CanonicalFormatException>(() => GameStateCodec.Instance.Decode([.. stateBytes, 0]));
        Assert.Throws<CanonicalFormatException>(() => GameStateCodec.Instance.Decode(stateBytes[..^1]));

        var input = new GameInput(5, 2, 17, -1, 1, true);
        Assert.Equal(input, GameInputCodec.Instance.Decode(GameInputCodec.Instance.EncodeCanonical(input)));
        var bad = GameInputCodec.Instance.EncodeCanonical(input);
        bad[13] = 9; // moveX byte
        Assert.Throws<CanonicalFormatException>(() => GameInputCodec.Instance.Decode(bad));

        var ctxBytes = GameContextCodec.Instance.EncodeCanonical(context);
        Assert.Equal(context, GameContextCodec.Instance.Decode(ctxBytes));
        var other = new CanonicalWriter().WriteString("ghost-v2").WriteString(GameRulesV1.LayoutVersion)
            .WriteInt64(1).WriteCount(1).WriteByte(0).ToArray();
        Assert.Throws<CanonicalFormatException>(() => GameContextCodec.Instance.Decode(other));
    }

    [Fact]
    public void Version_key_resolves_through_the_Marrow_registry_and_unknown_versions_fail_closed()
    {
        var registry = new TimelineVersionRegistry<GameState, GameInput, GameContext>(GhostLapTimelineVersions.All);
        Assert.True(registry.Resolve(GhostLapTimelineVersions.V1).IsSuccess);
        var future = GhostLapTimelineVersions.V1 with { ReducerVersion = "ghost-v2" };
        Assert.False(registry.Resolve(future).IsSuccess);
    }
}
