using System.Collections.Immutable;
using System.Diagnostics;
using Marrow.Kernel.Canonical;
using Marrow.Timeline;
using Revenant.Cases;
using Revenant.Contracts;
using Revenant.Finance;
using Revenant.Intelligence;
using Revenant.Observations;
using Revenant.Payments;
using Revenant.Scenario;
using Revenant.Timeline;

namespace Revenant.Synthetic;

internal sealed record V4AttemptOutcome(string IntentId, string Intake, string? Screening, string? Settlement, string? Return);
internal sealed record V4DispatchBand(int Band, int Commands, TimeSpan DispatchTime);
internal sealed record V4EpisodeTiming(TimeSpan Total, TimeSpan Replay, ImmutableArray<V4DispatchBand> Bands);
internal sealed record V4ExecutedEpisode(V4ProjectionHistory History, ImmutableArray<V4AttemptOutcome> Outcomes, V4EpisodeTiming Timing);

internal sealed class V4EpisodeExecutor(V4SimulationStore store, V4BranchExecution branch, PaymentLedgerStore ledger,
    Func<V4ScheduledCommand, long, CancellationToken, Task<V4Step?>>? dispatchOverride = null)
{
    internal async Task<V4ExecutedEpisode> ExecuteAsync(string tenant, string owner, V4EpisodePlan plan, CancellationToken ct,
        ObservedRiskMode riskMode = ObservedRiskMode.NotRequired, bool complete = true, long? sealThrough = null,
        IReadOnlyDictionary<string, long>? reviewTimes = null)
    {
        var timer = Stopwatch.StartNew();
        var run = plan.Opening;
        var id = run.RunId;
        var origin = run.Manifest.StartUtcMs;
        var finalBoundary = sealThrough ?? plan.EndMs;
        if (!ObservedFinanceVersion.IsBoundary(finalBoundary) || finalBoundary > plan.EndMs || finalBoundary < origin
            || (sealThrough.HasValue && (complete || plan.Payments.Any(p =>
                p.Intent.EventTimeUtcMs >= ObservedFinanceVersion.NextBoundary(finalBoundary)))))
            throw new InvalidOperationException("A partial episode must stop in the requested open interval.");
        if (run.Manifest.V4EpisodeDurationMs is not { } duration || plan.EndMs != checked(origin + duration)
            || plan.Payments.Any(p => p.Intent.RunId != id || p.Intent.Truth != SyntheticTruth.Legitimate
                || p.Intent.EventTimeUtcMs < origin || p.Intent.EventTimeUtcMs >= plan.EndMs))
        {
            throw new InvalidOperationException("Financial plan does not match the pinned opening or neutral input contract.");
        }
        var context = new ObservedFinanceContext(run.Manifest.ScenarioVersion, run.Manifest.ContentHash, origin,
            PolicyCatalog.RefOf(PolicyCatalog.V7), ReviewPolicy.Version, riskMode,
            riskMode == ObservedRiskMode.ModelRequired ? BehavioralRiskScorerV2.FeatureSchema : "",
            riskMode == ObservedRiskMode.ModelRequired ? BehavioralRiskScorerV2.PublishedPin : null, [], []);
        await branch.OpenSourceAsync(tenant, owner, run, context, ct);
        var latest = origin;
        var factCount = 0;
        var bands = new SortedDictionary<int, (int Count, TimeSpan Elapsed)>();
        var outcomes = ImmutableArray.CreateBuilder<V4AttemptOutcome>(plan.Payments.Length);
        var reviews = new PriorityQueue<PaymentIntent, (long Time, long Sequence)>();
        foreach (var proposed in plan.Payments)
        {
            var p = proposed.Intent;
            var at = p.EventTimeUtcMs;
            await ReviewThrough(at);
            var intake = await Dispatch(new(p.IntentId + ":intake", V4CommandClass.Exogenous, "PaymentIntent",
                ScenarioSchemas.InputSchemaVersion, [.. PaymentIntentCodec.Instance.EncodeCanonical(p)], at, at));
            if (intake.Status != V4CommandStatus.Completed)
            {
                outcomes.Add(new(p.IntentId, Code(intake), null, null, null));
                continue;
            }
            var screening = await Dispatch(Opportunity("Screening", p));
            if (screening.OutcomeCode == nameof(PaymentStatus.Held) && reviewTimes is not null
                && reviewTimes.TryGetValue(p.IntentId, out var reviewTime))
            {
                if (reviewTime <= at || reviewTime >= finalBoundary || ReviewPolicy.RequiredApprovals("USD", p.Amount.MinorUnits) != 1)
                    throw new InvalidOperationException("The synthetic review must be after intake, before closure, and eligible for one approval.");
                reviews.Enqueue(p, (reviewTime, p.Sequence));
            }
            string? settlement = null;
            string? returned = null;
            if (screening.Status == V4CommandStatus.Completed && screening.OutcomeCode == nameof(PaymentStatus.Cleared))
            {
                var posted = await Dispatch(Opportunity("Settlement", p));
                settlement = Code(posted);
                if (posted.Status == V4CommandStatus.Completed && settlement == nameof(PaymentStatus.Settled) && proposed.RequestReturn)
                {
                    var payment = await ledger.GetPaymentAsync(tenant, id, p.IntentId, ct)
                        ?? throw new InvalidOperationException("The committed settlement is unreadable.");
                    var result = await Dispatch(new(p.IntentId + ":return", V4CommandClass.ConditionalHumanRequest,
                        "ReturnInstruction", "v4-human-request/1",
                        [.. new CanonicalWriter().WriteInt32(1).WriteString(p.IntentId).WriteString(owner)
                            .WriteInt64(payment.Version).WriteBoolean(true).WriteByte((byte)CaseAction.Assign).ToArray()], at, at));
                    returned = Code(result);
                }
            }
            outcomes.Add(new(p.IntentId, Code(intake), Code(screening), settlement, returned));
        }

        await ReviewThrough(finalBoundary);
        while (latest < finalBoundary)
        {
            var boundary = ObservedFinanceVersion.NextBoundary(latest);
            var step = await store.SealAsync(tenant, id, boundary, ct);
            AcceptSeal(step);
        }

        var replayStart = Stopwatch.GetTimestamp();
        if (complete)
        {
            var completed = await branch.CompleteAsync(tenant, id, ct);
            if (completed.Status != HistoryStatus.Verified || completed.FinalPosition != factCount)
                throw new InvalidOperationException("Episode completion does not verify its recorded fact count.");
        }
        var history = await branch.ReadProjectionHistoryAsync(tenant, id, ct);
        if (history.Segments[^1].LastFact != factCount)
            throw new InvalidOperationException("Episode dispatch count does not match the SQL journal.");
        return new(history, outcomes.MoveToImmutable(),
            new(timer.Elapsed, Stopwatch.GetElapsedTime(replayStart),
                [.. bands.Select(x => new V4DispatchBand(x.Key, x.Value.Count, x.Value.Elapsed))]));

        async Task ReviewThrough(long at)
        {
            while (reviews.TryPeek(out var p, out var priority) && priority.Time <= at)
            {
                reviews.Dequeue();
                var review = await ledger.GetCaseAsync(tenant, id, p.IntentId, ct)
                    ?? throw new InvalidOperationException("The committed review case is unreadable.");
                var reviewed = await Dispatch(new(p.IntentId + ":review", V4CommandClass.ConditionalHumanRequest,
                    "ReviewDecision", "v4-human-request/1",
                    [.. new CanonicalWriter().WriteInt32(1).WriteString(p.IntentId).WriteString(owner)
                        .WriteInt64(review.Version).WriteBoolean(true).WriteByte((byte)CaseAction.Approve).ToArray()],
                    priority.Time, priority.Time));
                if (reviewed.OutcomeCode != "reviewed")
                    throw new InvalidOperationException($"Synthetic review failed for {p.IntentId}: {reviewed.OutcomeCode}.");
                var settlement = await Dispatch(Opportunity("Settlement", p) with
                {
                    RequestedTimeMs = priority.Time, DueTimeMs = priority.Time,
                });
                if (settlement.OutcomeCode != nameof(PaymentStatus.Settled))
                    throw new InvalidOperationException($"Reviewed synthetic payment did not settle: {settlement.OutcomeCode}.");
            }
        }

        void AcceptSeal(V4Step step)
        {
            var boundary = ObservedFinanceVersion.NextBoundary(latest);
            if (step.Kind != V4StepKind.Sealed || step.Seal is not { } seal || seal.EffectiveTimeMs != boundary
                || seal.Sequence != factCount + 1)
            {
                throw new InvalidOperationException("A legal completed seal was required before continuing.");
            }
            latest = boundary;
            factCount++;
        }

        async Task<V4ScheduledCommand> Dispatch(V4CommandRequest request)
        {
            var admitted = await store.AdmitAsync(tenant, id, request, ct);
            while (true)
            {
                var start = Stopwatch.GetTimestamp();
                var dispatched = dispatchOverride is null ? null : await dispatchOverride(admitted, factCount, ct);
                var step = dispatched ?? await store.AdvanceOneStepAsync(tenant, id, ct);
                if (step.Kind == V4StepKind.Sealed)
                {
                    AcceptSeal(step);
                    continue;
                }

                if (step.Kind != V4StepKind.ProcessedCommand || step.Command is not { } command
                    || command.Request.Key != request.Key || !V4SimulationScheduler.IsTerminal(command.Status))
                {
                    throw new InvalidOperationException($"Episode command {request.Key} did not resolve in causal order: {step.Kind}.");
                }

                var band = factCount / 200;
                var before = bands.GetValueOrDefault(band);
                bands[band] = (before.Count + 1, before.Elapsed + Stopwatch.GetElapsedTime(start));
                if (command.Status == V4CommandStatus.Completed) factCount++;
                return command;
            }
        }
    }

    private static V4CommandRequest Opportunity(string kind, PaymentIntent payment) =>
        new($"{payment.IntentId}:{kind}", V4CommandClass.DerivedOpportunity, kind, "v4-payment-opportunity/1",
            [.. new CanonicalWriter().WriteInt32(1).WriteString(payment.IntentId).ToArray()], payment.EventTimeUtcMs, payment.EventTimeUtcMs);

    private static string Code(V4ScheduledCommand command) => command.OutcomeCode
        ?? throw new InvalidOperationException("A terminal command requires an outcome.");
}
