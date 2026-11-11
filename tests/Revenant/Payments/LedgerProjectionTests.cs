using Marrow.Infrastructure.Messaging;
using Marrow.Infrastructure.Timeline;
using Marrow.Kernel.Results;
using Marrow.Timeline;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Revenant.Api;
using Revenant.Cases;
using Revenant.Contracts;
using Revenant.Finance;
using Revenant.Payments;
using Revenant.Projection;
using Revenant.Scenario;
using Revenant.Tests.Ledger;
using Revenant.Timeline;
using Xunit;
using Xunit.Abstractions;

namespace Revenant.Tests.Payments;

/// <summary>
/// Spec 07 task 7.5: real financial causality survives persistence. The Friday Ring runs through the production LiveOps paths
/// (screening and two concurrent settlement workers resolved from the real Worker registrations, the staff service for intake,
/// review and return) over real PostgreSQL with the logins migrate.sh provisions; the committed facts are then projected into a
/// V2 Marrow Timeline source after a simulated crash, caught up idempotently, reconciled against the live ledger and verified.
/// **Validates: Requirements R07.07, R07.09, R07.11, R07.12, R07.13** (and the 7.5 exit-gate journey)
/// </summary>
[Collection(PaymentCollection.Name)]
[Trait("Category", "Integration")]
public sealed class LedgerProjectionTests(LedgerDatabase db, ITestOutputHelper output)
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private const string Owner = PaymentKit.Operator;
    private const string Approver = "staff:reviewer-75";
    private const string Supervisor = "staff:supervisor-75";

    [Fact]
    public async Task Friday_ring_live_ledger_matches_the_versioned_pure_source_after_crash_catch_up_and_cold_replay()
    {
        var logins = await db.ProvisionDeployedLoginsAsync();
        var tenant = $"t75-{Guid.NewGuid():N}"[..16];
        var runId = new ScenarioRunId(Guid.NewGuid());
        var ownerActor = TestActor.Staff(Owner, "operator", tenant);
        var reviewer = TestActor.Staff(Approver, "operator", tenant);
        var supervisor = TestActor.Staff(Supervisor, "supervisor", tenant);

        // Two LiveOps "processes" from the real Worker registrations, each with screening + settlement loops on sands_worker_ledger.
        var liveA = LiveOpsHost(logins, tenant);
        var liveB = LiveOpsHost(logins, tenant);
        var service = liveA.GetRequiredService<RevenantLedgerService>();
        var store = liveA.GetRequiredService<PaymentLedgerStore>();
        Ok(await service.OpenRunAsync(ownerActor, 42, runId.Value, Ct));
        Assert.True(await store.GrantRunStaffAsync(runId, Approver, "operator", Ct));
        Assert.True(await store.GrantRunStaffAsync(runId, Supervisor, "supervisor", Ct));
        var intents = PaymentKit.Intents(runId);
        var hosted = liveA.GetServices<IHostedService>().Concat(liveB.GetServices<IHostedService>()).ToList();
        Assert.Equal(4, hosted.Count);
        foreach (var service0 in hosted)
        {
            await service0.StartAsync(Ct);
        }

        foreach (var intent in intents)
        {
            Ok(await service.SubmitPaymentAsync(ownerActor, runId, Request(intent), $"intake-{intent.Sequence}", Ct));
        }
        await WaitQuiescentAsync(runId.Value);

        // Review every hold while the workers keep running: legitimate payments are released (four-eyes when high value),
        // ring payments are rejected. Released payments are settled by whichever worker claims them.
        var held = await HeldAsync(runId.Value);
        output.WriteLine($"intents {intents.Length}, held after screening {held.Count}");
        foreach (var (intentId, truth) in held)
        {
            if (truth == SyntheticTruth.Legitimate)
            {
                await ReleaseAsync(service, runId, intentId, reviewer, supervisor);
            }
            else
            {
                var view = Ok(await service.GetCaseAsync(reviewer, runId, intentId, Ct, "review-read"));
                Ok(await service.CaseActionAsync(reviewer, runId, intentId, CaseAction.Reject, view.Version, $"reject-{intentId}", "ring_confirmed", "review", Ct));
            }
        }
        await WaitQuiescentAsync(runId.Value);

        // Policy v7 holds on a sanctions hit. A legitimate false positive L is released by one reviewer and settles; a ring
        // payment R is rejected and its reservation released.
        var last = intents[^1];
        var source = await RichestAccountAsync(runId.Value);
        var dest = intents.First(i => i.DestAccountId != source.Account).DestAccountId;
        var destBank = intents.First(i => i.DestAccountId == dest).DestBankId;
        var l = new PaymentIntent(runId, "INT-75-L", last.Sequence + 1, last.EventTimeUtcMs, source.Bank, source.Account, destBank, dest,
            new Money("USD", 250_000), SyntheticTruth.Legitimate, new PaymentFeatures(false, SanctionsHit: true));
        var ring = new PaymentIntent(runId, "INT-75-R", last.Sequence + 2, last.EventTimeUtcMs, source.Bank, source.Account, destBank, dest,
            new Money("USD", 300_000), SyntheticTruth.FraudMuleRing, new PaymentFeatures(false, SanctionsHit: true));
        Ok(await service.SubmitPaymentAsync(ownerActor, runId, Request(l), "intake-l", Ct));
        Ok(await service.SubmitPaymentAsync(ownerActor, runId, Request(ring), "intake-r", Ct));
        await WaitStatusAsync(runId.Value, l.IntentId, PaymentStatus.Held);
        await WaitStatusAsync(runId.Value, ring.IntentId, PaymentStatus.Held);
        await ReleaseAsync(service, runId, l.IntentId, reviewer, supervisor);
        var ringCase = Ok(await service.GetCaseAsync(reviewer, runId, ring.IntentId, Ct, "r-read"));
        Ok(await service.CaseActionAsync(reviewer, runId, ring.IntentId, CaseAction.Reject, ringCase.Version, "reject-r", "ring_confirmed", "r", Ct));
        await WaitStatusAsync(runId.Value, l.IntentId, PaymentStatus.Settled);
        last = ring;

        // The interleaved journey: a high-value ring payment H is held, an unrelated intent X arrives between its two approvals,
        // H settles, a supervisor returns it, and a later intent Y follows the return.
        var h = new PaymentIntent(runId, "INT-75-H", last.Sequence + 1, last.EventTimeUtcMs, source.Bank, source.Account, destBank, dest,
            new Money("USD", 1_500_000), SyntheticTruth.FraudMuleRing, new PaymentFeatures(false, SanctionsHit: true));
        var x = new PaymentIntent(runId, "INT-75-X", last.Sequence + 2, last.EventTimeUtcMs, destBank, dest, source.Bank, source.Account,
            new Money("USD", 1_000), SyntheticTruth.Legitimate, new PaymentFeatures(false, false));
        Ok(await service.SubmitPaymentAsync(ownerActor, runId, Request(h), "intake-h", Ct));
        await WaitStatusAsync(runId.Value, h.IntentId, PaymentStatus.Held);
        var hCase = Ok(await service.GetCaseAsync(reviewer, runId, h.IntentId, Ct, "h-read"));
        Assert.Equal(2, hCase.RequiredApprovals);
        hCase = Ok(await service.CaseActionAsync(reviewer, runId, h.IntentId, CaseAction.Approve, hCase.Version, "h-approve-1", null, "h1", Ct)).Case;
        Assert.Equal(CaseStatus.AwaitingSecondApproval, hCase.Status);
        Ok(await service.SubmitPaymentAsync(ownerActor, runId, Request(x), "intake-x", Ct));
        // The first approver cannot also be the second (four-eyes), even as the same command shape.
        Assert.Equal(PaymentFailureCode.DistinctSupervisorRequired, (await service.CaseActionAsync(
            reviewer, runId, h.IntentId, CaseAction.Approve, hCase.Version, "h-approve-self", null, "h2", Ct)).Error.Code);
        Ok(await service.CaseActionAsync(supervisor, runId, h.IntentId, CaseAction.Approve, hCase.Version, "h-approve-2", null, "h2", Ct));
        await WaitStatusAsync(runId.Value, h.IntentId, PaymentStatus.Settled);
        var settledH = Ok(await service.GetPaymentAsync(supervisor, runId, h.IntentId, Ct));
        Ok(await service.ReturnSettledAsync(supervisor, runId, h.IntentId, settledH.Version, "h-return", "ring_recall", "h-return", Ct));
        var yIntent = new PaymentIntent(runId, "INT-75-Y", last.Sequence + 3, last.EventTimeUtcMs, source.Bank, source.Account, destBank, dest,
            new Money("USD", 2_000), SyntheticTruth.Legitimate, new PaymentFeatures(false, false));
        Ok(await service.SubmitPaymentAsync(ownerActor, runId, Request(yIntent), "intake-y", Ct));
        await WaitQuiescentAsync(runId.Value);

        // "Crash" after the live commits: the LiveOps processes stop; nothing has been projected yet.
        foreach (var hs in hosted)
        {
            await hs.StopAsync(Ct);
        }
        await liveA.DisposeAsync();
        await liveB.DisposeAsync();
        var facts = await FactsAsync(runId.Value);
        Assert.Equal(Enumerable.Range(1, facts.Count).Select(i => (long)i), facts.Select(f => f.Sequence));
        Assert.Equal(0, await PaymentKit.Scalar<long>(db.Owner,
            "SELECT count(*) FROM marrow_timeline.histories WHERE operation_key = @k", ("k", LedgerProjector.SourceOperationKey(runId))));

        // Projection process 1 gets half way and dies; process 2 resumes from the history's own last position.
        var half = facts.Count / 2;
        await using (var p1 = ProjectionHost(logins.Worker))
        {
            var partial = Ok(await p1.GetRequiredService<LedgerProjector>().CatchUpAsync(tenant, runId, Ct, upTo: half));
            Assert.Equal(half, partial.ProjectedPosition);
            Assert.Equal(HistoryStatus.Recording, partial.Status);
        }
        SourceVerification verified;
        TimelineId history;
        await using (var p2 = ProjectionHost(logins.Worker))
        {
            var projector = p2.GetRequiredService<LedgerProjector>();
            var resumed = Ok(await projector.CatchUpAsync(tenant, runId, Ct));
            Assert.Equal(facts.Count - half, resumed.Appended);
            Assert.Equal(facts.Count, resumed.ProjectedPosition);
            Assert.Equal(0, Ok(await projector.CatchUpAsync(tenant, runId, Ct)).Appended); // idempotent catch-up

            // The run is still open to new facts, so its source cannot be verified; closing fixes the final fact set first.
            Assert.Equal(ProjectionErrorCode.RunNotClosed, (await projector.VerifySourceAsync(tenant, runId, Ct)).Error.Code);
            await using var closeHost = LiveOpsHost(logins, tenant, startWorkers: false);
            var closeStore = closeHost.GetRequiredService<PaymentLedgerStore>();
            var closure = Ok(await closeStore.CloseRunAsync(tenant, Owner, runId, "close-75", Ct));
            Assert.Equal((facts.Count, false), (closure.ClosedFactSequence, closure.IsReplay));
            Assert.True(Ok(await closeStore.CloseRunAsync(tenant, Owner, runId, "close-75-again", Ct)).IsReplay);
            var late = new PaymentIntent(runId, "INT-75-LATE", last.Sequence + 9, last.EventTimeUtcMs + 1, source.Bank, source.Account, destBank, dest,
                new Money("USD", 1_000), SyntheticTruth.Legitimate, new PaymentFeatures(false, false));
            var refused = await Assert.ThrowsAsync<PostgresException>(() => closeStore.SubmitIntentAsync(tenant, Owner, late, "late", Ct));
            Assert.Equal("RV009", refused.SqlState);
            Assert.Equal(facts.Count, await PaymentKit.Scalar<long>(db.Owner, "SELECT count(*) FROM revenant.finance_fact WHERE scenario_run_id = @r", ("r", runId.Value)));
            Assert.Equal(0L, await PaymentKit.Scalar<long>(db.Owner, "SELECT count(*) FROM revenant.payment WHERE scenario_run_id = @r AND intent_id = 'INT-75-LATE'", ("r", runId.Value)));
            verified = Ok(await projector.VerifySourceAsync(tenant, runId, Ct));
            history = verified.Projection.History;
        }
        foreach (var m in verified.Mismatches)
        {
            output.WriteLine($"mismatch {m.Subject}: source {m.Source} live {m.Live}");
        }
        Assert.True(verified.Reconciled);
        Assert.Equal(HistoryStatus.Verified, verified.Projection.Status);
        Assert.Equal(facts.Count, verified.Projection.ProjectedPosition);

        // Cold replay from the stored history equals an independent fold of the stored facts, byte for byte.
        await using (var cold = ProjectionHost(logins.Worker))
        {
            var engine = cold.GetRequiredService<ITimelineEngine<LedgerFinanceState, FinanceFact, LedgerFinanceContext>>();
            var replay = Ok(await engine.ReplayAsync(history, Ct));
            Assert.Equal(HistoryStatus.Verified, replay.Status);
            Assert.Equal(verified.Projection.FinalStateHash, replay.FinalStateHash);
            var run = FridayRingScenario.Create(42, ScenarioSchemas.FridayRingVersion, runId).Value;
            var context = LedgerFinanceContext.Pin(run.Manifest, PolicyCatalog.RefOf(LedgerFinanceContext.LivePolicy)).Value;
            var fold = FinanceLedgerReducer.ApplyAll(FinanceLedgerReducer.Initial(run, context).Value, facts, context).Value;
            Assert.Equal(LedgerFinanceStateCodec.Instance.EncodeCanonical(fold), LedgerFinanceStateCodec.Instance.EncodeCanonical(replay.FinalState));
            var restored = Ok(await engine.RestoreAsync(history, half, Ct));
            Assert.Equal(half, restored.State.LastFact);

            // Actual DB balances equal the pure source; the six banks conserve the run's total.
            var posted = await PostedAsync(runId.Value);
            Assert.Equal(fold.Accounts.Count, posted.Count);
            foreach (var account in fold.Accounts.Values)
            {
                Assert.Equal(account.PostedMinor, posted[account.AccountId]);
            }
            Assert.Equal(run.InitialState.Accounts.Sum(a => a.OpeningBalance.MinorUnits), posted.Values.Sum());
            Assert.Equal(0, fold.Accounts.Values.Sum(a => a.ReservedMinor));

            // Version keys: the source pins V2; a fork inherits it; the V1 engine shape cannot open a V2 history.
            Assert.Equal(
                $"{FinanceLedgerVersion.DomainId}|{FinanceLedgerVersion.ReducerVersion}|{FinanceLedgerVersion.InputSchemaVersion}|{FinanceLedgerVersion.StateSchemaVersion}|{FinanceLedgerVersion.ContextSchemaVersion}",
                await VersionKeyAsync(history.Value));
            var fork = Ok(await engine.ForkAsync(history, half, $"fork-75-{Guid.NewGuid():N}", context, Ct));
            Assert.Equal(await VersionKeyAsync(history.Value), await VersionKeyAsync(fork.Value));
            await using var v1 = new ServiceCollection()
                .AddSingleton(_ => NpgsqlDataSource.Create(logins.Worker))
                .AddMarrowTimeline(FinanceTimelineVersions.All, FinanceTimelineVersions.CheckpointInterval)
                .BuildServiceProvider();
            var v1Engine = v1.GetRequiredService<ITimelineEngine<FinanceProjectionState, PaymentIntent, FinanceContext>>();
            Assert.Equal(TimelineErrorCode.IncompatibleVersion, (await v1Engine.RestoreAsync(history, half, Ct)).Error.Code);

            // Exit-gate journeys, read back from the verified source.
            var payments = fold.Payments.Values;
            Assert.Contains(payments, p => p.Intent.Truth == SyntheticTruth.Legitimate
                && Trail(p).Contains("Held>Approved>Cleared>Settled", StringComparison.Ordinal));
            Assert.Contains(payments, p => p.Intent.Truth == SyntheticTruth.FraudMuleRing && Trail(p).EndsWith("Held>Rejected", StringComparison.Ordinal)
                && p.Case == CaseStatus.Rejected);
            Assert.Equal("Initiated>Screened>Held>Approved>Cleared>Settled>Returned", Trail(fold.Payments[h.IntentId]));
            Assert.Equal("Initiated>Screened>Held>Approved>Cleared>Settled", Trail(fold.Payments[l.IntentId]));
            Assert.Equal("Initiated>Screened>Held>Rejected", Trail(fold.Payments[ring.IntentId]));
            output.WriteLine($"facts {facts.Count}; statuses {string.Join(", ", payments.GroupBy(p => p.Status).Select(g => $"{g.Key}={g.Count()}"))}");
        }

        // Stable action order: H's intent, X's intent between H's two approvals, then H's settlement, return and Y's intent.
        long Seq(FinanceFactKind kind, string intentId, int nth = 0) =>
            facts.Where(f => f.Kind == kind && IntentOf(f) == intentId).Select(f => f.Sequence).ElementAt(nth);
        var order = new[]
        {
            Seq(FinanceFactKind.PaymentIntent, h.IntentId), Seq(FinanceFactKind.Screening, h.IntentId), Seq(FinanceFactKind.ReviewDecision, h.IntentId, 0),
            Seq(FinanceFactKind.PaymentIntent, x.IntentId), Seq(FinanceFactKind.ReviewDecision, h.IntentId, 1), Seq(FinanceFactKind.Settlement, h.IntentId),
            Seq(FinanceFactKind.ReturnInstruction, h.IntentId), Seq(FinanceFactKind.PaymentIntent, yIntent.IntentId),
        };
        Assert.Equal(order.OrderBy(s => s), order);

        // Hash-chained audit of the run is intact and covers every fact plus the audited case reads.
        await using var verifyHost = LiveOpsHost(logins, tenant, startWorkers: false);
        var audit = Ok(await verifyHost.GetRequiredService<RevenantLedgerService>().VerifyAuditAsync(supervisor, runId, Ct));
        Assert.True(audit.ChainIntact);
        Assert.Equal(AuditVerification.DatabaseTamperEvidence, audit.Assurance);
        Assert.True(audit.Events > facts.Count, $"{audit.Events} audit events for {facts.Count} facts");
        Assert.Equal(PaymentFailureCode.Forbidden, (await verifyHost.GetRequiredService<RevenantLedgerService>()
            .VerifyAuditAsync(reviewer, runId, Ct)).Error.Code); // reviewers verify, operators do not
        output.WriteLine($"audit events {audit.Events}, head {audit.HeadHashHex}");
    }

    [Fact]
    public async Task A_run_moved_by_steps_that_record_no_fact_does_not_verify()
    {
        var logins = await db.ProvisionDeployedLoginsAsync();
        var tenant = $"t75n-{Guid.NewGuid():N}"[..16];
        await using var live = LiveOpsHost(logins, tenant, startWorkers: false);
        var store = live.GetRequiredService<PaymentLedgerStore>();
        var run = await PaymentKit.OpenAsync(store, tenant);
        var intent = PaymentKit.Intents(run)[0];
        Ok(await store.SubmitIntentAsync(tenant, Owner, intent, "c", Ct));
        // A generic system step outside the screening entry point: it moves the payment but records no ordered fact.
        Ok(await store.TransitionAsync(tenant, Owner, new TransitionCommand(run, intent.IntentId, PaymentStatus.Screened, 1, "gen-1", null), "c", Ct));
        // A payment still in a LiveOps state blocks closing, and an open run cannot be verified.
        Assert.Equal(PaymentFailureCode.RunNotQuiescent, (await store.CloseRunAsync(tenant, Owner, run, "close-n", Ct)).Error.Code);
        await using var projection = ProjectionHost(logins.Worker);
        Assert.Equal(ProjectionErrorCode.RunNotClosed,
            (await projection.GetRequiredService<LedgerProjector>().VerifySourceAsync(tenant, run, Ct)).Error.Code);
        Ok(await store.TransitionAsync(tenant, Owner, new TransitionCommand(run, intent.IntentId, PaymentStatus.Rejected, 2, "gen-2", null), "c", Ct));
        Ok(await store.CloseRunAsync(tenant, Owner, run, "close-n", Ct));
        var verification = Ok(await projection.GetRequiredService<LedgerProjector>().VerifySourceAsync(tenant, run, Ct));
        Assert.False(verification.Reconciled);
        Assert.Equal(HistoryStatus.Recording, verification.Projection.Status);
        Assert.Contains(verification.Mismatches, m => m.Subject == $"payment {intent.IntentId} status" && m.Source == "Initiated" && m.Live == "Rejected");
    }

    [Fact]
    public async Task The_projection_and_replay_login_cannot_write_any_live_finance_table()
    {
        var logins = await db.ProvisionDeployedLoginsAsync();
        Assert.Contains("runtime login sands_worker: provisioned (marrow_timeline_runtime revenant_replay_reader revenant_read_projector)", logins.MigrateOutput);
        Assert.Contains("runtime login sands_worker_ledger: provisioned (revenant_ledger_writer revenant_outbox_relay)", logins.MigrateOutput);
        Assert.Contains("runtime login sands_api_ledger: provisioned (revenant_ledger_writer)", logins.MigrateOutput);
        Assert.Contains("runtime login sands_realtime_ledger: provisioned (revenant_replay_reader)", logins.MigrateOutput);
        var tenant = $"t75r-{Guid.NewGuid():N}"[..16];
        await using var live = LiveOpsHost(logins, tenant, startWorkers: false);
        var store = live.GetRequiredService<PaymentLedgerStore>();
        var run = await PaymentKit.OpenAsync(store, tenant);
        Ok(await store.SubmitIntentAsync(tenant, Owner, PaymentKit.Intents(run)[0], "c", Ct));
        await store.ScreenBatchAsync(tenant, 5, "c", Ct);

        await using var replay = NpgsqlDataSource.Create(logins.Worker);
        string[] attempts =
        [
            "UPDATE revenant.payment SET status = 'Rejected', version = version + 1 WHERE scenario_run_id = @run",
            "INSERT INTO revenant.payment (tenant_id, scenario_run_id, intent_id, source_account_id, dest_account_id, currency, amount_minor, status) SELECT tenant_id, scenario_run_id, 'x', source_account_id, dest_account_id, currency, 1, 'Initiated' FROM revenant.payment WHERE scenario_run_id = @run LIMIT 1",
            "DELETE FROM revenant.payment WHERE scenario_run_id = @run",
            "INSERT INTO revenant.ledger_group (tenant_id, scenario_run_id, payment_id, currency, purpose) SELECT tenant_id, scenario_run_id, id, currency, 'Settlement' FROM revenant.payment WHERE scenario_run_id = @run",
            "INSERT INTO revenant.ledger_entry (group_id, tenant_id, scenario_run_id, account_id, currency, direction, amount_minor) SELECT gen_random_uuid(), tenant_id, scenario_run_id, id, currency, 'Debit', 1 FROM revenant.account WHERE scenario_run_id = @run LIMIT 1",
            "UPDATE revenant.hold SET status = 'Released' WHERE scenario_run_id = @run",
            "INSERT INTO revenant.finance_fact (tenant_id, scenario_run_id, fact_seq, kind, schema_version, payment_id, canonical, canonical_sha256) SELECT tenant_id, scenario_run_id, 99, 'Screening', 's', payment_id, canonical, canonical_sha256 FROM revenant.finance_fact WHERE scenario_run_id = @run LIMIT 1",
            "UPDATE revenant.run_cursor SET last_fact_seq = last_fact_seq + 1 WHERE scenario_run_id = @run",
            "INSERT INTO revenant.payment_transition (tenant_id, scenario_run_id, payment_id, version, from_status, to_status, command_key, actor) SELECT tenant_id, scenario_run_id, id, 9, 'Held', 'Approved', 'k', 'a' FROM revenant.payment WHERE scenario_run_id = @run",
            "UPDATE revenant.review_case SET status = 'Released', version = version + 1 WHERE scenario_run_id = @run",
            "INSERT INTO revenant.audit_event (tenant_id, scenario_run_id, seq, event_type, actor, correlation_id, subject_ref, recorded_us, prev_hash, hash) SELECT tenant_id, scenario_run_id, 0, 'x', 'a', 'c', 's', 0, '\\x', '\\x' FROM revenant.payment WHERE scenario_run_id = @run LIMIT 1",
            "UPDATE revenant.outbox SET status = 'published'",
            "UPDATE revenant.audit_head SET last_seq = 0",
        ];
        foreach (var sql in attempts)
        {
            await using var connection = await replay.OpenConnectionAsync();
            await using var tx = await connection.BeginTransactionAsync();
            await using (var set = new NpgsqlCommand("SELECT set_config('revenant.tenant_id', @t, true)", connection, tx))
            {
                set.Parameters.AddWithValue("t", tenant);
                await set.ExecuteNonQueryAsync();
            }
            await using var cmd = new NpgsqlCommand(sql, connection, tx);
            cmd.Parameters.AddWithValue("run", run.Value);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
            Assert.True(ex.SqlState == PostgresErrorCodes.InsufficientPrivilege, $"{sql} -> {ex.SqlState} {ex.MessageText}");
        }
        // It can read its tenant's live state (projection needs it) ...
        await using (var connection = await replay.OpenConnectionAsync())
        await using (var tx = await connection.BeginTransactionAsync())
        {
            await using (var set = new NpgsqlCommand("SELECT set_config('revenant.tenant_id', @t, true)", connection, tx))
            {
                set.Parameters.AddWithValue("t", tenant);
                await set.ExecuteNonQueryAsync();
            }
            await using var read = new NpgsqlCommand("SELECT count(*) FROM revenant.finance_fact WHERE scenario_run_id = @run", connection, tx);
            read.Parameters.AddWithValue("run", run.Value);
            Assert.Equal(2L, await read.ExecuteScalarAsync());
        }
        // ... and its catalog roles carry no BYPASSRLS/superuser and own no revenant table.
        Assert.Equal(0L, await PaymentKit.Scalar<long>(db.Owner,
            """
            SELECT count(*) FROM pg_roles r WHERE r.rolname IN ('sands_worker', 'sands_worker_ledger', 'sands_api_ledger', 'sands_realtime_ledger')
               AND (r.rolsuper OR r.rolbypassrls OR EXISTS (SELECT 1 FROM pg_tables t WHERE t.schemaname = 'revenant' AND t.tableowner = r.rolname))
            """));
    }

    [Fact]
    public async Task Audit_chain_records_reads_comments_and_facts_and_detects_a_rewritten_row()
    {
        var logins = await db.ProvisionDeployedLoginsAsync();
        var tenant = $"t75a-{Guid.NewGuid():N}"[..16];
        await using var live = LiveOpsHost(logins, tenant, startWorkers: false);
        var store = live.GetRequiredService<PaymentLedgerStore>();
        var service = live.GetRequiredService<RevenantLedgerService>();
        var run = await PaymentKit.OpenAsync(store, tenant);
        var held = PaymentKit.Intents(run)[0];
        held = new PaymentIntent(run, held.IntentId, held.Sequence, held.EventTimeUtcMs, held.SourceBankId, held.SourceAccountId,
            held.DestBankId, held.DestAccountId, new Money("USD", 50_000), held.Truth, new PaymentFeatures(false, SanctionsHit: true));
        Ok(await store.SubmitIntentAsync(tenant, Owner, held, "corr-intake", Ct));
        Assert.Equal(PaymentStatus.Held, Assert.Single(await store.ScreenBatchAsync(tenant, 5, "corr-screen", Ct)).Status);
        var operatorActor = TestActor.Staff(Owner, "operator", tenant);
        var view = Ok(await service.GetCaseAsync(operatorActor, run, held.IntentId, Ct, "corr-read"));
        Ok(await service.CommentAsync(operatorActor, run, held.IntentId, "secret note text", "cm-1", "corr-comment", Ct));
        Ok(await service.CaseActionAsync(operatorActor, run, held.IntentId, CaseAction.Approve, view.Version, "ap-1", null, "corr-approve", Ct));

        var rows = await store.ReadAuditAsync(tenant, run, Ct);
        Assert.Equal(["fact.paymentintent", "fact.screening", "case.read", "case.comment", "fact.reviewdecision"], rows.Select(r => r.EventType));
        Assert.Equal([Owner, PaymentLedgerStore.ScreeningActor, Owner, Owner, Owner], rows.Select(r => r.Actor));
        Assert.Equal(["corr-intake", "corr-screen", "corr-read", "corr-comment", "corr-approve"], rows.Select(r => r.CorrelationId));
        Assert.DoesNotContain(rows, r => r.SubjectRef.Contains("secret", StringComparison.Ordinal));
        Assert.True((await store.VerifyAuditChainAsync(tenant, run, Ct)).ChainIntact);
        // The writer cannot rewrite the chain; the owner can only by disabling the guard, and the recomputation then exposes it.
        await using (var cmd = db.Writer.CreateCommand("UPDATE revenant.audit_event SET actor = 'staff:other' WHERE scenario_run_id = @r AND seq = 3"))
        {
            cmd.Parameters.AddWithValue("r", run.Value);
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync())).SqlState);
        }
        await using (var cmd = db.Owner.CreateCommand(
            """
            ALTER TABLE revenant.audit_event DISABLE TRIGGER audit_event_no_update;
            UPDATE revenant.audit_event SET actor = 'staff:other' WHERE scenario_run_id = @r AND seq = 3;
            ALTER TABLE revenant.audit_event ENABLE TRIGGER audit_event_no_update;
            """))
        {
            cmd.Parameters.AddWithValue("r", run.Value);
            await cmd.ExecuteNonQueryAsync();
        }
        var tampered = await store.VerifyAuditChainAsync(tenant, run, Ct);
        Assert.False(tampered.ChainIntact);
        Assert.Equal(3, tampered.FirstBrokenSequence);
    }

    // ---------------------------------------------------------------------------------------------------------

    private static ServiceProvider LiveOpsHost(LedgerDatabase.DeployedLogins logins, string tenant, bool startWorkers = true)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:RevenantLedger"] = logins.WorkerLedger,
            ["Revenant:Ledger:TenantId"] = tenant,
            ["Revenant:Screening:IdleDelayMs"] = "20",
            ["Revenant:Settlement:IdleDelayMs"] = "20",
            ["Revenant:Settlement:BatchSize"] = "3",
        }).Build();
        var services = new ServiceCollection().AddLogging();
        services.AddRevenantLedger(config, tx => new PostgresOutboxWriter(tx, new MessagingTables("revenant")));
        if (startWorkers)
        {
            services.AddRevenantScreeningWorker(config).AddRevenantSettlementWorker(config);
        }
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }

    /// <summary>The Worker's projection composition: shared engine (V2 registrations) over the sands_worker login.</summary>
    private static ServiceProvider ProjectionHost(string workerLogin)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Revenant:Ledger:TenantId"] = "unused" }).Build();
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(_ => NpgsqlDataSource.Create(workerLogin));
        services.AddSingleton(new RevenantLedgerOptions());
        services.AddMarrowTimeline(FinanceLedgerTimelineVersions.All, FinanceLedgerTimelineVersions.CheckpointInterval)
            .AddRevenantLedgerProjection(config, runWorker: false);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }

    private static async Task ReleaseAsync(RevenantLedgerService service, ScenarioRunId run, string intentId, TestActor reviewer, TestActor supervisor)
    {
        var view = Ok(await service.GetCaseAsync(reviewer, run, intentId, Ct, "release-read"));
        view = Ok(await service.CaseActionAsync(reviewer, run, intentId, CaseAction.Approve, view.Version, $"a1-{intentId}", null, "a1", Ct)).Case;
        if (view.Status == CaseStatus.AwaitingSecondApproval)
        {
            Ok(await service.CaseActionAsync(supervisor, run, intentId, CaseAction.Approve, view.Version, $"a2-{intentId}", null, "a2", Ct));
        }
    }

    private static SubmitPaymentRequest Request(PaymentIntent i) => new(
        i.IntentId, i.Sequence, i.EventTimeUtcMs, i.SourceBankId, i.SourceAccountId, i.DestBankId, i.DestAccountId, i.Amount.Currency,
        i.Amount.MinorUnits, i.Truth, i.Features.NewBeneficiary, i.Features.SanctionsHit);

    private static string Trail(LedgerPayment p) => string.Join(">", p.Trail);

    private static string IntentOf(FinanceFact f) => f.Kind switch
    {
        FinanceFactKind.PaymentIntent => PaymentIntentCodec.Instance.Decode(f.Payload).IntentId,
        FinanceFactKind.ReviewDecision => ReviewDecisionCodec.Decode(f.Payload).IntentId,
        FinanceFactKind.ReturnInstruction => ReturnInstructionCodec.Decode(f.Payload).IntentId,
        _ => PaymentStepCodec.Decode(f.Payload),
    };

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

    private async Task<List<(string IntentId, SyntheticTruth Truth)>> HeldAsync(Guid run)
    {
        var result = new List<(string, SyntheticTruth)>();
        await using var cmd = db.Owner.CreateCommand(
            """
            SELECT p.intent_id, f.canonical FROM revenant.payment p
              JOIN revenant.finance_fact f ON f.payment_id = p.id AND f.kind = 'PaymentIntent'
             WHERE p.scenario_run_id = @r AND p.status = 'Held' ORDER BY f.fact_seq
            """);
        cmd.Parameters.AddWithValue("r", run);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            result.Add((r.GetString(0), PaymentIntentCodec.Instance.Decode(r.GetFieldValue<byte[]>(1)).Truth));
        }
        return result;
    }

    private async Task<(string Bank, string Account)> RichestAccountAsync(Guid run)
    {
        await using var cmd = db.Owner.CreateCommand(
            """
            SELECT b.local_bank_id, v.local_account_id FROM revenant.account_available_balance v JOIN revenant.bank b ON b.id = v.bank_id
             WHERE v.scenario_run_id = @r ORDER BY v.available_minor DESC, v.local_account_id LIMIT 1
            """);
        cmd.Parameters.AddWithValue("r", run);
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        return (r.GetString(0), r.GetString(1));
    }

    private async Task<Dictionary<string, long>> PostedAsync(Guid run)
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        await using var cmd = db.Owner.CreateCommand("SELECT local_account_id, posted_minor FROM revenant.account_posted_balance WHERE scenario_run_id = @r");
        cmd.Parameters.AddWithValue("r", run);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            result[r.GetString(0)] = r.GetInt64(1);
        }
        return result;
    }

    private async Task<List<FinanceFact>> FactsAsync(Guid run)
    {
        var facts = new List<FinanceFact>();
        await using var cmd = db.Owner.CreateCommand(
            "SELECT fact_seq, kind, schema_version, canonical FROM revenant.finance_fact WHERE scenario_run_id = @r ORDER BY fact_seq");
        cmd.Parameters.AddWithValue("r", run);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            facts.Add(new FinanceFact(Enum.Parse<FinanceFactKind>(r.GetString(1)), r.GetInt64(0), r.GetString(2), r.GetFieldValue<byte[]>(3), null));
        }
        return facts;
    }

    private async Task<string> VersionKeyAsync(Guid timeline) => await PaymentKit.Scalar<string>(db.Owner,
        "SELECT domain_id || '|' || reducer_version || '|' || input_schema_version || '|' || state_schema_version || '|' || context_schema_version FROM marrow_timeline.histories WHERE timeline_id = @t",
        ("t", timeline));

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
