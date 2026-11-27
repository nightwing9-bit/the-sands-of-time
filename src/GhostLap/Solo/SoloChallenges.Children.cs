using System.Collections.Immutable;
using GhostLap.Rules;
using GhostLap.Timeline;
using Marrow.Kernel.Results;
using Marrow.Kernel.Security;
using Marrow.Timeline;

namespace GhostLap.Solo;

/// <summary>
/// One completed saved child (Challenge or Fork) of the owner, from its <c>solo_branches</c> row. <see cref="Score"/> is the child's
/// final score and is present only when the child's final state was restored and reproduced the recorded hash
/// (<see cref="Verified"/>); an unverifiable child stays listed honestly with no score.
/// </summary>
public sealed record SoloChildSummary(
    TimelineId Child,
    TimelineId Source,
    SoloBranchKind Kind,
    string ClientKey,
    long ForkTick,
    long FinalTick,
    string FinalStateHash,
    string LastRecordHash,
    int? Score,
    bool Verified,
    long Seed,
    byte MascotId,
    string RulesVersion,
    string LayoutVersion,
    TimelineVersionKey Versions);

/// <summary>A bounded saved-child frame slice with explicit source provenance.</summary>
public sealed record SoloChildTraceChunk(TimelineId Source, SoloBranchKind Kind, long ForkTick, string SourceFinalStateHash, SoloTraceChunk Trace);

/// <summary>A page of completed children; NextAfter is the cursor for the next page.</summary>
public sealed record SoloChildPage(IReadOnlyList<SoloChildSummary> Items, TimelineId? NextAfter);

/// <summary>The score parts of one world at one tick (fixed-point distance is exposed because movement points derive from it).</summary>
public sealed record SoloScoreParts(
    int Movement, int Pickup, int Penalty, int Total, int Pickups, int Bursts, int Charge, bool Overlit, long DistanceFixed)
{
    internal static SoloScoreParts Of(WorldFrame f) =>
        new(f.MovementPoints, f.PickupPoints, f.PenaltyPoints, f.TotalScore, f.Pickups, f.Bursts, f.Charge, f.IsOverlit, f.DistanceFixed);
}

/// <summary>
/// Source and child at the same absolute tick. <see cref="Delta"/> is child minus source <b>at that tick</b>, never final minus final.
/// <see cref="AfterFork"/> is true only for ticks strictly after the fork tick; through the fork tick both worlds are the same state.
/// </summary>
public sealed record SoloComparisonPoint(
    long Tick,
    int SourceScore,
    int ChildScore,
    int Delta,
    SoloScoreParts Source,
    SoloScoreParts Child,
    bool AfterFork,
    bool IsFinal,
    string SourceStateHash,
    string ChildStateHash);

/// <summary>
/// A bounded, ordered slice of the same-tick comparison of one completed child with its source. The identity of both histories
/// (versions and verified final hashes) is repeated on every chunk. <see cref="NextTick"/> is the first requested tick after this
/// slice (null once the final tick is included).
/// </summary>
public sealed record SoloComparisonChunk(
    TimelineId Child,
    TimelineId Source,
    SoloBranchKind Kind,
    long ForkTick,
    TimelineVersionKey Versions,
    string RulesVersion,
    string LayoutVersion,
    long FinalTick,
    string SourceFinalStateHash,
    string ChildFinalStateHash,
    long FromTick,
    int Stride,
    long? NextTick,
    ImmutableArray<SoloComparisonPoint> Points);

public sealed partial class SoloChallenges
{
    /// <summary>Largest page of the child catalog. Each item costs one verified restore of the child's final tick.</summary>
    public const int MaxChildPageLimit = 25;

    /// <summary>Largest number of comparison points one call returns.</summary>
    public const int MaxComparisonPoints = 300;

    /// <summary>
    /// The actor's own completed children, newest first (optionally only those of <paramref name="source"/>). A child that is still
    /// recording, was interrupted or was never completed is not listed. Never includes originals and never another owner's rows.
    /// </summary>
    public async Task<Result<SoloChildPage, SoloError>> ListChildrenAsync(
        IActorContext actor, TimelineId? source, TimelineId? after, int limit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!actor.IsAuthenticated || string.IsNullOrEmpty(actor.SubjectId))
        {
            return SoloResult.Fail<SoloChildPage>(SoloErrorCode.Unauthenticated, "Listing requires a verified actor.");
        }

