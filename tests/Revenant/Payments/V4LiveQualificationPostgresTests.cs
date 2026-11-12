using System.Diagnostics.Metrics;
using System.Text.Json;
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
using Revenant.Synthetic;
using Revenant.Tests.Ledger;
using Revenant.Timeline;
using Xunit;
using Xunit.Abstractions;

namespace Revenant.Tests.Payments;

[Collection(PaymentCollection.Name)]
public sealed class V4LiveQualificationPostgresTests(LedgerDatabase db, ITestOutputHelper output)
{
    private const string Tenant = PaymentKit.TenantA;
    private const string Owner = PaymentKit.Operator;

    [Fact]
    [Trait("Category", "R6Qualification")]
    public async Task Six_bank_model_required_run_measures_interventions_against_rules_only()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = timeout.Token;
        var destination = Environment.GetEnvironmentVariable("REVENANT_R6_EVIDENCE_DIR")
            ?? throw new InvalidOperationException("Set REVENANT_R6_EVIDENCE_DIR to retain the qualification evidence.");
        Directory.CreateDirectory(destination);
        var logins = await db.ProvisionDeployedLoginsAsync();
        await using var host = TimelineHost(logins.Worker);
        var engine = Engine(host);
        await using var writer = NpgsqlDataSource.Create(logins.WorkerLedger);
        var ledger = PaymentKit.Store(writer);
        var model = new Lazy<Marrow.Kernel.Results.Result<BehavioralRiskScorerV2, BehavioralRiskFailureV2>>(() =>
            BehavioralRiskScorerV2.Load(Path.Combine(AppContext.BaseDirectory, "Intelligence", "Artifacts",
                BehavioralRiskScorerV2.ArtifactDirectory), BehavioralRiskScorerV2.FeatureSchema, BehavioralRiskScorerV2.PublishedPin));
        var store = new V4SimulationStore(writer, ledger: ledger, model: model);
        var branch = new V4BranchExecution(store, engine);
        var executor = new V4EpisodeExecutor(store, branch, ledger);
        var config = V4EpisodeConfig.RiskRingChallenge;
        var scoringTimes = new List<double>();
        var transactionTimes = new List<double>();
        var measure = true;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, sink) =>
            {
                if (instrument.Meter.Name == V4FinanceMetrics.MeterName)
                    sink.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, _, _) =>
        {
            if (!measure) return;
            if (instrument.Name == V4FinanceMetrics.ScoringName) scoringTimes.Add(value);
            if (instrument.Name == V4FinanceMetrics.DispatchName) transactionTimes.Add(value);
        });
        listener.Start();
        // Roots 0 and 1 already ran in the vertical checkpoint. Root 0 admitted no model review
        // and root 1 admitted a hard negative. Try the remaining roots in a fixed order.
        var trials = new List<object>();
        (V4RootGroup Group, ScenarioRunId Id, V4EpisodeDesign Plan, V4ExecutedEpisode Episode, V4ModelScreeningEvidence Hold)? chosen = null;
        foreach (var group in config.Roots().Where(g => g.Ordinal >= 2))
        {
            scoringTimes.Clear();
            transactionTimes.Clear();
            var id = new ScenarioRunId(Guid.NewGuid());
            var plan = V4EpisodePlanner.Plan(config, group, id);
            var episode = await executor.ExecuteAsync(Tenant, Owner, plan.Financial, ct, ObservedRiskMode.ModelRequired, complete: false);
            var screened = ReadEvidence(episode);
            var malicious = plan.Truth.Payments.Where(t => t.Malicious).Select(t => t.IntentId).ToHashSet(StringComparer.Ordinal);
            var hold = screened.FirstOrDefault(e => e.Admitted && malicious.Contains(e.IntentId));
            trials.Add(new { group.Ordinal, Scored = screened.Count(e => e.Ppm.HasValue),
                Requests = screened.Count(e => e.AboveThreshold), Learned = screened.Count(e => e.Admitted), Malicious = hold is not null });
            output.WriteLine(JsonSerializer.Serialize(trials[^1]));
            if (hold is not null && screened.Any(e => e.Suppressed))
            {
                chosen = (group, id, plan, episode, hold);
                break;
            }
            Assert.Equal(HistoryStatus.Verified, (await branch.CompleteAsync(Tenant, id, ct)).Status);
        }
        Assert.True(chosen.HasValue, "The remaining deterministic challenge roots produced no malicious learned hold with a capacity suppression.");
        var (selectedGroup, learnedId, livePlan, learned, held) = chosen.Value;
        var liveSegment = Assert.Single(learned.History.Segments);
        var evidence = ReadEvidence(learned);
        Assert.Equal(evidence.Count(e => e.Ppm.HasValue), scoringTimes.Count);

        // Only offline qualification joins the hidden labels after dispatch.
        var truth = livePlan.Truth.Payments.ToDictionary(t => t.IntentId, StringComparer.Ordinal);
        var planByIntent = livePlan.Financial.Payments.ToDictionary(p => p.Intent.IntentId, p => p.Intent, StringComparer.Ordinal);
        Assert.Contains(truth.Values, t => t.HardNegative);
        var baselineId = new ScenarioRunId(Guid.NewGuid());
        var basePlan = V4EpisodePlanner.Plan(config, selectedGroup, baselineId);
        Assert.Equal(basePlan.HistoryFingerprint, livePlan.HistoryFingerprint);
        Assert.Equal(basePlan.Financial.Payments.Select(x => x.Intent.IntentId), livePlan.Financial.Payments.Select(x => x.Intent.IntentId));
        measure = false;
        var baseline = await executor.ExecuteAsync(Tenant, Owner, basePlan.Financial, ct);
        measure = true;
        var baselineOutcome = Assert.Single(baseline.Outcomes, e => e.IntentId == held.IntentId);
        var heldOutcome = Assert.Single(learned.Outcomes, e => e.IntentId == held.IntentId);
        Assert.Equal(nameof(PaymentStatus.Cleared), baselineOutcome.Screening);
        Assert.Equal(nameof(PaymentStatus.Settled), baselineOutcome.Settlement);
        Assert.Equal(nameof(PaymentStatus.Held), heldOutcome.Screening);
        Assert.Null(heldOutcome.Settlement);
        Assert.Contains(learned.Outcomes, e => e.Settlement == nameof(PaymentStatus.Settled) && !truth[e.IntentId].Malicious);
        var payer = planByIntent[held.IntentId];
        var afterHold = V4ObservationProjector.Project(learned.History, captureRisk: true);
        var baseObservations = V4ObservationProjector.Project(baseline.History, captureRisk: true);
        var firstSeal = ObservedFinanceVersion.NextBoundary(held.LastSeal);
        var liveBank = Assert.Single(afterHold.Banks, b => b.BankId == payer.SourceBankId && b.Seal.BoundaryMs == firstSeal);
        var baseBank = Assert.Single(baseObservations.Banks, b => b.BankId == payer.SourceBankId && b.Seal.BoundaryMs == firstSeal);
        Assert.True(liveBank.Reserved >= payer.Amount.MinorUnits);
        Assert.True(baseBank.SettledOut - liveBank.SettledOut >= payer.Amount.MinorUnits);
        var account = afterHold.AccountAsOf(payer.SourceAccountId, firstSeal);
        Assert.NotNull(account);
        Assert.True(account.Balance.Reserved >= payer.Amount.MinorUnits);
        Assert.NotEmpty(afterHold.Accounts);
        var beforeReview = (await branch.SynchronizeAsync(Tenant, learnedId, ct)).Ledger;
        Assert.Equal(PaymentStatus.Held, beforeReview.Payments[held.IntentId].Status);
        Assert.False(beforeReview.Payments[held.IntentId].SettlementPosted);
        var baseVectors = baseObservations.RiskAsOf.ToDictionary(r => r.IntentId, StringComparer.Ordinal);
        var changed = afterHold.RiskAsOf.Where(r => r.CutoffFactSeq > held.Sequence
            && baseVectors.TryGetValue(r.IntentId, out var baselineRisk) && baselineRisk.Vector.Digest != r.Vector.Digest).ToArray();
        Assert.NotEmpty(changed);

        var review = await ledger.GetCaseAsync(Tenant, learnedId, held.IntentId, ct);
        Assert.NotNull(review);
        Assert.Equal(CaseStatus.Open, review.Status);
        Assert.Equal(1, review.RequiredApprovals);
        var end = livePlan.Financial.EndMs;
        var approved = await Dispatch(new("r6-qualification-approve", V4CommandClass.ConditionalHumanRequest,
            nameof(ObservedFactKind.ReviewDecision), "v4-human-request/1",
            [.. new CanonicalWriter().WriteInt32(1).WriteString(held.IntentId).WriteString(Owner)
                .WriteInt64(review.Version).WriteBoolean(true).WriteByte((byte)CaseAction.Approve).ToArray()], end, end));
        Assert.Equal(V4CommandStatus.Completed, approved.Status);
        Assert.Equal(CaseStatus.Released, (await ledger.GetCaseAsync(Tenant, learnedId, held.IntentId, ct))!.Status);
        var settlement = await Dispatch(new("r6-qualification-settle", V4CommandClass.DerivedOpportunity,
            nameof(ObservedFactKind.Settlement), "v4-payment-opportunity/1",
            [.. new CanonicalWriter().WriteInt32(1).WriteString(held.IntentId).ToArray()], end, end));
        Assert.Equal(nameof(PaymentStatus.Settled), settlement.OutcomeCode);
        Assert.Equal(V4StepKind.Sealed, (await store.SealAsync(Tenant, learnedId, ObservedFinanceVersion.NextBoundary(end), ct)).Kind);
        Assert.Equal(HistoryStatus.Verified, (await branch.CompleteAsync(Tenant, learnedId, ct)).Status);
        var complete = V4ObservationProjector.Project(await branch.ReadProjectionHistoryAsync(Tenant, learnedId, ct), captureRisk: true);
        var targets = V4BankTargets.Build(complete.Banks).ToArray();
        var bySeal = complete.Banks.ToDictionary(b => (b.BankId, b.Seal.BoundaryMs));
        foreach (var target in targets)
        {
            for (var i = 0; i < V4BankTargets.HorizonCount; i++)
            {
                var at = target.Origin.BoundaryMs + (i + 1) * ObservedFinanceVersion.BucketMs;
                var actual = bySeal.GetValueOrDefault((target.BankId, at));
                var horizon = target.Horizons[i];
                Assert.Equal(actual?.SettledOut, horizon?.SettledOut);
                Assert.Equal(actual?.SettledIn, horizon?.SettledIn);
                Assert.Equal(actual?.ReturnedOut, horizon?.ReturnedOut);
            }
        }
        Assert.Contains(targets, t => t.Horizons.All(h => h is null));
        Assert.Equal(6, complete.Banks.Select(b => b.BankId).Distinct().Count());

        await using var coldHost = TimelineHost(logins.Worker);
        var cold = Engine(coldHost);
        var baselineFinal = await Replay(baselineId);
        var learnedFinal = await Replay(learnedId);
        Assert.Equal(baselineFinal.TotalPostedMinor, learnedFinal.TotalPostedMinor);
        Assert.Equal(PaymentStatus.Settled, learnedFinal.Payments[held.IntentId].Status);
        Assert.Equal(0, learnedFinal.Payments[held.IntentId].ReservedMinor);
        Assert.All(complete.Banks, b => Assert.True(b.Balance.Posted >= b.Balance.Reserved && b.Balance.Available >= 0));

        var report = new
        {
            Revision = Environment.GetEnvironmentVariable("REVENANT_R6_SOURCE_SHA"),
            SharedPlan = livePlan.HistoryFingerprint,
            Trials = trials,
            RootOrdinal = selectedGroup.Ordinal,
            BaselineExecution = baselineId,
            LearnedExecution = learnedId,
            Warmup = evidence.Count(e => e.Decision == V4ScreeningResult.WarmupRulesOnly),
            Eligible = evidence.Last().NextCounts.Eligible,
            AllowedSlots = evidence.Last().NextCounts.Slots,
            Scored = scoringTimes.Count,
            AboveThreshold = evidence.Count(e => e.AboveThreshold),
            Requests = evidence.Where(e => e.AboveThreshold).Select(e => new
            {
                e.IntentId, e.At, e.Sequence, e.Ppm, e.Capacity.Eligible, e.Available,
                e.Admitted, e.Suppressed, Malicious = truth[e.IntentId].Malicious,
            }),
            Mandatory = evidence.Count(e => e.Decision == V4ScreeningResult.BaseRuleHold),
            LearnedHolds = evidence.Count(e => e.Admitted),
            Suppressed = evidence.Count(e => e.Suppressed),
            LegitimateLearned = evidence.Count(e => e.Admitted && !truth[e.IntentId].Malicious),
            MaliciousLearned = evidence.Count(e => e.Admitted && truth[e.IntentId].Malicious),
            InferenceFailures = 0,
            Retries = 0,
            Workload = evidence.Where(e => e.OpensCase).GroupBy(e =>
                    (Bank: planByIntent[e.IntentId].SourceBankId, Day: (e.At - liveSegment.Context.OriginMs) / V4EpisodeConfig.WarmupMs))
                .Select(g => new { g.Key.Bank, g.Key.Day, Cases = g.Count() }).ToArray(),
            PeakFiveMinuteReviews = evidence.Where(e => e.OpensCase).GroupBy(e => e.LastSeal).Max(g => g.Count()),
            PeakFiveMinuteThresholdRequests = evidence.Where(e => e.AboveThreshold).GroupBy(e => e.LastSeal).Max(g => g.Count()),
            BaselineAtEndOfPlan = Flow(baseObservations),
            LearnedAtEndOfPlan = Flow(afterHold),
            LearnedAfterReview = Flow(complete),
            BaselineTerminal = Terminal(baselineFinal),
            LearnedTerminal = Terminal(learnedFinal),
            LatencyMs = new { Scoring = Latency(scoringTimes), Dispatch = Latency(transactionTimes) },
            ChangedLaterFeatureVectors = changed.Length,
            Example = new
            {
                held.IntentId, held.Sequence, held.PriorSequence, held.PriorDigest, held.VectorDigest,
                CanonicalVector = Convert.ToBase64String(held.Vector.AsSpan()),
                EvidenceSha256 = held.ToInput().EvidenceSha256,
                held.Ppm, held.RawBits, held.Capacity.Eligible, held.Allowed, held.Available,
                AttackFamily = truth[held.IntentId].Attack?.ToString(),
                PrincipalReserved = payer.Amount.MinorUnits,
                BeforeReview = Balance(beforeReview.Accounts[payer.SourceAccountId]),
                Final = Balance(learnedFinal.Accounts[payer.SourceAccountId]),
                Baseline = Balance(baselineFinal.Accounts[payer.SourceAccountId]),
                Review = CaseStatus.Released.ToString(), Terminal = PaymentStatus.Settled.ToString(),
            },
        };
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(destination, "qualification.json"), json, ct);
        await File.WriteAllLinesAsync(Path.Combine(destination, "model-aware-bank-observations.jsonl"),
            complete.Banks.Select(b => JsonSerializer.Serialize(new { b.BankId, b.Seal.BoundaryMs, b.Digest, Canonical = Convert.ToBase64String(b.Encode()) })), ct);
        output.WriteLine(json);

        async Task<V4ScheduledCommand> Dispatch(V4CommandRequest request)
        {
            _ = await store.AdmitAsync(Tenant, learnedId, request, ct);
            var step = await store.AdvanceOneStepAsync(Tenant, learnedId, ct);
            Assert.Equal(V4StepKind.ProcessedCommand, step.Kind);
            Assert.NotNull(step.Command);
            Assert.Equal(request.Key, step.Command.Request.Key);
            return step.Command;
        }

        async Task<LedgerFinanceState> Replay(ScenarioRunId id)
        {
            var binding = await new V4SimulationStore(writer).ReadBindingAsync(Tenant, id, ct);
            var replayed = (await cold.ReplayAsync(binding.TimelineId, ct)).Value;
            Assert.Equal(HistoryStatus.Verified, replayed.Status);
            return replayed.FinalState.Ledger;
        }

        static V4ModelScreeningEvidence[] ReadEvidence(V4ExecutedEpisode episode) =>
            Assert.Single(episode.History.Segments).Facts.Where(f => f.Input.Kind == ObservedFactKind.Screening)
                .Select(f => V4ModelScreeningEvidence.Decode(f.Input)).ToArray();

        static object Flow(V4ObservationProjector p) => new
        {
            SettledMinor = p.Banks.Sum(b => b.SettledOut),
            ReturnedMinor = p.Banks.Sum(b => b.ReturnedOut),
            ReservedMinor = p.Banks.Sum(b => b.Reserved),
            ReleasedMinor = p.Banks.Sum(b => b.Released),
            Held = p.Banks.Sum(b => b.Held),
            RejectedAtScreening = p.Banks.Sum(b => b.Rejected),
        };
        static object Terminal(LedgerFinanceState state) => new
        {
            Payments = state.Payments.Values.GroupBy(p => p.Status).Select(g => new
            {
                Status = g.Key.ToString(), Count = g.Count(), PrincipalMinor = g.Sum(p => p.Intent.Amount.MinorUnits),
            }).ToArray(),
            Cases = state.Payments.Values.Where(p => p.Case is not null).GroupBy(p => p.Case).Select(g => new
            {
                Status = g.Key.ToString(), Count = g.Count(),
            }).ToArray(),
        };
        static object Balance(LedgerAccount a) => new { a.PostedMinor, a.ReservedMinor, a.AvailableMinor };
        static object Latency(List<double> values)
        {
            values.Sort();
            return new { Samples = values.Count, Median = values[values.Count / 2], P95 = values[(int)Math.Ceiling(values.Count * .95) - 1] };
        }
    }

    private static ServiceProvider TimelineHost(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        services.AddMarrowTimeline(ObservedFinanceTimelineVersions.All, ObservedFinanceTimelineVersions.CheckpointInterval);
        return services.BuildServiceProvider();
    }

    private static ITimelineEngine<ObservedFinanceState, ObservedFinanceInput, ObservedFinanceContext> Engine(ServiceProvider host) =>
        host.GetRequiredService<ITimelineEngine<ObservedFinanceState, ObservedFinanceInput, ObservedFinanceContext>>();
}
