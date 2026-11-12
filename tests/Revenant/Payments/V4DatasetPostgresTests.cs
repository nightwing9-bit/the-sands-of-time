using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Marrow.Infrastructure.Timeline;
using Marrow.Infrastructure.Messaging;
using Marrow.Kernel.Canonical;
using Marrow.Kernel.Messaging;
using Marrow.Timeline;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Revenant.Api;
using Revenant.Contracts;
using Revenant.Finance;
using Revenant.Intelligence;
using Revenant.Observations;
using Revenant.Payments;
using Revenant.Synthetic;
using Revenant.Tests.Ledger;
using Revenant.Timeline;
using Xunit;
using Xunit.Abstractions;

namespace Revenant.Tests.Payments;

[Collection(PaymentCollection.Name)]
public sealed class V4DatasetPostgresTests(LedgerDatabase db, ITestOutputHelper output)
{
    private const string Tenant = PaymentKit.TenantA;
    private const string Owner = PaymentKit.Operator;
    private const string TestRevision = "0000000000000000000000000000000000000000";

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Published_Worker_dispatches_and_synchronizes_a_model_admitted_hold()
    {
        Assert.True(OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64);
        var published = Environment.GetEnvironmentVariable("REVENANT_R6_WORKER_DLL");
        Assert.True(File.Exists(published), "CI must publish the Linux AMD64 Worker before running this case.");
        using var time = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = time.Token;
        var logins = await db.ProvisionDeployedLoginsAsync();
        await using var writer = NpgsqlDataSource.Create(logins.WorkerLedger);
        await using var host = Host(logins.Worker);
        var engine = host.GetRequiredService<ITimelineEngine<ObservedFinanceState, ObservedFinanceInput, ObservedFinanceContext>>();
        var ledger = PaymentKit.Store(writer);
        var model = new Lazy<Marrow.Kernel.Results.Result<BehavioralRiskScorerV2, BehavioralRiskFailureV2>>(() =>
            BehavioralRiskScorerV2.Load(Path.Combine(AppContext.BaseDirectory, "Intelligence", "Artifacts",
                BehavioralRiskScorerV2.ArtifactDirectory), BehavioralRiskScorerV2.FeatureSchema, BehavioralRiskScorerV2.PublishedPin));
        var store = new V4SimulationStore(writer, ledger: ledger, model: model);
        var branch = new V4BranchExecution(store, engine);
        var id = new ScenarioRunId(Guid.NewGuid());
        const long workerScreeningSequence = 532; // First checkpoint's deterministic /2 challenge root 1.
        V4ModelScreeningEvidence? workerEvidence = null;
        var executor = new V4EpisodeExecutor(store, branch, ledger, async (command, prior, token) =>
        {
            if (prior != workerScreeningSequence - 1 || command.Request.Kind != nameof(ObservedFactKind.Screening))
                return null;

            Assert.Null(workerEvidence);
            var payload = new CanonicalReader(command.Request.CanonicalPayload.AsSpan());
            Assert.Equal(1, payload.ReadInt32());
            var intentId = payload.ReadString();
            payload.EnsureEnd();
            var pending = await ledger.GetPaymentAsync(Tenant, id, intentId, token);
            Assert.NotNull(pending);
            Assert.Equal(PaymentStatus.Initiated, pending.Status);

            var unavailable = new V4SimulationStore(writer, ledger: ledger);
            var blocked = await unavailable.AdvanceOneStepAsync(Tenant, id, token);
            Assert.Equal(V4StepKind.Blocked, blocked.Kind);
            Assert.Equal("model-configuration-not-registered", blocked.Command?.OutcomeCode);
            var before = await store.ReadFinanceHistoryAsync(Tenant, id, id, token);
            Assert.Equal(prior, before.LastFact);
            var boundary = before.Facts.Last(f => f.Input.Kind == ObservedFactKind.ObservationSeal).Input.EffectiveTimeMs;
            var nextSeal = ObservedFinanceVersion.NextBoundary(boundary);
            Assert.Equal(V4StepKind.Blocked, (await store.SealAsync(Tenant, id, nextSeal, token)).Kind);
            await store.RetryAsync(Tenant, id, command.Request.Key, token);
            var corruptDirectory = Directory.CreateTempSubdirectory("r6-bad-model-");
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "Intelligence", "Artifacts", BehavioralRiskScorerV2.ArtifactDirectory);
                File.Copy(Path.Combine(path, "manifest.json"), Path.Combine(corruptDirectory.FullName, "manifest.json"));
                await File.WriteAllBytesAsync(Path.Combine(corruptDirectory.FullName, "model.zip"), [0], token);
                var corrupt = new V4SimulationStore(writer, ledger: ledger, model: new(() =>
                    BehavioralRiskScorerV2.Load(corruptDirectory.FullName, BehavioralRiskScorerV2.FeatureSchema, BehavioralRiskScorerV2.PublishedPin)));
                var invalid = await corrupt.AdvanceOneStepAsync(Tenant, id, token);
                Assert.Equal(V4StepKind.Blocked, invalid.Kind);
                Assert.Equal("model-configuration-InvalidArtifact", invalid.Command?.OutcomeCode);
                await store.RetryAsync(Tenant, id, command.Request.Key, token);
            }
            finally
            {
                corruptDirectory.Delete(recursive: true);
            }

            var failing = new V4SimulationStore(writer, ledger: PaymentKit.Store(writer, tx => new CaseFailingOutbox(
                new PostgresOutboxWriter(tx, new MessagingTables("revenant")))), model: model);
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => failing.AdvanceOneStepAsync(Tenant, id, token));
            Assert.Equal("injected case outbox failure", failure.Message);
            await AssertScreeningRolledBack();

