using Marrow.Infrastructure.Timeline;
using Marrow.Infrastructure.Messaging;
using Marrow.Kernel.Canonical;
using Marrow.Kernel.Messaging;
using Marrow.Timeline;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Revenant.Cases;
using Revenant.Contracts;
using Revenant.Finance;
using Revenant.Intelligence;
using Revenant.Observations;
using Revenant.Payments;
using Revenant.Scenario;
using Revenant.Synthetic;
using Revenant.Tests.Ledger;
using Revenant.Timeline;
using Xunit;
using Xunit.Abstractions;
using LedgerSql = Revenant.Tests.Ledger.Ledger;

namespace Revenant.Tests.Payments;

[Collection(PaymentCollection.Name)]
public sealed class V4ModelForkPostgresTests(LedgerDatabase db, ITestOutputHelper output)
{
    private const string Tenant = PaymentKit.TenantA;
    private const string Owner = PaymentKit.Operator;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ModelRequired_fork_inherits_capacity_and_scores_its_own_suffix()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = timeout.Token;
        var logins = await db.ProvisionDeployedLoginsAsync();
        await using var host = TimelineHost(logins.Worker);
        var engine = Engine(host);
        await using var writer = NpgsqlDataSource.Create(logins.WorkerLedger);
        var ledger = PaymentKit.Store(writer);
        var model = new Lazy<Marrow.Kernel.Results.Result<BehavioralRiskScorerV2, BehavioralRiskFailureV2>>(() =>
            BehavioralRiskScorerV2.Load(Path.Combine(AppContext.BaseDirectory, "Intelligence", "Artifacts",
                BehavioralRiskScorerV2.ArtifactDirectory), BehavioralRiskScorerV2.FeatureSchema, BehavioralRiskScorerV2.PublishedPin));
        var store = new V4SimulationStore(writer, ledger: ledger, model: model);
        var execution = new V4BranchExecution(store, engine);
        var sourceId = new ScenarioRunId(Guid.NewGuid());
        var childId = new ScenarioRunId(Guid.NewGuid());
        var config = V4EpisodeConfig.RiskRingChallenge;
        var plan = V4EpisodePlanner.Plan(config, config.Roots().Single(g => g.Ordinal == 1), sourceId).Financial;
        var source = await new V4EpisodeExecutor(store, execution, ledger)
            .ExecuteAsync(Tenant, Owner, plan, ct, ObservedRiskMode.ModelRequired);
        var segment = Assert.Single(source.History.Segments);
        var hold = Assert.Single(segment.Facts.Where(f => f.Input.Kind == ObservedFactKind.Screening)
            .Select(f => V4ModelScreeningEvidence.Decode(f.Input)), e => e.Admitted);
        var forkFact = segment.Facts.First(f => f.Input.Sequence > hold.Sequence && f.Input.Kind == ObservedFactKind.ObservationSeal);
        var boundary = forkFact.Input.EffectiveTimeMs;
        var nextSource = segment.Facts.First(f => f.Input.Sequence > forkFact.Input.Sequence && f.Input.Kind == ObservedFactKind.PaymentIntent).Input;
        var at = nextSource.EffectiveTimeMs;
        var original = PaymentIntentCodec.Instance.Decode(nextSource.Payload.AsSpan());
        var sourceEvidence = segment.Facts.Where(f => f.Input.Kind == ObservedFactKind.Screening)
            .Select(f => V4ModelScreeningEvidence.Decode(f.Input)).Single(e => e.IntentId == original.IntentId);
        var sourceBinding = await store.ReadBindingAsync(Tenant, sourceId, ct);
        var sourceFinal = (await engine.ReplayAsync(sourceBinding.TimelineId, ct)).Value;
        Assert.Equal(HistoryStatus.Verified, sourceFinal.Status);
        Assert.True(sourceFinal.FinalPosition > forkFact.Input.Sequence);
        var before = (await engine.RestoreAsync(sourceBinding.TimelineId, forkFact.Input.Sequence, ct)).Value;
        Assert.True(before.State.ReviewCounts.Learned >= 1);
        var sourceSql = await FingerprintAsync(sourceId);
        var childContext = new ObservedFinanceContext(segment.Context.ScenarioVersion, segment.Context.ManifestSha256,
            segment.Context.OriginMs, PolicyCatalog.RefOf(PolicyCatalog.V8), segment.Context.ReviewPolicy,
            segment.Context.RiskMode, segment.Context.RiskFeatureSchema, segment.Context.RiskModel, [], []);

