using Marrow.Infrastructure.Timeline;
using Marrow.Kernel.Canonical;
using Marrow.Kernel.Messaging;
using Marrow.Infrastructure.Messaging;
using Revenant.Cases;
using Revenant.Finance;
using Revenant.Scenario;
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

        var sourceBefore = await SourceFingerprintAsync(sourceId);
        await using (var inject = db.Owner.CreateCommand($"""
            CREATE FUNCTION revenant.test_v4_child_failure() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
              RAISE EXCEPTION USING ERRCODE = 'RV098', MESSAGE = 'injected child registration failure';
            END $$;
            CREATE TRIGGER test_v4_child_failure BEFORE INSERT ON revenant.v4_scheduler_run
              FOR EACH ROW WHEN (NEW.run_id = '{childId.Value:D}'::uuid)
              EXECUTE FUNCTION revenant.test_v4_child_failure();
            """))
        {
            await inject.ExecuteNonQueryAsync(Ct);
        }
        TimelineId reservedTimeline;
        try
        {
            var crash = await Assert.ThrowsAsync<PostgresException>(() =>
                execution.ForkAsync(Tenant, Owner, run, sourceId, childId, Nine, key, childContext, Ct));
            Assert.Equal("RV098", crash.SqlState);
            Assert.Equal("Pending", await PaymentKit.Scalar<string>(db.Owner, """
                SELECT readiness_status FROM revenant.v4_branch_execution WHERE execution_run_id = @run
                """, ("run", childId.Value)));
            var pending = await Assert.ThrowsAsync<V4ScheduleException>(() => store.AdvanceOneStepAsync(Tenant, childId, Ct));
            Assert.Equal(V4ScheduleError.RunNotFound, pending.Error);
            reservedTimeline = new(await PaymentKit.Scalar<Guid>(db.Owner, """
                SELECT timeline_id FROM marrow_timeline.histories WHERE parent_timeline_id = @parent AND operation_key = @key
                """, ("parent", source.TimelineId.Value), ("key", key)));
            var wrongChild = await Assert.ThrowsAsync<PostgresException>(() =>
                execution.ForkAsync(Tenant, Owner, run, sourceId, new(Guid.NewGuid()), Nine, key, childContext, Ct));
            Assert.Equal(PostgresErrorCodes.UniqueViolation, wrongChild.SqlState);
        }
        finally
        {
            await using var drop = db.Owner.CreateCommand("""
                DROP TRIGGER test_v4_child_failure ON revenant.v4_scheduler_run;
                DROP FUNCTION revenant.test_v4_child_failure();
                """);
            await drop.ExecuteNonQueryAsync(Ct);
        }

        var child = await execution.ForkAsync(Tenant, Owner, run, sourceId, childId, Nine, key, childContext, Ct);
        Assert.Equal(child, await execution.ForkAsync(Tenant, Owner, run, sourceId, childId, Nine, key, childContext, Ct));
        Assert.Equal(child, await store.ReadBindingAsync(Tenant, childId, Ct));
        Assert.Equal(childId, child.ExecutionRunId);
        Assert.Equal(reservedTimeline, child.TimelineId);
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
        var sourceAfter = await SourceFingerprintAsync(sourceId);
        Assert.Equal(sourceBefore, sourceAfter);
        Assert.Equal(childFinal.FinalStateHash, childReplay.FinalStateHash);
        Assert.NotEqual(sourceFinal.FinalStateHash, childFinal.FinalStateHash);

        var rules = await PaymentKit.Scalar<long>(db.Owner, """
            SELECT count(*) FROM marrow_timeline.histories WHERE timeline_id IN (@source, @child) AND rules_hash = @hash
            """, ("source", source.TimelineId.Value), ("child", child.TimelineId.Value), ("hash", V4BranchExecution.FamilyRulesHash));
        Assert.Equal(2, rules);
        output.WriteLine($"Lineage and source execution {sourceId.Value:D}; source Timeline {source.TimelineId.Value:D}");
        output.WriteLine($"Child execution {childId.Value:D}; child Timeline {child.TimelineId.Value:D}");
        output.WriteLine($"Fork {atNine.Sequence} at {Nine} has identical state hash {checkpoint.StateHash} and prefix {checkpoint.State.PrefixDigest}");
        output.WriteLine($"Source SQL before {sourceBefore}; after {sourceAfter}");

        async Task AssertBindingRejectedAsync(V4ExecutionBinding candidate, string tenant)
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => store.OpenRunAsync(tenant, Owner, run, childContext, candidate, Ct));
            Assert.Equal("RV011", error.SqlState);
        }
    }

    [Fact]
    public async Task Financial_fork_preserves_the_opening_and_posts_or_reserves_under_the_actual_policy()
    {
        var sourceId = new ScenarioRunId(Guid.Parse("00000000-0000-0000-0000-000000010011"));
        var childId = new ScenarioRunId(Guid.Parse("00000000-0000-0000-0000-000000010012"));
        var (run, context, initial) = ObservedFinanceV4Tests.Start(sourceId);
        var logins = await db.ProvisionDeployedLoginsAsync();
        await using var host = Host(logins.Worker);
        var engine = Engine(host);
        var ledger = PaymentKit.Store(db.Writer);
        var store = new V4SimulationStore(db.Writer, ledger: ledger);
        var execution = new V4BranchExecution(store, engine);
        var source = await execution.OpenSourceAsync(Tenant, Owner, run, context, Ct);
        var template = run.Intents[0];
        const long amount = 1_000_000;
        var alreadySettled = Intent("r3-open-posted", 1, Nine - 300_000, 100_000, new(false, false));
        var inherited = Intent("r3-inherited", 2, Nine - 1, amount, new(false, true));
        var comparison = Intent("r3-comparison", 3, Nine, amount, new(true, false));
        var compensating = Intent("r3-return", 4, Nine + 1, 100_000, new(false, false));

        _ = await store.SealAsync(Tenant, sourceId, Nine - 300_000, Ct);
        await RunAsync(sourceId, Intake(alreadySettled));
        await RunAsync(sourceId, Opportunity("screen-opening", FinanceFactKind.Screening, alreadySettled));
        await RunAsync(sourceId, Opportunity("settle-opening", FinanceFactKind.Settlement, alreadySettled));
        await RunAsync(sourceId, Intake(inherited));
        await RunAsync(sourceId, Opportunity("hold-before-fork", FinanceFactKind.Screening, inherited));
        var forkSeal = (await store.SealAsync(Tenant, sourceId, Nine, Ct)).Seal!;
        Assert.Equal(Nine, forkSeal.EffectiveTimeMs);
        var sourceAtFork = await execution.SynchronizeAsync(Tenant, sourceId, Ct);
        Assert.Equal(forkSeal.Sequence, sourceAtFork.LastFact);

        await RunAsync(sourceId, Intake(comparison));
        await RunAsync(sourceId, Opportunity("screen-comparison", FinanceFactKind.Screening, comparison));
        await RunAsync(sourceId, Opportunity("settle-comparison", FinanceFactKind.Settlement, comparison));
        var posted = await BalanceAsync(sourceId, comparison.SourceAccountId);
        var postedDest = await BalanceAsync(sourceId, comparison.DestAccountId);
        Assert.Equal((sourceAtFork.Ledger.Accounts[comparison.SourceAccountId].PostedMinor - amount, amount), posted);
        Assert.Equal(sourceAtFork.Ledger.Accounts[comparison.DestAccountId].PostedMinor + amount, postedDest.Posted);
        Assert.Equal(PaymentStatus.Settled, (await ledger.GetPaymentAsync(Tenant, sourceId, comparison.IntentId, Ct))!.Status);
        Assert.Null(await ledger.GetCaseAsync(Tenant, sourceId, comparison.IntentId, Ct));

        var rejectInherited = Human("source-reject", FinanceFactKind.ReviewDecision, inherited.IntentId, Owner, 1, CaseAction.Reject, Nine + 1);
        await RunAsync(sourceId, rejectInherited);
        await RunAsync(sourceId, Intake(compensating));
        await RunAsync(sourceId, Opportunity("screen-return", FinanceFactKind.Screening, compensating));
        await RunAsync(sourceId, Opportunity("settle-return", FinanceFactKind.Settlement, compensating));
        await RunAsync(sourceId, Human("return-posting", FinanceFactKind.ReturnInstruction, compensating.IntentId, Owner, 4,
            CaseAction.Assign, Nine + 1));
        Assert.Equal(PaymentStatus.Returned, (await ledger.GetPaymentAsync(Tenant, sourceId, compensating.IntentId, Ct))!.Status);
        Assert.Equal(4, await PaymentKit.Count(db.Owner, "ledger_group", sourceId.Value));
        Assert.Equal((posted.Posted, 0L), await BalanceAsync(sourceId, comparison.SourceAccountId));
        Assert.Equal(postedDest, await BalanceAsync(sourceId, comparison.DestAccountId));
        _ = await store.SealAsync(Tenant, sourceId, Nine + 300_000, Ct);
        var sourceFinal = await execution.CompleteAsync(Tenant, sourceId, Ct);
        var sourceBefore = await SourceFingerprintAsync(sourceId);

        var childContext = ObservedFinanceV4Tests.ChildContext(context);
        var child = await execution.ForkAsync(Tenant, Owner, run, sourceId, childId, Nine, "r3-financial-nine", childContext, Ct);
        Assert.Equal(sourceAtFork.PrefixDigest, child.Fork!.PrefixDigest);
        var childAtFork = (await engine.RestoreAsync(child.TimelineId, forkSeal.Sequence, Ct)).Value;
        Assert.Equal(ObservedFinanceStateCodec.Instance.EncodeCanonical(sourceAtFork),
            ObservedFinanceStateCodec.Instance.EncodeCanonical(childAtFork.State));
        foreach (var account in sourceAtFork.Ledger.Accounts.Values)
        {
            Assert.Equal((account.PostedMinor, account.ReservedMinor), await BalanceAsync(childId, account.AccountId));
        }
        Assert.Equal(0, await PaymentKit.Count(db.Owner, "finance_fact", childId.Value));
        Assert.Equal(1, await PaymentKit.Count(db.Owner, "ledger_group", childId.Value));
        Assert.Equal(0, await PaymentKit.Count(db.Owner, "ledger_entry", childId.Value));
        Assert.Equal(PaymentStatus.Held, (await ledger.GetPaymentAsync(Tenant, childId, inherited.IntentId, Ct))!.Status);
        Assert.NotEqual((await ledger.GetCaseAsync(Tenant, sourceId, inherited.IntentId, Ct))!.CaseId,
            (await ledger.GetCaseAsync(Tenant, childId, inherited.IntentId, Ct))!.CaseId);

        // The intake's own outbox failure rolls back the payment, fact and command outcome.
        var intake = Intake(comparison);
        _ = await store.AdmitAsync(Tenant, childId, intake, Ct);
        var failing = new V4SimulationStore(db.Writer, ledger: PaymentKit.Store(db.Writer,
            _ => throw new InvalidOperationException("injected outbox failure")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.AdvanceOneStepAsync(Tenant, childId, Ct));
        Assert.Null(await ledger.GetPaymentAsync(Tenant, childId, comparison.IntentId, Ct));
        Assert.Equal(0, await PaymentKit.Count(db.Owner, "finance_fact", childId.Value));
        Assert.Equal(V4CommandStatus.Pending, (await store.AdmitAsync(Tenant, childId, intake, Ct)).Status);

        _ = await store.AdmitAsync(Tenant, childId, Opportunity("screen-comparison", FinanceFactKind.Screening, comparison), Ct);
        var competing = new V4SimulationStore(db.Writer, ledger: PaymentKit.Store(db.Writer));
        var steps = await Task.WhenAll(store.AdvanceOneStepAsync(Tenant, childId, Ct), competing.AdvanceOneStepAsync(Tenant, childId, Ct));
        Assert.All(steps, step => Assert.Equal(V4CommandStatus.Completed, step.Command!.Status));
        Assert.Equal(2, await PaymentKit.Count(db.Owner, "finance_fact", childId.Value));
        Assert.Equal(V4CommandStatus.Completed, (await store.AdmitAsync(Tenant, childId, intake, Ct)).Status);
        var childSql = await store.ReadFinanceHistoryAsync(Tenant, childId, sourceId, Ct);
        Assert.Equal(new[] { forkSeal.Sequence + 1, forkSeal.Sequence + 2 }, childSql.Facts.Select(f => f.Input.Sequence));
        Assert.All(childSql.Facts, f => Assert.Equal(Nine, f.Input.EffectiveTimeMs));
        var expectedDigest = sourceAtFork.PrefixDigest;
        foreach (var fact in childSql.Facts)
        {
            expectedDigest = ObservedFinanceReducer.AppendDigest(expectedDigest, fact.Input);
            Assert.Equal(expectedDigest, fact.PrefixDigest);
        }

        var held = await BalanceAsync(childId, comparison.SourceAccountId);
        var heldDest = await BalanceAsync(childId, comparison.DestAccountId);
        Assert.Equal((posted.Posted + amount, amount * 2), held);
        Assert.Equal(postedDest.Posted - amount, heldDest.Posted);
        Assert.Equal(PaymentStatus.Held, (await ledger.GetPaymentAsync(Tenant, childId, comparison.IntentId, Ct))!.Status);
        Assert.Equal(CaseStatus.Open, (await ledger.GetCaseAsync(Tenant, childId, comparison.IntentId, Ct))!.Status);
        await RunAsync(childId, Opportunity("settle-comparison", FinanceFactKind.Settlement, comparison), V4CommandStatus.Cancelled);
        Assert.Equal(1, await PaymentKit.Count(db.Owner, "ledger_group", childId.Value));
        Assert.Equal(0, await PaymentKit.Count(db.Owner, "ledger_entry", childId.Value));
        Assert.Equal(sourceAtFork.LastFact, (await engine.ReplayAsync(child.TimelineId, Ct)).Value.FinalPosition);
        var reconciled = await execution.SynchronizeAsync(Tenant, childId, Ct);
        Assert.Equal(expectedDigest, reconciled.PrefixDigest);
        Assert.Equal(reconciled.PrefixDigest, (await execution.SynchronizeAsync(Tenant, childId, Ct)).PrefixDigest);

        Assert.True(await ledger.GrantRunStaffAsync(childId, PaymentKit.Supervisor, "supervisor", Ct));
        await RunAsync(childId, Human("child-first-approval", FinanceFactKind.ReviewDecision, inherited.IntentId, Owner,
            1, CaseAction.Approve, Nine + 1));
        var stale = await RunAsync(childId, rejectInherited, V4CommandStatus.Cancelled);
        Assert.Equal("inapplicable-StaleVersion", stale.OutcomeCode);
        var sameActor = await RunAsync(childId, Human("child-same-actor", FinanceFactKind.ReviewDecision, inherited.IntentId, Owner,
            2, CaseAction.Approve, Nine + 1), V4CommandStatus.Cancelled);
        Assert.Equal("inapplicable-DistinctSupervisorRequired", sameActor.OutcomeCode);
        await RunAsync(childId, Human("child-second-approval", FinanceFactKind.ReviewDecision, inherited.IntentId, PaymentKit.Supervisor,
            2, CaseAction.Approve, Nine + 1));
        await RunAsync(childId, Opportunity("child-settle-approved", FinanceFactKind.Settlement, inherited, Nine + 1));
        Assert.Equal(PaymentStatus.Settled, (await ledger.GetPaymentAsync(Tenant, childId, inherited.IntentId, Ct))!.Status);
        Assert.Equal(amount, (await BalanceAsync(childId, comparison.SourceAccountId)).Reserved);

        var beforeRace = await BalanceAsync(childId, comparison.SourceAccountId);
        var competingAmount = (beforeRace.Posted - beforeRace.Reserved) / 2 + 1;
        var firstHold = Intent("r3-hold-first", 5, Nine + 2, competingAmount, new(true, false));
        var secondHold = Intent("r3-hold-second", 6, Nine + 2, competingAmount, new(true, false));
        await RunAsync(childId, Intake(firstHold));
        await RunAsync(childId, Intake(secondHold));
        _ = await store.AdmitAsync(Tenant, childId, Opportunity("reserve-first", FinanceFactKind.Screening, firstHold), Ct);
        _ = await store.AdmitAsync(Tenant, childId, Opportunity("reserve-second", FinanceFactKind.Screening, secondHold), Ct);
        var reserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstWorker = new V4SimulationStore(db.Writer, ledger: PaymentKit.Store(db.Writer,
            tx => new HoldBarrierOutbox(new PostgresOutboxWriter(tx, new MessagingTables("revenant")), reserved, proceed)));
        var firstTask = firstWorker.AdvanceOneStepAsync(Tenant, childId, Ct);
        Task<V4Step> secondTask;
        try
        {
            await reserved.Task.WaitAsync(TimeSpan.FromSeconds(15), Ct);
            secondTask = competing.AdvanceOneStepAsync(Tenant, childId, Ct);
        }
        finally
        {
            proceed.TrySetResult();
        }
        var heldSteps = await Task.WhenAll(firstTask, secondTask);
        Assert.Equal(nameof(PaymentStatus.Held), heldSteps[0].Command!.OutcomeCode);
        Assert.Equal(nameof(PaymentStatus.Rejected), heldSteps[1].Command!.OutcomeCode);
        Assert.Equal((beforeRace.Posted, beforeRace.Reserved + competingAmount),
            await BalanceAsync(childId, comparison.SourceAccountId));
        Assert.Null(await ledger.GetCaseAsync(Tenant, childId, secondHold.IntentId, Ct));

        _ = await store.SealAsync(Tenant, childId, Nine + 300_000, Ct);
        var childFinal = await execution.CompleteAsync(Tenant, childId, Ct);
        await using var coldHost = Host(logins.Worker);
        var cold = Engine(coldHost);
        var coldSource = (await cold.ReplayAsync(source.TimelineId, Ct)).Value;
        var coldChild = (await cold.ReplayAsync(child.TimelineId, Ct)).Value;
        Assert.Equal(sourceFinal.FinalStateHash, coldSource.FinalStateHash);
        Assert.Equal(sourceFinal.LastRecordHash, coldSource.LastRecordHash);
        Assert.Equal(childFinal.FinalStateHash, coldChild.FinalStateHash);
        Assert.Equal(initial.Ledger.TotalPostedMinor, coldChild.FinalState.Ledger.TotalPostedMinor);
        Assert.Equal(initial.Ledger.TotalPostedMinor, coldSource.FinalState.Ledger.TotalPostedMinor);
        var sourceAfter = await SourceFingerprintAsync(sourceId);
        Assert.Equal(sourceBefore, sourceAfter);
        output.WriteLine($"Source SQL {sourceBefore} -> {sourceAfter}; Marrow {sourceFinal.FinalStateHash} -> {coldSource.FinalStateHash}");
        output.WriteLine($"Lineage {sourceId.Value:D}, source Timeline {source.TimelineId.Value:D}, child execution {childId.Value:D}, child Timeline {child.TimelineId.Value:D}");
        output.WriteLine($"Fork {forkSeal.Sequence} at {Nine}: {childAtFork.StateHash}, prefix {sourceAtFork.PrefixDigest}");
        output.WriteLine($"{comparison.IntentId} amount {amount}: v7 Settled payer {posted} payee {postedDest}; v8 Held payer {held} payee {heldDest}");

        PaymentIntent Intent(string key, long sequence, long when, long minor, PaymentFeatures features) =>
            new(sourceId, key, sequence, when, template.SourceBankId, template.SourceAccountId, template.DestBankId,
                template.DestAccountId, Money.Usd(minor), SyntheticTruth.Legitimate, features);

        async Task<V4ScheduledCommand> RunAsync(ScenarioRunId id, V4CommandRequest request,
            V4CommandStatus status = V4CommandStatus.Completed)
        {
            _ = await store.AdmitAsync(Tenant, id, request, Ct);
            var step = await store.AdvanceOneStepAsync(Tenant, id, Ct);
            Assert.Equal(V4StepKind.ProcessedCommand, step.Kind);
            Assert.Equal(request.Key, step.Command!.Request.Key);
            Assert.Equal(status, step.Command.Status);
            return step.Command;
        }
    }

    [Fact]
    public async Task Reconciliation_rejects_conflicting_bytes_already_written_at_the_SQL_position()
    {
        var (run, context, _) = ObservedFinanceV4Tests.Start(new(Guid.NewGuid()));
        var logins = await db.ProvisionDeployedLoginsAsync();
        await using var host = Host(logins.Worker);
        var engine = Engine(host);
        var store = new V4SimulationStore(db.Writer);
        var execution = new V4BranchExecution(store, engine);
        var root = await execution.OpenSourceAsync(Tenant, Owner, run, context, Ct);
        var actual = (await store.SealAsync(Tenant, root.ExecutionRunId, Nine - 300_000, Ct)).Seal!;
        var conflicting = ObservedFinanceInput.Seal(ObservationSealCodec.Decode(actual.Payload.AsSpan()) with
        {
            ResolvedCommandDigestSha256 = ObservedFinanceV4Tests.EmptyDigest,
        });
        var inputId = FinanceLedgerTransition.InputId(root.LineageRunId, actual.Sequence);
        var recorded = await engine.AppendAsync(root.TimelineId, 0, new(actual.Sequence, [new(inputId, 0, conflicting)]), inputId, Ct);
        Assert.True(recorded.IsSuccess);
        await Assert.ThrowsAsync<InvalidOperationException>(() => execution.SynchronizeAsync(Tenant, root.ExecutionRunId, Ct));
        Assert.Equal(1, await CursorAsync(root.ExecutionRunId));
    }

    private sealed class HoldBarrierOutbox(IOutboxWriter writer, TaskCompletionSource entered, TaskCompletionSource proceed) : IOutboxWriter
    {
        public async Task EnqueueAsync(OutboxMessage message, CancellationToken ct)
        {
            if (message.EventType == PaymentLedgerStore.EventHoldReserved)
            {
                entered.TrySetResult();
                await proceed.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
            }
            await writer.EnqueueAsync(message, ct);
        }
    }

    private static V4CommandRequest Intake(PaymentIntent intent) =>
        new("intake-" + intent.IntentId, V4CommandClass.Exogenous, nameof(FinanceFactKind.PaymentIntent),
            ScenarioSchemas.InputSchemaVersion, [.. PaymentIntentCodec.Instance.EncodeCanonical(intent)], intent.EventTimeUtcMs, intent.EventTimeUtcMs);

    private static V4CommandRequest Opportunity(string key, FinanceFactKind kind, PaymentIntent intent, long? when = null) =>
        new(key, V4CommandClass.DerivedOpportunity, kind.ToString(), "v4-payment-opportunity/1",
            [.. new CanonicalWriter().WriteInt32(1).WriteString(intent.IntentId).ToArray()], when ?? intent.EventTimeUtcMs, when ?? intent.EventTimeUtcMs);

    private static V4CommandRequest Human(string key, FinanceFactKind kind, string intent, string actor, long version, CaseAction action, long when) =>
        new(key, V4CommandClass.ConditionalHumanRequest, kind.ToString(), "v4-human-request/1",
            [.. new CanonicalWriter().WriteInt32(1).WriteString(intent).WriteString(actor).WriteInt64(version).WriteBoolean(true)
                .WriteByte((byte)action).ToArray()], when, when);

    private async Task<(long Posted, long Reserved)> BalanceAsync(ScenarioRunId id, string account)
    {
        await using var command = db.Owner.CreateCommand("""
            SELECT posted_minor, reserved_minor, available_minor FROM revenant.account_available_balance
            WHERE scenario_run_id = @run AND local_account_id = @account
            """);
        command.Parameters.AddWithValue("run", id.Value);
        command.Parameters.AddWithValue("account", account);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        var posted = reader.GetInt64(0);
        var reserved = reader.GetInt64(1);
        Assert.Equal(checked(posted - reserved), reader.GetInt64(2));
        return (posted, reserved);
    }

    private Task<string> SourceFingerprintAsync(ScenarioRunId id) => PaymentKit.Scalar<string>(db.Owner, """
        SELECT encode(sha256(convert_to(jsonb_build_array(
          (SELECT to_jsonb(c) FROM revenant.run_cursor c WHERE scenario_run_id = @run),
          (SELECT to_jsonb(s) FROM revenant.v4_scheduler_run s WHERE run_id = @run),
          (SELECT jsonb_agg(f ORDER BY fact_seq) FROM revenant.finance_fact f WHERE scenario_run_id = @run),
          (SELECT jsonb_agg(a ORDER BY local_account_id) FROM revenant.account_available_balance a WHERE scenario_run_id = @run),
          (SELECT jsonb_agg(p ORDER BY intent_id) FROM revenant.payment p WHERE scenario_run_id = @run),
          (SELECT jsonb_agg(h ORDER BY id) FROM revenant.hold h WHERE scenario_run_id = @run),
          (SELECT jsonb_agg(c ORDER BY id) FROM revenant.review_case c WHERE scenario_run_id = @run),
          (SELECT jsonb_agg(g ORDER BY id) FROM revenant.ledger_group g WHERE scenario_run_id = @run),
          (SELECT jsonb_agg(e ORDER BY id) FROM revenant.ledger_entry e WHERE scenario_run_id = @run),
          (SELECT jsonb_agg(q ORDER BY command_seq) FROM revenant.v4_scheduled_command q WHERE run_id = @run)
        )::text, 'UTF8')), 'hex')
        """, ("run", id.Value));

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
