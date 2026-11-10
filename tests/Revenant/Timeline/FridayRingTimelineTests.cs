using Marrow.Infrastructure.Timeline;
using Marrow.Timeline;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Revenant.Contracts;
using Revenant.Finance;
using Revenant.Scenario;
using Revenant.Tests.Scenario;
using Revenant.Timeline;
using Xunit;
using Xunit.Abstractions;

namespace Revenant.Tests.Timeline;

/// <summary>
/// Task 5.3: the Friday Ring under policy-v7 recorded as a real Marrow Timeline source, forked at 09:00 under
/// policy-v8 and continued through the same engine, against a freshly migrated PostgreSQL and the runtime login.
/// **Validates: Requirements R05.03, R05.04, R05.05, R05.09, R05.10, R05.11**
/// </summary>
[Trait("Category", "Integration")]
public sealed class FridayRingTimelineTests(RevenantTimelineDatabase db, ITestOutputHelper output) : IClassFixture<RevenantTimelineDatabase>
{
    private const string Owner = "staff-revenant-test";
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly ScenarioRunId RunA = new(Guid.Parse("00000000-0000-0000-0000-0000000053a1"));
    private static readonly ScenarioRunId RunB = new(Guid.Parse("00000000-0000-0000-0000-0000000053b1"));