        var binding = await execution.ForkAsync(Tenant, Owner, plan.Opening, sourceId, childId, boundary, "r6-model-fork", childContext, ct);
        Assert.Equal(sourceId, binding.LineageRunId);
        Assert.Equal(childId, binding.ExecutionRunId);
        Assert.NotEqual(sourceBinding.TimelineId, binding.TimelineId);
        var inherited = (await engine.RestoreAsync(binding.TimelineId, forkFact.Input.Sequence, ct)).Value;
        Assert.Equal(before.StateHash, inherited.StateHash);
        Assert.Equal(ObservedFinanceStateCodec.Instance.EncodeCanonical(before.State),
            ObservedFinanceStateCodec.Instance.EncodeCanonical(inherited.State));
        Assert.Equal(before.State.ReviewCounts, inherited.State.ReviewCounts);
        Assert.Equal(before.State.PrefixDigest, binding.Fork!.PrefixDigest);
        Assert.Equal(segment.Context.RiskModel, (await store.ReadPinnedContextAsync(Tenant, childId, ct)).RiskModel);
        Assert.Equal(0, await PaymentKit.Count(db.Owner, "finance_fact", childId.Value));
        var wrongTenant = await Assert.ThrowsAsync<V4ScheduleException>(() => store.ReadBindingAsync(PaymentKit.TenantB, childId, ct));
        Assert.Equal(V4ScheduleError.RunNotFound, wrongTenant.Error);
        await using (var replay = await db.Replay.OpenConnectionAsync(ct))
        await using (var tx = await LedgerSql.BeginTenantAsync(replay, Tenant))
        {
            var denied = await Assert.ThrowsAsync<PostgresException>(() => LedgerSql.ExecAsync(replay, """
                UPDATE revenant.finance_fact SET evidence_sha256 = evidence_sha256 WHERE scenario_run_id = @run
                """, ("run", sourceId.Value)));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
        }
        var sourceCase = await ledger.GetCaseAsync(Tenant, sourceId, hold.IntentId, ct);
        var inheritedCase = await ledger.GetCaseAsync(Tenant, childId, hold.IntentId, ct);
        Assert.NotNull(sourceCase);
        Assert.NotNull(inheritedCase);
        Assert.NotEqual(sourceCase.CaseId, inheritedCase.CaseId);
        Assert.Equal(CaseStatus.Open, inheritedCase.Status);

        var highAmount = ReviewPolicy.HighValueThresholdMinor;
        Assert.True(before.State.Ledger.Accounts[original.SourceAccountId].AvailableMinor >= highAmount);
        var branchOnly = new PaymentIntent(sourceId, "r6-child-review", before.State.Ledger.LastIntentSequence + 1, at,
            original.SourceBankId, original.SourceAccountId, original.DestBankId, original.DestAccountId, Money.Usd(highAmount),
            SyntheticTruth.Legitimate, new(true, false));
        var continued = new PaymentIntent(sourceId, original.IntentId, branchOnly.Sequence + 1, at, original.SourceBankId,
            original.SourceAccountId, original.DestBankId, original.DestAccountId, original.Amount, original.Truth, original.Features);
        await Dispatch(Intake(branchOnly));
        var screening = Opportunity(branchOnly.IntentId, at, ObservedFactKind.Screening);
        var duplicate = screening with { Key = screening.Key + "duplicate" };
        _ = await store.AdmitAsync(Tenant, childId, screening, ct);
        _ = await store.AdmitAsync(Tenant, childId, duplicate, ct);
        var competing = new V4SimulationStore(writer, ledger: PaymentKit.Store(writer), model: model);
        var race = await Task.WhenAll(store.AdvanceOneStepAsync(Tenant, childId, ct), competing.AdvanceOneStepAsync(Tenant, childId, ct));
        Assert.Single(race, step => step.Command?.OutcomeCode == nameof(PaymentStatus.Held));
        Assert.Single(race, step => step.Command?.OutcomeCode == "payment-not-applicable");
        Assert.Equal(V4CommandStatus.Completed, (await store.AdmitAsync(Tenant, childId, screening, ct)).Status);
        await Dispatch(Intake(continued));
        await Dispatch(Opportunity(continued.IntentId, at, ObservedFactKind.Screening));

