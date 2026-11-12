using Marrow.Infrastructure.Timeline;
using Marrow.Kernel.Canonical;
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
        Assert.Equal(childHold.Capacity.Eligible + 1, coldChild.FinalState.ReviewCounts.Eligible);
        Assert.Equal(decisions[1].NextCounts, coldChild.FinalState.ReviewCounts);
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
