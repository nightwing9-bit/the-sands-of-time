using GhostLap.Rules;
using GhostLap.Simulation;
using GhostLap.Solo;
using GhostLap.Tests.Simulation;
using GhostLap.Timeline;
using Marrow.Infrastructure.Identity;
using Marrow.Timeline;
using Xunit;
using Xunit.Abstractions;
using static GhostLap.Tests.Solo.BranchFixture;
using static GhostLap.Tests.Solo.SoloDatabase;

namespace GhostLap.Tests.Solo;

/// <summary>
/// Task 6.4: a branch forked from a complete solo original through the one Marrow Timeline (ForkAsync / RestoreAsync /
/// CompareAlignedAsync) over real PostgreSQL, played with a different route, while the original stays byte-for-byte
/// unchanged and its ghost stays a read-only projection.
/// **Validates: Requirements R06.07, R06.08, R06.09, R06.11**
/// </summary>
[Trait("Category", "Integration")]
public sealed class SoloBranchTests(BranchFixture fx, ITestOutputHelper output) : IClassFixture<BranchFixture>
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private SoloDatabase Db => fx.Db;

    private static byte[] Bytes(GameState s) => GameStateCodec.Instance.EncodeCanonical(s);

    /// <summary>R06.07/R06.11: the fork tick restores the complete source state, timers and remainder included.</summary>
    [Fact]
    public async Task Fork_at_a_tick_with_charge_timers_remainder_and_collected_candles_starts_from_the_exact_source_state()
    {
        var t = (int)fx.ForkTick;
        var source = Db.Run.Trace[t];
        var p = source.Players[0];
        output.WriteLine($"fork tick {t}: charge {p.Charge}, overlit deadline {p.OverlitDeadlineTick}, hold until {p.HoldUntilTick}, " +
                         $"distance {p.DistanceFixed} (remainder {p.DistanceFixed % GameRulesV1.MovementScoreUnitFixed}), " +
                         $"score {p.TotalScore}, collected {source.Collected.Length}, active {source.ActiveCandleIds.Length}");
        Assert.True(p.Charge > 0 && p.HoldUntilTick > t && source.Collected.Length > 0);

        await using var cold = Db.NewHost();
        var atFork = Ok(await cold.Branches.RestoreAsync(Db.OwnerA, fx.Branch.Timeline, t, Ct));
        Assert.Equal(Bytes(source), Bytes(atFork.State));
        Assert.Equal(Db.Run.Receipts[t - 1].StateHash, atFork.StateHash);
        Assert.Equal(Bytes(source), Bytes(fx.BranchTrace[t]));

        // The shared prefix reads back as the source at earlier ticks as well.
        foreach (var earlier in new[] { 0, t / 2, t - 1 })
        {
            Assert.Equal(Bytes(Db.Run.Trace[earlier]), Bytes(Ok(await cold.Branches.RestoreAsync(Db.OwnerA, fx.Branch.Timeline, earlier, Ct)).State));
        }

        // The branch's first own tick is T+1, and with the source's recorded step it would equal the source's T+1 exactly.
        var pinned = GameContext.Create(Db.Original.Seed, 1, [Db.Original.MascotId]);
        var replayedNext = GhostLapTransition.Instance.Apply(atFork.State, t + 1, Ordered(Db.Run.Steps[t]), pinned);
        Assert.Equal(Bytes(Db.Run.Trace[t + 1]), Bytes(replayedNext));
        Assert.Equal(t + 1, fx.BranchTrace[t + 1].Tick);
    }

    /// <summary>R06.08: a real branch changes the outcome; the source history and its hashes are unchanged.</summary>
    [Fact]
    public async Task Branch_with_changed_inputs_changes_the_outcome_and_leaves_every_source_row_unchanged()
    {
        var sourceFinal = Db.Original.FinalState.Players[0];
        var branchFinal = fx.Branch.FinalState.Players[0];
        output.WriteLine($"source final {sourceFinal.TotalScore} ({sourceFinal.Pickups} pickups), hash {Db.Original.FinalStateHash}");
        output.WriteLine($"branch final {branchFinal.TotalScore} ({branchFinal.Pickups} pickups), hash {fx.Branch.FinalStateHash}");
        Assert.Equal(GameRulesV1.RoundTicks, fx.Branch.FinalTick);
        Assert.NotEqual(Db.Original.FinalStateHash, fx.Branch.FinalStateHash);
        Assert.NotEqual(sourceFinal.TotalScore, branchFinal.TotalScore);
        Assert.Contains(Enumerable.Range((int)fx.ForkTick + 1, GameRulesV1.RoundTicks - (int)fx.ForkTick),
            tick => fx.BranchSteps[tick - (int)fx.ForkTick - 1].Single() is var b && !Db.Run.Steps[tick - 1].Contains(b));

        // Every persisted source fact is identical to before the fork, and the original still reverifies from a cold host.
        Assert.Equal(fx.SourceBefore, await fx.SourceFingerprintAsync());
        await using var cold = Db.NewHost();
        var reopened = Ok(await cold.Originals.OpenAsync(Db.OwnerA, Db.Original.Timeline, Ct));
        Assert.Equal((Db.Original.FinalStateHash, Db.Original.LastRecordHash), (reopened.FinalStateHash, reopened.LastRecordHash));
        Assert.Equal(Bytes(Db.Run.Trace[^1]), Bytes(reopened.FinalState));

        // The branch is a verified child of the source in the one Timeline, with its own rows only after T.
        await using var cmd = Db.Superuser.CreateCommand("""
            SELECT h.parent_timeline_id, h.fork_position, h.status, h.owner_id,
                   (SELECT min(position) FROM marrow_timeline.steps WHERE timeline_id = h.timeline_id),
                   (SELECT count(*) FROM marrow_timeline.steps WHERE timeline_id = h.timeline_id),
                   b.status, b.final_state_hash
            FROM marrow_timeline.histories h JOIN ghostlap.solo_branches b ON b.timeline_id = h.timeline_id
            WHERE h.timeline_id = @id
            """);
        cmd.Parameters.AddWithValue("id", fx.Branch.Timeline.Value);
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        Assert.Equal(Db.Original.Timeline.Value, r.GetGuid(0));
        Assert.Equal(fx.ForkTick, r.GetInt64(1));
        Assert.Equal(("Verified", Db.OwnerA.SubjectId), (r.GetString(2), r.GetString(3)));
        Assert.Equal(fx.ForkTick + 1, r.GetInt64(4));
        Assert.Equal(GameRulesV1.RoundTicks - fx.ForkTick, r.GetInt64(5));
        Assert.Equal(("Complete", fx.Branch.FinalStateHash), (r.GetString(6), r.GetString(7)));

        // A branch replayed from the database in a cold host reproduces the live branch at every sampled tick.
        for (var tick = (int)fx.ForkTick; tick <= GameRulesV1.RoundTicks; tick += 97)
        {
            Assert.Equal(Bytes(fx.BranchTrace[tick]), Bytes(Ok(await cold.Branches.RestoreAsync(Db.OwnerA, fx.Branch.Timeline, tick, Ct)).State));
        }
    }

    /// <summary>
    /// R06.08: both worlds face the same pinned candle schedule, each consumes candles only from its own set, and the ghost
    /// never enters the living world (one player, no contact or Burst hit, branch states derive from branch inputs only).
    /// </summary>
    [Fact]
    public void Ghost_and_branch_collect_independently_from_the_same_candle_schedule()
    {
        var plan = GameContext.Create(Db.Original.Seed, 1, [Db.Original.MascotId]).Plan;
        var ghostOnly = 0;
        var branchOnly = 0;
        for (var tick = 0; tick <= GameRulesV1.RoundTicks; tick++)
        {
            var ghost = Db.Run.Trace[tick];
            var current = fx.BranchTrace[tick];
            var planned = plan.ActiveIdsAt(tick);
            // Same opportunities: each world's active set is the plan minus that world's own collected ids.
            Assert.Equal(planned.Except(ghost.Collected.Select(c => c.Id)), ghost.ActiveCandleIds);
            Assert.Equal(planned.Except(current.Collected.Select(c => c.Id)), current.ActiveCandleIds);
            if (tick <= fx.ForkTick)
            {
                Assert.Equal(Bytes(ghost), Bytes(current)); // exact overlap up to and including T
                continue;
            }

            // A candle the ghost collects this tick is still collectible in the branch unless the branch itself took it.
            foreach (var c in ghost.Events.Where(e => e.Kind == GameEventKind.CandleCollected))
            {
                if (!current.Collected.Any(x => x.Id == c.CandleId))
                {
                    Assert.Contains(c.CandleId, current.ActiveCandleIds);
                    ghostOnly++;
                }
            }

            foreach (var c in current.Events.Where(e => e.Kind == GameEventKind.CandleCollected))
            {
                if (!ghost.Collected.Any(x => x.Id == c.CandleId))
                {
                    Assert.Contains(c.CandleId, ghost.ActiveCandleIds);
                    branchOnly++;
                }
            }

            Assert.Single(current.Players);
            Assert.DoesNotContain(current.Events, e => e.Kind is GameEventKind.ContactDrain or GameEventKind.BurstHit);
            Assert.Equal(0, current.Players[0].PenaltyPoints);
        }

        output.WriteLine($"after T={fx.ForkTick}: {ghostOnly} candles taken only by the ghost, {branchOnly} only by the branch");
        Assert.True(ghostOnly > 0 && branchOnly > 0);

        // The branch world is a function of the restored T state and the branch's own inputs; no source data enters it.
        var pinned = GameContext.Create(Db.Original.Seed, 1, [Db.Original.MascotId]);
        var s = fx.BranchTrace[(int)fx.ForkTick];
        foreach (var step in fx.BranchSteps)
        {
            s = GhostLapTransition.Instance.Apply(s, s.Tick + 1, Ordered(step), pinned);
            Assert.Equal(Bytes(fx.BranchTrace[(int)s.Tick]), Bytes(s));
        }
    }

    /// <summary>R06.09: aligned ticks show both scores at that tick and the signed delta; final totals only at the final tick.</summary>
    [Fact]
    public async Task Aligned_comparison_reports_per_tick_scores_and_signed_delta_from_the_database()
    {
        var t = fx.ForkTick;
        long[] ticks = [0, t - 1, t, t + 1, t + 30, .. Enumerable.Range(1, 26).Select(i => (long)i * 100).Where(x => x > t + 30),
            GameRulesV1.RoundTicks - 1, GameRulesV1.RoundTicks];
        await using var cold = Db.NewHost();
        var aligned = Ok(await cold.Branches.CompareAsync(Db.OwnerA, fx.Branch.Timeline, ticks, Ct));
        Assert.Equal(ticks, aligned.Select(a => a.Tick));
        foreach (var a in aligned)
        {
            var ghost = Db.Run.Trace[(int)a.Tick].Players[0];
            var current = fx.BranchTrace[(int)a.Tick].Players[0];
            Assert.Equal((ghost.TotalScore, ghost.XFixed, ghost.YFixed, ghost.Charge), (a.Ghost.TotalScore, a.Ghost.XFixed, a.Ghost.YFixed, a.Ghost.Charge));
            Assert.Equal((current.TotalScore, current.XFixed, current.YFixed, current.Charge),
                (a.Current.TotalScore, a.Current.XFixed, a.Current.YFixed, a.Current.Charge));
            Assert.Equal(current.TotalScore - ghost.TotalScore, a.Delta);
            Assert.Equal(a.Tick == GameRulesV1.RoundTicks, a.IsFinal);
            Assert.Equal(HistoryStatus.Verified, a.CurrentStatus);
            if (a.Tick <= t)
            {
                Assert.Equal(0, a.Delta);
            }
        }

        Assert.Contains(aligned, a => a.Delta != 0);
        var last = aligned[^1];
        Assert.Equal(Db.Original.Result.Scores[0].TotalScore, last.Ghost.TotalScore);
        Assert.Equal(fx.Branch.FinalState.FinalResult!.Scores[0].TotalScore, last.Current.TotalScore);
        output.WriteLine(string.Join(", ", aligned.Select(a => $"{a.Tick}:{a.Ghost.TotalScore}/{a.Current.TotalScore}({a.Delta:+0;-0;0})")));
    }

    [Fact]
    public async Task Fork_is_owned_idempotent_rejects_the_final_tick_and_conflicting_duplicates_without_creating_histories()
    {
        await using var host = Db.NewHost();
        var source = Db.Original.Timeline;

        // Final tick is inspect-only; outside the round is an invalid tick. Neither creates anything.
        Assert.Equal(SoloErrorCode.ForkNotAllowed, (await host.Branches.ForkAsync(Db.OwnerA, source, GameRulesV1.RoundTicks, "final", Ct)).Error.Code);
        Assert.Equal(SoloErrorCode.InvalidTick, (await host.Branches.ForkAsync(Db.OwnerA, source, GameRulesV1.RoundTicks + 1, "beyond", Ct)).Error.Code);
        Assert.Equal(SoloErrorCode.InvalidTick, (await host.Branches.ForkAsync(Db.OwnerA, source, -1, "before", Ct)).Error.Code);
        Assert.Equal(Bytes(Db.Run.Trace[^1]), Bytes(Ok(await host.Originals.RestoreAsync(Db.OwnerA, source, GameRulesV1.RoundTicks, Ct)).State));

        // Same key + same tick: the same branch, resumed from what the database committed.
        var key = "retry-" + Guid.NewGuid().ToString("N");
        var first = Ok(await host.Branches.ForkAsync(Db.OwnerA, source, 100, key, Ct));
        Assert.Equal(Bytes(Db.Run.Trace[100]), Bytes(first.State));
        var seq = first.State.Players[0].LastSequence;
        Assert.True(first.Submit(new SubmittedInput(0, seq + 1, 101, -1, 0, false)).IsSuccess);
        var committed = Ok(await first.CommitTickAsync(Ct));
        await using (var restarted = Db.NewHost())
        {
            var again = Ok(await restarted.Branches.ForkAsync(Db.OwnerA, source, 100, key, Ct));
            Assert.Equal(first.Timeline, again.Timeline);
            Assert.Equal(Bytes(committed.State), Bytes(again.State));
            Assert.Equal(SoloErrorCode.NotComplete, (await again.CompleteAsync(Ct)).Error.Code);
        }

        // Same key + different tick: Conflict, and no Timeline child was created for it.
        Assert.Equal(SoloErrorCode.Conflict, (await host.Branches.ForkAsync(Db.OwnerA, source, 200, key, Ct)).Error.Code);
        Assert.Equal(1, await CountChildrenAsync("ghost-lap/branch/" + key));

        // Only the owner may fork or read; strangers get the same NotFound as an unknown id.
        var unknown = new TimelineId(Guid.NewGuid());
        foreach (var actor in new[] { Guest(), Staff() })
        {
            var fork = await host.Branches.ForkAsync(actor, source, 100, "steal", Ct);
            Assert.Equal(SoloErrorCode.NotFound, fork.Error.Code);
            Assert.Equal((await host.Branches.ForkAsync(Db.OwnerA, unknown, 100, "steal", Ct)).Error, fork.Error);
            Assert.Equal(SoloErrorCode.NotFound, (await host.Branches.RestoreAsync(actor, fx.Branch.Timeline, 10, Ct)).Error.Code);
            Assert.Equal(SoloErrorCode.NotFound, (await host.Branches.CompareAsync(actor, fx.Branch.Timeline, [10], Ct)).Error.Code);
        }

        Assert.Equal(SoloErrorCode.NotFound, (await host.Branches.CompareAsync(Db.OwnerA, unknown, [10], Ct)).Error.Code);
        Assert.Equal(SoloErrorCode.Unauthenticated, (await host.Branches.ForkAsync(ClaimsActorContext.Anonymous, source, 100, "anon", Ct)).Error.Code);
        Assert.Equal(SoloErrorCode.InvalidRequest, (await host.Branches.ForkAsync(Db.OwnerA, source, 100, "bad key/", Ct)).Error.Code);
        Assert.Equal(0, await CountChildrenAsync("ghost-lap/branch/steal"));

        // A branch reads only its committed prefix; malformed comparison requests are typed failures.
        Assert.Equal(SoloErrorCode.InvalidTick, (await host.Branches.CompareAsync(Db.OwnerA, first.Timeline, [102], Ct)).Error.Code);
        Assert.Equal(101, Ok(await host.Branches.CompareAsync(Db.OwnerA, first.Timeline, [101], Ct)).Single().Tick);
        Assert.Equal(HistoryStatus.Recording, Ok(await host.Branches.CompareAsync(Db.OwnerA, first.Timeline, [101], Ct)).Single().CurrentStatus);
        Assert.Equal(SoloErrorCode.InvalidRequest, (await host.Branches.CompareAsync(Db.OwnerA, first.Timeline, [], Ct)).Error.Code);

        // An unfinished run is not an original and cannot be forked.
        var partialOwner = Guest();
        var partial = Ok(await host.Originals.StartAsync(partialOwner, 5, 0, "partial", Ct));
        await RecordAsync(partial, 20);
        Assert.Equal(SoloErrorCode.NotComplete, (await host.Branches.ForkAsync(partialOwner, partial.Timeline, 10, "p", Ct)).Error.Code);

        Assert.Equal(fx.SourceBefore, await fx.SourceFingerprintAsync());
    }

    /// <summary>
    /// Solo has no contact pairs, so the contact-cooldown part of R06.07 is proven on a 4-player Ghost Lap history in the same
    /// shared engine and codec: forking at a tick with an active pair cooldown, charge and collected candles restores the
    /// exact state, and the child continued with the source's steps reproduces the source's step hashes.
    /// </summary>
    [Fact]
    public async Task Fork_restores_an_active_contact_cooldown_in_a_four_player_history()
    {
        await using var host = Db.NewHost();
        var engine = host.Engine;
        var ctx = GameContext.Create(1234, 4);
        var key = "coop-" + Guid.NewGuid().ToString("N");
        var source = (await engine.CreateSourceAsync(new CreateSourceRequest<GameState, GameContext>(
            Db.OwnerA.SubjectId, GhostLapTimelineVersions.V1, GameState.Initial(ctx), ctx, SoloOriginals.RulesHash,
            SoloOriginals.SeedHash(ctx)), key, Ct)).Value;
        var driver = new CandleSeekingDriver(4);
        var states = new List<GameState> { GameState.Initial(ctx) };
        var steps = new List<IReadOnlyList<OrderedInput<GameInput>>>();
        var hashes = new List<string>();
        long? fork = null;
        while (states[^1].Tick < GameRulesV1.RoundTicks && (fork is null || states[^1].Tick < fork + 30))
        {
            var s = states[^1];
            var step = Ordered(driver.Inputs(s, ctx));
            var receipt = (await engine.AppendAsync(source, s.Tick, new StepRecord<GameInput>(s.Tick + 1, step), $"{key}/{s.Tick + 1}", Ct)).Value;
            var next = GhostLapTransition.Instance.Apply(s, s.Tick + 1, step, ctx);
            states.Add(next);
            steps.Add(step);
            hashes.Add(receipt.StateHash);
            if (fork is null && next.PairContactReadyTicks.Any(r => r > next.Tick) && next.Players.Any(p => p.Charge > 0)
                && next.Collected.Length > 0)
            {
                fork = next.Tick;
            }
        }

        Assert.NotNull(fork);
        var t = fork.Value;
        output.WriteLine($"4P fork tick {t}: cooldowns [{string.Join(",", states[(int)t].PairContactReadyTicks)}], " +
                         $"charges [{string.Join(",", states[(int)t].Players.Select(p => p.Charge))}]");
        Assert.True((await engine.CompleteAsync(source, states[^1].Tick, Ct)).IsSuccess);

        var child = (await engine.ForkAsync(source, t, key + "/fork", ctx, Ct)).Value;
        var restored = (await engine.RestoreAsync(child, t, Ct)).Value;
        Assert.Equal(Bytes(states[(int)t]), Bytes(restored.State));
        Assert.Equal(states[(int)t].PairContactReadyTicks.ToArray(), restored.State.PairContactReadyTicks.ToArray());
        for (var tick = t + 1; tick <= states[^1].Tick; tick++)
        {
            var r = (await engine.AppendAsync(child, tick - 1, new StepRecord<GameInput>(tick, steps[(int)tick - 1]), $"{key}/child/{tick}", Ct)).Value;
            Assert.Equal(hashes[(int)tick - 1], r.StateHash);
        }
    }

    [Fact]
    public async Task The_database_keeps_branch_source_fork_tick_owner_and_completion_immutable()
    {
        var id = fx.Branch.Timeline.Value;
        foreach (var sql in new[]
                 {
                     "UPDATE ghostlap.solo_branches SET fork_tick = 0 WHERE timeline_id = @id",
                     "UPDATE ghostlap.solo_branches SET final_state_hash = repeat('0', 64) WHERE timeline_id = @id",
                     "DELETE FROM ghostlap.solo_branches WHERE timeline_id = @id",
                 })
        {
            await using var cmd = Db.Superuser.CreateCommand(sql);
            cmd.Parameters.AddWithValue("id", id);
            Assert.Equal("GL001", (await Assert.ThrowsAsync<Npgsql.PostgresException>(() => cmd.ExecuteNonQueryAsync())).SqlState);
        }

        await using var app = Npgsql.NpgsqlDataSource.Create(Db.AppConnectionString);
        await using var delete = app.CreateCommand("DELETE FROM ghostlap.solo_branches WHERE timeline_id = @id");
        delete.Parameters.AddWithValue("id", id);
        Assert.Equal(Npgsql.PostgresErrorCodes.InsufficientPrivilege,
            (await Assert.ThrowsAsync<Npgsql.PostgresException>(() => delete.ExecuteNonQueryAsync())).SqlState);
        Assert.Contains("module ghostlap: applying 0002_solo_branches", Db.MigrationLog);
    }

    /// <summary>
    /// Crash window of <c>SoloBranches.ForkAsync</c>: the Timeline child is committed, the process dies before the Ghost Lap
    /// branch row exists. The child is unreachable through Ghost Lap (ownership answers from the row), and a retry with the same
    /// operation key adopts that very child, even when several retries race, so no duplicate branch or Timeline child appears.
    /// </summary>
    [Fact]
    public async Task A_timeline_child_orphaned_before_its_branch_row_is_adopted_by_a_retry_with_the_same_key_exactly_once()
    {
        await using var host = Db.NewHost();
        var source = Db.Original.Timeline;
        var key = "orphan-" + Guid.NewGuid().ToString("N");
        var timelineKey = "ghost-lap/branch/" + key;
        var context = GameContext.Create(Db.Original.Seed, 1, [Db.Original.MascotId]);

        // The crash: only the engine half of ForkAsync ran.
        var orphan = (await host.Engine.ForkAsync(source, 100, timelineKey, context, Ct)).Value;
        Assert.Equal(0, await CountBranchRowsAsync(key));
        Assert.Equal(1, await CountChildrenAsync(timelineKey));

        // Unreachable through Ghost Lap while no row exists: the same NotFound as an unknown id, for the owner too.
        Assert.Equal(SoloErrorCode.NotFound, (await host.Branches.RestoreAsync(Db.OwnerA, orphan, 100, Ct)).Error.Code);
        Assert.Equal(SoloErrorCode.NotFound, (await host.Branches.CompareAsync(Db.OwnerA, orphan, [100], Ct)).Error.Code);

        // Another guest cannot fork the owner's original at all, so it cannot adopt or even observe the orphan.
        var stranger = Guest();
        Assert.Equal(SoloErrorCode.NotFound, (await host.Branches.ForkAsync(stranger, source, 100, key, Ct)).Error.Code);
        Assert.Equal(0, await CountBranchRowsAsync(key));

        // Racing retries by the owner all adopt the one orphan.
        var retries = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => host.Branches.ForkAsync(Db.OwnerA, source, 100, key, Ct)));
        Assert.All(retries, r => Assert.Equal(orphan, Ok(r).Timeline));
        Assert.All(retries, r => Assert.Equal(Bytes(Db.Run.Trace[100]), Bytes(Ok(r).State)));
        Assert.Equal(1, await CountBranchRowsAsync(key));
        Assert.Equal(1, await CountChildrenAsync(timelineKey));
        Assert.Equal(Bytes(Db.Run.Trace[100]), Bytes(Ok(await host.Branches.RestoreAsync(Db.OwnerA, orphan, 100, Ct)).State));

        Assert.Equal(fx.SourceBefore, await fx.SourceFingerprintAsync());
    }

    private async Task<long> CountBranchRowsAsync(string operationKey)
    {
        await using var cmd = Db.Superuser.CreateCommand(
            "SELECT count(*) FROM ghostlap.solo_branches WHERE source_timeline_id = @id AND operation_key = @key");
        cmd.Parameters.AddWithValue("id", Db.Original.Timeline.Value);
        cmd.Parameters.AddWithValue("key", operationKey);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<long> CountChildrenAsync(string operationKey)
    {
        await using var cmd = Db.Superuser.CreateCommand(
            "SELECT count(*) FROM marrow_timeline.histories WHERE parent_timeline_id = @id AND operation_key = @key");
        cmd.Parameters.AddWithValue("id", Db.Original.Timeline.Value);
        cmd.Parameters.AddWithValue("key", operationKey);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}