        var suffix = await store.ReadFinanceHistoryAsync(Tenant, childId, sourceId, ct);
        var decisions = suffix.Facts.Where(f => f.Input.Kind == ObservedFactKind.Screening)
            .Select(f => V4ModelScreeningEvidence.Decode(f.Input)).ToArray();
        Assert.Equal(2, decisions.Length);
        var childHold = decisions[0];
        Assert.Equal(V4ScreeningResult.BaseRuleHold, childHold.Decision);
        Assert.Equal(PaymentReason.NewBeneficiaryAmountRule, childHold.BaseReason);
        Assert.Equal(PolicyCatalog.RefOf(PolicyCatalog.V8), childHold.BasePolicy);
        Assert.Equal(before.State.ReviewCounts.Eligible + 1, childHold.Capacity.Eligible);
        Assert.Equal(before.State.ReviewCounts.Learned, childHold.Capacity.Learned);
        Assert.Equal(before.State.ReviewCounts.Mandatory + 1, decisions[1].Capacity.Mandatory);
        Assert.All(decisions, e =>
        {
            Assert.Equal(sourceId, e.RunId);
            Assert.Equal(BehavioralRiskScorerV2.PublishedPin, e.Model);
            Assert.Equal(model.Value.Value.Score(e.Vector.AsSpan()).Value.RiskPpm, e.Ppm);
        });
        Assert.NotEqual(sourceEvidence.VectorDigest, decisions[1].VectorDigest);
        Assert.NotEqual(sourceEvidence.PriorDigest, decisions[1].PriorDigest);
        Assert.Equal(PaymentStatus.Held, (await ledger.GetPaymentAsync(Tenant, childId, branchOnly.IntentId, ct))!.Status);
        Assert.Null(await ledger.GetPaymentAsync(Tenant, sourceId, branchOnly.IntentId, ct));
        var open = await execution.SynchronizeAsync(Tenant, childId, ct);
        var insufficient = new PaymentIntent(sourceId, "r6-child-insufficient", continued.Sequence + 1, at,
            original.SourceBankId, original.SourceAccountId, original.DestBankId, original.DestAccountId,
            Money.Usd(open.Ledger.Accounts[original.SourceAccountId].PostedMinor + 1), SyntheticTruth.Legitimate, new(true, false));
        await Dispatch(Intake(insufficient));
        Assert.Equal(nameof(PaymentStatus.Rejected),
            (await Dispatch(Opportunity(insufficient.IntentId, at, ObservedFactKind.Screening))).OutcomeCode);
        var rejectedFact = (await store.ReadFinanceHistoryAsync(Tenant, childId, sourceId, ct)).Facts[^1].Input;
        var rejected = V4ModelScreeningEvidence.Decode(rejectedFact);
        Assert.Equal(V4ScreeningResult.BaseRuleRejection, rejected.Decision);
        Assert.False(rejected.OpensCase || rejected.Admitted);
        Assert.Null(await ledger.GetCaseAsync(Tenant, childId, insufficient.IntentId, ct));
        Assert.Equal(open.ReviewCounts with { Eligible = open.ReviewCounts.Eligible + 1 }, rejected.NextCounts);

