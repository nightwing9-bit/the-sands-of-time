using Marrow.Kernel.Results;
using Marrow.Timeline;
using Revenant.Contracts;
using Revenant.Finance;
using Revenant.Scenario;

namespace Revenant.Timeline;

/// <summary>
/// Typed adapter failure: either the shared Timeline's own error code or a finance pin/reducer error code.
/// Exactly one code is set.
/// </summary>
public sealed record FinanceTimelineError(TimelineErrorCode? TimelineCode, FinanceErrorCode? FinanceCode, string Message)
{
    public static FinanceTimelineError From(TimelineError error) => new(error.Code, null, error.Message);

    public static FinanceTimelineError From(FinanceError error) => new(null, error.Code, error.Message);

    public override string ToString() => $"{(object?)TimelineCode ?? FinanceCode}: {Message}";
}

/// <summary>A recorded, replay-verified source history of one run under one pinned policy.</summary>
public sealed record RecordedSource(
    TimelineId Id,
    ScenarioRunId RunId,
    PolicyRef Policy,
    string ContextHash,
    long FinalPosition,
    string FinalStateHash,
    string LastRecordHash,
    HistoryStatus Status);

/// <summary>
/// Request to fork a source at <see cref="ForkPosition"/> under another pinned policy. Every pin is explicit:
/// scenario manifest, policy reference and reducer version. Nothing defaults to "current".
/// </summary>
public sealed record BranchRequest(
    TimelineId Source,
    long ForkPosition,
    ScenarioManifest Manifest,
    PolicyRef Policy,
    string ReducerVersion,
    string OperationKey);

/// <summary>A created child history and the pinned context its suffix uses.</summary>
public sealed record BranchHandle(TimelineId Source, TimelineId Branch, long ForkPosition, ScenarioRunId RunId, FinanceContext Context);

/// <summary>A continued branch. <see cref="Status"/> is Verified only after the engine's completion replay.</summary>
public sealed record RecordedBranch(
    BranchHandle Handle,
    long FinalPosition,
    string FinalStateHash,
    string LastRecordHash,
    HistoryStatus Status);

/// <summary>Verified original and alternate states at one shared logical position plus the adapter's synthetic metrics.</summary>
public sealed record AlignedFinanceComparison(
    long Position,
    VerifiedState<FinanceProjectionState> Original,
    VerifiedState<FinanceProjectionState> Alternate,
    CounterfactualMetrics Metrics);

/// <summary>Result of <see cref="FinanceTimelineAdapter.RunCounterfactualAsync"/>: the branch and its aligned comparison.</summary>
public sealed record CounterfactualRun(RecordedBranch Branch, IReadOnlyList<AlignedFinanceComparison> Aligned);

/// <summary>
/// Binds the pure Revenant finance reducer to the one shared Marrow Timeline engine. Position <c>k</c> of every
/// finance history is exactly one exogenous <see cref="PaymentIntent"/> with logical sequence <c>k</c>; the
/// journal input id is the intent's run-scoped key. A branch is forked through <see cref="ITimelineEngine{TState,TInput,TContext}.ForkAsync"/>
/// and continued with the same <c>AppendAsync</c> as the source, replaying the source's recorded exogenous
/// intents after the fork position; the source's derived decisions are never fed to a branch. The adapter
/// writes nothing but Timeline appends: there is no ledger posting here.
/// </summary>
public sealed class FinanceTimelineAdapter(ITimelineEngine<FinanceProjectionState, PaymentIntent, FinanceContext> engine)
{
    private readonly ITimelineEngine<FinanceProjectionState, PaymentIntent, FinanceContext> _engine =
        engine ?? throw new ArgumentNullException(nameof(engine));

    /// <summary>
    /// Records <paramref name="run"/> as a source history under <paramref name="policy"/> and completes it to
    /// Verified. <paramref name="ownerId"/> must be the verified server actor. Retrying with the same operation key
    /// resumes or returns the same history.
    /// </summary>
    public async Task<Result<RecordedSource, FinanceTimelineError>> RecordSourceAsync(
        string ownerId, ScenarioRun run, PolicyRef policy, string operationKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationKey);

