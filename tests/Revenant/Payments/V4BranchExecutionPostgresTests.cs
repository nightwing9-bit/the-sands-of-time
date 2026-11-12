using Marrow.Infrastructure.Timeline;
using Marrow.Timeline;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Revenant.Contracts;
using Revenant.Payments;
using Revenant.Tests.Ledger;
using Revenant.Tests.Timeline;
using Revenant.Timeline;
using Xunit;
using Xunit.Abstractions;
using LedgerSql = Revenant.Tests.Ledger.Ledger;

namespace Revenant.Tests.Payments;

[Collection(PaymentCollection.Name)]
[Trait("Category", "Integration")]
public sealed class V4BranchExecutionPostgresTests(LedgerDatabase db, ITestOutputHelper output)
{
    private const string Tenant = PaymentKit.TenantA;
    private const string Owner = PaymentKit.Operator;
    private const long Nine = ObservedFinanceV4Tests.Nine;
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task Verified_source_forks_byte_identically_and_the_child_appends_only_its_suffix()
    {
        var sourceId = new ScenarioRunId(Guid.Parse("00000000-0000-0000-0000-000000010001"));
        var childId = new ScenarioRunId(Guid.Parse("00000000-0000-0000-0000-000000010002"));
        var (run, context, _) = ObservedFinanceV4Tests.Start(sourceId);
        var childContext = ObservedFinanceV4Tests.ChildContext(context);
        var logins = await db.ProvisionDeployedLoginsAsync();
        var store = new V4SimulationStore(db.Writer);
        await using var host = Host(logins.Worker);
        var engine = Engine(host);
        var execution = new V4BranchExecution(store, engine);

        var source = await execution.OpenSourceAsync(Tenant, Owner, run, context, Ct);
        Assert.Equal(source, await execution.OpenSourceAsync(Tenant, Owner, run, context, Ct));
        Assert.Equal(sourceId, source.LineageRunId);
        Assert.Equal(sourceId, source.ExecutionRunId);
        _ = await store.SealAsync(Tenant, sourceId, Nine - 300_000, Ct);
        var atNine = Assert.IsType<ObservedFinanceInput>((await store.SealAsync(Tenant, sourceId, Nine, Ct)).Seal);
        Assert.Equal(2, atNine.Sequence);
        Assert.Equal(Nine - 300_000, ObservationSealCodec.Decode(atNine.Payload.AsSpan()).IntervalStartMs);
        _ = await store.SealAsync(Tenant, sourceId, Nine + 300_000, Ct);

        const string key = "r3-identity-nine";
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            execution.ForkAsync(Tenant, Owner, run, sourceId, childId, Nine, key, childContext, Ct));
        var sourceFinal = await execution.CompleteAsync(Tenant, sourceId, Ct);
        Assert.Equal(3, sourceFinal.FinalPosition);
        Assert.Equal(HistoryStatus.Verified, sourceFinal.Status);
        var checkpoint = (await engine.RestoreAsync(source.TimelineId, atNine.Sequence, Ct)).Value;

        var child = await execution.ForkAsync(Tenant, Owner, run, sourceId, childId, Nine, key, childContext, Ct);
        Assert.Equal(child, await execution.ForkAsync(Tenant, Owner, run, sourceId, childId, Nine, key, childContext, Ct));
        Assert.Equal(child, await store.ReadBindingAsync(Tenant, childId, Ct));
        Assert.Equal(childId, child.ExecutionRunId);
        Assert.Equal(sourceId, child.LineageRunId);
        Assert.NotEqual(source.TimelineId, child.TimelineId);
        Assert.NotEqual(source.ContextHash, child.ContextHash);
        Assert.Equal(childContext.BasePolicy, (await store.ReadPinnedContextAsync(Tenant, childId, Ct)).BasePolicy);

        var inherited = (await engine.RestoreAsync(child.TimelineId, atNine.Sequence, Ct)).Value;
        Assert.Equal(checkpoint.StateHash, inherited.StateHash);
        var codec = ObservedFinanceStateCodec.Instance;
        Assert.Equal(codec.EncodeCanonical(checkpoint.State), codec.EncodeCanonical(inherited.State));
        Assert.Equal(checkpoint.State.PrefixDigest, child.Fork!.PrefixDigest);
        Assert.Equal(checkpoint.StateHash, child.Fork.StateHash);
        Assert.Equal(atNine.Sequence, await CursorAsync(childId));
        Assert.Equal(0, await PaymentKit.Count(db.Owner, "finance_fact", childId.Value));
        Assert.Equal(Nine, await PaymentKit.Scalar<long>(db.Owner, """
            SELECT watermark_exclusive_ms FROM revenant.v4_scheduler_run WHERE run_id = @run
            """, ("run", childId.Value)));

        // The branch binding has no mutation grant for the replay login and is invisible in another tenant.
        var wrongTenant = await Assert.ThrowsAsync<V4ScheduleException>(() => store.ReadBindingAsync(PaymentKit.TenantB, childId, Ct));
        Assert.Equal(V4ScheduleError.RunNotFound, wrongTenant.Error);
        await using (var replay = await db.Replay.OpenConnectionAsync(Ct))
        await using (var tx = await LedgerSql.BeginTenantAsync(replay, Tenant))
        {
            var denied = await Assert.ThrowsAsync<PostgresException>(() => LedgerSql.ExecAsync(replay,
                "DELETE FROM revenant.v4_branch_execution WHERE execution_run_id = @run", ("run", childId.Value)));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
        }