        var heldIntent = branchOnly.IntentId;
        Assert.Equal(V4CommandStatus.Cancelled, (await Dispatch(Opportunity(heldIntent, at, ObservedFactKind.Settlement, "denied"))).Status);
        Assert.True(await ledger.GrantRunStaffAsync(childId, PaymentKit.Supervisor, "supervisor", ct));
        Assert.Equal(V4CommandStatus.Completed, (await Dispatch(Human(heldIntent, at, ObservedFactKind.ReviewDecision,
            "first", Owner, 1, CaseAction.Approve))).Status);
        Assert.Equal(CaseStatus.AwaitingSecondApproval, (await ledger.GetCaseAsync(Tenant, childId, heldIntent, ct))!.Status);
        var repeated = await Dispatch(Human(heldIntent, at, ObservedFactKind.ReviewDecision, "repeat", Owner, 2, CaseAction.Approve));
        Assert.Equal("inapplicable-DistinctSupervisorRequired", repeated.OutcomeCode);
        Assert.Equal(V4CommandStatus.Completed, (await Dispatch(Human(heldIntent, at, ObservedFactKind.ReviewDecision,
            "second", PaymentKit.Supervisor, 2, CaseAction.Approve))).Status);
        Assert.Equal(nameof(PaymentStatus.Settled), (await Dispatch(Opportunity(heldIntent, at, ObservedFactKind.Settlement, "approved"))).OutcomeCode);
        var settled = (await ledger.GetPaymentAsync(Tenant, childId, heldIntent, ct))!;
        Assert.Equal(nameof(PaymentStatus.Returned), (await Dispatch(Human(heldIntent, at, ObservedFactKind.ReturnInstruction,
            "return", Owner, settled.Version, CaseAction.Assign))).OutcomeCode);
        var returned = (await ledger.GetPaymentAsync(Tenant, childId, heldIntent, ct))!;
        Assert.Equal(V4CommandStatus.Cancelled, (await Dispatch(Human(heldIntent, at, ObservedFactKind.ReturnInstruction,
            "return-again", Owner, returned.Version, CaseAction.Assign))).Status);
        Assert.Equal(V4CommandStatus.Completed, (await Dispatch(Human(hold.IntentId, at, ObservedFactKind.ReviewDecision,
            "reject", Owner, inheritedCase.Version, CaseAction.Reject))).Status);
        Assert.Equal(PaymentStatus.Rejected, (await ledger.GetPaymentAsync(Tenant, childId, hold.IntentId, ct))!.Status);
        Assert.Equal(PaymentStatus.Held, (await ledger.GetPaymentAsync(Tenant, sourceId, hold.IntentId, ct))!.Status);
        var next = ObservedFinanceVersion.NextBoundary(at - at % ObservedFinanceVersion.BucketMs);
        Assert.Equal(V4StepKind.Sealed, (await store.SealAsync(Tenant, childId, next, ct)).Kind);
        var childFinal = await execution.CompleteAsync(Tenant, childId, ct);
        Assert.Equal(HistoryStatus.Verified, childFinal.Status);
        var branchHistory = await execution.ReadProjectionHistoryAsync(Tenant, childId, ct);
        var observations = V4ObservationProjector.Project(branchHistory, captureRisk: true);
        Assert.Equal(decisions[1].VectorDigest, observations.RiskAsOf.Single(r => r.IntentId == continued.IntentId).Vector.Digest);

        await using var coldHost = TimelineHost(logins.Worker);
        var cold = Engine(coldHost);
        var coldSource = (await cold.ReplayAsync(sourceBinding.TimelineId, ct)).Value;
        var coldChild = (await cold.ReplayAsync(binding.TimelineId, ct)).Value;
        Assert.Equal(sourceSql, await FingerprintAsync(sourceId));
        Assert.Equal(sourceFinal.LastRecordHash, coldSource.LastRecordHash);
        Assert.Equal(sourceFinal.FinalStateHash, coldSource.FinalStateHash);
        Assert.Equal(childFinal.FinalStateHash, coldChild.FinalStateHash);
        Assert.Equal(PaymentStatus.Returned, coldChild.FinalState.Ledger.Payments[heldIntent].Status);
        Assert.Equal(0, coldChild.FinalState.Ledger.Payments[hold.IntentId].ReservedMinor);
        Assert.Equal(childHold.Capacity.Eligible + 2, coldChild.FinalState.ReviewCounts.Eligible);
        Assert.Equal(rejected.NextCounts, coldChild.FinalState.ReviewCounts);
        output.WriteLine($"fork={forkFact.Input.Sequence} checkpoint={inherited.StateHash} sourceSql={sourceSql} "
            + $"sourceRecord={coldSource.LastRecordHash} childState={coldChild.FinalStateHash} capacity={inherited.State.ReviewCounts} "
            + $"sourceVector={sourceEvidence.VectorDigest} childVector={decisions[1].VectorDigest} childPpm={decisions[1].Ppm} "
            + $"releasedAndReturned={highAmount} rejectedInheritedPrincipal={inheritedCase.AmountMinor}");