        var invalid = ValidateRun(run);
        if (invalid is not null)
        {
            return Fail<RecordedSource>(invalid);
        }
        var pinned = FinanceContext.Pin(run.Manifest, policy, FinanceReducer.Version);
        if (!pinned.TryGetValue(out var context))
        {
            return Fail<RecordedSource>(pinned.Error);
        }
        var initial = FinanceReducer.Initial(run, context);
        if (!initial.TryGetValue(out var state))
        {
            return Fail<RecordedSource>(initial.Error);
        }

        var request = new CreateSourceRequest<FinanceProjectionState, FinanceContext>(
            // Rules/seed hashes are inherited by forks, so they name branch-independent facts: the shared reducer
            // and the run's exogenous facts. The per-segment policy lives in each segment's pinned context.
            ownerId, FinanceTimelineVersions.V1, state, context, FinanceReducer.Version, run.ExogenousFactsHash);
        var created = await _engine.CreateSourceAsync(request, operationKey, ct);
        if (!created.TryGetValue(out var id))
        {
            return Fail<RecordedSource>(created.Error);
        }

        var appended = await AppendIntentsAsync(id, 0, state, run.Intents, context, ct);
        if (appended is not null)
        {
            return Result<RecordedSource, FinanceTimelineError>.Failure(appended);
        }
        var completed = await _engine.CompleteAsync(id, run.Intents.Length, ct);
        if (!completed.TryGetValue(out var receipt))
        {
            return Fail<RecordedSource>(completed.Error);
        }
        return Result<RecordedSource, FinanceTimelineError>.Success(new RecordedSource(
            id, run.RunId, policy, context.ContentHash, receipt.FinalPosition, receipt.FinalStateHash,
            receipt.LastRecordHash, receipt.Status));
    }

    /// <summary>
    /// Pins the branch context, checks it against the verified source state at the fork position and creates the
    /// Recording child through the shared engine. The source is only read.
    /// </summary>
    public async Task<Result<BranchHandle, FinanceTimelineError>> CreateBranchAsync(BranchRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OperationKey);
        var pinned = FinanceContext.Pin(request.Manifest, request.Policy, request.ReducerVersion);
        if (!pinned.TryGetValue(out var context))
        {
            return Fail<BranchHandle>(pinned.Error);
        }
        var restored = await _engine.RestoreAsync(request.Source, request.ForkPosition, ct);
        if (!restored.TryGetValue(out var atFork))
        {
            return Fail<BranchHandle>(restored.Error);
        }
        var incompatible = CheckCompatible(atFork.State, context);
        if (incompatible is not null)
        {
            return Fail<BranchHandle>(incompatible);
        }
        var forked = await _engine.ForkAsync(request.Source, request.ForkPosition, request.OperationKey, context, ct);
        return forked.TryGetValue(out var branch)
            ? Result<BranchHandle, FinanceTimelineError>.Success(
                new BranchHandle(request.Source, branch, request.ForkPosition, atFork.State.RunId, context))
            : Fail<BranchHandle>(forked.Error);
    }

    /// <summary>
    /// Appends the source's recorded exogenous intents after the branch's current last position under the branch
    /// context, then completes the branch (engine replay verification). Safe to call again after a cancellation:
    /// it resumes from the last committed branch position and returns the same result once Verified.
    /// </summary>
    public async Task<Result<RecordedBranch, FinanceTimelineError>> ContinueBranchAsync(BranchHandle handle, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(handle);
        var current = await _engine.ReplayAsync(handle.Branch, ct);
        if (!current.TryGetValue(out var branchNow))
        {
            return Fail<RecordedBranch>(current.Error);
        }
        if (branchNow.Status == HistoryStatus.Verified)
        {
            return Result<RecordedBranch, FinanceTimelineError>.Success(new RecordedBranch(
                handle, branchNow.FinalPosition, branchNow.FinalStateHash, branchNow.LastRecordHash, branchNow.Status));
        }
        if (branchNow.Status != HistoryStatus.Recording)
        {
            return Fail<RecordedBranch>(new TimelineError(TimelineErrorCode.Conflict, $"Branch is {branchNow.Status}."));
        }
        var incompatible = CheckCompatible(branchNow.FinalState, handle.Context);
        if (incompatible is not null || branchNow.FinalState.RunId != handle.RunId)
        {
            return Fail<RecordedBranch>(incompatible ?? new FinanceError(FinanceErrorCode.RunMismatch, "Branch belongs to another run."));
        }

        var source = await _engine.ReplayAsync(handle.Source, ct);
        if (!source.TryGetValue(out var sourceFinal))
        {
            return Fail<RecordedBranch>(source.Error);
        }
        if (sourceFinal.Status != HistoryStatus.Verified)
        {
            return Fail<RecordedBranch>(new TimelineError(TimelineErrorCode.Conflict, "Source history is not Verified."));
        }

        // Retained exogenous facts recorded by the source after the branch's last position, in logical sequence
        // order (the position axis itself). Only the intent is taken; the source's decision evidence is not.
        var retained = sourceFinal.FinalState.Payments.Values
            .Select(p => p.Intent)
            .Where(i => i.Sequence > branchNow.FinalPosition)
            .OrderBy(i => i.Sequence)
            .ToList();
        for (var i = 0; i < retained.Count; i++)
        {
            if (retained[i].Sequence != branchNow.FinalPosition + i + 1)
            {
                return Fail<RecordedBranch>(new FinanceError(
                    FinanceErrorCode.InvalidScenarioRun, "Source intents are not one contiguous intent per position."));
            }
        }
        if (branchNow.FinalPosition + retained.Count != sourceFinal.FinalPosition)
        {
            return Fail<RecordedBranch>(new FinanceError(
                FinanceErrorCode.InvalidScenarioRun, "Source position count does not match its recorded intents."));
        }

        var appended = await AppendIntentsAsync(
            handle.Branch, branchNow.FinalPosition, branchNow.FinalState, retained, handle.Context, ct);
        if (appended is not null)
        {
            return Result<RecordedBranch, FinanceTimelineError>.Failure(appended);
        }
        var completed = await _engine.CompleteAsync(handle.Branch, sourceFinal.FinalPosition, ct);
        return completed.TryGetValue(out var receipt)
            ? Result<RecordedBranch, FinanceTimelineError>.Success(new RecordedBranch(
                handle, receipt.FinalPosition, receipt.FinalStateHash, receipt.LastRecordHash, receipt.Status))
            : Fail<RecordedBranch>(completed.Error);
    }

    /// <summary>Verified original/alternate states at each requested position with synthetic counterfactual metrics.</summary>
    public async Task<Result<IReadOnlyList<AlignedFinanceComparison>, FinanceTimelineError>> CompareAsync(
        TimelineId original, TimelineId alternate, IReadOnlyList<long> positions, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(positions);
        var aligned = await _engine.CompareAlignedAsync(original, alternate, positions, ct);
        if (!aligned.TryGetValue(out var pairs))
        {
            return Fail<IReadOnlyList<AlignedFinanceComparison>>(aligned.Error);
        }
        var result = new List<AlignedFinanceComparison>(pairs.Count);
        foreach (var pair in pairs)
        {
            var metrics = CounterfactualMetrics.Compare(pair.Source.State, pair.Alternative.State, CounterfactualCostModel.V1);
            if (!metrics.TryGetValue(out var m))
            {
                return Fail<IReadOnlyList<AlignedFinanceComparison>>(metrics.Error);
            }
            result.Add(new AlignedFinanceComparison(pair.Position, pair.Source, pair.Alternative, m));
        }
        return Result<IReadOnlyList<AlignedFinanceComparison>, FinanceTimelineError>.Success(result);
    }

    /// <summary>
    /// The selected-position path: fork the source at the requested position under the requested policy,
    /// continue it with the retained exogenous intents, and compare at the fork position and the final position.
    /// </summary>
    public async Task<Result<CounterfactualRun, FinanceTimelineError>> RunCounterfactualAsync(BranchRequest request, CancellationToken ct)
    {
        var created = await CreateBranchAsync(request, ct);
        if (!created.TryGetValue(out var handle))
        {
            return Result<CounterfactualRun, FinanceTimelineError>.Failure(created.Error);
        }
        var continued = await ContinueBranchAsync(handle, ct);
        if (!continued.TryGetValue(out var branch))
        {
            return Result<CounterfactualRun, FinanceTimelineError>.Failure(continued.Error);
        }
        long[] positions = branch.FinalPosition == handle.ForkPosition
            ? [handle.ForkPosition]
            : [handle.ForkPosition, branch.FinalPosition];
        var compared = await CompareAsync(handle.Source, handle.Branch, positions, ct);
        return compared.TryGetValue(out var aligned)
            ? Result<CounterfactualRun, FinanceTimelineError>.Success(new CounterfactualRun(branch, aligned))
            : Result<CounterfactualRun, FinanceTimelineError>.Failure(compared.Error);
    }

    /// <summary>
    /// Logical position of the last intent whose business time is at or before <paramref name="utcMs"/> (0 when
    /// none). Selection only: the run's sequence order is the authority and is never re-sorted by time.
    /// </summary>
    public static long PositionAtOrBefore(ScenarioRun run, long utcMs)
    {
        ArgumentNullException.ThrowIfNull(run);
        long position = 0;
        foreach (var intent in run.Intents)
        {
            if (intent.EventTimeUtcMs > utcMs)
            {
                break;
            }
            position = intent.Sequence;
        }
        return position;
    }

    private async Task<FinanceTimelineError?> AppendIntentsAsync(
        TimelineId history, long lastPosition, FinanceProjectionState state, IReadOnlyList<PaymentIntent> intents,
        FinanceContext context, CancellationToken ct)
    {
        var expected = lastPosition;
        var local = state;
        foreach (var intent in intents)
        {
            // Pre-validate with the same pure reducer the engine will run, so a rejected fact is a typed error
            // and never reaches the journal.
            var next = FinanceReducer.Apply(local, intent, context);
            if (!next.TryGetValue(out var after))
            {
                return FinanceTimelineError.From(next.Error);
            }
            var position = expected + 1;
            var step = new StepRecord<PaymentIntent>(position, [new OrderedInput<PaymentIntent>(intent.RunScopedKey, 0, intent)]);
            var appended = await _engine.AppendAsync(history, expected, step, StepKey(history, position), ct);
            if (!appended.TryGetValue(out _))
            {
                return FinanceTimelineError.From(appended.Error);
            }
            local = after;
            expected = position;
        }
        return null;
    }

    private static string StepKey(TimelineId history, long position) => $"revenant-step/{history.Value:N}/{position}";

    private static FinanceError? ValidateRun(ScenarioRun run)
    {
        for (var i = 0; i < run.Intents.Length; i++)
        {
            var intent = run.Intents[i];
            if (intent.RunId != run.RunId || intent.Sequence != i + 1)
            {
                return new FinanceError(FinanceErrorCode.InvalidScenarioRun,
                    $"Intent {intent.IntentId} is not run-scoped sequence {i + 1} of run {run.RunId}.");
            }
        }
        return string.Equals(
            ScenarioManifestCodec.ComputeFactsHash(run.Manifest, run.Intents), run.ExogenousFactsHash, StringComparison.Ordinal)
            ? null
            : new FinanceError(FinanceErrorCode.InvalidScenarioRun, "Run facts do not match its exogenous facts hash.");
    }

    private static FinanceError? CheckCompatible(FinanceProjectionState state, FinanceContext context) =>
        string.Equals(state.ScenarioVersion, context.ScenarioVersion, StringComparison.Ordinal)
        && string.Equals(state.ManifestContentHash, context.ManifestContentHash, StringComparison.Ordinal)
        && string.Equals(state.Currency, context.Policy.Currency, StringComparison.Ordinal)
            ? null
            : new FinanceError(FinanceErrorCode.VersionMismatch,
                "Pinned branch context does not match the scenario and currency of the restored state.");

    private static Result<T, FinanceTimelineError> Fail<T>(TimelineError error) =>
        Result<T, FinanceTimelineError>.Failure(FinanceTimelineError.From(error));

    private static Result<T, FinanceTimelineError> Fail<T>(FinanceError error) =>
        Result<T, FinanceTimelineError>.Failure(FinanceTimelineError.From(error));
}