            // This trigger changes the actual SQL reference after the ledger and outbox writes but
            // before the production evidence guard. An invalid reference must abort the entire transaction.
            await using (var install = db.Owner.CreateCommand($"""
                CREATE FUNCTION revenant.test_r6_corrupt_evidence() RETURNS trigger LANGUAGE plpgsql AS $body$
                BEGIN
                  IF NEW.scenario_run_id = '{id.Value:D}'::uuid AND NEW.kind = 'Screening' THEN
                    NEW.evidence_sha256 := decode(repeat('00', 32), 'hex');
                  END IF;
                  RETURN NEW;
                END
                $body$;
                CREATE TRIGGER a_test_r6_corrupt_evidence BEFORE INSERT ON revenant.finance_fact
                  FOR EACH ROW EXECUTE FUNCTION revenant.test_r6_corrupt_evidence();
                """))
                _ = await install.ExecuteNonQueryAsync(token);
            try
            {
                var invalid = await Assert.ThrowsAsync<PostgresException>(() => store.AdvanceOneStepAsync(Tenant, id, token));
                Assert.Equal("RV010", invalid.SqlState);
                await AssertScreeningRolledBack();
            }
            finally
            {
                await using var remove = db.Owner.CreateCommand("""
                    DROP TRIGGER a_test_r6_corrupt_evidence ON revenant.finance_fact;
                    DROP FUNCTION revenant.test_r6_corrupt_evidence();
                    """);
                _ = await remove.ExecuteNonQueryAsync(CancellationToken.None);
            }

            var receipt = await RunWorker(1, token);
            Assert.Equal(1, receipt.Steps);
            Assert.Equal(workerScreeningSequence, receipt.LastFact);
            Assert.Null(receipt.BlockedCode);
            var binding = await store.ReadBindingAsync(Tenant, id, token);
            var history = await store.ReadFinanceHistoryAsync(Tenant, id, binding.LineageRunId, token);
            var fact = history.Facts[^1];
            Assert.Equal(receipt.PrefixDigest, fact.PrefixDigest);
            var evidence = V4ModelScreeningEvidence.Decode(fact.Input);
            workerEvidence = evidence;
            Assert.True(evidence.AboveThreshold && evidence.Admitted && evidence.OpensCase);
            var posted = (await engine.RestoreAsync(binding.TimelineId, receipt.LastFact, token)).Value.State;
            var payment = posted.Ledger.Payments[evidence.IntentId];
            Assert.Equal(PaymentStatus.Held, payment.Status);
            Assert.Equal(payment.Intent.Amount.MinorUnits, payment.ReservedMinor);
            var physical = await ledger.GetPaymentAsync(Tenant, id, evidence.IntentId, token);
            Assert.NotNull(physical);
            Assert.Equal(PaymentStatus.Held, physical.Status);
            Assert.Equal(1, await PaymentKit.Scalar<long>(db.Owner, """
                SELECT count(*) FROM revenant.hold h JOIN revenant.review_case c ON c.payment_id = h.payment_id
                WHERE h.scenario_run_id = @run AND h.payment_id = @payment AND h.status = 'Active' AND c.status = 'Open'
                """, ("run", id.Value), ("payment", physical.PaymentId)));