        async Task<V4ScheduledCommand> Dispatch(V4CommandRequest request)
        {
            _ = await store.AdmitAsync(Tenant, childId, request, ct);
            V4Step step;
            do step = await store.AdvanceOneStepAsync(Tenant, childId, ct);
            while (step.Kind == V4StepKind.Sealed);
            Assert.Equal(V4StepKind.ProcessedCommand, step.Kind);
            Assert.NotNull(step.Command);
            Assert.Equal(request.Key, step.Command.Request.Key);
            return step.Command;
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Competing_model_screenings_consume_the_last_slot_once()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = timeout.Token;
        var logins = await db.ProvisionDeployedLoginsAsync();
        await using var host = TimelineHost(logins.Worker);
        await using var writer = NpgsqlDataSource.Create(logins.WorkerLedger);
        var ledger = PaymentKit.Store(writer);
        var model = new Lazy<Marrow.Kernel.Results.Result<BehavioralRiskScorerV2, BehavioralRiskFailureV2>>(() =>
            BehavioralRiskScorerV2.Load(Path.Combine(AppContext.BaseDirectory, "Intelligence", "Artifacts",
                BehavioralRiskScorerV2.ArtifactDirectory), BehavioralRiskScorerV2.FeatureSchema, BehavioralRiskScorerV2.PublishedPin));
        var store = new V4SimulationStore(writer, ledger: ledger, model: model);
        var branch = new V4BranchExecution(store, Engine(host));
        var id = new ScenarioRunId(Guid.NewGuid());
        var config = V4EpisodeConfig.RiskRingChallenge;
        var plan = V4EpisodePlanner.Plan(config, config.Roots().Single(g => g.Ordinal == 1), id).Financial;
        var first = plan.Payments.Single(p => p.Intent.IntentId == "P-00000054").Intent;
        var at = first.EventTimeUtcMs;
        var boundary = at - at % ObservedFinanceVersion.BucketMs;
        var pre = plan with { Payments = [.. plan.Payments.TakeWhile(p => p.Intent.IntentId != first.IntentId)] };
        _ = await new V4EpisodeExecutor(store, branch, ledger).ExecuteAsync(Tenant, Owner, pre, ct,
            ObservedRiskMode.ModelRequired, complete: false, sealThrough: boundary);
        await IntakeAsync(first);
        var state = await branch.SynchronizeAsync(Tenant, id, ct);
        Assert.Equal(new V4ReviewCounts(27, 0, 0), state.ReviewCounts);
        var history = await branch.ReadProjectionHistoryAsync(Tenant, id, ct);
        var segment = Assert.Single(history.Segments);

        // Pick a distinct, funded payer for the same busy beneficiary. The actual model scores the
        // prospective prefixes in the test. Both commands below still run inference and admission in SQL.
        PaymentIntent? second = null;
        foreach (var account in state.Ledger.Accounts.Values)
        {
            if (account.AccountId == first.SourceAccountId || account.AccountId == first.DestAccountId)
                continue;
            foreach (var amount in new[] { first.Amount.MinorUnits, 64_000L, 32_000L, 99_000L })
            {
                if (account.AvailableMinor < amount || amount >= PolicyCatalog.V7.VelocityAmountFloorMinor)
                    continue;
                var candidate = new PaymentIntent(id, "r6-concurrent-second", first.Sequence + 1, at,
                    account.BankId, account.AccountId, first.DestBankId, first.DestAccountId, Money.Usd(amount),
                    SyntheticTruth.Legitimate, first.Features);
                var proposed = new ObservedFinanceInput(id, state.LastFact + 1, at, ObservedFactKind.PaymentIntent,
                    ScenarioSchemas.InputSchemaVersion, [.. PaymentIntentCodec.Instance.EncodeCanonical(candidate)]);
                var added = ObservedFinanceReducer.Apply(state, proposed, segment.Context);
                if (added.IsFailure) continue;
                var facts = segment.Facts.Append(new(proposed, added.Value.PrefixDigest)).ToArray();
                var beforeFirst = history with { Segments = [segment with { Facts = facts, LastFact = proposed.Sequence }] };
                var firstVector = V4ObservationProjector.RiskAt(beforeFirst, first.IntentId, at).Vector;
                var firstScore = model.Value.Value.Score(firstVector).Value;
                if (!firstScore.AtOrAboveThreshold) continue;
                var admitted = V4ModelScreeningEvidence.Create(added.Value, segment.Context, first.IntentId, at,
                    [.. firstVector.Encode()], BitConverter.SingleToInt32Bits(firstScore.RawScore), firstScore.RiskPpm);
                if (!admitted.Admitted) continue;
                var firstFact = admitted.ToInput();
                var firstHeld = ObservedFinanceReducer.Apply(added.Value, firstFact, segment.Context).Value;
                var beforeSecond = history with
                {
                    Segments = [segment with { Facts = [.. facts, new(firstFact, firstHeld.PrefixDigest)], LastFact = firstFact.Sequence }],
                };
                var secondVector = V4ObservationProjector.RiskAt(beforeSecond, candidate.IntentId, at).Vector;
                if (!model.Value.Value.Score(secondVector).Value.AtOrAboveThreshold) continue;
                second = candidate;
                break;
            }
            if (second is not null) break;
        }
        Assert.NotNull(second);
        await IntakeAsync(second);
        var firstRequest = Opportunity(first.IntentId, at, ObservedFactKind.Screening);
        var secondRequest = Opportunity(second.IntentId, at, ObservedFactKind.Screening);
        _ = await store.AdmitAsync(Tenant, id, firstRequest, ct);
        _ = await store.AdmitAsync(Tenant, id, secondRequest, ct);
        var reserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new V4SimulationStore(writer, ledger: PaymentKit.Store(writer, tx =>
            new BarrierOutbox(new PostgresOutboxWriter(tx, new MessagingTables("revenant")), reserved, release)), model: model);
        var firstTask = worker.AdvanceOneStepAsync(Tenant, id, ct);
        Task<V4Step> secondTask;
        try
        {
            await reserved.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
            secondTask = store.AdvanceOneStepAsync(Tenant, id, ct);
        }
        finally
        {
            release.TrySetResult();
        }
        var steps = await Task.WhenAll(firstTask, secondTask);
        Assert.Equal(nameof(PaymentStatus.Held), steps[0].Command?.OutcomeCode);
        Assert.Equal(nameof(PaymentStatus.Cleared), steps[1].Command?.OutcomeCode);
        var historyAfter = await store.ReadFinanceHistoryAsync(Tenant, id, id, ct);
        var firstEvidence = V4ModelScreeningEvidence.Decode(historyAfter.Facts[^2].Input);
        var secondEvidence = V4ModelScreeningEvidence.Decode(historyAfter.Facts[^1].Input);
        Assert.True(firstEvidence.AboveThreshold && firstEvidence.Admitted);
        Assert.True(secondEvidence.AboveThreshold && secondEvidence.Suppressed);
        Assert.Equal(firstEvidence.Sequence, secondEvidence.PriorSequence);
        Assert.Equal(historyAfter.Facts[^2].PrefixDigest, secondEvidence.PriorDigest);
        Assert.Equal(new V4ReviewCounts(29, 0, 1), secondEvidence.NextCounts);
        Assert.Equal(0, secondEvidence.Available);
        Assert.NotNull(await ledger.GetCaseAsync(Tenant, id, first.IntentId, ct));
        Assert.Null(await ledger.GetCaseAsync(Tenant, id, second.IntentId, ct));
        Assert.Equal(V4CommandStatus.Completed, (await store.AdmitAsync(Tenant, id, firstRequest, ct)).Status);
        Assert.Equal(historyAfter.LastFact, (await store.ReadFinanceHistoryAsync(Tenant, id, id, ct)).LastFact);
        Assert.Equal(V4StepKind.Sealed, (await store.SealAsync(Tenant, id, ObservedFinanceVersion.NextBoundary(boundary), ct)).Kind);
        Assert.Equal(HistoryStatus.Verified, (await branch.CompleteAsync(Tenant, id, ct)).Status);
        output.WriteLine($"eligible=28/29 slots=1 ppms={firstEvidence.Ppm}/{secondEvidence.Ppm} "
            + $"admitted={firstEvidence.IntentId} suppressed={secondEvidence.IntentId} reserved={first.Amount.MinorUnits}");

        async Task IntakeAsync(PaymentIntent intent)
        {
            var request = Intake(intent);
            _ = await store.AdmitAsync(Tenant, id, request, ct);
            var step = await store.AdvanceOneStepAsync(Tenant, id, ct);
            Assert.Equal(request.Key, step.Command?.Request.Key);
            Assert.Equal(V4CommandStatus.Completed, step.Command?.Status);
        }
    }

