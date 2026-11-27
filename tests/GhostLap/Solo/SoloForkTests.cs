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
/// Task 11.4: fork a verified solo original at tick T and save genuinely independent futures, over the real recorder, the one
/// Marrow Timeline and PostgreSQL. It reuses the Task 11.3 session (<see cref="SoloChallenge"/> built by
/// <see cref="SoloChallenges.BeginFork"/>), so the prepared ghost trace, input admission, 30 Hz commit path, completion and
/// ownership are the ones already tested; these tests cover what differs: a nonzero start tick and the Fork classification.
/// Rounds are driven tick by tick without waiting for real time.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SoloForkTests(SoloTraceDatabase db, ITestOutputHelper output) : IClassFixture<SoloTraceDatabase>
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private const int ForkAt = 1140;

    /// <summary>Shared across tests of this class so the 2,701-frame trace is prepared once (the 11.3 reuse rule keeps it safe).</summary>
    private static readonly SoloGhostTraceCache SharedCache = new(4, TimeSpan.FromHours(1));

    private sealed record Rig(SoloHost Host, CountingEngine Engine, SoloOriginals Originals, SoloBranches Branches, SoloChallenges Challenges);

    private static Rig NewRig(SoloHost host, SoloGhostTraceCache cache)
    {
        var engine = new CountingEngine(host.Engine);
        var authorizer = new OwnerResourceAuthorizer(new SoloOwnershipLookup(host.Store));
        var originals = new SoloOriginals(engine, host.Store, authorizer);
        var branches = new SoloBranches(engine, host.Store, authorizer);
        return new Rig(host, engine, originals, branches, new SoloChallenges(originals, branches, host.Store, authorizer, cache));
    }

    private Rig NewRig(SoloGhostTraceCache? cache = null) => NewRig(db.NewHost(), cache ?? SharedCache);

    private static byte[] Bytes(GameState s) => GameStateCodec.Instance.EncodeCanonical(s);

    private static SubmittedInput Sub(GameInput g) => new(g.Slot, g.Sequence, g.Tick, g.MoveX, g.MoveY, g.RequestBurst);

    private static long Remainder(GameState s) => s.Players[0].DistanceFixed % GameRulesV1.MovementScoreUnitFixed;

    /// <summary>Source frames the fork keeps at the action boundaries: a manual-Burst tick and the tick before an automatic Burst.</summary>
    private (int Manual, int BeforeAuto) Boundaries()
    {
        var trace = db.Run.Trace;
        var manual = trace.First(s => s.Tick is > 0 and < GameRulesV1.RoundTicks - 1 && s.Events.Any(e => e.Kind == GameEventKind.BurstManual)).Tick;
        var auto = trace.First(s => s.Tick is > 1 and < GameRulesV1.RoundTicks && s.Events.Any(e => e.Kind == GameEventKind.BurstAuto)).Tick;
        return ((int)manual, (int)auto - 1);
    }

    [Fact]
    public async Task Fork_restores_the_exact_source_state_at_T0_T1140_and_T2699_and_T2700_is_inspect_only()
    {
        var rig = NewRig();
        await using var _ = rig.Host;
        var id = db.Original.Timeline;
        var finalScore = db.Original.Result.Scores[0].TotalScore;
        var sourceBefore = await SourceFingerprintAsync();

        foreach (var t in new[] { 0, ForkAt, 2699 })
        {
            var fork = rig.Challenges.BeginFork(db.OwnerA, id, t);
            Assert.Equal((SoloBranchKind.Fork, (long)t), (fork.Kind, fork.ForkTick));
            Ok(await fork.PrepareAsync(Ct));
            var started = Ok(await fork.StartAsync($"probe-{t}", Ct));
            var source = db.Run.Trace[t];
            var p = source.Players[0];

            Assert.Equal((SoloBranchKind.Fork, (long)t, $"fork.probe-{t}", (long)t, (long)t, id),
                (started.Kind, started.ForkTick, started.OperationKey, started.Ghost.Tick, started.Living.Tick, started.Original));
            Assert.NotEqual(id, started.Child);

            // Byte-exact canonical state: seed/layout/candle schedule+consumption, remainder, score, Soulfire/Overlit, pending input.
            Assert.True(Bytes(source).AsSpan().SequenceEqual(Bytes(fork.LivingState!)), $"living state at T{t} equals the source's");
            Assert.Equal(db.Original.Seed, started.Seed);
            Assert.Equal(source.Collected.Select(c => c.Id).ToArray(), fork.LivingState!.Collected.Select(c => c.Id).ToArray());
            Assert.Equal(source.ActiveCandleIds.ToArray(), fork.LivingState.ActiveCandleIds.ToArray());
            Assert.Equal(db.Run.Context.Plan.ActiveIdsAt(t + 1).ToArray(), fork.LivingContext!.Plan.ActiveIdsAt(t + 1).ToArray());
            Assert.Equal((p.TotalScore, p.Charge, p.OverlitDeadlineTick, p.HoldUntilTick, p.LastSequence),
                (started.Living.TotalScore, started.Living.Charge, started.Living.OverlitDeadlineTick, fork.LivingState.Players[0].HoldUntilTick,
                    fork.LivingState.Players[0].LastSequence));

            // Hash identity three ways: the source's own restore, the child's restore, and the recorded live receipt.
            var sourceAtT = Ok(await rig.Originals.RestoreAsync(db.OwnerA, id, t, Ct));
            var childAtT = Ok(await rig.Branches.RestoreAsync(db.OwnerA, started.Child, t, Ct));
            Assert.Equal(sourceAtT.StateHash, started.Ghost.StateHash);
            Assert.Equal(sourceAtT.StateHash, childAtT.StateHash);
            Assert.True(Bytes(sourceAtT.State).AsSpan().SequenceEqual(Bytes(childAtT.State)));
            if (t > 0)
            {
                Assert.Equal(db.Run.Receipts[t - 1].StateHash, childAtT.StateHash);
            }

            // The canonical source prefix is the child's prefix at every earlier tick, and the aligned comparison shows no divergence yet.
            var prefixTicks = new[] { 0, t / 2, Math.Max(0, t - 1), t }.Distinct().OrderBy(x => x).ToArray();
            foreach (var k in prefixTicks)
            {
                var restored = Ok(await rig.Branches.RestoreAsync(db.OwnerA, started.Child, k, Ct));
                Assert.True(Bytes(db.Run.Trace[k]).AsSpan().SequenceEqual(Bytes(restored.State)), $"child prefix at T{k}");
            }

            var aligned = Ok(await rig.Branches.CompareAsync(db.OwnerA, started.Child, [.. prefixTicks.Select(k => (long)k)], Ct));
            Assert.All(aligned, a => Assert.Equal((0, a.Ghost.TotalScore), (a.Delta, a.Current.TotalScore)));
            Assert.Equal(prefixTicks.Select(k => db.Run.Trace[k].Players[0].TotalScore), aligned.Select(a => a.Ghost.TotalScore));

            var row = Ok(await rig.Host.Store.FindBranchAsync(started.Child, Ct));
            Assert.Equal((id, (long)t, SoloOriginalStatus.Recording, SoloBranchKind.Fork, db.OwnerA.SubjectId),
                (row.Source, row.ForkTick, row.Status, SoloBranchKinds.Of(row), row.OwnerSubjectId));

            if (t == ForkAt)
            {
                Assert.True(Remainder(source) != 0, "nonzero movement remainder");
                Assert.True(source.Collected.Length > 0, "historical candle consumption");
                Assert.True(p.TotalScore is > 0 && p.TotalScore < finalScore, "current score is historical, not final");
                Assert.True(p.Charge > 0 || p.OverlitDeadlineTick > 0 || p.HoldUntilTick > t);
                output.WriteLine($"T{t}: remainder {Remainder(source)}, collected {source.Collected.Length}, score {p.TotalScore}, charge {p.Charge}, " +
                                 $"overlit deadline {p.OverlitDeadlineTick}, hold until {p.HoldUntilTick}, hash {sourceAtT.StateHash[..8]}");
            }

            if (t == 2699)
            {
                // Exactly one transition remains: the source's own tick-2700 input reproduces the source's final state and hash.
                Assert.Equal(1, GameRulesV1.RoundTicks - t);
                foreach (var g in db.Run.Steps[GameRulesV1.RoundTicks - 1])
                {
                    Assert.True(fork.Submit(Sub(g)).IsSuccess);
                }

                var last = Ok(await fork.CommitTickAsync(Ct));
                Assert.Equal((2700L, true), (last.Tick, last.IsFinal));
                Assert.Equal((0, finalScore), (last.Delta, last.Living.TotalScore));
                Assert.Equal(InputRejection.RoundOver, fork.Submit(new SubmittedInput(0, int.MaxValue, 2701, 0, 0, false)).Error);
                var done = Ok(await fork.CompleteAsync(Ct));
                Assert.Equal((SoloBranchKind.Fork, 2699L, 2700L, id), (done.Kind, done.ForkTick, done.FinalTick, done.Original));
                Assert.Equal(db.Original.FinalStateHash, done.ChildFinalStateHash);
                Assert.Equal(done.Child, Ok(await rig.Challenges.ResolveForkAsync(db.OwnerA, "probe-2699", Ct)).Child);
                Assert.Equal(SoloErrorCode.NotFound, (await rig.Challenges.ResolveAsync(db.OwnerA, "probe-2699", Ct)).Error.Code);
            }
            else
            {
                fork.Cancel();
            }
        }

        // T2700 is inspect-only; out-of-range ticks are invalid. Nothing is read, forked or stored.
        rig.Engine.Reset();
        foreach (var (bad, code) in new[] { (2700L, SoloErrorCode.ForkNotAllowed), (-1L, SoloErrorCode.InvalidTick), (2701L, SoloErrorCode.InvalidTick) })
        {
            var refused = rig.Challenges.BeginFork(db.OwnerA, id, bad);
            var result = await refused.PrepareAsync(Ct);
            Assert.Equal(code, result.Error.Code);
            Assert.Equal(SoloChallengePhase.Failed, refused.Phase);
            Assert.Equal(SoloErrorCode.Conflict, (await refused.StartAsync($"bad-{bad + 1}", Ct)).Error.Code);
            Assert.Equal(0L, await CountBranchesAsync($"fork.bad-{bad + 1}"));
        }

        Assert.Equal((0, 0, 0), (rig.Engine.Restores, rig.Engine.Replays, rig.Engine.Forks));
        Assert.Equal(SoloErrorCode.ForkNotAllowed, (await rig.Branches.ForkAsync(db.OwnerA, id, 2700, "fork.direct-2700", Ct)).Error.Code);
        Assert.Equal(sourceBefore, await SourceFingerprintAsync());
    }

    [Fact]
    public async Task The_first_new_transition_is_T_plus_1_and_replaying_source_inputs_from_a_fork_reproduces_the_source_exactly()
    {
        var rig = NewRig();
        await using var _ = rig.Host;
        var (manual, beforeAuto) = Boundaries();
        output.WriteLine($"boundaries: manual Burst tick {manual}, tick before automatic Burst {beforeAuto}");

        var probes = new List<(int Tick, int Length)> { (ForkAt, 60), (manual, 40), (beforeAuto, 40) };
        foreach (var (t, length) in probes)
        {
            var fork = rig.Challenges.BeginFork(db.OwnerA, db.Original.Timeline, t);
            Ok(await fork.PrepareAsync(Ct));
            Ok(await fork.StartAsync($"replay-{t}", Ct));
            var atFork = fork.LivingState!;
            var p = atFork.Players[0];

            // No reset: the sequence counter, hold timer and Burst latch are inherited, so an old sequence is refused.
            Assert.Equal(t, atFork.Tick);
            Assert.Equal(InputRejection.StaleSequence, fork.Submit(new SubmittedInput(0, p.LastSequence, t, 0, 0, false)).Error);
            Assert.Equal(InputRejection.LateTick, fork.Submit(new SubmittedInput(0, int.MaxValue, t - 1000, 0, 0, false)).Error);
            var first = fork.Submit(new SubmittedInput(0, p.LastSequence + 1, t, 1, 0, false));
            Assert.Equal(t + 1L, first.Value.Tick);

            // The admitted probe input is discarded by replaying the source on a fresh fork of the same tick (below).
            fork.Cancel();
            var again = rig.Challenges.BeginFork(db.OwnerA, db.Original.Timeline, t);
            Ok(await again.PrepareAsync(Ct));
            Ok(await again.StartAsync($"replay-source-{t}", Ct));
            for (var tick = t + 1; tick <= t + length; tick++)
            {
                foreach (var g in db.Run.Steps[tick - 1])
                {
                    var admitted = again.Submit(Sub(g));
                    Assert.Equal(g, admitted.Value);
                }

                var receipt = Ok(await again.CommitTickAsync(Ct));
                var source = db.Run.Trace[tick];
                Assert.Equal((long)tick, receipt.Tick);
                Assert.True(Bytes(source).AsSpan().SequenceEqual(Bytes(again.LivingState!)), $"fork T{t}: living state at T{tick} equals the source's");
                Assert.Equal(db.Run.Receipts[tick - 1].StateHash, receipt.StateHash);
                Assert.Equal(source.Events.ToArray(), receipt.Living.Events.ToArray());
                Assert.Equal((source.Players[0].TotalScore, 0), (receipt.Living.TotalScore, receipt.Delta));
                Assert.Equal((long)tick, receipt.Ghost.Tick);
                Assert.Equal(receipt.StateHash, receipt.Ghost.StateHash);
            }

            // Exactly the source's scoring: the score difference over the window is the source's, with nothing counted twice.
            Assert.Equal(db.Run.Trace[t + length].Players[0].TotalScore - db.Run.Trace[t].Players[0].TotalScore,
                again.LivingState!.Players[0].TotalScore - atFork.Players[0].TotalScore);
            if (t == beforeAuto)
            {
                Assert.Contains(db.Run.Trace[t + 1].Events, e => e.Kind == GameEventKind.BurstAuto);
            }

            if (t == manual)
            {
                Assert.True(atFork.Players[0].BurstLatch, "the fork inherits the Burst edge of the manual-Burst tick");
            }

            again.Cancel();
        }
    }

    [Fact]
    public async Task Two_siblings_from_T1140_play_different_futures_save_verified_Fork_children_and_leave_the_original_unchanged()
    {
        var sourceBefore = await SourceFingerprintAsync();
        var rig = NewRig();
        await using var writerHost = rig.Host;
        var id = db.Original.Timeline;
        var finalScore = db.Original.Result.Scores[0].TotalScore;

        var route = new AlternateRouteDriver();
        var x = await PlayAsync(rig, "sib-x", (s, c) => route.Inputs(s, c));
        var y = await PlayAsync(rig, "sib-y", (s, c) => Pattern(s));

        var (rx, ry) = (x.Result, y.Result);
        Assert.Equal((SoloBranchKind.Fork, SoloBranchKind.Fork), (rx.Kind, ry.Kind));
        Assert.Equal(((long)ForkAt, (long)ForkAt, 2700L, 2700L, id, id), (rx.ForkTick, ry.ForkTick, rx.FinalTick, ry.FinalTick, rx.Original, ry.Original));
        Assert.NotEqual(rx.Child, ry.Child);
        Assert.DoesNotContain(id, new[] { rx.Child, ry.Child });
        Assert.NotEqual(rx.ChildFinalStateHash, ry.ChildFinalStateHash);
        Assert.All(new[] { rx, ry }, r =>
        {
            Assert.NotEqual(db.Original.FinalStateHash, r.ChildFinalStateHash);
            Assert.Equal(db.Original.FinalStateHash, r.SourceFinalStateHash);
            Assert.Equal(finalScore, r.SourceScore);
            Assert.Equal(r.LivingScore - r.SourceScore, r.Delta);
        });
        Assert.NotEqual(Bytes(x.States[2700]), Bytes(y.States[2700]));

        // Each child verifies independently through the engine, from its own stored history.
        foreach (var (r, states) in new[] { (rx, x.States), (ry, y.States) })
        {
            var replay = (await rig.Host.Engine.ReplayAsync(r.Child, Ct)).Value;
            Assert.Equal((HistoryStatus.Verified, r.ChildFinalStateHash, r.ChildLastRecordHash, 2700L),
                (replay.Status, replay.FinalStateHash, replay.LastRecordHash, replay.FinalPosition));
            Assert.True(Bytes(replay.FinalState).AsSpan().SequenceEqual(Bytes(states[2700])));

            // Shared canonical prefix through T, own future from T+1.
            foreach (var k in new[] { 0, 570, ForkAt })
            {
                var prefix = Ok(await rig.Branches.RestoreAsync(db.OwnerA, r.Child, k, Ct));
                Assert.True(Bytes(db.Run.Trace[k]).AsSpan().SequenceEqual(Bytes(prefix.State)), $"sibling prefix at T{k}");
            }

            var row = Ok(await rig.Host.Store.FindBranchAsync(r.Child, Ct));
            Assert.Equal((SoloOriginalStatus.Complete, SoloBranchKind.Fork, (long)ForkAt, id, db.OwnerA.SubjectId),
                (row.Status, SoloBranchKinds.Of(row), row.ForkTick, row.Source, row.OwnerSubjectId));
            Assert.Equal((r.ChildFinalStateHash, r.ChildLastRecordHash), (row.FinalStateHash, row.LastRecordHash));
        }

        // Same-tick scores through the existing comparison port: the source's historical score at every tick, never its final score.
        long[] ticks = [0, 1, 570, 1139, ForkAt, ForkAt + 1, 1500, 2699, 2700];
        foreach (var (r, states) in new[] { (rx, x.States), (ry, y.States) })
        {
            var aligned = Ok(await rig.Branches.CompareAsync(db.OwnerA, r.Child, ticks, Ct));
            Assert.Equal(ticks, aligned.Select(a => a.Tick));
            foreach (var a in aligned)
            {
                var t = (int)a.Tick;
                Assert.Equal(db.Run.Trace[t].Players[0].TotalScore, a.Ghost.TotalScore);
                Assert.Equal(states[t].Players[0].TotalScore, a.Current.TotalScore);
                Assert.Equal(a.Current.TotalScore - a.Ghost.TotalScore, a.Delta);
                Assert.Equal(t == 2700, a.IsFinal);
                if (t <= ForkAt)
                {
                    Assert.Equal(0, a.Delta);
                }
            }

            Assert.NotEqual(finalScore, aligned.Single(a => a.Tick == ForkAt + 1).Ghost.TotalScore);
            Assert.Equal(r.Delta, aligned[^1].Delta);
        }

        // The original and its recorded future are untouched, and neither child is an original.
        Assert.Equal(sourceBefore, await SourceFingerprintAsync());
        var reopened = Ok(await rig.Originals.OpenAsync(db.OwnerA, id, Ct));
        Assert.Equal((db.Original.FinalStateHash, db.Original.LastRecordHash), (reopened.FinalStateHash, reopened.LastRecordHash));
        var listed = Ok(await rig.Originals.ListAsync(db.OwnerA, 100, Ct));
        Assert.Contains(listed, o => o.Timeline == id);
        Assert.DoesNotContain(listed, o => o.Timeline == rx.Child || o.Timeline == ry.Child);
        Assert.Equal(SoloErrorCode.NotFound, (await rig.Originals.OpenAsync(db.OwnerA, rx.Child, Ct)).Error.Code);

        // Remove the serving provider and its pool, then actually restart PostgreSQL with the same durable data.
        // The recovery composition has a new pool and empty trace cache; no retained session is used for any recovery read.
        await writerHost.DisposeAsync();
        await db.RestartAsync();
        await using var coldHost = db.NewHost();
        var cold = NewRig(coldHost, new SoloGhostTraceCache());
        var resolvedX = Ok(await cold.Challenges.ResolveForkAsync(db.OwnerA, "sib-x", Ct));
        var resolvedY = Ok(await cold.Challenges.ResolveForkAsync(db.OwnerA, "sib-y", Ct));
        Assert.Equal(rx, resolvedX with { LivingResult = rx.LivingResult });
        Assert.Equal((ry.Child, ry.ChildFinalStateHash, ry.LivingScore), (resolvedY.Child, resolvedY.ChildFinalStateHash, resolvedY.LivingScore));
        Assert.Equal(SoloErrorCode.NotFound, (await cold.Challenges.ResolveAsync(db.OwnerA, "sib-x", Ct)).Error.Code);
        Assert.Equal(SoloErrorCode.NotFound, (await cold.Challenges.ResolveForkAsync(Guest(), "sib-x", Ct)).Error.Code);
        Assert.Equal(SoloErrorCode.NotFound, (await cold.Branches.RestoreAsync(Guest(), rx.Child, 0, Ct)).Error.Code);

        var recoveredSource = Ok(await cold.Originals.OpenAsync(db.OwnerA, id, Ct));
        Assert.Equal((db.Original.FinalStateHash, db.Original.LastRecordHash),
            (recoveredSource.FinalStateHash, recoveredSource.LastRecordHash));
        var sourceAtFork = Ok(await cold.Originals.RestoreAsync(db.OwnerA, id, ForkAt, Ct));
        foreach (var (r, states, key) in new[] { (rx, x.States, "sib-x"), (ry, y.States, "sib-y") })
        {
            var replay = (await cold.Host.Engine.ReplayAsync(r.Child, Ct)).Value;
            Assert.Equal((HistoryStatus.Verified, 2700L, r.ChildFinalStateHash, r.ChildLastRecordHash),
                (replay.Status, replay.FinalPosition, replay.FinalStateHash, replay.LastRecordHash));
            Assert.Equal(Bytes(states[2700]), Bytes(replay.FinalState));
            foreach (var t in ticks)
            {
                var restored = Ok(await cold.Branches.RestoreAsync(db.OwnerA, r.Child, t, Ct));
                Assert.Equal(Bytes(states[t]), Bytes(restored.State));
                if (t == ForkAt)
                {
                    Assert.Equal(sourceAtFork.StateHash, restored.StateHash);
                    Assert.Equal(Bytes(sourceAtFork.State), Bytes(restored.State));
                }
            }

            var aligned = Ok(await cold.Branches.CompareAsync(db.OwnerA, r.Child, ticks, Ct));
            Assert.All(aligned, a =>
            {
                Assert.Equal(db.Run.Trace[(int)a.Tick].Players[0].TotalScore, a.Ghost.TotalScore);
                Assert.Equal(states[a.Tick].Players[0].TotalScore, a.Current.TotalScore);
            });
            // Repeated completed-key resolution cannot append or create anything.
            Assert.Equal(r.Child, Ok(await cold.Challenges.ResolveForkAsync(db.OwnerA, key, Ct)).Child);
            Assert.Equal(1L, await CountBranchesAsync("fork." + key));
            Assert.Equal(SoloErrorCode.NotFound, (await cold.Challenges.ResolveForkAsync(Guest(), key, Ct)).Error.Code);
            await AssertCanonicalChildAsync(r);
        }
        Assert.Equal(0, cold.Engine.Appends);
        Assert.Equal(sourceBefore, await SourceFingerprintAsync());
        output.WriteLine($"owner {db.OwnerA.SubjectId}; seed {db.Original.Seed}; mascot {db.Original.MascotId}; layout {db.Run.Context.LayoutVersion}; versions {GhostLapTimelineVersions.V1}");
        output.WriteLine($"T{ForkAt} state hash {sourceAtFork.StateHash}; canonical byte length {Bytes(sourceAtFork.State).Length}");
        output.WriteLine($"source fingerprint before/after restart {sourceBefore}");
        output.WriteLine($"source last record {db.Original.LastRecordHash}; X last record {rx.ChildLastRecordHash}; Y last record {ry.ChildLastRecordHash}");
        output.WriteLine("Writer provider disposed; PostgreSQL stopped/started; new restricted runtime provider and empty cache; both histories replay Verified.");
        output.WriteLine($"source {id.Value:D} final {db.Original.FinalStateHash} score {finalScore}");
        output.WriteLine($"sibling X {rx.Child.Value:D} final {rx.ChildFinalStateHash} score {rx.LivingScore} delta {rx.Delta}");
        output.WriteLine($"sibling Y {ry.Child.Value:D} final {ry.ChildFinalStateHash} score {ry.LivingScore} delta {ry.Delta}");
        output.WriteLine($"T{ForkAt}+1 divergence: X {x.States[ForkAt + 1].Players[0].TotalScore} / Y {y.States[ForkAt + 1].Players[0].TotalScore} / source {db.Run.Trace[ForkAt + 1].Players[0].TotalScore}");
    }

    [Fact]
    public async Task Retrying_an_operation_never_duplicates_a_child_a_new_key_creates_a_distinct_sibling_and_a_key_cannot_move_its_tick()
    {
        var rig = NewRig();
        await using var _ = rig.Host;
        var id = db.Original.Timeline;

        var first = rig.Challenges.BeginFork(db.OwnerA, id, ForkAt);
        Ok(await first.PrepareAsync(Ct));
        var a = Ok(await first.StartAsync("retry-key", Ct));
        first.Cancel();

        // A lost response: the same operation starts again at the same child, with one row.
        var second = rig.Challenges.BeginFork(db.OwnerA, id, ForkAt);
        Ok(await second.PrepareAsync(Ct));
        var b = Ok(await second.StartAsync("retry-key", Ct));
        Assert.Equal(a.Child, b.Child);
        Assert.Equal(1L, await CountBranchesAsync("fork.retry-key"));

        // A different operation at the same source and tick is a distinct sibling.
        var sibling = rig.Challenges.BeginFork(db.OwnerA, id, ForkAt);
        Ok(await sibling.PrepareAsync(Ct));
        var c = Ok(await sibling.StartAsync("retry-other", Ct));
        Assert.NotEqual(a.Child, c.Child);
        Assert.Equal(1L, await CountBranchesAsync("fork.retry-other"));
        sibling.Cancel();

        // The same key for another tick is a conflict that creates nothing; the session stays Ready.
        var moved = rig.Challenges.BeginFork(db.OwnerA, id, ForkAt + 1);
        Ok(await moved.PrepareAsync(Ct));
        Assert.Equal(SoloErrorCode.Conflict, (await moved.StartAsync("retry-key", Ct)).Error.Code);
        Assert.Equal(SoloChallengePhase.Ready, moved.Phase);
        moved.Cancel();
        Assert.Equal(1L, await CountBranchesAsync("fork.retry-key"));

        // Bind the key to the source identity too. Record a second authentic source with the existing legal-input
        // fixture at test speed; no second real-time session or synthetic catalog/manifest is used.
        var otherRecording = Ok(await rig.Originals.StartAsync(db.OwnerA, Seed, 2, "key-identity-original", Ct));
        await RecordAsync(otherRecording, GameRulesV1.RoundTicks, suppressManualBurstFrom: 1700);
        var otherOriginal = Ok(await otherRecording.CompleteAsync(Ct));
        rig.Engine.Reset();
        Assert.Equal(SoloErrorCode.Conflict,
            (await rig.Branches.ForkAsync(db.OwnerA, otherOriginal.Timeline, ForkAt, "fork.retry-key", Ct)).Error.Code);
        Assert.Equal(0, rig.Engine.Forks);
        Assert.Equal(1L, await CountBranchesAsync("fork.retry-key"));
        // The identical client key has a separate server-minted Challenge key and classification at T0.
        var challenge = rig.Challenges.Begin(db.OwnerA, id);
        Ok(await challenge.PrepareAsync(Ct));
        var challengeStart = Ok(await challenge.StartAsync("retry-key", Ct));
        Assert.Equal(SoloBranchKind.Challenge, challengeStart.Kind);
        Assert.NotEqual(a.Child, challengeStart.Child);
        Assert.Equal(SoloErrorCode.NotComplete, (await rig.Challenges.ResolveAsync(db.OwnerA, "retry-key", Ct)).Error.Code);
        challenge.Cancel();

        // An unfinished child is never continued after its ticks were committed: no duplicate, no result.
        for (var i = 0; i < 3; i++)
        {
            Ok(await second.CommitTickAsync(Ct));
        }

        second.Cancel();
        var retry = rig.Challenges.BeginFork(db.OwnerA, id, ForkAt);
        Ok(await retry.PrepareAsync(Ct));
        Assert.Equal(SoloErrorCode.Conflict, (await retry.StartAsync("retry-key", Ct)).Error.Code);
        Assert.Equal(1L, await CountBranchesAsync("fork.retry-key"));
        Assert.Equal(SoloErrorCode.NotComplete, (await rig.Challenges.ResolveForkAsync(db.OwnerA, "retry-key", Ct)).Error.Code);
        retry.Cancel();
        Assert.Equal(db.Original.FinalStateHash, Ok(await rig.Originals.OpenAsync(db.OwnerA, id, Ct)).FinalStateHash);
    }

    [Fact]
    public async Task Foreign_unknown_unfinished_nested_and_incompatible_originals_fail_closed_without_creating_anything()
    {
        var rig = NewRig(new SoloGhostTraceCache());
        await using var _ = rig.Host;
        var id = db.Original.Timeline;

        // Setup: a real child branch (never an original) and an unfinished recording.
        var child = Ok(await rig.Branches.ForkAsync(db.OwnerA, id, 5, "fork.nested-probe", Ct));
        var partial = Ok(await rig.Originals.StartAsync(db.OwnerA, Seed, 1, "fork-partial", Ct));
        await RecordAsync(partial, 30);
        rig.Engine.Reset();

        var cases = new List<(string Name, SoloChallenge Session, SoloErrorCode Expected)>
        {
            ("foreign owner", rig.Challenges.BeginFork(Guest(), id, ForkAt), SoloErrorCode.NotFound),
            ("unknown id", rig.Challenges.BeginFork(db.OwnerA, new TimelineId(Guid.NewGuid()), ForkAt), SoloErrorCode.NotFound),
            ("a child is not an original: no nested fork", rig.Challenges.BeginFork(db.OwnerA, child.Timeline, 3), SoloErrorCode.NotFound),
            ("unfinished", rig.Challenges.BeginFork(db.OwnerA, partial.Timeline, 10), SoloErrorCode.NotComplete),
        };

        foreach (var (name, session, expected) in cases)
        {
            var prepared = await session.PrepareAsync(Ct);
            Assert.True(prepared.Error.Code == expected, $"{name}: {prepared.Error}");
            Assert.Equal(SoloChallengePhase.Failed, session.Phase);
            Assert.Equal(SoloErrorCode.Conflict, (await session.StartAsync("refused", Ct)).Error.Code);
        }

        Assert.Equal((0, 0, 0), (rig.Engine.Restores, rig.Engine.Replays, rig.Engine.Forks));
        Assert.Equal(0L, await CountBranchesAsync("fork.refused"));

        // A build without the pinned ghost-v1 codec refuses instead of reinterpreting the bytes.
        var other = GhostLapTimelineVersions.V1Components with { Versions = GhostLapTimelineVersions.V1 with { ReducerVersion = "ghost-v2" } };
        var incompatible = NewRig(db.NewHost([other]), new SoloGhostTraceCache());
        await using var __ = incompatible.Host;
        var session2 = incompatible.Challenges.BeginFork(db.OwnerA, id, ForkAt);
        Assert.Equal(SoloErrorCode.IncompatibleVersion, (await session2.PrepareAsync(Ct)).Error.Code);
        Assert.Equal(SoloChallengePhase.Failed, session2.Phase);
        Assert.Equal(0, incompatible.Engine.Forks);
        Assert.Equal(0L, await CountBranchesAsync("fork.refused"));

        // Damage only this isolated test database, bypassing guards as admin, and restore in finally.
        // An empty cache ensures preparation really verifies the persisted source.
        await using var admin = await db.Superuser.OpenConnectionAsync();
        await using var bypass = new Npgsql.NpgsqlCommand("SET session_replication_role = replica", admin);
        await bypass.ExecuteNonQueryAsync();
        foreach (var (sql, bad, good, expected) in new[]
        {
            ("UPDATE marrow_timeline.histories SET status = @value WHERE timeline_id = @id", "Recording", "Verified", SoloErrorCode.VerificationFailed),
            ("UPDATE marrow_timeline.checkpoints SET state_hash = @value WHERE timeline_id = @id AND position = 1140",
                new string('0', 64), db.Run.Receipts[ForkAt - 1].StateHash, SoloErrorCode.CorruptHistory),
        })
        {
            await using var damage = new Npgsql.NpgsqlCommand(sql, admin);
            damage.Parameters.AddWithValue("id", id.Value);
            var value = damage.Parameters.AddWithValue("value", bad);
            Assert.Equal(1, await damage.ExecuteNonQueryAsync());
            try
            {
                var check = NewRig(new SoloGhostTraceCache());
                await using var checkHost = check.Host;
                var refused = check.Challenges.BeginFork(db.OwnerA, id, ForkAt);
                Assert.Equal(expected, (await refused.PrepareAsync(Ct)).Error.Code);
                Assert.Equal(SoloChallengePhase.Failed, refused.Phase);
                Assert.Equal(0, check.Engine.Forks);
                Assert.Equal(SoloErrorCode.Conflict, (await refused.StartAsync("refused-corrupt", Ct)).Error.Code);
                Assert.Equal(0L, await CountBranchesAsync("fork.refused-corrupt"));
            }
            finally
            {
                value.Value = good;
                Assert.Equal(1, await damage.ExecuteNonQueryAsync());
            }
        }
        Ok(await rig.Originals.OpenAsync(db.OwnerA, id, Ct));
    }

    private sealed record Played(SoloChallengeResult Result, GameState[] States);

    /// <summary>Different legal inputs from <see cref="AlternateRouteDriver"/>: a square wave of directions, no Burst, sequence continued from state.</summary>
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

    /// <summary>
    /// Forks at <see cref="ForkAt"/>, plays the remaining 1,560 ticks with <paramref name="inputs"/>, and checks every tick against an
    /// independent reducer fold and the source's recorded state at the same tick. No history read is allowed once gameplay starts.
    /// </summary>
    private async Task<Played> PlayAsync(Rig rig, string key, Func<GameState, GameContext, GameInput[]> inputs)
    {
        var fork = rig.Challenges.BeginFork(db.OwnerA, db.Original.Timeline, ForkAt);
        Ok(await fork.PrepareAsync(Ct));
        var started = Ok(await fork.StartAsync(key, Ct));
        Assert.Equal((long)ForkAt, started.Living.Tick);
        rig.Engine.Reset();

        var context = fork.LivingContext!;
        var reference = db.Run.Trace[ForkAt];
        var states = new GameState[GameRulesV1.RoundTicks + 1];
        for (var k = 0; k <= ForkAt; k++)
        {
            states[k] = db.Run.Trace[k];
        }

        var first = true;
        while (!fork.LivingState!.IsFinal)
        {
            var step = inputs(fork.LivingState!, context);
            foreach (var g in step)
            {
                var admitted = fork.Submit(Sub(g));
                Assert.Equal(g, admitted.Value);
            }

            var tick = Ok(await fork.CommitTickAsync(Ct));
            if (first)
            {
                Assert.Equal(ForkAt + 1L, tick.Tick);
                first = false;
            }

            reference = GhostLapTransition.Instance.Apply(reference, tick.Tick, BranchFixture.Ordered(step), context);
            Assert.Equal(tick.Living.TotalScore, reference.Players[0].TotalScore);
            Assert.Equal(db.Run.Receipts[(int)tick.Tick - 1].StateHash, tick.Ghost.StateHash);
            Assert.Equal(db.Run.Trace[(int)tick.Tick].Players[0].TotalScore, tick.Ghost.TotalScore);
            Assert.Equal(tick.Living.TotalScore - tick.Ghost.TotalScore, tick.Delta);
            states[tick.Tick] = fork.LivingState!;
            Assert.True(Bytes(reference).AsSpan().SequenceEqual(Bytes(fork.LivingState!)), $"living state at T{tick.Tick} is the reducer's");
        }

        // 2700 - T transitions, nothing but appends, ending at absolute tick 2700.
        Assert.Equal((0, 0, 0, 0), (rig.Engine.Restores, rig.Engine.Replays, rig.Engine.Compares, rig.Engine.Forks));
        Assert.Equal(GameRulesV1.RoundTicks - ForkAt, rig.Engine.Appends);
        Assert.Equal(2700L, fork.LivingState.Tick);
        // Lose one genuine completion acknowledgement: unavailable is not a falsely completed fork.
        if (key == "sib-x")
        {
            rig.Engine.LoseNextCompletionReply = true;
            Assert.Equal(SoloErrorCode.Unavailable, (await fork.CompleteAsync(Ct)).Error.Code);
            Assert.Equal(SoloChallengePhase.Running, fork.Phase);
            Assert.Equal(SoloErrorCode.NotComplete, (await rig.Challenges.ResolveForkAsync(db.OwnerA, key, Ct)).Error.Code);
        }
        var result = Ok(await fork.CompleteAsync(Ct));
        Assert.Equal(result, Ok(await fork.CompleteAsync(Ct)));
        Assert.Equal(SoloChallengePhase.Completed, fork.Phase);
        return new Played(result, states);
    }

    private async Task AssertCanonicalChildAsync(SoloChallengeResult child)
    {
        await using var cmd = db.Superuser.CreateCommand("""
            SELECT h.parent_timeline_id, h.fork_position, h.status, h.owner_id,
                   (SELECT min(position) FROM marrow_timeline.steps WHERE timeline_id = h.timeline_id),
                   (SELECT max(position) FROM marrow_timeline.steps WHERE timeline_id = h.timeline_id),
                   (SELECT count(*) FROM marrow_timeline.steps WHERE timeline_id = h.timeline_id),
                   h.context_hash = p.context_hash, h.rules_hash = p.rules_hash, h.seed_hash = p.seed_hash
            FROM marrow_timeline.histories h
            JOIN marrow_timeline.histories p ON p.timeline_id = h.parent_timeline_id
            WHERE h.timeline_id = @id
            """);
        cmd.Parameters.AddWithValue("id", child.Child.Value);
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        Assert.Equal(db.Original.Timeline.Value, r.GetGuid(0));
        Assert.Equal((long)ForkAt, r.GetInt64(1));
        Assert.Equal(("Verified", db.OwnerA.SubjectId), (r.GetString(2), r.GetString(3)));
        Assert.Equal((1141L, 2700L, 1560L), (r.GetInt64(4), r.GetInt64(5), r.GetInt64(6)));
        Assert.True(r.GetBoolean(7) && r.GetBoolean(8) && r.GetBoolean(9), "canonical context/rules/seed inherited");
    }

    private async Task<long> CountBranchesAsync(string operationKey)
    {
        await using var cmd = db.Superuser.CreateCommand("SELECT count(*) FROM ghostlap.solo_branches WHERE operation_key = @key");
        cmd.Parameters.AddWithValue("key", operationKey);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>Every persisted fact of the source original (manifest, step/checkpoint/input digests, Ghost Lap row), as the superuser.</summary>
    private async Task<string> SourceFingerprintAsync()
    {
        await using var cmd = db.Superuser.CreateCommand("""
            SELECT md5(row_to_json(h)::text)
              || '|' || (SELECT count(*) || ':' || md5(string_agg(row_to_json(s)::text, ',' ORDER BY position))
                         FROM marrow_timeline.steps s WHERE timeline_id = @id)
              || '|' || (SELECT count(*) || ':' || md5(string_agg(row_to_json(c)::text, ',' ORDER BY position))
                         FROM marrow_timeline.checkpoints c WHERE timeline_id = @id)
              || '|' || (SELECT count(*) || ':' || md5(string_agg(row_to_json(i)::text, ',' ORDER BY position, input_index))
                         FROM marrow_timeline.step_inputs i WHERE timeline_id = @id)
              || '|' || (SELECT md5(row_to_json(o)::text) FROM ghostlap.solo_originals o WHERE timeline_id = @id)
            FROM marrow_timeline.histories h WHERE h.timeline_id = @id
            """);
        cmd.Parameters.AddWithValue("id", db.Original.Timeline.Value);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }
}
