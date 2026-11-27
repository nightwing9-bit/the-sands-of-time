using GhostLap.Timeline;
using Marrow.Kernel.Results;
using Marrow.Kernel.Security;
using Marrow.Timeline;

namespace GhostLap.Solo;

/// <summary>The honest outcome of saving a finished solo run. Only <see cref="Saved"/> means a verified, owner-visible original exists.</summary>
public enum SoloSaveStatus
{
    /// <summary>The Timeline history is Verified, its replay equals the live run, and the owner's row is Complete with the same hashes.</summary>
    Saved,
    /// <summary>The save could not be confirmed right now (database unavailable or cancelled). Nothing was acknowledged; retry the same key.</summary>
    SavePending,
    /// <summary>The run is not, or cannot become, a verified original (not finished, failed verification, conflict, unsupported version).</summary>
    Failed,
}

/// <summary>
/// Owner-visible facts of one saved original, taken from its Complete row. The versions are the pinned solo
/// <c>ghost-v1</c> Timeline key (the only registered reducer/codec, so a replay under any other pin cannot succeed) and the
/// rules/layout versions recorded when the run started. <see cref="Score"/> is only known after a verified replay.
/// </summary>
public sealed record SoloOriginalSummary(
    TimelineId Timeline,
    string OperationKey,
    long Seed,
    byte MascotId,
    string RulesVersion,
    string LayoutVersion,
    TimelineVersionKey Versions,
    long FinalTick,
    string FinalStateHash,
    string LastRecordHash,
    int? Score)
{
    internal static SoloOriginalSummary From(SoloOriginalRecord row, int? score = null) => new(
        row.Timeline, row.OperationKey, row.Seed, row.MascotId, row.RulesVersion, row.LayoutVersion, GhostLapTimelineVersions.V1,
        row.FinalTick ?? throw new InvalidOperationException("Only a Complete row is an original."),
        row.FinalStateHash!, row.LastRecordHash!, score);

    internal static SoloOriginalSummary From(SoloOriginalRecord row, SoloOriginal verified) => From(row, verified.Result.Scores[0].TotalScore);
}

/// <summary>The save result. <see cref="Original"/> is set exactly when <see cref="Status"/> is <see cref="SoloSaveStatus.Saved"/>.</summary>
public sealed record SoloSaveOutcome(SoloSaveStatus Status, SoloOriginalSummary? Original, SoloError? Error)
{
    public static SoloSaveOutcome Saved(SoloOriginalSummary original) => new(SoloSaveStatus.Saved, original, null);

    public static SoloSaveOutcome Pending(SoloError error) => new(SoloSaveStatus.SavePending, null, error);

    public static SoloSaveOutcome Failed(SoloError error) => new(SoloSaveStatus.Failed, null, error);
}

public sealed partial class SoloOriginals
{
    /// <summary>Largest page of the owner's library one call returns.</summary>
    public const int MaxListLimit = 100;

    /// <summary>
    /// Saves a finished run. <see cref="SoloSaveStatus.Saved"/> is returned only when the recorder completed and replay-verified
    /// the history and the owner's row is Complete; it is always read back from the stored row, never from client facts. A
    /// transient failure (database unavailable, cancelled) is resolved once by an authoritative read of the owner's key; if that
    /// cannot confirm a verified original the result is <see cref="SoloSaveStatus.SavePending"/>, never a second history. The
    /// same recorder (or, after a restart, <see cref="ResolveAsync"/>) can retry and returns the identical identity.
    /// </summary>
    public async Task<SoloSaveOutcome> SaveAsync(IActorContext actor, SoloRecording run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(run);
        if (!actor.IsAuthenticated || string.IsNullOrEmpty(actor.SubjectId))
        {
            return SoloSaveOutcome.Failed(new SoloError(SoloErrorCode.Unauthenticated, "Saving requires a verified actor."));
        }

        // The recorder must belong to this actor (defense in depth: the host also keys sessions by owner).
        var own = await _store.FindByOwnerKeyAsync(actor.SubjectId, run.OperationKey, ct).ConfigureAwait(false);
        if (!own.TryGetValue(out var row))
        {
            return IsTransient(own.Error) ? SoloSaveOutcome.Pending(own.Error) : SoloSaveOutcome.Failed(new SoloError(SoloErrorCode.NotFound, "No such solo original."));
        }

        if (row.Timeline != run.Timeline)
        {
            return SoloSaveOutcome.Failed(new SoloError(SoloErrorCode.NotFound, "No such solo original."));
        }

        var completed = await run.CompleteAsync(ct).ConfigureAwait(false);
        if (completed.TryGetValue(out var original))
        {
            // Read the stored row back: Saved reports what the database holds, not what the recorder held in memory.
            var stored = await _store.FindAsync(run.Timeline, ct).ConfigureAwait(false);
            if (stored.TryGetValue(out var saved) && saved.Status == SoloOriginalStatus.Complete
                && saved.FinalStateHash == original.FinalStateHash && saved.LastRecordHash == original.LastRecordHash)
            {
                return SoloSaveOutcome.Saved(SoloOriginalSummary.From(saved, original));
            }

            return stored.IsFailure && IsTransient(stored.Error)
                ? SoloSaveOutcome.Pending(stored.Error)
                : SoloSaveOutcome.Failed(new SoloError(SoloErrorCode.VerificationFailed, "The saved row does not match the verified run."));
        }

        if (!IsTransient(completed.Error))
        {
            return SoloSaveOutcome.Failed(completed.Error);
        }

        // Ambiguous: the commit may or may not have landed. One authoritative read decides; otherwise stay pending.
        var resolved = await ResolveAsync(actor, run.OperationKey, ct).ConfigureAwait(false);
        return resolved.Status == SoloSaveStatus.Saved ? resolved : SoloSaveOutcome.Pending(completed.Error);
    }