    private sealed class BarrierOutbox(IOutboxWriter writer, TaskCompletionSource reserved, TaskCompletionSource release) : IOutboxWriter
    {
        public async Task EnqueueAsync(OutboxMessage message, CancellationToken ct)
        {
            if (message.EventType == PaymentLedgerStore.EventHoldReserved)
            {
                reserved.TrySetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
            }
            await writer.EnqueueAsync(message, ct);
        }
    }

    private static V4CommandRequest Intake(PaymentIntent p) =>
        new(p.IntentId + ":intake", V4CommandClass.Exogenous, nameof(ObservedFactKind.PaymentIntent),
            ScenarioSchemas.InputSchemaVersion, [.. PaymentIntentCodec.Instance.EncodeCanonical(p)], p.EventTimeUtcMs, p.EventTimeUtcMs);

    private static V4CommandRequest Opportunity(string intent, long at, ObservedFactKind kind, string key = "") =>
        new($"{intent}:{kind}:{key}", V4CommandClass.DerivedOpportunity, kind.ToString(), "v4-payment-opportunity/1",
            [.. new CanonicalWriter().WriteInt32(1).WriteString(intent).ToArray()], at, at);

    private static V4CommandRequest Human(string intent, long at, ObservedFactKind kind, string key, string actor, long version, CaseAction action) =>
        new($"{intent}:{kind}:{key}", V4CommandClass.ConditionalHumanRequest, kind.ToString(), "v4-human-request/1",
            [.. new CanonicalWriter().WriteInt32(1).WriteString(intent).WriteString(actor).WriteInt64(version)
                .WriteBoolean(true).WriteByte((byte)action).ToArray()], at, at);

