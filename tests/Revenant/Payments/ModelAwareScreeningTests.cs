using System.Text.Json;
using Marrow.Infrastructure.Messaging;
using Marrow.Infrastructure.Timeline;
using Marrow.Kernel.Results;
using Marrow.Timeline;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Revenant.Api;
using Revenant.Cases;
using Revenant.Contracts;
using Revenant.Finance;
using Revenant.Intelligence;
using Revenant.Payments;
using Revenant.Projection;
using Revenant.Scenario;
using Revenant.Tests.Ledger;
using Revenant.Timeline;
using Xunit;
using Xunit.Abstractions;

namespace Revenant.Tests.Payments;

/// <summary>
/// Spec 08 task 8.3 over real PostgreSQL with the deployed runtime logins: a model-aware (V3) run screened by the real Worker
/// registration of <see cref="ScreeningWorker"/>, with the committed, verified FastTree artifact loaded through
/// <see cref="RiskModelProvider"/>. Every score below is produced by that artifact from the committed pre-screening facts; the test
/// records them and asserts only the decision rule, the ledger consequences and replay parity.
/// **Validates: Requirements R08.02, R08.08, R08.10, R08.11**
/// </summary>
[Collection(PaymentCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ModelAwareScreeningTests(LedgerDatabase db, ITestOutputHelper output)
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private const string Owner = PaymentKit.Operator;
    private const string Reviewer = "staff:reviewer-83";
    private const string Supervisor = "staff:supervisor-83";

    [Fact]
    public async Task A_learned_score_holds_a_base_clear_payment_in_a_live_V3_run_that_then_settles_after_review_and_verifies_after_crash_and_cold_replay()
    {
        var logins = await db.ProvisionDeployedLoginsAsync();
        var tenant = $"t83-{Guid.NewGuid():N}"[..16];
        var runId = new ScenarioRunId(Guid.NewGuid());
        var ownerActor = TestActor.Staff(Owner, "operator", tenant);
        var reviewer = TestActor.Staff(Reviewer, "operator", tenant);
        var supervisor = TestActor.Staff(Supervisor, "supervisor", tenant);
        var outsider = TestActor.Staff("staff:outsider-83", "supervisor", tenant);

        await using var live = LiveOpsHost(logins, tenant);
        var service = live.GetRequiredService<RevenantLedgerService>();
        var store = live.GetRequiredService<PaymentLedgerStore>();
        var opened = Ok(await service.OpenRunAsync(ownerActor, 42, runId.Value, Ct, modelAware: true));
        Assert.Equal(ModelLedgerVersion.ReducerVersion, opened.Run.FinanceVersion);
        // The family is fixed at opening: the same run id cannot be re-opened as rules-only.
        Assert.Equal(PaymentFailureCode.ConflictingDuplicate, (await service.OpenRunAsync(ownerActor, 42, runId.Value, Ct)).Error.Code);
        Assert.True(await store.GrantRunStaffAsync(runId, Reviewer, "operator", Ct));
        Assert.True(await store.GrantRunStaffAsync(runId, Supervisor, "supervisor", Ct));
        Assert.True(store.ModelAvailability().IsSuccess);

        // Phase 1: the real screening worker only (no settlement yet), over the 90 generated Friday Ring intents.
        var screening = live.GetRequiredService<ScreeningWorker>();
        var settlement = live.GetRequiredService<SettlementWorker>();
        await screening.StartAsync(Ct);
        var intents = PaymentKit.Intents(runId);
        foreach (var intent in intents)
        {
            Ok(await service.SubmitPaymentAsync(ownerActor, runId, Request(intent), $"intake-{intent.Sequence}", Ct));
        }
        await WaitAsync(async () => await StatusCount(runId.Value, "Initiated") == 0, "screening of the generated intents");

        var table = await DecisionTableAsync(runId.Value);
        Assert.Equal(90, intents.Length);
        var generated = intents.ToDictionary(i => i.IntentId, StringComparer.Ordinal);
        Assert.Equal(generated.Keys.Order(StringComparer.Ordinal), table.Select(r => r.IntentId).Order(StringComparer.Ordinal));
        Assert.Equal(60, table.Count(row => row.Truth == SyntheticTruth.Legitimate));
        Assert.Equal(30, table.Count(row => row.Truth == SyntheticTruth.FraudMuleRing));
        var scorer = store.ModelAvailability().Value;
        output.WriteLine($"threshold {RiskPolicyCatalog.V1.Model.ThresholdPpm} PPM; generated Friday Ring payments (screened before any settlement):");
        foreach (var row in table.OrderBy(r => r.IntentId, StringComparer.Ordinal))
        {
            var e = row.Evidence;
            Assert.True(e.CheckBinds(generated[row.IntentId]).IsSuccess);
            Assert.Equal(generated[row.IntentId].Truth, row.Truth);
            Assert.Equal(scorer.Identity, new RiskModelIdentity(e.ModelId, e.ModelArtifactSha256, e.ModelManifestSha256,
                e.MlNetPackageVersion, e.FeatureSchemaId, e.ThresholdPpm));
            Assert.Equal(RiskPolicyCatalog.V1.PolicyId, e.PolicyId);
            Assert.Equal(RiskScoreNormalization.NormalizationId, e.NormalizationId);
            Assert.Equal(row.Score, scorer.Score(RiskFeaturesV1.Vector(e.Features)).Value);
            output.WriteLine(JsonSerializer.Serialize(new
            {
                Seed = 42, e.RunId, e.IntentId, Truth = row.Truth.ToString(), row.Amount, row.PostedBefore, e.AsOfFactSeq,
                e.ScreeningFactSeq, e.FeatureHash, e.ModelId, e.ModelArtifactSha256, e.ModelManifestSha256, e.MlNetPackageVersion,
                e.FeatureSchemaId, e.PolicyId, e.NormalizationId, e.ScorePpm, e.ThresholdPpm,
                BaseDecision = e.BaseDecision.ToString(), FinalDecision = e.FinalDecision.ToString(),
            }));
            AssertRule(row);
        }
        var histogram = table.GroupBy(r => r.Score).OrderBy(g => g.Key)
            .Select(g => new { ScorePpm = g.Key, Count = g.Count(), Legitimate = g.Count(r => r.Truth == SyntheticTruth.Legitimate),
                Mule = g.Count(r => r.Truth == SyntheticTruth.FraudMuleRing) }).ToArray();
        Assert.Equal(90, histogram.Sum(b => b.Count));
        Assert.Equal(60, histogram.Sum(b => b.Legitimate));
        Assert.Equal(30, histogram.Sum(b => b.Mule));
        output.WriteLine(JsonSerializer.Serialize(histogram));
        var fr41 = Assert.Single(table, r => r.IntentId == "FR-0041").Evidence;
        Assert.Equal((143_381L, 1_000_000L), (fr41.Features.AmountMinor, fr41.Features.SourcePostedBeforeMinor));
        Assert.Equal("a36f27bb806a02c8648134bd5390dca3050d6b5da445746fc692f9cccab77bd3", fr41.FeatureHash);
        Assert.Equal(18_524, fr41.ScorePpm);
        output.WriteLine($"  learned holds among generated: {table.Count(r => r.Base == ScreeningDecisionKind.Clear && r.Final == ScreeningDecisionKind.Held)}");

        // Phase 2: settlement runs too, so posted balances move; then explicitly labelled CONTROLLED intents (not generated by the
        // scenario) exercise each branch of the decision contract against the committed state.
        await settlement.StartAsync(Ct);
        await WaitQuiescentAsync(runId.Value);
        var last = intents[^1];
        var muleA = FridayRingScenario.MuleAccountId(0);
        var muleAPosted = await PostedAsync(runId.Value, muleA);
        output.WriteLine($"mule {muleA} posted after its hop settled: {muleAPosted}");
        PaymentIntent Controlled(string id, int k, int sourceBank, string source, int destBank, string dest, long amount, bool sanctions) =>
            new(runId, id, last.Sequence + k, last.EventTimeUtcMs + (k * 60_000L), FridayRingScenario.BankId(sourceBank), source,
                FridayRingScenario.BankId(destBank), dest, Money.Usd(amount), SyntheticTruth.Legitimate, new PaymentFeatures(false, sanctions));
        // M: a base-clear transfer of most of a drained account's posted balance (no v7 rule applies: no sanctions, velocity 1).
        var m = Controlled("CTL-83-M", 1, 0, muleA, 1, FridayRingScenario.CustomerAccountId(1, 1), 99_000, false);
        // C: a small base-clear transfer from a well-funded customer.
        var c = Controlled("CTL-83-C", 2, 0, FridayRingScenario.CustomerAccountId(0, 1), 1, FridayRingScenario.CustomerAccountId(1, 2), 10_000, false);
        // S: a v7 hard hold (sanctions flag).
        var s = Controlled("CTL-83-S", 3, 2, FridayRingScenario.CustomerAccountId(2, 1), 3, FridayRingScenario.CustomerAccountId(3, 1), 50_000, true);
        foreach (var intent in new[] { m, c, s })
        {
            Ok(await service.SubmitPaymentAsync(ownerActor, runId, Request(intent), $"intake-{intent.IntentId}", Ct));
        }
        await WaitStatusAsync(runId.Value, m.IntentId, PaymentStatus.Held);
        await WaitStatusAsync(runId.Value, s.IntentId, PaymentStatus.Held);
        await WaitStatusAsync(runId.Value, c.IntentId, PaymentStatus.Settled);
        var controlled = (await DecisionTableAsync(runId.Value)).Where(r => r.IntentId.StartsWith("CTL-", StringComparison.Ordinal)).ToDictionary(r => r.IntentId);
        output.WriteLine("controlled intents:");
        foreach (var row in controlled.Values)
        {
            output.WriteLine($"  {row.IntentId} amount {row.Amount} posted-before {row.PostedBefore} base {row.Base} scorePpm {row.Score} final {row.Final} live {row.Status} reason {row.Reason ?? "-"}");
            AssertRule(row);
        }
        var mRow = controlled[m.IntentId];
        Assert.Equal((ScreeningDecisionKind.Clear, ScreeningDecisionKind.Held, PaymentLedgerStore.ReasonMlRiskHold, muleAPosted),
            (mRow.Base, mRow.Final, mRow.Reason, mRow.PostedBefore));
        Assert.True(mRow.Score >= mRow.Threshold);
        Assert.Equal((ScreeningDecisionKind.Clear, ScreeningDecisionKind.Clear, (string?)null), (controlled[c.IntentId].Base, controlled[c.IntentId].Final, controlled[c.IntentId].Reason));
        Assert.True(controlled[c.IntentId].Score < controlled[c.IntentId].Threshold);
        Assert.Equal((ScreeningDecisionKind.Held, ScreeningDecisionKind.Held, "sanctions_hit"), (controlled[s.IntentId].Base, controlled[s.IntentId].Final, controlled[s.IntentId].Reason));
        // The learned hold reserved the amount and opened a normal review case; the score itself releases nothing.
        Assert.Equal(("Active", 99_000L), await HoldAsync(runId.Value, m.IntentId));
        var mCase = Ok(await service.GetCaseAsync(reviewer, runId, m.IntentId, Ct, "m-read"));
        Assert.Equal((CaseStatus.Open, 1), (mCase.Status, mCase.RequiredApprovals));

        // Authorization: a staff member outside the run cannot read or act on the case; another tenant sees none of the facts.
        Assert.Equal(PaymentFailureCode.Forbidden, (await service.GetCaseAsync(outsider, runId, m.IntentId, Ct, "x")).Error.Code);
        Assert.Equal(PaymentFailureCode.Forbidden, (await service.CaseActionAsync(outsider, runId, m.IntentId, CaseAction.Approve, mCase.Version, "x-approve", null, "x", Ct)).Error.Code);
        Assert.Equal(0L, await AsTenantAsync(logins.WorkerLedger, "other-tenant", "SELECT count(*) FROM revenant.finance_fact WHERE scenario_run_id = @run", runId.Value));
        Assert.Empty(await store.ScreenBatchAsync("other-tenant", 5, "x", Ct));

        // Downstream authorized workflow: M is released by its reviewer and settled by the settlement worker; S is rejected.
        Ok(await service.CaseActionAsync(reviewer, runId, m.IntentId, CaseAction.Approve, mCase.Version, "m-approve", null, "m-approve", Ct));
        var sCase = Ok(await service.GetCaseAsync(reviewer, runId, s.IntentId, Ct, "s-read"));
        Ok(await service.CaseActionAsync(reviewer, runId, s.IntentId, CaseAction.Reject, sCase.Version, "s-reject", "sanctions_confirmed", "s-reject", Ct));
        await WaitStatusAsync(runId.Value, m.IntentId, PaymentStatus.Settled);
        await WaitQuiescentAsync(runId.Value);
        Assert.Equal(("Consumed", 99_000L), await HoldAsync(runId.Value, m.IntentId));
        Assert.Equal(("Released", 50_000L), await HoldAsync(runId.Value, s.IntentId));

        // Retries: another screening pass decides nothing again; one Screening fact per payment, each with its evidence.
        Assert.Empty(await store.ScreenBatchAsync(tenant, 10, "again", Ct));
        Assert.Equal((long)intents.Length + 3, await PaymentKit.Scalar<long>(db.Owner,
            "SELECT count(*) FROM revenant.finance_fact WHERE scenario_run_id = @r AND kind = 'Screening' AND evidence_sha256 = canonical_sha256", ("r", runId.Value)));
        // A conflicting second screening of M, with or without evidence, cannot commit (unique screening, evidence shape RV010).
        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => AsTenantAsync(logins.WorkerLedger, tenant,
            """
            INSERT INTO revenant.finance_fact (tenant_id, scenario_run_id, fact_seq, kind, schema_version, payment_id, canonical, canonical_sha256, evidence_schema, evidence_sha256)
            SELECT tenant_id, scenario_run_id, 100000, 'Screening', schema_version, payment_id, canonical, canonical_sha256, evidence_schema, evidence_sha256
              FROM revenant.finance_fact WHERE scenario_run_id = @run AND kind = 'Screening' ORDER BY fact_seq DESC LIMIT 1
            """, runId.Value));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
        var evidenceFree = await Assert.ThrowsAsync<PostgresException>(() => AsTenantAsync(logins.WorkerLedger, tenant,
            """
            INSERT INTO revenant.finance_fact (tenant_id, scenario_run_id, fact_seq, kind, schema_version, payment_id, canonical, canonical_sha256)
            SELECT tenant_id, scenario_run_id, 100000, 'Screening', 'revenant-screening/1', payment_id, canonical, canonical_sha256
              FROM revenant.finance_fact WHERE scenario_run_id = @run AND kind = 'Screening' ORDER BY fact_seq DESC LIMIT 1
            """, runId.Value));
        Assert.Equal("RV010", evidenceFree.SqlState);

        // "Crash" after every live commit and before any Timeline append: the LiveOps services stop, nothing is projected.
        await screening.StopAsync(Ct);
        await settlement.StopAsync(Ct);
        var facts = await FactsAsync(runId.Value);
        Assert.Equal(Enumerable.Range(1, facts.Count).Select(i => (long)i), facts.Select(f => f.Sequence));
        Assert.Equal(0L, await PaymentKit.Scalar<long>(db.Owner,
            "SELECT count(*) FROM marrow_timeline.histories WHERE operation_key = @k", ("k", LedgerProjector.SourceOperationKey(runId))));

        // Projection: one process dies half way, another resumes; close the run; verify; all through the V3 projector.
        var half = facts.Count / 2;
        await using (var p1 = ProjectionHost(logins.Worker))
        {
            var partial = Ok(await p1.GetRequiredService<ModelLedgerProjector>().CatchUpAsync(tenant, runId, Ct, upTo: half));
            Assert.Equal((half, HistoryStatus.Recording), (partial.ProjectedPosition, partial.Status));
            // The rules-only projector refuses a V3 run instead of misreading its evidence facts.
            Assert.Equal(ProjectionErrorCode.NotDurableV2, (await p1.GetRequiredService<LedgerProjector>().CatchUpAsync(tenant, runId, Ct)).Error.Code);
        }
        ModelSourceVerification verified;
        await using (var p2 = ProjectionHost(logins.Worker))
        {
            var projector = p2.GetRequiredService<ModelLedgerProjector>();
            Assert.Equal(facts.Count - half, Ok(await projector.CatchUpAsync(tenant, runId, Ct)).Appended);
            Assert.Equal(0, Ok(await projector.CatchUpAsync(tenant, runId, Ct)).Appended);
            Assert.Equal(ProjectionErrorCode.RunNotClosed, (await projector.VerifySourceAsync(tenant, runId, Ct)).Error.Code);
            Ok(await store.CloseRunAsync(tenant, Owner, runId, "close-83", Ct));
            verified = Ok(await projector.VerifySourceAsync(tenant, runId, Ct));
        }
        foreach (var mm in verified.Mismatches)
        {
            output.WriteLine($"mismatch {mm.Subject}: source {mm.Source} live {mm.Live}");
        }
        Assert.True(verified.Reconciled);
        Assert.Equal(HistoryStatus.Verified, verified.Projection.Status);

        // Cold replay in a process with NO model provider (predictor never invoked): byte-identical to an independent pure fold.
        await using (var cold = ProjectionHost(logins.Worker))
        {
            Assert.Null(cold.GetService<RiskModelProvider>());
            var engine = cold.GetRequiredService<ITimelineEngine<ModelLedgerState, FinanceFact, ModelLedgerContext>>();
            var replay = Ok(await engine.ReplayAsync(verified.Projection.History, Ct));
            Assert.Equal((HistoryStatus.Verified, verified.Projection.FinalStateHash), (replay.Status, replay.FinalStateHash));
            var run = FridayRingScenario.Create(42, ScenarioSchemas.FridayRingVersion, runId).Value;
            var context = ModelLedgerProjector.ContextFor(run).Value;
            var fold = ModelLedgerReducer.ApplyAll(ModelLedgerReducer.Initial(run, context).Value, facts, context).Value;
            Assert.Equal(ModelLedgerStateCodec.Instance.EncodeCanonical(fold), ModelLedgerStateCodec.Instance.EncodeCanonical(replay.FinalState));
            Assert.Equal(intents.Length + 3, fold.Screenings.Count);
            var ms = fold.Screenings[m.IntentId];
            Assert.Equal((mRow.Score, true, ScreeningDecisionKind.Held), (ms.ScorePpm, ms.LearnedHold, ms.FinalDecision));
            Assert.Equal("Initiated>Screened>Held>Approved>Cleared>Settled", string.Join(">", fold.Ledger.Payments[m.IntentId].Trail));
            Assert.Equal(PaymentReason.MlRiskHold, fold.Ledger.Payments[m.IntentId].Reason);
            Assert.Equal(run.InitialState.Accounts.Sum(a => a.OpeningBalance.MinorUnits), fold.Ledger.TotalPostedMinor);
            Assert.Equal(
                $"{ModelLedgerVersion.DomainId}|{ModelLedgerVersion.ReducerVersion}|{ModelLedgerVersion.InputSchemaVersion}|{ModelLedgerVersion.StateSchemaVersion}|{ModelLedgerVersion.ContextSchemaVersion}",
                await VersionKeyAsync(verified.Projection.History.Value));
            await ColdModelReplay.VerifyInChildAsync(logins.Worker, tenant, runId, replay, output);
            // The V2 engine shape cannot open the V3 history.
            var v2 = cold.GetRequiredService<ITimelineEngine<LedgerFinanceState, FinanceFact, LedgerFinanceContext>>();
            Assert.Equal(TimelineErrorCode.IncompatibleVersion, (await v2.RestoreAsync(verified.Projection.History, half, Ct)).Error.Code);
            var restored = Ok(await engine.RestoreAsync(verified.Projection.History, half, Ct));
            Assert.Equal(half, restored.State.LastFact);
        }
    }

    [Theory]
    [InlineData(RiskModelError.ManifestMissing)]
    [InlineData(RiskModelError.ArtifactChecksumMismatch)]
    [InlineData(RiskModelError.ArtifactIoUnavailable)]
    public async Task Unavailable_models_fail_closed_while_the_worker_screens_V2_and_recovers(RiskModelError code)
    {
        var logins = await db.ProvisionDeployedLoginsAsync();
        var tenant = $"t83m-{Guid.NewGuid():N}"[..16];
        var root = Directory.CreateTempSubdirectory("risk-worker-");
        try
        {
            var source = Path.Combine(RiskModelOptions.DefaultArtifactRoot, RiskPolicyCatalog.V1.ArtifactDirectory);
            var target = Directory.CreateDirectory(Path.Combine(root.FullName, RiskPolicyCatalog.V1.ArtifactDirectory));
            var manifest = RiskModelScorer.ManifestFileName;
            var zip = RiskModelScorer.ArtifactFileName;
            var zipPath = Path.Combine(target.FullName, zip);
            if (code != RiskModelError.ManifestMissing)
            {
                File.Copy(Path.Combine(source, manifest), Path.Combine(target.FullName, manifest));
            }
            if (code == RiskModelError.ArtifactChecksumMismatch)
            {
                var bytes = await File.ReadAllBytesAsync(Path.Combine(source, zip), Ct);
                bytes[bytes.Length / 2] ^= 0x01;
                await File.WriteAllBytesAsync(zipPath, bytes, Ct);
            }
            if (code == RiskModelError.ArtifactIoUnavailable)
            {
                Directory.CreateDirectory(zipPath);
            }

            await using var host = LiveOpsHost(logins, tenant, artifactRoot: root.FullName);
            var store = host.GetRequiredService<PaymentLedgerStore>();
            Assert.Equal(code, store.ModelAvailability().Error.Code);
            var v3 = new ScenarioRunId(Guid.NewGuid());
            var v2 = new ScenarioRunId(Guid.NewGuid());
            Ok(await store.OpenRunAsync(tenant, Owner, "operator", 42, v3, Ct, PaymentLedgerStore.ModelAwareFinanceVersion));
            Ok(await store.OpenRunAsync(tenant, Owner, "operator", 42, v2, Ct));
            var intent = PaymentKit.Intents(v3)[0];
            Ok(await store.SubmitIntentAsync(tenant, Owner, intent, "v3-intake", Ct));
            Ok(await store.SubmitIntentAsync(tenant, Owner, PaymentKit.Intents(v2)[0], "v2-intake", Ct));

            var worker = host.GetRequiredService<ScreeningWorker>();
            await worker.StartAsync(Ct);
            try
            {
                await WaitAsync(async () => await StatusCount(v2.Value, "Initiated") == 0, "rules-only screening");
                Assert.Equal(1L, await StatusCount(v3.Value, "Initiated"));
                foreach (var table in new[] { "finance_fact", "payment_transition", "payment_command", "audit_event" })
                {
                    Assert.Equal(1L, await PaymentKit.Count(db.Owner, table, v3.Value));
                }
                foreach (var table in new[] { "hold", "review_case", "ledger_entry" })
                {
                    Assert.Equal(0L, await PaymentKit.Count(db.Owner, table, v3.Value));
                }
                Assert.Equal(1L, await PaymentKit.Scalar<long>(db.Owner,
                    "SELECT count(*) FROM revenant.outbox WHERE payload::jsonb ->> 'runId' = @run", ("run", v3.Value.ToString())));
                Assert.Equal(PaymentFailureCode.RunNotQuiescent, (await store.CloseRunAsync(tenant, Owner, v3, "close-m", Ct)).Error.Code);

                if (code == RiskModelError.ArtifactIoUnavailable)
                {
                    Directory.Delete(zipPath);
                }
                // Publish verified bytes atomically into the test directory while the same Worker/provider keeps running.
                File.Copy(Path.Combine(source, zip), zipPath + ".tmp");
                File.Move(zipPath + ".tmp", zipPath, overwrite: true);
                if (code == RiskModelError.ManifestMissing)
                {
                    File.Copy(Path.Combine(source, manifest), Path.Combine(target.FullName, manifest + ".tmp"));
                    File.Move(Path.Combine(target.FullName, manifest + ".tmp"), Path.Combine(target.FullName, manifest));
                }
                await WaitAsync(async () => await StatusCount(v3.Value, "Initiated") == 0, "restored model screening");
                var decision = Assert.Single(await DecisionTableAsync(v3.Value));
                Assert.Equal(intent.IntentId, decision.IntentId);
                Assert.Equal(RiskPolicyCatalog.V1.Model.ThresholdPpm, decision.Threshold);
                Assert.Equal(RiskPolicyCatalog.V1.Model, store.ModelAvailability().Value.Identity);
            }
            finally
            {
                await worker.StopAsync(Ct);
            }
        }
        finally
        {
            root.Delete(true);
        }
    }

    // ---------------------------------------------------------------------------------------------------------

    private sealed record DecisionRow(string IntentId, SyntheticTruth Truth, long Amount, long PostedBefore, ScreeningDecisionKind Base, int Score,
        int Threshold, ScreeningDecisionKind Final, string Status, string? Reason, InferenceEvidenceV1 Evidence);

    private static void AssertRule(DecisionRow row)
    {
        Assert.Equal(InferenceEvidenceV1.Decide(row.Base, row.Score >= row.Threshold), row.Final);
        var expectedReason = row.Final == ScreeningDecisionKind.Clear ? null
            : row.Base == ScreeningDecisionKind.Clear ? PaymentLedgerStore.ReasonMlRiskHold : row.Reason;
        Assert.Equal(expectedReason, row.Reason);
        if (row.Base == ScreeningDecisionKind.Held)
        {
            Assert.NotEqual(PaymentLedgerStore.ReasonMlRiskHold, row.Reason); // v7 precedence: its own rule reason
        }
    }

    /// <summary>Every Screening fact of the run, decoded from its committed evidence bytes, with the live status and hold reason.</summary>
    private async Task<List<DecisionRow>> DecisionTableAsync(Guid run)
    {
        var rows = new List<DecisionRow>();
        await using var cmd = db.Owner.CreateCommand(
            """
            SELECT f.canonical, i.canonical, p.status,
                   (SELECT t.reason FROM revenant.payment_transition t WHERE t.payment_id = p.id AND t.from_status = 'Screened' AND t.to_status = 'Held'),
                   f.fact_seq, p.intent_id
              FROM revenant.finance_fact f
              JOIN revenant.payment p ON p.id = f.payment_id
              JOIN revenant.finance_fact i ON i.payment_id = p.id AND i.kind = 'PaymentIntent'
             WHERE f.scenario_run_id = @r AND f.kind = 'Screening' ORDER BY f.fact_seq
            """);
        cmd.Parameters.AddWithValue("r", run);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var e = InferenceEvidenceV1.Decode(r.GetFieldValue<byte[]>(0)).Value;
            var intent = PaymentIntentCodec.Instance.Decode(r.GetFieldValue<byte[]>(1));
            Assert.Equal((run, r.GetString(5), r.GetInt64(4)), (e.RunId.Value, e.IntentId, e.ScreeningFactSeq));
            Assert.True(e.CheckBinds(intent).IsSuccess);
            rows.Add(new DecisionRow(e.IntentId, intent.Truth, e.Features.AmountMinor, e.Features.SourcePostedBeforeMinor, e.BaseDecision, e.ScorePpm,
                e.ThresholdPpm, e.FinalDecision, r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), e));
        }
        return rows;
    }

    private static ServiceProvider LiveOpsHost(LedgerDatabase.DeployedLogins logins, string tenant, bool startWorkers = true, string? artifactRoot = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:RevenantLedger"] = logins.WorkerLedger,
            ["Revenant:Ledger:TenantId"] = tenant,
            ["Revenant:Screening:IdleDelayMs"] = "20",
            ["Revenant:Settlement:IdleDelayMs"] = "20",
            ["Revenant:Settlement:BatchSize"] = "3",
            ["Revenant:RiskModels:ArtifactRoot"] = artifactRoot,
        }).Build();
        var services = new ServiceCollection().AddLogging();
        services.AddRevenantLedger(config, tx => new PostgresOutboxWriter(tx, new MessagingTables("revenant")));
        if (startWorkers)
        {
            services.AddRevenantScreeningWorker(config).AddRevenantSettlementWorker(config);
        }
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }

    /// <summary>The Worker's projection composition (V2 and V3 engine shapes) over the restricted sands_worker login; no model provider.</summary>
    internal static ServiceProvider ProjectionHost(string workerLogin)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Revenant:Ledger:TenantId"] = "unused" }).Build();
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(_ => NpgsqlDataSource.Create(workerLogin));
        services.AddSingleton(new RevenantLedgerOptions());
        services.AddMarrowTimeline(FinanceLedgerTimelineVersions.All, FinanceLedgerTimelineVersions.CheckpointInterval)
            .AddMarrowTimeline(ModelLedgerTimelineVersions.All, ModelLedgerTimelineVersions.CheckpointInterval)
            .AddRevenantLedgerProjection(config, runWorker: false);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }

    private static async Task<long> AsTenantAsync(string login, string tenant, string sql, Guid run)
    {
        await using var source = NpgsqlDataSource.Create(login);
        await using var connection = await source.OpenConnectionAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await using (var set = new NpgsqlCommand("SELECT set_config('revenant.tenant_id', @t, true)", connection, tx))
        {
            set.Parameters.AddWithValue("t", tenant);
            await set.ExecuteNonQueryAsync();
        }
        await using var cmd = new NpgsqlCommand(sql, connection, tx);
        cmd.Parameters.AddWithValue("run", run);
        var result = await cmd.ExecuteScalarAsync();
        await tx.RollbackAsync();
        return result is long value ? value : 0;
    }

    private static SubmitPaymentRequest Request(PaymentIntent i) => new(
        i.IntentId, i.Sequence, i.EventTimeUtcMs, i.SourceBankId, i.SourceAccountId, i.DestBankId, i.DestAccountId, i.Amount.Currency,
        i.Amount.MinorUnits, i.Truth, i.Features.NewBeneficiary, i.Features.SanctionsHit);

    private async Task<long> StatusCount(Guid run, string status) => await PaymentKit.Scalar<long>(db.Owner,
        "SELECT count(*) FROM revenant.payment WHERE scenario_run_id = @r AND status = @s", ("r", run), ("s", status));

    private async Task<long> PostedAsync(Guid run, string account) => await PaymentKit.Scalar<long>(db.Owner,
        "SELECT posted_minor FROM revenant.account_posted_balance WHERE scenario_run_id = @r AND local_account_id = @a", ("r", run), ("a", account));

    private async Task<(string, long)> HoldAsync(Guid run, string intentId)
    {
        await using var cmd = db.Owner.CreateCommand(
            "SELECT h.status, h.amount_minor FROM revenant.hold h JOIN revenant.payment p ON p.id = h.payment_id WHERE p.scenario_run_id = @r AND p.intent_id = @i");
        cmd.Parameters.AddWithValue("r", run);
        cmd.Parameters.AddWithValue("i", intentId);
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        return (r.GetString(0), r.GetInt64(1));
    }

    private async Task<List<FinanceFact>> FactsAsync(Guid run)
    {
        var facts = new List<FinanceFact>();
        await using var cmd = db.Owner.CreateCommand(
            "SELECT fact_seq, kind, schema_version, canonical, evidence_schema, evidence_sha256 FROM revenant.finance_fact WHERE scenario_run_id = @r ORDER BY fact_seq");
        cmd.Parameters.AddWithValue("r", run);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            facts.Add(new FinanceFact(Enum.Parse<FinanceFactKind>(r.GetString(1)), r.GetInt64(0), r.GetString(2), r.GetFieldValue<byte[]>(3),
                r.IsDBNull(4) ? null : new FactEvidenceRef(r.GetString(4), r.GetFieldValue<byte[]>(5))));
        }
        return facts;
    }

    private async Task<string> VersionKeyAsync(Guid timeline) => await PaymentKit.Scalar<string>(db.Owner,
        "SELECT domain_id || '|' || reducer_version || '|' || input_schema_version || '|' || state_schema_version || '|' || context_schema_version FROM marrow_timeline.histories WHERE timeline_id = @t",
        ("t", timeline));

    private async Task WaitQuiescentAsync(Guid run) =>
        await WaitAsync(async () => await PaymentKit.Scalar<long>(db.Owner,
            "SELECT count(*) FROM revenant.payment WHERE scenario_run_id = @r AND status IN ('Initiated', 'Screened', 'Approved', 'Cleared')",
            ("r", run)) == 0, "the LiveOps queues to drain");

    private async Task WaitStatusAsync(Guid run, string intentId, PaymentStatus status) =>
        await WaitAsync(async () => await PaymentKit.Scalar<string>(db.Owner,
            "SELECT status FROM revenant.payment WHERE scenario_run_id = @r AND intent_id = @i", ("r", run), ("i", intentId)) == status.ToString(),
            $"{intentId} to reach {status}");

    private static async Task WaitAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {what}.");
            await Task.Delay(50);
        }
    }

    private static T Ok<T>(Result<T, PaymentFailure> result)
    {
        Assert.True(result.IsSuccess, result.IsSuccess ? "" : result.Error.ToString());
        return result.Value;
    }

    private static T Ok<T>(Result<T, ProjectionError> result)
    {
        Assert.True(result.IsSuccess, result.IsSuccess ? "" : result.Error.ToString());
        return result.Value;
    }

    private static T Ok<T>(Result<T, TimelineError> result)
    {
        Assert.True(result.IsSuccess, result.IsSuccess ? "" : result.Error.ToString());
        return result.Value;
    }
}