    [Fact]
    public async Task V7_source_and_v8_fork_share_one_engine_compute_distinct_holds_and_leave_the_source_byte_identical()
    {
        var run = FridayRingScenarioTests.Create(42, RunA);
        var opening = run.InitialState.Accounts.Sum(a => a.OpeningBalance.MinorUnits);
        var v7Ref = PolicyCatalog.RefOf(PolicyCatalog.V7);
        var v8Ref = PolicyCatalog.RefOf(PolicyCatalog.V8);
        var forkAt = FinanceTimelineAdapter.PositionAtOrBefore(run, run.Manifest.StartUtcMs + (60 * 60 * 1000L)); // 09:00

        RecordedSource source;
        SourceEvidence before;
        RecordedBranch branch;
        await using (var host = NewHost(FinanceTimelineVersions.All))
        {
            var engine = host.GetRequiredService<ITimelineEngine<FinanceProjectionState, PaymentIntent, FinanceContext>>();
            var adapter = new FinanceTimelineAdapter(engine);

            // Real recorded source: one exogenous intent per position, completed to Verified by engine replay.
            source = Ok(await adapter.RecordSourceAsync(Owner, run, v7Ref, Key(), Ct));
            Assert.Equal(HistoryStatus.Verified, source.Status);
            Assert.Equal(run.Intents.Length, source.FinalPosition);
            var replay = Ok(await engine.ReplayAsync(source.Id, Ct));
            var replayAgain = Ok(await engine.ReplayAsync(source.Id, Ct));
            Assert.Equal(source.FinalStateHash, replay.FinalStateHash);
            Assert.Equal(replay.FinalStateHash, replayAgain.FinalStateHash);
            Assert.Equal(source.FinalStateHash, Ok(await engine.RestoreAsync(source.Id, source.FinalPosition, Ct)).StateHash);
            // Engine replay is byte-identical to an independent local fold of the pure reducer.
            var localV7 = FinanceTimelineCodecTests.Fold(run, FinanceTimelineCodecTests.Pin(run, PolicyCatalog.V7), run.Intents.Length);
            Assert.Equal(Bytes(localV7[^1]), Bytes(replay.FinalState));
            Assert.All(replay.FinalState.Payments.Values, p => Assert.Equal(PaymentStatus.Settled, p.Status));

            before = await EvidenceAsync(engine, source.Id);

            // R05.04: selected position -> Marrow restore/fork -> pure reducer -> aligned states and metrics.
            var counterfactual = Ok(await adapter.RunCounterfactualAsync(Request(source.Id, forkAt, run.Manifest, v8Ref), Ct));
            branch = counterfactual.Branch;
            Assert.Equal(HistoryStatus.Verified, branch.Status);
            Assert.Equal(source.FinalPosition, branch.FinalPosition);
            Assert.Equal([forkAt, source.FinalPosition], counterfactual.Aligned.Select(a => a.Position));

            var atFork = counterfactual.Aligned[0];
            Assert.Equal(atFork.Original.StateHash, atFork.Alternate.StateHash); // shared verified prefix
            Assert.Equal(0, atFork.Metrics.DecisionDifferenceCount);

            // R05.03: computed, different decisions and downstream balance/hold trajectory after the fork.
            var end = counterfactual.Aligned[1];
            Assert.NotEqual(end.Original.StateHash, end.Alternate.StateHash);
            Assert.True(end.Metrics.DecisionDifferenceCount >= 1);
            Assert.Equal(0, end.Original.State.TotalReservedMinor);
            Assert.True(end.Alternate.State.TotalReservedMinor > 0);
            Assert.Equal(opening, end.Original.State.TotalPostedMinor);
            Assert.Equal(opening, end.Alternate.State.TotalPostedMinor);
            Assert.Contains(end.Original.State.Banks.Zip(end.Alternate.State.Banks),
                pair => pair.First.PostedMinor != pair.Second.PostedMinor || pair.First.AvailableMinor != pair.Second.AvailableMinor);
            // The v8 context applies only from T+1; earlier decisions are the shared v7 prefix.
            Assert.All(end.Alternate.State.Payments.Values, p =>
                Assert.Equal(p.Intent.Sequence <= forkAt ? PolicyCatalog.V7Id : PolicyCatalog.V8Id, p.PolicyId));
            var expectedV8 = FinanceReducer.ApplyAll(localV7[(int)forkAt], run.Intents.Skip((int)forkAt),
                FinanceTimelineCodecTests.Pin(run, PolicyCatalog.V8)).Value;
            Assert.Equal(Bytes(expectedV8), Bytes(end.Alternate.State));

            // R05.11: v8 only holds the ring at this horizon, so it is deferred, never prevented.
            Assert.Equal(10, end.Metrics.FraudDeferredAtHorizonCount);
            Assert.Equal(0, end.Metrics.FraudPreventedCount);
            Assert.Equal(0, end.Metrics.FraudPreventedExposureMinor);
            // Exact figures from the independent reference calculation: the 09:00 fork is position 30, so legitimate
            // FR-0021 (position 21) stays in the shared v7 prefix and only FR-0054 is delayed; a full v8 run delays both.
            Assert.Equal(30, forkAt);
            Assert.Equal((1, 250L, 360L, -610L, 11),
                (end.Metrics.LegitimateDelayedCount, end.Metrics.FalsePositiveCostMinor, end.Metrics.IncrementalDelayCostMinor,
                 end.Metrics.NetEffectMinor, end.Metrics.DecisionDifferenceCount));

            // R05.05 (cancel): a cancelled continuation stays Recording at T; resuming reaches the same hash.
            var second = Ok(await adapter.CreateBranchAsync(Request(source.Id, forkAt, run.Manifest, v8Ref), Ct));
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();
            var cancelledResult = await adapter.ContinueBranchAsync(second, cancelled.Token);
            Assert.Equal(TimelineErrorCode.Cancelled, cancelledResult.Error.TimelineCode);
            var partial = Ok(await engine.ReplayAsync(second.Branch, Ct));
            Assert.Equal(HistoryStatus.Recording, partial.Status);
            Assert.Equal(forkAt, partial.FinalPosition);
            Assert.Equal(before, await EvidenceAsync(engine, source.Id));
            var resumed = Ok(await adapter.ContinueBranchAsync(second, Ct));
            Assert.Equal(HistoryStatus.Verified, resumed.Status);
            Assert.Equal(branch.FinalStateHash, resumed.FinalStateHash);

            // R05.10: a second run with the same seed is its own source and never touches the first.
            var otherRun = FridayRingScenarioTests.Create(42, RunB);
            var other = Ok(await adapter.RecordSourceAsync(Owner, otherRun, v7Ref, Key(), Ct));
            Assert.NotEqual(source.Id, other.Id);
            Assert.NotEqual(source.FinalStateHash, other.FinalStateHash);
            Assert.Equal(run.ExogenousFactsHash, otherRun.ExogenousFactsHash);

            // R05.05 (create): source rows, manifest, replay hash and v7 decision evidence are byte-identical.
            var after = await EvidenceAsync(engine, source.Id);
            Assert.Equal(before, after);
            output.WriteLine($"forkAt={forkAt} sourceFinal={source.FinalStateHash} branchFinal={branch.FinalStateHash}");
            output.WriteLine($"sourceRows before={before.Rows} after={after.Rows}");
            output.WriteLine($"sourceFinalStateBytes sha-equal={before.FinalStateBytesHex == after.FinalStateBytesHex} length={before.FinalStateBytesHex.Length / 2}");
            output.WriteLine($"metrics@{end.Position}: {end.Metrics}");
            output.WriteLine($"reserved v7={end.Original.State.TotalReservedMinor} v8={end.Alternate.State.TotalReservedMinor}");
        }

        // Cold host: replay from durable pinned contexts only.
        await using (var cold = NewHost(FinanceTimelineVersions.All))
        {
            var engine = cold.GetRequiredService<ITimelineEngine<FinanceProjectionState, PaymentIntent, FinanceContext>>();
            Assert.Equal(before, await EvidenceAsync(engine, source.Id));
            Assert.Equal(branch.FinalStateHash, Ok(await engine.ReplayAsync(branch.Handle.Branch, Ct)).FinalStateHash);
        }
    }