    private static ServiceProvider TimelineHost(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        services.AddMarrowTimeline(ObservedFinanceTimelineVersions.All, ObservedFinanceTimelineVersions.CheckpointInterval);
        return services.BuildServiceProvider();
    }

    private static ITimelineEngine<ObservedFinanceState, ObservedFinanceInput, ObservedFinanceContext> Engine(ServiceProvider host) =>
        host.GetRequiredService<ITimelineEngine<ObservedFinanceState, ObservedFinanceInput, ObservedFinanceContext>>();

    private Task<string> FingerprintAsync(ScenarioRunId run) => PaymentKit.Scalar<string>(db.Owner, """
        SELECT encode(sha256(convert_to(jsonb_build_array(
          (SELECT to_jsonb(c) FROM revenant.run_cursor c WHERE scenario_run_id = @run),
          (SELECT jsonb_agg(f ORDER BY fact_seq) FROM revenant.finance_fact f WHERE scenario_run_id = @run),
          (SELECT jsonb_agg(p ORDER BY id) FROM revenant.payment p WHERE scenario_run_id = @run),
          (SELECT jsonb_agg(h ORDER BY id) FROM revenant.hold h WHERE scenario_run_id = @run),
          (SELECT jsonb_agg(c ORDER BY id) FROM revenant.review_case c WHERE scenario_run_id = @run),
          (SELECT jsonb_agg(a ORDER BY local_account_id) FROM revenant.account_available_balance a WHERE scenario_run_id = @run)
        )::text, 'UTF8')), 'hex')
        """, ("run", run.Value));
}