        if (limit is < 1 or > MaxChildPageLimit)
        {
            return SoloResult.Fail<SoloChildPage>(SoloErrorCode.InvalidRequest, $"Request 1-{MaxChildPageLimit} children per page.");
        }

        return await SoloResult.GuardAsync(async () =>
        {
            if (after is { } cursor)
            {
                var row = await Store.ReadBranchAsync(cursor, ct).ConfigureAwait(false);
                if (row is null || row.OwnerSubjectId != actor.SubjectId || row.Status != SoloOriginalStatus.Complete)
                {
                    // Foreign, unknown and unfinished cursors are one answer.
                    return SoloResult.Fail<SoloChildPage>(SoloErrorCode.InvalidRequest, "Unknown page cursor.");
                }
            }

            var rows = await Store.ListCompleteBranchesByOwnerAsync(actor.SubjectId, source, after, limit + 1, ct).ConfigureAwait(false);
            if (!rows.TryGetValue(out var found))
            {
                return SoloResult.Fail<SoloChildPage>(rows.Error);
            }

            var page = found.Take(limit).ToArray();
            var items = new List<SoloChildSummary>(page.Length);
            foreach (var row in page)
            {
                var restored = await Branches.RestoreAsync(actor, row.Timeline, GameRulesV1.RoundTicks, ct).ConfigureAwait(false);
                if (restored.IsFailure && restored.Error.Code is SoloErrorCode.Unavailable or SoloErrorCode.Cancelled)
                {
                    return SoloResult.Fail<SoloChildPage>(restored.Error);
                }

                int? score = null;
                if (restored.TryGetValue(out var final) && final.HistoryStatus == HistoryStatus.Verified && final.State.IsFinal
                    && final.StateHash == row.FinalStateHash)
                {
                    score = final.State.Players[0].TotalScore;
                }

                items.Add(Summarize(row, score));
            }

            return SoloResult.Ok(new SoloChildPage(items, found.Count > limit ? page[^1].Timeline : null));
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The verified result of one of the actor's completed children by its id (refresh, restart, selection for replay or comparison).
    /// Foreign and unknown ids are the same <see cref="SoloErrorCode.NotFound"/>; an unfinished child is <see cref="SoloErrorCode.NotComplete"/>.
    /// </summary>
    public Task<Result<SoloChallengeResult, SoloError>> GetChildAsync(IActorContext actor, TimelineId child, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return SoloResult.GuardAsync(async () =>
        {
            var row = await OwnedChildAsync(actor, child, ct).ConfigureAwait(false);
            return row.TryGetValue(out var branch)
                ? await VerifiedResultAsync(actor, branch, ct).ConfigureAwait(false)
                : SoloResult.Fail<SoloChallengeResult>(row.Error);
        }, ct);
    }

    /// <summary>
    /// Same-tick comparison of a completed child with its source for <paramref name="count"/> ticks starting at <paramref name="fromTick"/>
    /// with a step of <paramref name="stride"/> (so one call can sample a whole round for a graph, or every tick of a window). It reuses
    /// <see cref="SoloBranches.CompareAsync"/> (the Timeline's aligned verified states): source and child scores at the same tick and
    /// their signed difference. The two histories must be identical through the fork tick and, at the final tick, reproduce the hashes
    /// recorded at completion; otherwise the call fails closed. Nothing is replayed into a living world and nothing is written.
    /// </summary>
    public Task<Result<SoloComparisonChunk, SoloError>> CompareChildAsync(
        IActorContext actor, TimelineId child, long fromTick, int count, int stride, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (count < 1 || count > MaxComparisonPoints || stride < 1 || stride > MaxComparisonPoints)
        {
            return Task.FromResult(SoloResult.Fail<SoloComparisonChunk>(SoloErrorCode.InvalidRequest,
                $"Request 1-{MaxComparisonPoints} points with a stride of 1-{MaxComparisonPoints}."));
        }

        if (fromTick < 0 || fromTick > GameRulesV1.RoundTicks)
        {
            return Task.FromResult(SoloResult.Fail<SoloComparisonChunk>(SoloErrorCode.InvalidTick, $"Tick must be 0-{GameRulesV1.RoundTicks}."));
        }

        return SoloResult.GuardAsync(async () =>
        {
            var owned = await OwnedChildAsync(actor, child, ct).ConfigureAwait(false);
            if (!owned.TryGetValue(out var row))
            {
                return SoloResult.Fail<SoloComparisonChunk>(owned.Error);
            }

            if (row.Status != SoloOriginalStatus.Complete || row.FinalTick != GameRulesV1.RoundTicks || string.IsNullOrEmpty(row.FinalStateHash))
            {
                return SoloResult.Fail<SoloComparisonChunk>(SoloErrorCode.NotComplete, "This child was not completed and verified.");
            }

            if (row.RulesVersion != GameRulesV1.RulesVersion || row.LayoutVersion != GameRulesV1.LayoutVersion)
            {
                return SoloResult.Fail<SoloComparisonChunk>(SoloErrorCode.IncompatibleVersion, "Unsupported solo rules or layout.");
            }

            var sourceRow = await SoloOriginals.ReadableOriginalAsync(Authorizer, Store, actor, row.Source, ct).ConfigureAwait(false);
            if (!sourceRow.TryGetValue(out var original))
            {
                return SoloResult.Fail<SoloComparisonChunk>(sourceRow.Error);
            }

            var ticks = new List<long>(count);
            for (var i = 0; i < count; i++)
            {
                var tick = fromTick + (long)i * stride;
                if (tick > GameRulesV1.RoundTicks)
                {
                    break;
                }

                ticks.Add(tick);
            }

            var aligned = await Branches.CompareAsync(actor, child, ticks, ct).ConfigureAwait(false);
            if (!aligned.TryGetValue(out var states))
            {
                return SoloResult.Fail<SoloComparisonChunk>(aligned.Error);
            }

            var points = ImmutableArray.CreateBuilder<SoloComparisonPoint>(states.Count);
            for (var i = 0; i < states.Count; i++)
            {
                var a = states[i];
                var afterFork = a.Tick > row.ForkTick;
                var ghost = SoloScoreParts.Of(a.Ghost);
                var current = SoloScoreParts.Of(a.Current);
                var final = a.Tick == GameRulesV1.RoundTicks;
                var mismatch = a.Tick != ticks[i] || a.Ghost.Tick != a.Tick || a.Current.Tick != a.Tick
                    || (!afterFork && (a.Delta != 0 || ghost != current || a.GhostStateHash != a.CurrentStateHash))
                    || (final && (a.GhostStateHash != original.FinalStateHash || a.CurrentStateHash != row.FinalStateHash));
                if (mismatch)
                {
                    return SoloResult.Fail<SoloComparisonChunk>(SoloErrorCode.VerificationFailed,
                        $"The comparison at tick {ticks[i]} does not match the verified histories.");
                }

                points.Add(new SoloComparisonPoint(
                    a.Tick, a.Ghost.TotalScore, a.Current.TotalScore, a.Delta, ghost, current, afterFork, final, a.GhostStateHash, a.CurrentStateHash));
            }

            var next = ticks[^1] + stride;
            return SoloResult.Ok(new SoloComparisonChunk(
                row.Timeline, row.Source, SoloBranchKinds.Of(row), row.ForkTick, GhostLapTimelineVersions.V1, row.RulesVersion,
                row.LayoutVersion, GameRulesV1.RoundTicks, original.FinalStateHash!, row.FinalStateHash, fromTick, stride,
                next <= GameRulesV1.RoundTicks ? next : null, points.MoveToImmutable()));
        }, ct);
    }

    /// <summary>Bounded saved-child visual replay after refresh, over the existing authorized canonical restore port.</summary>
    public Task<Result<SoloChildTraceChunk, SoloError>> TraceChildAsync(
        IActorContext actor, TimelineId child, long fromTick, int count, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (count is < 1 or > SoloOriginals.MaxTraceTicks)
            return Task.FromResult(SoloResult.Fail<SoloChildTraceChunk>(SoloErrorCode.InvalidRequest, "Invalid trace count."));
        if (fromTick is < 0 or > GameRulesV1.RoundTicks)
            return Task.FromResult(SoloResult.Fail<SoloChildTraceChunk>(SoloErrorCode.InvalidTick, "Invalid trace tick."));
        return SoloResult.GuardAsync(async () =>
        {
            var owned = await OwnedChildAsync(actor, child, ct).ConfigureAwait(false);
            if (!owned.TryGetValue(out var row)) return SoloResult.Fail<SoloChildTraceChunk>(owned.Error);
            if (row.Status != SoloOriginalStatus.Complete || row.FinalTick != GameRulesV1.RoundTicks || string.IsNullOrEmpty(row.FinalStateHash))
                return SoloResult.Fail<SoloChildTraceChunk>(SoloErrorCode.NotComplete, "The child is not completed.");
            if (row.RulesVersion != GameRulesV1.RulesVersion || row.LayoutVersion != GameRulesV1.LayoutVersion)
                return SoloResult.Fail<SoloChildTraceChunk>(SoloErrorCode.IncompatibleVersion, "Unsupported solo rules or layout.");
            var source = await Originals.TraceAsync(actor, row.Source, GameRulesV1.RoundTicks, 1, ct).ConfigureAwait(false);
            if (!source.TryGetValue(out var original)) return SoloResult.Fail<SoloChildTraceChunk>(source.Error);
            var terminal = await Branches.RestoreAsync(actor, child, GameRulesV1.RoundTicks, ct).ConfigureAwait(false);
            if (!terminal.TryGetValue(out var final)) return SoloResult.Fail<SoloChildTraceChunk>(terminal.Error);
            if (final.HistoryStatus != HistoryStatus.Verified || !final.State.IsFinal || final.StateHash != row.FinalStateHash)
                return SoloResult.Fail<SoloChildTraceChunk>(SoloErrorCode.VerificationFailed, "Child terminal identity mismatch.");
            var last = Math.Min(GameRulesV1.RoundTicks, fromTick + count - 1);
            var frames = ImmutableArray.CreateBuilder<SoloTraceFrame>((int)(last-fromTick+1));
            for (var tick = fromTick; tick <= last; tick++)
            {
                var restored = tick == GameRulesV1.RoundTicks ? terminal : await Branches.RestoreAsync(actor, child, tick, ct).ConfigureAwait(false);
                if (!restored.TryGetValue(out var state)) return SoloResult.Fail<SoloChildTraceChunk>(restored.Error);
                if (state.HistoryStatus != HistoryStatus.Verified || state.Position != tick || state.State.Tick != tick || state.State.IsFinal != (tick == GameRulesV1.RoundTicks))
                    return SoloResult.Fail<SoloChildTraceChunk>(SoloErrorCode.VerificationFailed, "Child trace identity mismatch.");
                frames.Add(SoloTraceFrame.Of(state));
            }
            return SoloResult.Ok(new SoloChildTraceChunk(row.Source, SoloBranchKinds.Of(row), row.ForkTick, original.SourceFinalStateHash,
                new SoloTraceChunk(child, GhostLapTimelineVersions.V1, row.RulesVersion, row.LayoutVersion, GameRulesV1.RoundTicks,
                    row.FinalStateHash, fromTick, last < GameRulesV1.RoundTicks ? last+1 : null, frames.MoveToImmutable(), original.Seed)));
        }, ct);
    }

    /// <summary>The actor's own branch row: the owner policy first, then the row. Foreign and unknown are the same NotFound.</summary>
    private async Task<Result<SoloBranchRecord, SoloError>> OwnedChildAsync(IActorContext actor, TimelineId child, CancellationToken ct)
    {
        var decision = await Authorizer.AuthorizeAsync(actor, SoloOwnershipLookup.BranchResourceOf(child), ResourceOperation.Read, ct)
            .ConfigureAwait(false);
        return decision.IsAllowed
            ? await Store.FindBranchAsync(child, ct).ConfigureAwait(false)
            : SoloResult.Fail<SoloBranchRecord>(SoloErrorCode.NotFound, "No such challenge or fork.");
    }

    private static SoloChildSummary Summarize(SoloBranchRecord row, int? score)
    {
        var kind = SoloBranchKinds.Of(row);
        var prefix = SoloBranchKinds.PrefixLength(kind);
        var clientKey = row.OperationKey.Length > prefix && row.OperationKey.StartsWith(
            kind == SoloBranchKind.Challenge ? SoloBranchKinds.ChallengeKeyPrefix : SoloBranchKinds.ForkKeyPrefix, StringComparison.Ordinal)
            ? row.OperationKey[prefix..]
            : row.OperationKey;
        return new SoloChildSummary(
            row.Timeline, row.Source, kind, clientKey, row.ForkTick, row.FinalTick!.Value, row.FinalStateHash!, row.LastRecordHash!, score,
            score is not null, row.Seed, row.MascotId, row.RulesVersion, row.LayoutVersion, GhostLapTimelineVersions.V1);
    }
}