    [Fact]
    public async Task Branch_pins_fail_closed_on_any_mismatch_and_create_no_child()
    {
        var run = FridayRingScenarioTests.Create(7, RunA);
        var v8Ref = PolicyCatalog.RefOf(PolicyCatalog.V8);
        await using var host = NewHost(FinanceTimelineVersions.All);
        var engine = host.GetRequiredService<ITimelineEngine<FinanceProjectionState, PaymentIntent, FinanceContext>>();
        var adapter = new FinanceTimelineAdapter(engine);
        var source = Ok(await adapter.RecordSourceAsync(Owner, run, PolicyCatalog.RefOf(PolicyCatalog.V7), Key(), Ct));

        var wrongPolicyHash = v8Ref with { ConfigHash = PolicyCatalog.V7.ContentHash };
        var otherSeed = FridayRingScenarioTests.Create(8, RunA).Manifest;
        Assert.Equal(FinanceErrorCode.VersionMismatch,
            (await adapter.CreateBranchAsync(Request(source.Id, 40, run.Manifest, wrongPolicyHash), Ct)).Error.FinanceCode);
        Assert.Equal(FinanceErrorCode.VersionMismatch,
            (await adapter.CreateBranchAsync(Request(source.Id, 40, otherSeed, v8Ref), Ct)).Error.FinanceCode);
        Assert.Equal(FinanceErrorCode.VersionMismatch,
            (await adapter.CreateBranchAsync(Request(source.Id, 40, run.Manifest, v8Ref) with { ReducerVersion = "revenant-finance-reducer/2" }, Ct)).Error.FinanceCode);
        Assert.Equal(TimelineErrorCode.InvalidPosition,
            (await adapter.CreateBranchAsync(Request(source.Id, source.FinalPosition + 1, run.Manifest, v8Ref), Ct)).Error.TimelineCode);
        await using (var count = db.Owner.CreateCommand(
            "SELECT count(*) FROM marrow_timeline.histories WHERE parent_timeline_id = @p"))
        {
            count.Parameters.Add(new NpgsqlParameter("p", source.Id.Value));
            Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
        }

        // A deployment without this history's pinned version key fails closed instead of using "current".
        var later = FinanceTimelineVersions.V1Components with
        {
            Versions = FinanceTimelineVersions.V1 with { StateSchemaVersion = "revenant-finance-state/2" },
        };
        await using var laterHost = NewHost([later]);
        var laterEngine = laterHost.GetRequiredService<ITimelineEngine<FinanceProjectionState, PaymentIntent, FinanceContext>>();
        Assert.Equal(TimelineErrorCode.IncompatibleVersion, (await laterEngine.RestoreAsync(source.Id, 10, Ct)).Error.Code);
    }

    /// <summary>Everything about a source that a branch must never change.</summary>
    private sealed record SourceEvidence(string Rows, string Manifest, string ReplayHash, string LastRecordHash, string FinalStateBytesHex);

    private async Task<SourceEvidence> EvidenceAsync(
        ITimelineEngine<FinanceProjectionState, PaymentIntent, FinanceContext> engine, TimelineId id)
    {
        var replay = Ok(await engine.ReplayAsync(id, Ct));
        await using var rows = db.Owner.CreateCommand("""
            SELECT
              (SELECT count(*) || ':' || coalesce(md5(string_agg(position || ':' || state_hash || ':' || previous_record_hash || ':' || record_hash, ',' ORDER BY position)), '')
                 FROM marrow_timeline.steps WHERE timeline_id = @p)
              || '|' ||
              (SELECT count(*) || ':' || coalesce(md5(string_agg(position || ':' || input_id || ':' || ordinal || ':' || encode(payload, 'hex'), ',' ORDER BY position, input_index)), '')
                 FROM marrow_timeline.step_inputs WHERE timeline_id = @p)
              || '|' ||
              (SELECT count(*) || ':' || coalesce(md5(string_agg(position || ':' || state_hash || ':' || encode(state_bytes, 'hex'), ',' ORDER BY position)), '')
                 FROM marrow_timeline.checkpoints WHERE timeline_id = @p)
            """);
        rows.Parameters.Add(new NpgsqlParameter("p", id.Value));
        await using var manifest = db.Owner.CreateCommand(
            "SELECT row_to_json(h)::text FROM marrow_timeline.histories h WHERE timeline_id = @p");
        manifest.Parameters.Add(new NpgsqlParameter("p", id.Value));
        return new SourceEvidence(
            (string)(await rows.ExecuteScalarAsync())!,
            (string)(await manifest.ExecuteScalarAsync())!,
            replay.FinalStateHash,
            replay.LastRecordHash,
            Convert.ToHexStringLower(Bytes(replay.FinalState)));
    }

    private ServiceProvider NewHost(IEnumerable<TimelineComponents<FinanceProjectionState, PaymentIntent, FinanceContext>> components)
    {
        var connectionString = db.AppConnectionString;
        var services = new ServiceCollection();
        services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        services.AddMarrowTimeline(components, FinanceTimelineVersions.CheckpointInterval);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private static BranchRequest Request(TimelineId source, long position, ScenarioManifest manifest, PolicyRef policy) =>
        new(source, position, manifest, policy, FinanceReducer.Version, Key());

    private static byte[] Bytes(FinanceProjectionState state) => FinanceStateCodec.Instance.EncodeCanonical(state);

    private static string Key() => Guid.NewGuid().ToString("N");

    private static T Ok<T>(Marrow.Kernel.Results.Result<T, FinanceTimelineError> result)
    {
        Assert.True(result.IsSuccess, result.ToString());
        return result.Value;
    }

    private static T Ok<T>(Marrow.Kernel.Results.Result<T, TimelineError> result)
    {
        Assert.True(result.IsSuccess, result.ToString());
        return result.Value;
    }
}
