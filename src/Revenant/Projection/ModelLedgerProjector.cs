using System.Data;
using Marrow.Kernel.Results;
using Marrow.Timeline;
using Npgsql;
using Revenant.Contracts;
using Revenant.Finance;
using Revenant.Intelligence;
using Revenant.Payments;
using Revenant.Scenario;
using Revenant.Timeline;

namespace Revenant.Projection;

/// <summary>Outcome of <see cref="ModelLedgerProjector.VerifySourceAsync"/>; Verified only after catch-up, closure and an empty mismatch list.</summary>
public sealed record ModelSourceVerification(
    ProjectionStatus Projection, bool Reconciled, IReadOnlyList<ReconciliationMismatch> Mismatches, ModelLedgerState SourceState);

/// <summary>
/// Spec 08 task 8.3: the same restartable, idempotent projection as <see cref="LedgerProjector"/> (ADR-P1-17 decision 3: fact
/// <c>k</c> at position <c>k</c>, deterministic operation keys, progress = the history's last position, payload hash checked) for
/// model-aware runs (<see cref="PaymentLedgerStore.ModelAwareFinanceVersion"/>), into a V3 history pinned to
/// <see cref="ModelLedgerTimelineVersions.V3"/> with the run's <see cref="ModelLedgerContext"/>. Screening facts are replayed from
/// their recorded evidence; nothing here loads or invokes a model. Verification additionally reconciles each recorded learned decision
/// with the live hold reason. It reads live tables and writes only Timeline records (the restricted replay login is enough).
/// Operation keys are the V2 projector's: a run is exactly one family, so the two never collide.
/// </summary>
public sealed class ModelLedgerProjector(NpgsqlDataSource reader, ITimelineEngine<ModelLedgerState, FinanceFact, ModelLedgerContext> engine)
{
    public async Task<IReadOnlyList<ScenarioRunId>> ListRunsAsync(string tenantId, CancellationToken ct)
    {
        await using var cmd = reader.CreateCommand(
            "SELECT id FROM revenant.scenario_run WHERE tenant_id = @tenant AND finance_version = @v ORDER BY created_at, id");
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.AddWithValue("v", ModelLedgerVersion.ReducerVersion);
        var runs = new List<ScenarioRunId>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            runs.Add(new ScenarioRunId(r.GetGuid(0)));
        }
        return runs;
    }

    public async Task<Result<ProjectionStatus, ProjectionError>> CatchUpAsync(string tenantId, ScenarioRunId runId, CancellationToken ct, long? upTo = null)
    {
        var opened = await OpenAsync(tenantId, runId, ct);
        if (!opened.TryGetValue(out var source))
        {
            return Result<ProjectionStatus, ProjectionError>.Failure(opened.Error);
        }
        for (var attempt = 0; ; attempt++)
        {
            var result = await AppendPendingAsync(source, upTo, ct);
            if (result.IsSuccess || result.Error.Code != ProjectionErrorCode.Timeline || attempt >= 2
                || !result.Error.Message.StartsWith(nameof(TimelineErrorCode.Conflict), StringComparison.Ordinal))
            {
                return result;
            }
        }
    }

    public async Task<Result<ModelSourceVerification, ProjectionError>> VerifySourceAsync(string tenantId, ScenarioRunId runId, CancellationToken ct)
    {
        var opened = await OpenAsync(tenantId, runId, ct);
        if (!opened.TryGetValue(out var source))
        {
            return Result<ModelSourceVerification, ProjectionError>.Failure(opened.Error);
        }
        var caught = await AppendPendingAsync(source, null, ct);
        if (!caught.TryGetValue(out var status))
        {
            return Result<ModelSourceVerification, ProjectionError>.Failure(caught.Error);
        }
        var replay = await engine.ReplayAsync(source.History, ct);
        if (!replay.TryGetValue(out var replayed))
        {
            return TimelineFail<ModelSourceVerification>(replay.Error);
        }
        var live = await LedgerProjector.ReadLiveSnapshotAsync(reader, source.TenantId, runId, ct);
        if (live.ClosedAt is null)
        {
            return Fail<ModelSourceVerification>(ProjectionErrorCode.RunNotClosed,
                "The live run is open to new financial facts; close it before verifying so the verified source is its final set of facts.");
        }
        if (live.CommittedFacts != replayed.FinalPosition || live.ClosedAt != live.CommittedFacts)
        {
            return Fail<ModelSourceVerification>(ProjectionErrorCode.NotCaughtUp,
                $"The live run has {live.CommittedFacts} facts and the source history {replayed.FinalPosition}; retry after catch-up.");
        }
        var mismatches = LedgerProjector.Reconcile(replayed.FinalState.Ledger, live);
        mismatches.AddRange(ReconcileReasons(replayed.FinalState, await ReadScreeningReasonsAsync(source.TenantId, runId, ct), source.Context.Base.Policy));
        if (mismatches.Count > 0 || replayed.Status == HistoryStatus.Verified)
        {
            return Result<ModelSourceVerification, ProjectionError>.Success(new ModelSourceVerification(
                status with { Status = replayed.Status, FinalStateHash = replayed.FinalStateHash }, mismatches.Count == 0, mismatches, replayed.FinalState));
        }
        var completed = await engine.CompleteAsync(source.History, replayed.FinalPosition, ct);
        if (!completed.TryGetValue(out var receipt))
        {
            return TimelineFail<ModelSourceVerification>(completed.Error);
        }
        return Result<ModelSourceVerification, ProjectionError>.Success(new ModelSourceVerification(
            status with { Status = receipt.Status, FinalStateHash = receipt.FinalStateHash, ProjectedPosition = receipt.FinalPosition },
            true, [], replayed.FinalState));
    }

    /// <summary>The pinned V3 context of a run of seed/scenario <paramref name="run"/>: base policy v7 plus risk policy V1.</summary>
    public static Result<ModelLedgerContext, LedgerFinanceError> ContextFor(ScenarioRun run) =>
        ModelLedgerContext.Pin(run.Manifest, RiskPolicyCatalog.V1);

    // ---------------------------------------------------------------------------------------------------------

    private sealed record OpenedSource(ScenarioRunId RunId, string TenantId, TimelineId History, ModelLedgerContext Context);

    private async Task<Result<OpenedSource, ProjectionError>> OpenAsync(string tenantId, ScenarioRunId runId, CancellationToken ct)
    {
        var row = await LedgerProjector.ReadRunAsync(reader, runId, ct);
        if (row is null || !string.Equals(row.TenantId, tenantId, StringComparison.Ordinal))
        {
            return Fail<OpenedSource>(ProjectionErrorCode.RunNotFound, "The run does not exist in this tenant.");
        }
        if (!string.Equals(row.FinanceVersion, ModelLedgerVersion.ReducerVersion, StringComparison.Ordinal))
        {
            return Fail<OpenedSource>(ProjectionErrorCode.NotDurableV2, $"The run is recorded under '{row.FinanceVersion}', not the model-aware family.");
        }
        var created = FridayRingScenario.Create(row.Seed, row.ScenarioVersion, runId);
        if (!created.TryGetValue(out var run) || !string.Equals(run.Manifest.ContentHash, row.ManifestHash, StringComparison.Ordinal))
        {
            return Fail<OpenedSource>(ProjectionErrorCode.ManifestMismatch, "The run's stored manifest hash does not match its seed and scenario version.");
        }
        var pinned = ContextFor(run);
        if (!pinned.TryGetValue(out var context))
        {
            return Fail<OpenedSource>(ProjectionErrorCode.Reducer, pinned.Error.ToString());
        }
        var initial = ModelLedgerReducer.Initial(run, context);
        if (!initial.TryGetValue(out var state))
        {
            return Fail<OpenedSource>(ProjectionErrorCode.Reducer, initial.Error.ToString());
        }
        var history = await engine.CreateSourceAsync(
            new CreateSourceRequest<ModelLedgerState, ModelLedgerContext>(
                row.Owner, ModelLedgerTimelineVersions.V3, state, context, ModelLedgerVersion.ReducerVersion, row.ManifestHash),
            LedgerProjector.SourceOperationKey(runId), ct);
        return history.TryGetValue(out var id)
            ? Result<OpenedSource, ProjectionError>.Success(new OpenedSource(runId, row.TenantId, id, context))
            : TimelineFail<OpenedSource>(history.Error);
    }

    private async Task<Result<ProjectionStatus, ProjectionError>> AppendPendingAsync(OpenedSource source, long? upTo, CancellationToken ct)
    {
        var replay = await engine.ReplayAsync(source.History, ct);
        if (!replay.TryGetValue(out var current))
        {
            return TimelineFail<ProjectionStatus>(replay.Error);
        }
        var (committed, facts) = await LedgerProjector.ReadFactsAsync(reader, source.TenantId, source.RunId, current.FinalPosition, upTo, ct);
        if (facts.Count > 0 && current.Status != HistoryStatus.Recording)
        {
            return Fail<ProjectionStatus>(ProjectionErrorCode.FactsAfterVerification,
                $"The source history is {current.Status} at position {current.FinalPosition}, and fact {facts[0].Sequence} was committed after it.");
        }
        var position = current.FinalPosition;
        var state = current.FinalState;
        var stateHash = current.FinalStateHash;
        foreach (var fact in facts)
        {
            if (fact.Sequence != position + 1)
            {
                return Fail<ProjectionStatus>(ProjectionErrorCode.Reducer, $"Fact {fact.Sequence} does not follow position {position}.");
            }
            var next = ModelLedgerReducer.Apply(state, fact, source.Context);
            if (!next.TryGetValue(out var after))
            {
                return Fail<ProjectionStatus>(ProjectionErrorCode.Reducer, $"Fact {fact.Sequence}: {next.Error}");
            }
            var step = new StepRecord<FinanceFact>(fact.Sequence,
                [new OrderedInput<FinanceFact>(FinanceLedgerTransition.InputId(source.RunId, fact.Sequence), 0, fact)]);
            var appended = await engine.AppendAsync(source.History, position, step, LedgerProjector.StepOperationKey(source.RunId, fact.Sequence), ct);
            if (!appended.TryGetValue(out var receipt))
            {
                return TimelineFail<ProjectionStatus>(appended.Error);
            }
            (position, state, stateHash) = (receipt.Position, after, receipt.StateHash);
        }
        return Result<ProjectionStatus, ProjectionError>.Success(
            new ProjectionStatus(source.RunId, source.History, position, committed, current.Status, stateHash, facts.Count));
    }

    /// <summary>The live hold reason (Screened → Held trail row) of every payment of the run that was held at screening.</summary>
    private async Task<Dictionary<string, string?>> ReadScreeningReasonsAsync(string tenantId, ScenarioRunId runId, CancellationToken ct)
    {
        await using var connection = await reader.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await LedgerProjector.SetTenantAsync(connection, tx, tenantId, ct);
        var reasons = new Dictionary<string, string?>(StringComparer.Ordinal);
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT p.intent_id, t.reason FROM revenant.payment_transition t JOIN revenant.payment p ON p.id = t.payment_id
             WHERE t.scenario_run_id = @run AND t.from_status = 'Screened' AND t.to_status = 'Held'
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue("run", runId.Value);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                reasons[r.GetString(0)] = r.IsDBNull(1) ? null : r.GetString(1);
            }
        }
        await tx.CommitAsync(ct);
        return reasons;
    }

    internal static IEnumerable<ReconciliationMismatch> ReconcileReasons(
        ModelLedgerState source, Dictionary<string, string?> liveHeldReasons, PolicyConfig basePolicy)
    {
        foreach (var p in source.Ledger.Payments.Values)
        {
            var id = p.Intent.IntentId;
            if (!source.Screenings.TryGetValue(id, out var screening))
            {
                if (p.Status != PaymentStatus.Initiated)
                {
                    yield return new ReconciliationMismatch($"payment {id} evidence", "missing", p.Status.ToString());
                }
                continue;
            }
            // The Screened -> Held trail row carries the effective reason: the v7 rule reason when the base held, ml_risk_hold when only
            // the recorded score held a base-clear payment. A cleared payment has no such row.
            string? expected = screening.FinalDecision == ScreeningDecisionKind.Held
                ? PaymentLedgerStore.ReasonText(ModelLedgerReducer.EffectiveReason(
                    FinanceLedgerReducer.ScreeningReason(p.Intent, p.Velocity15m, basePolicy), screening.FinalDecision))
                : null;
            liveHeldReasons.TryGetValue(id, out var live);
            if (!string.Equals(expected, live, StringComparison.Ordinal))
            {
                yield return new ReconciliationMismatch($"payment {id} hold reason", expected ?? "none", live ?? "none");
            }
        }
    }

    private static Result<T, ProjectionError> Fail<T>(ProjectionErrorCode code, string message) =>
        Result<T, ProjectionError>.Failure(new ProjectionError(code, message));

    private static Result<T, ProjectionError> TimelineFail<T>(TimelineError error) =>
        Fail<T>(ProjectionErrorCode.Timeline, $"{error.Code}: {error.Message}");
}