    /// <summary>
    /// Resolves a save by the owner's operation key without a live recorder (lost response, refresh, host restart): the same
    /// verified identity and hashes if the original is Complete and its stored history still replays to them, otherwise a
    /// typed non-Saved outcome. A run that never finished is <see cref="SoloErrorCode.NotComplete"/>, not an original.
    /// </summary>
    public async Task<SoloSaveOutcome> ResolveAsync(IActorContext actor, string operationKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!actor.IsAuthenticated || string.IsNullOrEmpty(actor.SubjectId))
        {
            return SoloSaveOutcome.Failed(new SoloError(SoloErrorCode.Unauthenticated, "Saving requires a verified actor."));
        }

        if (!IsValidOperationKey(operationKey))
        {
            return SoloSaveOutcome.Failed(new SoloError(SoloErrorCode.InvalidRequest, "Invalid operation key."));
        }

        var found = await _store.FindByOwnerKeyAsync(actor.SubjectId, operationKey, ct).ConfigureAwait(false);
        if (!found.TryGetValue(out var row))
        {
            return IsTransient(found.Error) ? SoloSaveOutcome.Pending(found.Error) : SoloSaveOutcome.Failed(found.Error);
        }

        if (row.Status != SoloOriginalStatus.Complete)
        {
            return SoloSaveOutcome.Failed(new SoloError(SoloErrorCode.NotComplete,
                "This run was not completed and verified, so it is not an original."));
        }

        var opened = await OpenAsync(actor, row.Timeline, ct).ConfigureAwait(false);
        if (opened.TryGetValue(out var original))
        {
            return SoloSaveOutcome.Saved(SoloOriginalSummary.From(row, original));
        }

        return IsTransient(opened.Error) ? SoloSaveOutcome.Pending(opened.Error) : SoloSaveOutcome.Failed(opened.Error);
    }

    /// <summary>
    /// The actor's own Complete originals (newest first), from the Ghost Lap rows only: no replay per item, so
    /// <see cref="SoloOriginalSummary.Score"/> is null here and is returned by <see cref="OpenAsync"/>. A recording that is
    /// unfinished or interrupted is never listed.
    /// </summary>
    public async Task<Result<IReadOnlyList<SoloOriginalSummary>, SoloError>> ListAsync(IActorContext actor, int limit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!actor.IsAuthenticated || string.IsNullOrEmpty(actor.SubjectId))
        {
            return SoloResult.Fail<IReadOnlyList<SoloOriginalSummary>>(SoloErrorCode.Unauthenticated, "Listing requires a verified actor.");
        }

        var rows = await _store.ListCompleteByOwnerAsync(actor.SubjectId, Math.Clamp(limit, 1, MaxListLimit), ct).ConfigureAwait(false);
        return rows.TryGetValue(out var list)
            ? SoloResult.Ok<IReadOnlyList<SoloOriginalSummary>>([.. list.Select(r => SoloOriginalSummary.From(r))])
            : SoloResult.Fail<IReadOnlyList<SoloOriginalSummary>>(rows.Error);
    }

    /// <summary>The summary of a verified original (replayed), with its score.</summary>
    public async Task<Result<SoloOriginalSummary, SoloError>> GetAsync(IActorContext actor, TimelineId timeline, CancellationToken ct)
    {
        var opened = await OpenAsync(actor, timeline, ct).ConfigureAwait(false);
        if (!opened.TryGetValue(out var original))
        {
            return SoloResult.Fail<SoloOriginalSummary>(opened.Error);
        }

        var row = await _store.FindAsync(timeline, ct).ConfigureAwait(false);
        return row.TryGetValue(out var stored)
            ? SoloResult.Ok(SoloOriginalSummary.From(stored, original))
            : SoloResult.Fail<SoloOriginalSummary>(row.Error);
    }

    private static bool IsTransient(SoloError error) => error.Code is SoloErrorCode.Unavailable or SoloErrorCode.Cancelled;
}