        await AssertBindingRejectedAsync(child with { ExecutionRunId = new(Guid.NewGuid()) }, PaymentKit.TenantB);
        await AssertBindingRejectedAsync(child with { ExecutionRunId = new(Guid.NewGuid()), TimelineId = new(Guid.NewGuid()) }, Tenant);
        await AssertBindingRejectedAsync(child with { ExecutionRunId = new(Guid.NewGuid()), LineageRunId = new(Guid.NewGuid()) }, Tenant);
        await AssertBindingRejectedAsync(child with { ExecutionRunId = new(Guid.NewGuid()),
            Fork = child.Fork with { StateHash = ObservedFinanceV4Tests.EmptyDigest } }, Tenant);
        await AssertBindingRejectedAsync(child with { ExecutionRunId = new(Guid.NewGuid()),
            Fork = child.Fork with { PrefixDigest = ObservedFinanceV4Tests.EmptyDigest } }, Tenant);

        var blocked = await Assert.ThrowsAsync<PostgresException>(() => store.AdmitAsync(Tenant, childId,
            new("phase-one-blocked", V4CommandClass.Exogenous, "PaymentIntent", "test-command/1", [1], Nine, Nine), Ct));
        Assert.Equal("RV011", blocked.SqlState);
        Assert.Empty(await store.ReadJournalAsync(Tenant, childId, Ct));

        var next = Assert.IsType<ObservedFinanceInput>((await store.SealAsync(Tenant, childId, Nine + 300_000, Ct)).Seal);
        var retry = Assert.IsType<ObservedFinanceInput>((await store.SealAsync(Tenant, childId, Nine + 300_000, Ct)).Seal);
        Assert.Equal(ObservedFinanceInputCodec.Instance.EncodeCanonical(next), ObservedFinanceInputCodec.Instance.EncodeCanonical(retry));
        Assert.Equal(atNine.Sequence + 1, next.Sequence);
        Assert.Equal(sourceId, next.RunId);
        Assert.Equal(checkpoint.State.PrefixDigest, ObservationSealCodec.Decode(next.Payload.AsSpan()).PrefixFactDigestSha256);
        Assert.Equal(1, await PaymentKit.Count(db.Owner, "finance_fact", childId.Value));
        Assert.Equal(3, await CursorAsync(childId));
        var childFinal = await execution.CompleteAsync(Tenant, childId, Ct);
        Assert.Equal(HistoryStatus.Verified, childFinal.Status);

        await using var coldHost = Host(logins.Worker);
        var cold = Engine(coldHost);
        var sourceReplay = (await cold.ReplayAsync(source.TimelineId, Ct)).Value;
        var childReplay = (await cold.ReplayAsync(child.TimelineId, Ct)).Value;
        Assert.Equal(sourceFinal.FinalStateHash, sourceReplay.FinalStateHash);
        Assert.Equal(sourceFinal.LastRecordHash, sourceReplay.LastRecordHash);
        Assert.Equal(3, await CursorAsync(sourceId));
        Assert.Equal(3, await PaymentKit.Count(db.Owner, "finance_fact", sourceId.Value));
        Assert.Equal(childFinal.FinalStateHash, childReplay.FinalStateHash);
        Assert.NotEqual(sourceFinal.FinalStateHash, childFinal.FinalStateHash);

        var rules = await PaymentKit.Scalar<long>(db.Owner, """
            SELECT count(*) FROM marrow_timeline.histories WHERE timeline_id IN (@source, @child) AND rules_hash = @hash
            """, ("source", source.TimelineId.Value), ("child", child.TimelineId.Value), ("hash", V4BranchExecution.FamilyRulesHash));
        Assert.Equal(2, rules);
        output.WriteLine($"Lineage and source execution {sourceId.Value:D}; source Timeline {source.TimelineId.Value:D}");
        output.WriteLine($"Child execution {childId.Value:D}; child Timeline {child.TimelineId.Value:D}");
        output.WriteLine($"Fork {atNine.Sequence} at {Nine} has identical state hash {checkpoint.StateHash} and prefix {checkpoint.State.PrefixDigest}");

        async Task AssertBindingRejectedAsync(V4ExecutionBinding candidate, string tenant)
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => store.OpenRunAsync(tenant, Owner, run, childContext, candidate, Ct));
            Assert.Equal("RV011", error.SqlState);
        }
    }

    private Task<long> CursorAsync(ScenarioRunId id) => PaymentKit.Scalar<long>(db.Owner, """
        SELECT last_fact_seq FROM revenant.run_cursor WHERE scenario_run_id = @run
        """, ("run", id.Value));

    private static ServiceProvider Host(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        services.AddMarrowTimeline(ObservedFinanceTimelineVersions.All, ObservedFinanceTimelineVersions.CheckpointInterval);
        return services.BuildServiceProvider();
    }

    private static ITimelineEngine<ObservedFinanceState, ObservedFinanceInput, ObservedFinanceContext> Engine(ServiceProvider host) =>
        host.GetRequiredService<ITimelineEngine<ObservedFinanceState, ObservedFinanceInput, ObservedFinanceContext>>();
}