            var retry = await RunWorker(0, token);
            Assert.Equal(receipt with { Steps = 0 }, retry);
            var completed = await store.AdmitAsync(Tenant, id, command.Request, token);
            Assert.Equal(V4CommandStatus.Completed, completed.Status);
            Assert.Equal(nameof(PaymentStatus.Held), completed.OutcomeCode);
            var attemptedSettlement = command.Request with
            {
                Key = command.Request.Key + ":unauthorized-settlement", Kind = nameof(ObservedFactKind.Settlement),
            };
            _ = await store.AdmitAsync(Tenant, id, attemptedSettlement, token);
            var denied = await store.AdvanceOneStepAsync(Tenant, id, token);
            Assert.Equal(V4CommandStatus.Cancelled, denied.Command?.Status);
            Assert.Equal("payment-not-applicable", denied.Command?.OutcomeCode);
            Assert.Equal(workerScreeningSequence, (await store.ReadFinanceHistoryAsync(Tenant, id, id, token)).LastFact);
            output.WriteLine($"Published Worker Linux AMD64: fact={receipt.LastFact} prefix={receipt.PrefixDigest} "
                + $"vector={evidence.VectorDigest} ppm={evidence.Ppm} reserved={payment.ReservedMinor}");
            return new(V4StepKind.ProcessedCommand, completed);

