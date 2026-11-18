using GhostLap.Rules;
using GhostLap.Simulation;
using GhostLap.Solo;
using GhostLap.Tests.Simulation;
using GhostLap.Timeline;
using Marrow.Infrastructure.Identity.Policy;
using Marrow.Timeline;
using Xunit;
using Xunit.Abstractions;
using static GhostLap.Tests.Solo.SoloDatabase;

namespace GhostLap.Tests.Solo;

/// <summary>
/// Task 11.5 domain checks over the real recorder, the one Marrow Timeline and PostgreSQL: the owner-facing catalog of completed
/// Challenge/Fork children (kept apart from the Originals list) and the same-tick source/child comparison. One real Challenge (2,700
/// ticks), one Fork at T1140 (1,560 ticks) and one Fork at T2699 (one tick) are played tick by tick, without waiting for real time.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SoloHistoryCatalogTests(SoloTraceDatabase db, ITestOutputHelper output) : IClassFixture<SoloTraceDatabase>
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private const int ForkAt = 1140;

    private static readonly SoloGhostTraceCache SharedCache = new(4, TimeSpan.FromHours(1));
    private static readonly SemaphoreSlim BuildGate = new(1, 1);
    private static Built? _built;

    private sealed record Played(
        SoloChallengeResult Result, int[] Scores, string[] Hashes, long FirstTick, WorldFrame LivingAtFirstTick, GameState LivingAtFirstState);

    private sealed record Built(SoloChallenges Challenges, SoloOriginals Originals, Played Challenge, Played ForkX, Played ForkLast, TimelineId Pending);

    private static byte[] Bytes(GameState s) => GameStateCodec.Instance.EncodeCanonical(s);

    private static SubmittedInput Sub(GameInput g) => new(g.Slot, g.Sequence, g.Tick, g.MoveX, g.MoveY, g.RequestBurst);

    private async Task<Built> BuildAsync()
    {
        await BuildGate.WaitAsync();
        try
        {
            if (_built is not null)
            {
                return _built;
            }

            var host = db.NewHost();
            var authorizer = new OwnerResourceAuthorizer(new SoloOwnershipLookup(host.Store));
            var originals = new SoloOriginals(host.Engine, host.Store, authorizer);
            var branches = new SoloBranches(host.Engine, host.Store, authorizer);
            var challenges = new SoloChallenges(originals, branches, host.Store, authorizer, SharedCache);
            var route = new AlternateRouteDriver();

            var challenge = await PlayAsync(challenges, SoloBranchKind.Challenge, 0, "cat-challenge", (s, c) => route.Inputs(s, c));
            var routeX = new AlternateRouteDriver();
            var forkX = await PlayAsync(challenges, SoloBranchKind.Fork, ForkAt, "cat-fork-x", (s, c) => routeX.Inputs(s, c));
            var forkLast = await PlayAsync(challenges, SoloBranchKind.Fork, 2699, "cat-fork-last", (s, _) => Pattern(s));

            // An unfinished fork: five ticks committed, never completed. It must not appear anywhere as a completed child.
            var pending = challenges.BeginFork(db.OwnerA, db.Original.Timeline, ForkAt);
            Ok(await pending.PrepareAsync(Ct));
            var started = Ok(await pending.StartAsync("cat-pending", Ct));
            for (var i = 0; i < 5; i++)
            {
                foreach (var g in Pattern(pending.LivingState!))
                {
                    Assert.True(pending.Submit(Sub(g)).IsSuccess);
                }

                Ok(await pending.CommitTickAsync(Ct));
            }

            pending.Cancel();
            return _built = new Built(challenges, originals, challenge, forkX, forkLast, started.Child);
        }
        finally
        {
            BuildGate.Release();
        }
    }

    private static GameInput[] Pattern(GameState s)
    {
        var tick = s.Tick + 1;
        var (mx, my) = ((tick / 45) % 4) switch
        {
            0 => ((sbyte)1, (sbyte)0),
            1 => ((sbyte)0, (sbyte)1),
            2 => ((sbyte)-1, (sbyte)0),
            _ => ((sbyte)0, (sbyte)-1),
        };
        return [new GameInput(tick, 0, s.Players[0].LastSequence + 1, mx, my, false)];
    }

    private async Task<Played> PlayAsync(
        SoloChallenges challenges, SoloBranchKind kind, int forkTick, string key, Func<GameState, GameContext, GameInput[]> inputs)
    {
        var session = kind == SoloBranchKind.Challenge
            ? challenges.Begin(db.OwnerA, db.Original.Timeline)
            : challenges.BeginFork(db.OwnerA, db.Original.Timeline, forkTick);
        Ok(await session.PrepareAsync(Ct));
        Ok(await session.StartAsync(key, Ct));
        var context = session.LivingContext!;
        var scores = new int[GameRulesV1.RoundTicks + 1];
        var hashes = new string[GameRulesV1.RoundTicks + 1];
        for (var t = 0; t <= forkTick; t++)
        {
            scores[t] = db.Run.Trace[t].Players[0].TotalScore;
            hashes[t] = t == 0 ? "" : db.Run.Receipts[t - 1].StateHash;
        }

        long firstTick = 0;
        WorldFrame? firstFrame = null;
        GameState? firstState = null;
        while (!session.LivingState!.IsFinal)
        {
            foreach (var g in inputs(session.LivingState!, context))
            {
                Assert.True(session.Submit(Sub(g)).IsSuccess);
            }

            var tick = Ok(await session.CommitTickAsync(Ct));
            scores[tick.Tick] = tick.Living.TotalScore;
            hashes[tick.Tick] = tick.StateHash;
            if (firstFrame is null)
            {
                (firstTick, firstFrame, firstState) = (tick.Tick, tick.Living, session.LivingState!);
            }
        }

        return new Played(Ok(await session.CompleteAsync(Ct)), scores, hashes, firstTick, firstFrame!, firstState!);
    }

    [Fact]
    public async Task Catalog_lists_completed_children_by_kind_without_touching_the_originals_list_or_other_owners()
    {
        var b = await BuildAsync();

        var originals = Ok(await b.Originals.ListAsync(db.OwnerA, 100, Ct));
        var original = Assert.Single(originals);
        Assert.Equal(db.Original.Timeline, original.Timeline);

        var page = Ok(await b.Challenges.ListChildrenAsync(db.OwnerA, null, null, 25, Ct));
        Assert.Null(page.NextAfter);
        Assert.Equal(3, page.Items.Count); // the unfinished fork is not a completed child
        Assert.DoesNotContain(page.Items, i => i.Child == b.Pending || i.Child == db.Original.Timeline);
        Assert.All(page.Items, i => Assert.True(i.Verified && i.Score is not null && i.Source == db.Original.Timeline));

        var byKey = page.Items.ToDictionary(i => i.ClientKey);
        Assert.Equal((SoloBranchKind.Challenge, 0L), (byKey["cat-challenge"].Kind, byKey["cat-challenge"].ForkTick));
        Assert.Equal((SoloBranchKind.Fork, (long)ForkAt), (byKey["cat-fork-x"].Kind, byKey["cat-fork-x"].ForkTick));
        Assert.Equal((SoloBranchKind.Fork, 2699L), (byKey["cat-fork-last"].Kind, byKey["cat-fork-last"].ForkTick));
        foreach (var (key, played) in new[] { ("cat-challenge", b.Challenge), ("cat-fork-x", b.ForkX), ("cat-fork-last", b.ForkLast) })
        {
            var item = byKey[key];
            Assert.Equal((played.Result.Child, played.Result.ChildFinalStateHash, played.Result.ChildLastRecordHash, played.Result.LivingScore),
                (item.Child, item.FinalStateHash, item.LastRecordHash, item.Score));
            Assert.Equal(("ghost-v1", "arena-v1", (long)GameRulesV1.RoundTicks, SoloDatabase.Seed, (byte)2),
                (item.Versions.ReducerVersion, item.LayoutVersion, item.FinalTick, item.Seed, item.MascotId));
        }

        // Newest first, stable, and the keyset cursor neither skips nor repeats.
        var first = Ok(await b.Challenges.ListChildrenAsync(db.OwnerA, null, null, 2, Ct));
        Assert.Equal(2, first.Items.Count);
        Assert.NotNull(first.NextAfter);
        var second = Ok(await b.Challenges.ListChildrenAsync(db.OwnerA, null, first.NextAfter, 2, Ct));
        Assert.Single(second.Items);
        Assert.Null(second.NextAfter);
        Assert.Equal(page.Items.Select(i => i.Child), first.Items.Concat(second.Items).Select(i => i.Child));
        Assert.Equal(3, Ok(await b.Challenges.ListChildrenAsync(db.OwnerA, db.Original.Timeline, null, 25, Ct)).Items.Count);
        Assert.Empty(Ok(await b.Challenges.ListChildrenAsync(db.OwnerA, new TimelineId(Guid.NewGuid()), null, 25, Ct)).Items);

        // Bounds and cursors fail closed; a foreign owner's id is not a valid cursor and nothing of A's is visible to B.
        Assert.Equal(SoloErrorCode.InvalidRequest, (await b.Challenges.ListChildrenAsync(db.OwnerA, null, null, 0, Ct)).Error.Code);
        Assert.Equal(SoloErrorCode.InvalidRequest, (await b.Challenges.ListChildrenAsync(db.OwnerA, null, null, SoloChallenges.MaxChildPageLimit + 1, Ct)).Error.Code);
        var stranger = Guest();
        Assert.Empty(Ok(await b.Challenges.ListChildrenAsync(stranger, null, null, 25, Ct)).Items);
        Assert.Empty(Ok(await b.Challenges.ListChildrenAsync(stranger, db.Original.Timeline, null, 25, Ct)).Items);
        Assert.Equal(SoloErrorCode.InvalidRequest, (await b.Challenges.ListChildrenAsync(stranger, null, first.NextAfter, 25, Ct)).Error.Code);
        Assert.Equal(SoloErrorCode.InvalidRequest,
            (await b.Challenges.ListChildrenAsync(db.OwnerA, null, new TimelineId(Guid.NewGuid()), 25, Ct)).Error.Code);
    }

    [Fact]
    public async Task Child_detail_resolves_the_verified_result_and_refuses_foreign_unknown_and_unfinished_ids_identically()
    {
        var b = await BuildAsync();
        foreach (var played in new[] { b.Challenge, b.ForkX, b.ForkLast })
        {
            var detail = Ok(await b.Challenges.GetChildAsync(db.OwnerA, played.Result.Child, Ct));
            Assert.Equal((played.Result.Kind, played.Result.ForkTick, played.Result.ChildFinalStateHash, played.Result.LivingScore, played.Result.SourceScore, played.Result.Delta),
                (detail.Kind, detail.ForkTick, detail.ChildFinalStateHash, detail.LivingScore, detail.SourceScore, detail.Delta));
            Assert.Equal((db.Original.FinalStateHash, SoloDatabase.Seed, (byte)2, "ghost-v1", "arena-v1"),
                (detail.SourceFinalStateHash, detail.Seed, detail.MascotId, detail.RulesVersion, detail.LayoutVersion));
        }

        var foreign = await b.Challenges.GetChildAsync(Guest(), b.ForkX.Result.Child, Ct);
        var unknown = await b.Challenges.GetChildAsync(db.OwnerA, new TimelineId(Guid.NewGuid()), Ct);
        var original = await b.Challenges.GetChildAsync(db.OwnerA, db.Original.Timeline, Ct); // an original id is not a child
        Assert.Equal(SoloErrorCode.NotFound, foreign.Error.Code);
        Assert.Equal(foreign.Error.Code, unknown.Error.Code);
        Assert.Equal(SoloErrorCode.NotFound, original.Error.Code);
        Assert.Equal(SoloErrorCode.NotComplete, (await b.Challenges.GetChildAsync(db.OwnerA, b.Pending, Ct)).Error.Code);
    }

    [Fact]
    public async Task Comparison_reports_true_same_tick_scores_agrees_through_the_fork_tick_and_diverges_after()
    {
        var b = await BuildAsync();
        var final = db.Original.Result.Scores[0].TotalScore;

        // The window around the T1140 fork: tick 1140 is the last shared state, 1141 the first new transition.
        var window = Ok(await b.Challenges.CompareChildAsync(db.OwnerA, b.ForkX.Result.Child, 1100, 100, 1, Ct));
        Assert.Equal((b.ForkX.Result.Child, db.Original.Timeline, SoloBranchKind.Fork, (long)ForkAt), (window.Child, window.Source, window.Kind, window.ForkTick));
        Assert.Equal((db.Original.FinalStateHash, b.ForkX.Result.ChildFinalStateHash, 1100L, 1, 1200L), (window.SourceFinalStateHash, window.ChildFinalStateHash, window.FromTick, window.Stride, window.NextTick));
        Assert.Equal(Enumerable.Range(1100, 100).Select(t => (long)t), window.Points.Select(p => p.Tick));
        foreach (var p in window.Points)
        {
            var t = (int)p.Tick;
            Assert.Equal(db.Run.Trace[t].Players[0].TotalScore, p.SourceScore); // the source's score AT t, never its final score
            Assert.Equal(b.ForkX.Scores[t], p.ChildScore);
            Assert.Equal(p.ChildScore - p.SourceScore, p.Delta);
            Assert.Equal(t > ForkAt, p.AfterFork);
            Assert.Equal(db.Run.Receipts[t - 1].StateHash, p.SourceStateHash);
            if (t <= ForkAt)
            {
                Assert.Equal((0, p.Source), (p.Delta, p.Child));
                Assert.Equal(p.SourceStateHash, p.ChildStateHash);
            }
            else
            {
                Assert.Equal(b.ForkX.Hashes[t], p.ChildStateHash);
            }
        }

        Assert.NotEqual(final, window.Points.Single(p => p.Tick == ForkAt).SourceScore);
        Assert.Equal(SoloScorePartsOf(db.Run.Trace[ForkAt]), window.Points.Single(p => p.Tick == ForkAt).Source);
        Assert.Contains(window.Points, p => p.AfterFork && p.Delta != 0);

        // A whole round in one bounded call (every 10th tick) with identical source identity on every chunk and a continuing cursor.
        var round = Ok(await b.Challenges.CompareChildAsync(db.OwnerA, b.ForkX.Result.Child, 0, 300, 10, Ct));
        Assert.Null(round.NextTick);
        Assert.Equal(271, round.Points.Length);
        var end = round.Points[^1];
        Assert.Equal((2700L, true, final, b.ForkX.Result.LivingScore, b.ForkX.Result.Delta), (end.Tick, end.IsFinal, end.SourceScore, end.ChildScore, end.Delta));
        Assert.Equal((db.Original.FinalStateHash, b.ForkX.Result.ChildFinalStateHash), (end.SourceStateHash, end.ChildStateHash));
        var part1 = Ok(await b.Challenges.CompareChildAsync(db.OwnerA, b.ForkX.Result.Child, 0, 100, 10, Ct));
        var part2 = Ok(await b.Challenges.CompareChildAsync(db.OwnerA, b.ForkX.Result.Child, part1.NextTick!.Value, 100, 10, Ct));
        Assert.Equal((1000L, 1000L), (part1.NextTick, part2.FromTick));
        Assert.Equal((part1.Versions, part1.SourceFinalStateHash, part1.ChildFinalStateHash), (part2.Versions, part2.SourceFinalStateHash, part2.ChildFinalStateHash));
        Assert.Equal(round.Points.Take(200), part1.Points.Concat(part2.Points));

        // A Challenge is a fork at tick 0: identical at tick 0, new transitions from tick 1.
        var challenge = Ok(await b.Challenges.CompareChildAsync(db.OwnerA, b.Challenge.Result.Child, 0, 2, 1, Ct));
        Assert.Equal((SoloBranchKind.Challenge, 0L), (challenge.Kind, challenge.ForkTick));
        Assert.Equal((false, 0), (challenge.Points[0].AfterFork, challenge.Points[0].Delta));
        Assert.True(challenge.Points[1].AfterFork);
        Assert.Equal(b.Challenge.Scores[1], challenge.Points[1].ChildScore);

        // A fork at T2699 agrees at 2699 and differs only in its last transition.
        var last = Ok(await b.Challenges.CompareChildAsync(db.OwnerA, b.ForkLast.Result.Child, 2698, 3, 1, Ct));
        Assert.Equal([false, false, true], last.Points.Select(p => p.AfterFork));
        Assert.Equal([0, 0], last.Points.Take(2).Select(p => p.Delta));
        Assert.Equal((final, b.ForkLast.Result.LivingScore), (last.Points[2].SourceScore, last.Points[2].ChildScore));
        output.WriteLine($"T{ForkAt} source {window.Points[40].SourceScore}; final source {final}, fork X {b.ForkX.Result.LivingScore} (delta {b.ForkX.Result.Delta}), " +
                         $"challenge {b.Challenge.Result.LivingScore} (delta {b.Challenge.Result.Delta}), fork T2699 {b.ForkLast.Result.LivingScore}");
    }

    [Fact]
    public async Task Comparison_fails_closed_for_bad_bounds_foreign_unknown_unfinished_and_original_ids()
    {
        var b = await BuildAsync();
        var child = b.ForkX.Result.Child;
        Assert.Equal(SoloErrorCode.InvalidRequest, (await b.Challenges.CompareChildAsync(db.OwnerA, child, 0, 0, 1, Ct)).Error.Code);
        Assert.Equal(SoloErrorCode.InvalidRequest, (await b.Challenges.CompareChildAsync(db.OwnerA, child, 0, 301, 1, Ct)).Error.Code);
        Assert.Equal(SoloErrorCode.InvalidRequest, (await b.Challenges.CompareChildAsync(db.OwnerA, child, 0, 10, 0, Ct)).Error.Code);
        Assert.Equal(SoloErrorCode.InvalidTick, (await b.Challenges.CompareChildAsync(db.OwnerA, child, 2701, 1, 1, Ct)).Error.Code);
        Assert.Equal(SoloErrorCode.InvalidTick, (await b.Challenges.CompareChildAsync(db.OwnerA, child, -1, 1, 1, Ct)).Error.Code);
        var foreign = await b.Challenges.CompareChildAsync(Guest(), child, 0, 1, 1, Ct);
        var unknown = await b.Challenges.CompareChildAsync(db.OwnerA, new TimelineId(Guid.NewGuid()), 0, 1, 1, Ct);
        Assert.Equal((SoloErrorCode.NotFound, SoloErrorCode.NotFound), (foreign.Error.Code, unknown.Error.Code));
        Assert.Equal(SoloErrorCode.NotFound, (await b.Challenges.CompareChildAsync(db.OwnerA, db.Original.Timeline, 0, 1, 1, Ct)).Error.Code);
        Assert.Equal(SoloErrorCode.NotComplete, (await b.Challenges.CompareChildAsync(db.OwnerA, b.Pending, 0, 1, 1, Ct)).Error.Code);
    }

    /// <summary>The living frame handed to the renderer is the reducer's state: components, fixed-point distance, Burst state, candles, events and the last applied input.</summary>
    [Fact]
    public async Task Living_world_frame_carries_the_reducer_state_the_renderer_needs()
    {
        var b = await BuildAsync();
        var f = b.ForkX.LivingAtFirstTick;
        var p = b.ForkX.LivingAtFirstState.Players[0];
        Assert.Equal(ForkAt + 1L, b.ForkX.FirstTick);
        Assert.Equal((p.DistanceFixed, p.IsOverlit, p.MovementPoints, p.PickupPoints, p.PenaltyPoints, p.TotalScore, p.Pickups, p.Bursts, p.BurstHits),
            (f.DistanceFixed, f.IsOverlit, f.MovementPoints, f.PickupPoints, f.PenaltyPoints, f.TotalScore, f.Pickups, f.Bursts, f.BurstHits));
        Assert.Equal((p.HeldMoveX, p.HeldMoveY, p.BurstLatch, p.LastSequence, b.ForkX.LivingAtFirstState.Collected.Length),
            (f.HeldMoveX, f.HeldMoveY, f.BurstLatch, f.LastSequence, f.CollectedCount));
        Assert.Equal(b.ForkX.LivingAtFirstState.ActiveCandleIds.ToArray(), f.ActiveCandleIds.ToArray());
        Assert.Equal(b.ForkX.LivingAtFirstState.Events.ToArray(), f.Events.ToArray());
        Assert.True(f.LastSequence >= 0 && f.DistanceFixed > 0);
    }

    private static SoloScoreParts SoloScorePartsOf(GameState s)
    {
        var p = s.Players[0];
        return new SoloScoreParts(p.MovementPoints, p.PickupPoints, p.PenaltyPoints, p.TotalScore, p.Pickups, p.Bursts, p.Charge, p.IsOverlit, p.DistanceFixed);
    }
    [Fact]
    public async Task Saved_child_trace_is_bounded_verified_owner_only_and_keeps_parent_identity()
    {
        var built = await BuildAsync();
        var child = built.ForkX.Result;
        var chunk = Ok(await built.Challenges.TraceChildAsync(db.OwnerA, child.Child, 1139, 4, Ct));
        Assert.Equal((child.Original, SoloBranchKind.Fork, 1140L, child.SourceFinalStateHash, child.ChildFinalStateHash, 1143L),
            (chunk.Source, chunk.Kind, chunk.ForkTick, chunk.SourceFinalStateHash, chunk.Trace.SourceFinalStateHash, chunk.Trace.NextTick));
        Assert.Equal(new long[] {1139,1140,1141,1142}, chunk.Trace.Frames.Select(f => f.Tick));
        foreach (var frame in chunk.Trace.Frames)
            Assert.Equal(built.ForkX.Hashes[(int)frame.Tick], frame.StateHash);
        var terminal = Ok(await built.Challenges.TraceChildAsync(db.OwnerA, child.Child, 2699, 300, Ct));
        Assert.Null(terminal.Trace.NextTick); Assert.Equal(2, terminal.Trace.Frames.Length);
        Assert.Equal(child.ChildFinalStateHash, terminal.Trace.Frames[^1].StateHash);
        Assert.True((await built.Challenges.TraceChildAsync(Guest(), child.Child, 0, 1, Ct)).IsFailure);
        Assert.True((await built.Challenges.TraceChildAsync(db.OwnerA, built.Pending, 0, 1, Ct)).IsFailure);
        Assert.True((await built.Challenges.TraceChildAsync(db.OwnerA, child.Original, 0, 1, Ct)).IsFailure);
        foreach (var (from,count) in new[] {(-1L,1),(2701L,1),(0L,0),(0L,301)})
            Assert.True((await built.Challenges.TraceChildAsync(db.OwnerA, child.Child, from, count, Ct)).IsFailure);
    }

}