            async Task AssertScreeningRolledBack()
            {
                Assert.Equal(prior, (await store.ReadFinanceHistoryAsync(Tenant, id, id, token)).LastFact);
                Assert.Equal(V4CommandStatus.Pending, (await store.AdmitAsync(Tenant, id, command.Request, token)).Status);
                Assert.Equal(PaymentStatus.Initiated, (await ledger.GetPaymentAsync(Tenant, id, intentId, token))?.Status);
                Assert.Null(await ledger.GetCaseAsync(Tenant, id, intentId, token));
                Assert.Equal(0, await PaymentKit.Scalar<long>(db.Owner,
                    "SELECT count(*) FROM revenant.hold WHERE scenario_run_id = @run AND payment_id = @payment",
                    ("run", id.Value), ("payment", pending.PaymentId)));
            }
        });
        var config = V4EpisodeConfig.RiskRingChallenge;
        var design = V4EpisodePlanner.Plan(config, config.Roots().Single(x => x.Ordinal == 1), id);
        _ = await executor.ExecuteAsync(Tenant, Owner, design.Financial, ct, ObservedRiskMode.ModelRequired);
        Assert.NotNull(workerEvidence);

        async Task<RevenantV4DispatchReceipt> RunWorker(int steps, CancellationToken token)
        {
            var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { published!, "--v4-dispatch", id.Value.ToString("D"), steps.ToString(System.Globalization.CultureInfo.InvariantCulture) })
                start.ArgumentList.Add(arg);
            start.Environment["ConnectionStrings__Postgres"] = logins.Worker;
            start.Environment["ConnectionStrings__RevenantLedger"] = logins.WorkerLedger;
            start.Environment["ConnectionStrings__Redis"] = "localhost:6379";
            start.Environment["Revenant__Ledger__TenantId"] = Tenant;
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the published Worker.");
            var stdout = process.StandardOutput.ReadToEndAsync(token);
            var stderr = process.StandardError.ReadToEndAsync(token);
            try
            {
                await process.WaitForExitAsync(token);
                var text = await stdout;
                Assert.True(process.ExitCode == 0, $"{text} {await stderr}");
                return JsonSerializer.Deserialize<RevenantV4DispatchReceipt>(text.Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1])
                    ?? throw new InvalidOperationException("Published Worker returned no V4 receipt.");
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Published_R5_score_admits_a_real_V4_reservation_and_replays_cold()
    {
        using var time = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = time.Token;
        var logins = await db.ProvisionDeployedLoginsAsync();
        await using var host = Host(logins.Worker);
        var engine = host.GetRequiredService<ITimelineEngine<ObservedFinanceState, ObservedFinanceInput, ObservedFinanceContext>>();
        var ledger = PaymentKit.Store(db.Writer);
        var model = new Lazy<Marrow.Kernel.Results.Result<BehavioralRiskScorerV2, BehavioralRiskFailureV2>>(() =>
            BehavioralRiskScorerV2.Load(Path.Combine(AppContext.BaseDirectory, "Intelligence", "Artifacts",
                BehavioralRiskScorerV2.ArtifactDirectory), BehavioralRiskScorerV2.FeatureSchema, BehavioralRiskScorerV2.PublishedPin));
        Assert.True(model.Value.IsSuccess, model.Value.IsFailure ? model.Value.Error.ToString() : null);
        var store = new V4SimulationStore(db.Writer, ledger: ledger, model: model);
        var branch = new V4BranchExecution(store, engine);
        var executor = new V4EpisodeExecutor(store, branch, ledger);
        var config = V4EpisodeConfig.RiskRingChallenge;
        foreach (var group in config.Roots())
        {
            var id = new ScenarioRunId(Guid.NewGuid());
            var design = V4EpisodePlanner.Plan(config, group, id);
            var executed = await executor.ExecuteAsync(Tenant, Owner, design.Financial, ct, ObservedRiskMode.ModelRequired);
            var segment = Assert.Single(executed.History.Segments);
            var screening = segment.Facts.Where(f => f.Input.Kind == ObservedFactKind.Screening)
                .Select(f => V4ModelScreeningEvidence.Decode(f.Input)).ToArray();
            Assert.Contains(screening, e => e.Decision == V4ScreeningResult.WarmupRulesOnly);
            output.WriteLine($"Root {group.Ordinal}: {screening.Count(e => e.Ppm.HasValue)} scores, "
                + $"{screening.Count(e => e.AboveThreshold)} requests, {screening.Count(e => e.Admitted)} admissions.");
            var hold = screening.FirstOrDefault(e => e.Admitted);
            if (hold is null) continue;

            var prefix = executed.History with
            {
                Segments = [segment with { Facts = segment.Facts.TakeWhile(f => f.Input.Sequence < hold.Sequence).ToArray(), LastFact = hold.PriorSequence }],
            };
            var asOf = V4ObservationProjector.RiskAt(prefix, hold.IntentId, hold.At);
            Assert.Equal(asOf.Vector.Encode(), hold.Vector.ToArray());
            Assert.Equal(asOf.Vector.Digest, hold.VectorDigest);
            Assert.Equal(asOf.CutoffPrefixDigest, hold.PriorDigest);
            var score = model.Value.Value.Score(hold.Vector.AsSpan()).Value;
            Assert.Equal(score.RiskPpm, hold.Ppm);
            Assert.Equal(BitConverter.SingleToInt32Bits(score.RawScore), hold.RawBits);
            Assert.True(hold.AboveThreshold && hold.OpensCase);
            Assert.Equal(PaymentReason.None, hold.BaseReason);
            Assert.True(hold.Available > 0);

            var payment = await ledger.GetPaymentAsync(Tenant, id, hold.IntentId, ct);
            Assert.NotNull(payment);
            Assert.Equal(PaymentStatus.Held, payment.Status);
            Assert.Equal(1, await PaymentKit.Scalar<long>(db.Owner, """
                SELECT count(*) FROM revenant.hold h JOIN revenant.review_case c ON c.payment_id = h.payment_id
                WHERE h.scenario_run_id = @run AND h.payment_id = @payment AND h.status = 'Active'
                  AND c.status = 'Open' AND h.amount_minor = @amount
                """, ("run", id.Value), ("payment", payment.PaymentId), ("amount", payment.AmountMinor)));
            Assert.Equal(1, await PaymentKit.Scalar<long>(db.Owner, """
                SELECT count(*) FROM revenant.finance_fact f JOIN revenant.v4_scheduled_command q
                  ON (f.scenario_run_id, f.v4_command_seq) = (q.run_id, q.command_seq)
                WHERE f.scenario_run_id = @run AND f.fact_seq = @seq AND q.status = 'Completed'
                  AND f.evidence_schema = @schema AND f.evidence_sha256 = sha256(f.canonical)
                  AND f.canonical = @bytes
                """, ("run", id.Value), ("seq", hold.Sequence), ("schema", V4ModelScreeningEvidence.Schema),
                ("bytes", hold.ToInput().Payload.ToArray())));

            await using var coldHost = Host(logins.Worker);
            var coldEngine = coldHost.GetRequiredService<ITimelineEngine<ObservedFinanceState, ObservedFinanceInput, ObservedFinanceContext>>();
            var coldStore = new V4SimulationStore(db.Writer);
            var binding = await coldStore.ReadBindingAsync(Tenant, id, ct);
            var replay = (await coldEngine.ReplayAsync(binding.TimelineId, ct)).Value;
            Assert.Equal(HistoryStatus.Verified, replay.Status);
            Assert.Equal(segment.LastFact, replay.FinalPosition);
            var before = (await coldEngine.RestoreAsync(binding.TimelineId, hold.PriorSequence, ct)).Value.State;
            var after = (await coldEngine.RestoreAsync(binding.TimelineId, hold.Sequence, ct)).Value.State;
            var source = before.Ledger.Payments[hold.IntentId].Intent.SourceAccountId;
            var prior = before.Ledger.Accounts[source];
            var next = after.Ledger.Accounts[source];
            Assert.Equal(prior.PostedMinor, next.PostedMinor);
            Assert.Equal(prior.ReservedMinor + payment.AmountMinor, next.ReservedMinor);
            Assert.Equal(prior.AvailableMinor - payment.AmountMinor, next.AvailableMinor);
            var end = replay.FinalState.Ledger.Accounts[source];
            Assert.True(end.ReservedMinor >= payment.AmountMinor);
            Assert.Equal(1, await PaymentKit.Scalar<long>(db.Owner, """
                SELECT count(*) FROM revenant.account_available_balance
                WHERE scenario_run_id = @run AND local_account_id = @account
                  AND posted_minor = @posted AND reserved_minor = @reserved AND available_minor = @available
                """, ("run", id.Value), ("account", source), ("posted", end.PostedMinor), ("reserved", end.ReservedMinor),
                ("available", end.AvailableMinor)));
            var reconciled = await new V4BranchExecution(coldStore, coldEngine).SynchronizeAsync(Tenant, id, ct);
            Assert.Equal(replay.FinalState.PrefixDigest, reconciled.PrefixDigest);
            output.WriteLine($"Execution={id.Value} Timeline={binding.TimelineId.Value} fact={hold.Sequence} "
                + $"prefix={hold.PriorDigest} vector={hold.VectorDigest} ppm={hold.Ppm} "
                + $"eligible={hold.Capacity.Eligible} slots={hold.Allowed} mandatory={hold.Capacity.Mandatory} "
                + $"priorLearned={hold.Capacity.Learned} before={prior.PostedMinor}/{prior.ReservedMinor}/{prior.AvailableMinor} "
                + $"after={next.PostedMinor}/{next.ReservedMinor}/{next.AvailableMinor}");
            return;
        }
        Assert.Fail("No real R5 score admitted a learned hold across the four deterministic six-bank roots.");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task V2_six_bank_smoke_verifies_real_opening_risk_export_and_reader()
    {
        using var time = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        var config = V4EpisodeConfig.RiskSmoke;
        var directory = Directory.CreateTempSubdirectory("r5-smoke-");
        try
        {
            var logins = await db.ProvisionDeployedLoginsAsync();
            await using var host = Host(logins.Worker);
            var (store, executor) = Executor(host);
            var group = Assert.Single(config.Roots());
            var (design, execution, projection) = await ExecuteRiskRoot(config, group, store, executor, time.Token);
            V4DatasetManifest manifest;
            using (var exporter = new V4DatasetExporter(config, TestRevision, directory.FullName, writeTables: true))
            {
                exporter.Add(design, execution, projection);
                manifest = exporter.Complete();
            }
            var reader = new V4RiskDatasetReader(directory.FullName, manifest.Sha256);
            var rows = reader.ReadSplit(group.Split);
            Assert.Contains(rows, r => r.Truth.Malicious && r.Truth.Attack == EpisodeAttack.Ring);
            Assert.Contains(rows, r => r.Truth.HardNegative);
            Assert.Equal(576 * 6, projection.Banks.Count);
            var ordinary = design.Truth.Payments.First(x => !x.Malicious);
            var attack = design.Truth.Payments.First(x => x.Malicious);
            using (var relabeled = new V4DatasetExporter(config, TestRevision))
            {
                var changed = ordinary with { Malicious = true, Attack = attack.Attack, CampaignKey = attack.CampaignKey, HardNegative = false };
                relabeled.Add(design with { Truth = design.Truth with { Payments = design.Truth.Payments.Replace(ordinary, changed) } },
                    execution, projection);
                var changedSplit = Assert.Single(relabeled.Complete().Splits);
                Assert.Equal(manifest.Splits[0].ModelSha256, changedSplit.ModelSha256);
                Assert.NotEqual(manifest.Splits[0].TruthSha256, changedSplit.TruthSha256);
            }
            var damaged = Path.Combine(directory.FullName, "Train-risk-asof.bin");
            using (var stream = File.Open(damaged, FileMode.Open, FileAccess.Write))
                stream.WriteByte(255);
            Assert.Throws<Marrow.Kernel.Canonical.CanonicalFormatException>(() => reader.ReadSplit(group.Split));
            output.WriteLine($"V2 native smoke: {rows.Count} eligible rows, {manifest.Sha256}");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "R5Cohort")]
    public async Task Generate_native_R5_cohort()
    {
        var config = Environment.GetEnvironmentVariable("R5_DATASET_MODE") switch
        {
            "qualification" => V4EpisodeConfig.RiskQualification,
            "challenge" => V4EpisodeConfig.RiskRingChallenge,
            _ => throw new InvalidOperationException("Select qualification or the frozen independent challenge explicitly."),
        };
        var revision = Environment.GetEnvironmentVariable("R5_CODE_REVISION") ?? throw new InvalidOperationException("Source revision required.");
        var directory = Environment.GetEnvironmentVariable("R5_DATASET_OUTPUT") ?? throw new InvalidOperationException("Output directory required.");
        using var time = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        var logins = await db.ProvisionDeployedLoginsAsync();
        await using var host = Host(logins.Worker);
        var (store, executor) = Executor(host);
        V4DatasetManifest manifest;
        using (var exporter = new V4DatasetExporter(config, revision, directory, writeTables: true))
        {
            foreach (var group in config.Roots())
            {
                var (design, executed, projected) = await ExecuteRiskRoot(config, group, store, executor, time.Token);
                exporter.Add(design, executed, projected);
                output.WriteLine($"{group.Split} {group.Ordinal}: {projected.Banks.Count / 6} seals; {projected.RiskAsOf.Count} screening rows");
            }
            manifest = exporter.Complete();
        }
        Assert.Equal(config.Roots().Count() * 864 * 6, manifest.Splits.Sum(s => s.Tables.Single(t => t.Name == "bank").Rows));
        if (config.ShiftedRing)
        {
            output.WriteLine($"Independent ring challenge manifest {manifest.Sha256}");
            return;
        }
        var reader = new V4RiskDatasetReader(directory, manifest.Sha256);
        var developmentEvidence = new List<object>();
        foreach (var split in new[] { V4DatasetSplit.Train, V4DatasetSplit.Validation })
        {
            var rows = reader.ReadSplit(split);
            output.WriteLine($"{split}: {rows.Count} eligible opportunities, {rows.Count(r => r.Truth.Malicious)} malicious; "
                + $"{rows.Count(r => r.Screening == nameof(PaymentStatus.Held))} v7 holds");
            developmentEvidence.Add(new
            {
                Split = split.ToString(),
                Day = Distribution(rows, r => (r.At - r.OriginMs) / V4EpisodeConfig.WarmupMs),
                Weekday = Distribution(rows, r => r.AsOf.UtcDayOfWeek),
                Hour = Distribution(rows, r => r.AsOf.UtcMinuteOfDay / 60),
                HistoryCoverage = Distribution(rows, r => string.Join(",", r.AsOf.Windows.Select(w => w.Coverage))),
                AmountBand = Distribution(rows, r => r.AsOf.AmountMinor switch
                {
                    < 10_000 => "<100 USD", < 50_000 => "100–499 USD", < 100_000 => "500–999 USD",
                    < 200_000 => "1,000–1,999 USD", _ => "2,000+ USD",
                }),
                ReportedNovelty = Distribution(rows, r => r.AsOf.ReportedNewBeneficiary),
                Roots = Distribution(rows, r => r.Root),
                Attack = Distribution(rows.Where(r => r.Truth.Malicious), r => r.Truth.Attack!.Value.ToString()),
                Baselines = new[] { 2, 5, 10 }.Select(percent => new
                {
                    Percent = percent,
                    V7 = Review(V4RiskReviewBudget.Evaluate(rows, percent, true, null)),
                    Heuristic = Review(V4RiskReviewBudget.Evaluate(rows, percent, false, V4RiskReviewBudget.Heuristic)),
                    V7PlusHeuristic = Review(V4RiskReviewBudget.Evaluate(rows, percent, true, V4RiskReviewBudget.Heuristic)),
                }),
            });
        }
        File.WriteAllBytes(Path.Combine(directory, "development-evidence.json"), JsonSerializer.SerializeToUtf8Bytes(
            developmentEvidence, new JsonSerializerOptions { WriteIndented = true }));
        output.WriteLine($"R5 qualification export manifest {manifest.Sha256}; risk schema {Revenant.Intelligence.RiskFeatureVectorV2.SchemaHash}");

        static object Distribution<TKey>(IEnumerable<V4RiskResearchRow> rows, Func<V4RiskResearchRow, TKey> key) =>
            rows.GroupBy(key).OrderBy(g => g.Key).Select(g => new
            {
                Key = g.Key, Malicious = g.Count(r => r.Truth.Malicious), Legitimate = g.Count(r => !r.Truth.Malicious),
            }).ToArray();

        static object Review(V4ReviewResult r) => new
        {
            r.Opportunities, r.Rejections, r.MandatoryHolds, r.Slots, r.Additional, r.Reviewed, r.Malicious, r.Legitimate,
            r.HardNegatives, r.TotalMalicious, r.Overloaded, r.ReviewRate, r.Recall, r.Precision,
        };
    }

    private static async Task<(V4EpisodeDesign Design, V4ExecutedEpisode Executed, V4ObservationProjector Projected)>
        ExecuteRiskRoot(V4EpisodeConfig config, V4RootGroup group, V4SimulationStore store, V4EpisodeExecutor executor,
            CancellationToken ct)
    {
        var design = V4EpisodePlanner.Plan(config, group, new(Guid.NewGuid()));
        var execution = await executor.ExecuteAsync(Tenant, Owner, design.Financial, ct);
        var projection = V4ObservationProjector.Project(execution.History, captureRisk: true);
        var segment = Assert.Single(execution.History.Segments);
        var firstEligible = projection.RiskAsOf.First(x => x.EffectiveTimeMs - design.Financial.Opening.Manifest.StartUtcMs
            >= V4EpisodeConfig.WarmupMs);
        var prefix = execution.History with
        {
            Segments = [segment with { Facts = segment.Facts.Take(checked((int)firstEligible.CutoffFactSeq)).ToArray(),
                LastFact = firstEligible.CutoffFactSeq }],
        };
        var live = V4ObservationProjector.RiskAt(prefix, firstEligible.IntentId, firstEligible.EffectiveTimeMs);
        Assert.Equal(firstEligible.CutoffPrefixDigest, live.CutoffPrefixDigest);
        Assert.Equal(firstEligible.Vector.Encode(), live.Vector.Encode());
        await store.MaterializeBanksAsync(Tenant, execution.History.ExecutionRunId, projection.Banks, ct);
        await store.MaterializeAccountsAsync(Tenant, execution.History.ExecutionRunId, projection.Accounts, ct);
        return (design, execution, projection);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Generated_smoke_reproduces_semantic_exports_under_different_execution_ids()
    {
        using var time = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = time.Token;
        var config = V4EpisodeConfig.Smoke;
        var group = Assert.Single(config.Roots());
        var logins = await db.ProvisionDeployedLoginsAsync();
        await using var host = Host(logins.Worker);
        var (store, executor) = Executor(host);
        using var one = new V4DatasetExporter(config, TestRevision);
        using var two = new V4DatasetExporter(config, TestRevision);
        var repeatA = V4EpisodePlanner.Plan(config, group, new(Guid.NewGuid()));
        var repeatB = V4EpisodePlanner.Plan(config, group, new(Guid.NewGuid()));
        var a = await executor.ExecuteAsync(Tenant, Owner, repeatA.Financial, ct);
        var b = await executor.ExecuteAsync(Tenant, Owner, repeatB.Financial, ct);
        var observationsA = V4ObservationProjector.Project(a.History);
        var observationsB = V4ObservationProjector.Project(b.History);
        Assert.NotEqual(a.History.ExecutionRunId, b.History.ExecutionRunId);
        Assert.NotEqual(observationsA.Banks[0].Seal.PrefixDigest, observationsB.Banks[0].Seal.PrefixDigest);
        Assert.Equal(144, observationsA.Banks.Count);
        Assert.NotEmpty(observationsA.Accounts);
        await store.MaterializeBanksAsync(Tenant, a.History.ExecutionRunId, observationsA.Banks, ct);
        await store.MaterializeAccountsAsync(Tenant, a.History.ExecutionRunId, observationsA.Accounts, ct);
        one.Add(repeatA, a, observationsA);
        two.Add(repeatB, b, observationsB);
        var manifest = one.Complete();
        Assert.Equal(manifest.Sha256, two.Complete().Sha256);

        // Change only the offline annotation, retaining the original commands, SQL facts, and observations.
        var original = repeatA.Truth.Payments.First(x => !x.Malicious);
        var attack = repeatA.Truth.Payments.First(x => x.Malicious);
        var flipped = original with { Malicious = true, Attack = attack.Attack, CampaignKey = attack.CampaignKey, HardNegative = false };
        using var relabeled = new V4DatasetExporter(config, TestRevision);
        relabeled.Add(repeatA with { Truth = repeatA.Truth with { Payments = repeatA.Truth.Payments.Replace(original, flipped) } },
            a, observationsA);
        var relabeledManifest = relabeled.Complete();
        Assert.Equal(manifest.Splits[0].ModelSha256, relabeledManifest.Splits[0].ModelSha256);
        Assert.NotEqual(manifest.Splits[0].TruthSha256, relabeledManifest.Splits[0].TruthSha256);
        var split = manifest.Splits[0];
        output.WriteLine($"2h generated smoke repeated under distinct execution IDs: {manifest.Sha256}; {split.Screened} screenings, {split.Settled} settlements, {split.Held} holds, {split.Returned} returns, {observationsA.Accounts.Count} account rows; root execution times {a.Timing.Total.TotalSeconds:F3}s/{b.Timing.Total.TotalSeconds:F3}s");
    }

    [Fact]
    [Trait("Category", "R4Cohort")]
    public async Task Generate_native_R4_cohort()
    {
        var mode = Environment.GetEnvironmentVariable("R4_DATASET_MODE");
        var config = mode switch
        {
            "pilot" => V4EpisodeConfig.Pilot,
            "qualification" => V4EpisodeConfig.Qualification,
            _ => throw new InvalidOperationException("Select pilot or qualification explicitly."),
        };
        var revision = Environment.GetEnvironmentVariable("R4_CODE_REVISION") ?? throw new InvalidOperationException("Source revision required.");
        var directory = Environment.GetEnvironmentVariable("R4_DATASET_OUTPUT") ?? throw new InvalidOperationException("Output directory required.");
        var full = Environment.GetEnvironmentVariable("R4_WRITE_TABLES") == "1";
        using var time = new CancellationTokenSource(TimeSpan.FromMinutes(mode == "pilot" ? 10 : 30));
        var ct = time.Token;
        using var export = new V4DatasetExporter(config, revision, directory, full);
        var timer = Stopwatch.StartNew();
        var logins = await db.ProvisionDeployedLoginsAsync();
        await using var host = Host(logins.Worker);
        var (store, executor) = Executor(host);
        var reports = new List<object>();
        using var process = Process.GetCurrentProcess();
        foreach (var group in config.Roots())
        {
            ct.ThrowIfCancellationRequested();
            var design = V4EpisodePlanner.Plan(config, group, new(Guid.NewGuid()));
            var executed = await executor.ExecuteAsync(Tenant, Owner, design.Financial, ct);
            var start = Stopwatch.GetTimestamp();
            var projected = V4ObservationProjector.Project(executed.History);
            var projectionMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            Assert.Equal(config.Duration / ObservedFinanceVersion.BucketMs * 6, projected.Banks.Count);
            await store.MaterializeBanksAsync(Tenant, executed.History.ExecutionRunId, projected.Banks, ct);
            await store.MaterializeAccountsAsync(Tenant, executed.History.ExecutionRunId, projected.Accounts, ct);
            export.Add(design, executed, projected);
            process.Refresh();
            reports.Add(new
            {
                group.Key, group.Split,
                Accounts = design.Financial.Opening.Manifest.Accounts.Length,
                Facts = executed.History.Segments[0].LastFact,
                Seals = projected.Banks.Count / 6, Bank = projected.Banks.Count, Activity = projected.Accounts.Count,
                AsOf = projected.FraudAsOf.Count, Attempts = design.Financial.Payments.Length,
                ExecutionMs = executed.Timing.Total.TotalMilliseconds,
                ReplayMs = executed.Timing.Replay.TotalMilliseconds, ProjectionMs = projectionMs,
                PeakWorkingSetBytes = process.PeakWorkingSet64,
                Bands = executed.Timing.Bands.Select(b => new { b.Band, b.Commands, DispatchMs = b.DispatchTime.TotalMilliseconds }),
            });
            output.WriteLine($"{group.Split} {group.Ordinal}: {projected.Banks.Count / 6} seals; {executed.History.Segments[0].LastFact} facts; {executed.Timing.Total.TotalSeconds:F3}s");
        }

        var manifest = export.Complete();
        var totalBanks = manifest.Splits.Sum(split => split.Tables.Single(table => table.Name == "bank").Rows);
        var expectedBanks = mode == "pilot" ? 6_912 : 124_416;
        Assert.Equal(expectedBanks, totalBanks);
        Assert.Equal(expectedBanks - manifest.Roots.Count * 8 * 6, manifest.Splits.Sum(split => split.FullTargets));
        File.WriteAllBytes(Path.Combine(directory, "performance.json"), JsonSerializer.SerializeToUtf8Bytes(new
        {
            Mode = mode, Revision = revision, Roots = manifest.Roots.Count, TotalMs = timer.Elapsed.TotalMilliseconds,
            PeakWorkingSetBytes = process.PeakWorkingSet64, RootsEvidence = reports,
        }, new JsonSerializerOptions { WriteIndented = true }));
        output.WriteLine($"{mode}: {manifest.Roots.Count} roots; {totalBanks / 6} seals; {totalBanks} bank records; manifest {manifest.Sha256}; {timer.Elapsed.TotalSeconds:F3}s");
    }

    private sealed class CaseFailingOutbox(IOutboxWriter writer) : IOutboxWriter
    {
        public Task EnqueueAsync(OutboxMessage message, CancellationToken ct) =>
            message.EventType == PaymentLedgerStore.EventCaseOpened
                ? throw new InvalidOperationException("injected case outbox failure")
                : writer.EnqueueAsync(message, ct);
    }

    private (V4SimulationStore Store, V4EpisodeExecutor Executor) Executor(ServiceProvider host)
    {
        var engine = host.GetRequiredService<ITimelineEngine<ObservedFinanceState, ObservedFinanceInput, ObservedFinanceContext>>();
        var ledger = PaymentKit.Store(db.Writer);
        var store = new V4SimulationStore(db.Writer, ledger: ledger);
        return (store, new(store, new V4BranchExecution(store, engine), ledger));
    }

    private static ServiceProvider Host(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        services.AddMarrowTimeline(ObservedFinanceTimelineVersions.All, ObservedFinanceTimelineVersions.CheckpointInterval);
        return services.BuildServiceProvider();
    }
}
