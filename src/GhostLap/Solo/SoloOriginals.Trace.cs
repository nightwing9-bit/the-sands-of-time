using System.Collections.Immutable;
using GhostLap.Rules;
using GhostLap.Simulation;
using GhostLap.Timeline;
using Marrow.Kernel.Results;
using Marrow.Kernel.Security;
using Marrow.Timeline;

namespace GhostLap.Solo;

/// <summary>
/// The verified historical state of one source tick, projected for a read-only ghost. Every field is taken from the full
/// state restored (checkpoint + canonical fold) at exactly <see cref="Tick"/>; scores are that tick's running score, never
/// the final score. <see cref="Events"/> are the cues resolved on this tick only (candle collected, Overlit started,
/// manual/auto Burst, ...) in the reducer's stable order. A frame is data: it has no collider and never enters a living world.
/// </summary>
public sealed record SoloTraceFrame(
    long Tick,
    int XFixed,
    int YFixed,
    long DistanceFixed,
    int Charge,
    bool IsOverlit,
    long OverlitDeadlineTick,
    int MovementPoints,
    int PickupPoints,
    int PenaltyPoints,
    int TotalScore,
    int Pickups,
    int Bursts,
    int BurstHits,
    sbyte HeldMoveX,
    sbyte HeldMoveY,
    bool BurstLatch,
    int CollectedCount,
    ImmutableArray<string> ActiveCandleIds,
    ImmutableArray<GameEvent> Events,
    string StateHash,
    StateHashOrigin HashOrigin,
    bool IsFinal)
{
    internal static SoloTraceFrame Of(VerifiedState<GameState> verified)
    {
        var s = verified.State;
        var p = s.Players[0];
        return new SoloTraceFrame(s.Tick, p.XFixed, p.YFixed, p.DistanceFixed, p.Charge, p.IsOverlit, p.OverlitDeadlineTick,
            p.MovementPoints, p.PickupPoints, p.PenaltyPoints, p.TotalScore, p.Pickups, p.Bursts, p.BurstHits,
            p.HeldMoveX, p.HeldMoveY, p.BurstLatch, s.Collected.Length, s.ActiveCandleIds, s.Events,
            verified.StateHash, verified.HashOrigin, s.IsFinal);
    }
}

/// <summary>
/// One bounded, tick-ordered slice of an owner's verified original. <see cref="NextTick"/> is the first tick after this
/// slice (null once tick <see cref="SourceFinalTick"/> is included). The source identity (versions, final tick and hash)
/// is repeated on every chunk so a chunk can always be tied to the requested original and pinned <c>ghost-v1</c> rules.
/// </summary>
public sealed record SoloTraceChunk(
    TimelineId Timeline,
    TimelineVersionKey Versions,
    string RulesVersion,
    string LayoutVersion,
    long SourceFinalTick,
    string SourceFinalStateHash,
    long FromTick,
    long? NextTick,
    ImmutableArray<SoloTraceFrame> Frames,
    long Seed);

public sealed partial class SoloOriginals
{
    /// <summary>Largest number of consecutive ticks one trace call returns (a full round is read in ten chunks).</summary>
    public const int MaxTraceTicks = 300;

    /// <summary>
    /// Reads <paramref name="count"/> consecutive ticks starting at <paramref name="fromTick"/> (stride one, ascending) from
    /// the actor's own verified original. The owner policy and the Complete row come first, so a foreign, unknown or
    /// unfinished id is <see cref="SoloErrorCode.NotFound"/>/<see cref="SoloErrorCode.NotComplete"/>. Each frame is the
    /// engine's restored full state and must be Verified. Every read checks the final tick's hash against the hash
    /// recorded at completion, including slices that end before the final tick. An unsupported rules/layout or codec is
    /// <see cref="SoloErrorCode.IncompatibleVersion"/>, never reinterpreted. The slice is clamped to the final tick.
    /// </summary>
    public Task<Result<SoloTraceChunk, SoloError>> TraceAsync(
        IActorContext actor, TimelineId timeline, long fromTick, int count, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (count is < 1 or > MaxTraceTicks)
        {
            return Task.FromResult(SoloResult.Fail<SoloTraceChunk>(SoloErrorCode.InvalidRequest,
                $"Request 1-{MaxTraceTicks} ticks per trace chunk."));
        }

        return SoloResult.GuardAsync(async () =>
        {
            var found = await ReadableOriginalAsync(actor, timeline, ct).ConfigureAwait(false);
            if (!found.TryGetValue(out var row))
            {
                return SoloResult.Fail<SoloTraceChunk>(found.Error);
            }

            if (row.RulesVersion != GameRulesV1.RulesVersion || row.LayoutVersion != GameRulesV1.LayoutVersion)
            {
                return SoloResult.Fail<SoloTraceChunk>(SoloErrorCode.IncompatibleVersion, "Unsupported solo rules or layout.");
            }

            var finalTick = (long)GameRulesV1.RoundTicks;
            if (row.FinalTick != finalTick)
            {
                return SoloResult.Fail<SoloTraceChunk>(SoloErrorCode.VerificationFailed, "The original has an invalid final tick.");
            }

            if (fromTick < 0 || fromTick > finalTick)
            {
                return SoloResult.Fail<SoloTraceChunk>(SoloErrorCode.InvalidTick, $"Tick must be 0-{finalTick}.");
            }

            var last = Math.Min(finalTick, fromTick + count - 1);
            if (last < finalTick)
            {
                var terminal = await RestoreOriginalTickAsync(row, finalTick, ct).ConfigureAwait(false);
                if (terminal.IsFailure)
                {
                    return SoloResult.Fail<SoloTraceChunk>(terminal.Error);
                }
            }

            var frames = ImmutableArray.CreateBuilder<SoloTraceFrame>((int)(last - fromTick + 1));
            for (var tick = fromTick; tick <= last; tick++)
            {
                ct.ThrowIfCancellationRequested();
                var restored = await RestoreOriginalTickAsync(row, tick, ct).ConfigureAwait(false);
                if (!restored.TryGetValue(out var state))
                {
                    return SoloResult.Fail<SoloTraceChunk>(restored.Error);
                }

                frames.Add(SoloTraceFrame.Of(state));
            }

            return SoloResult.Ok(new SoloTraceChunk(
                timeline, GhostLapTimelineVersions.V1, row.RulesVersion, row.LayoutVersion, finalTick, row.FinalStateHash!,
                fromTick, last < finalTick ? last + 1 : null, frames.MoveToImmutable(), row.Seed));
        }, ct);
    }

    private async Task<Result<VerifiedState<GameState>, SoloError>> RestoreOriginalTickAsync(
        SoloOriginalRecord row, long tick, CancellationToken ct)
    {
        var restored = await _engine.RestoreAsync(row.Timeline, tick, ct).ConfigureAwait(false);
        if (!restored.TryGetValue(out var state))
        {
            return SoloResult.Fail<VerifiedState<GameState>>(restored.Error);
        }

        var isFinal = tick == row.FinalTick;
        if (state.HistoryStatus != HistoryStatus.Verified || state.Position != tick || state.State.Tick != tick
            || state.State.IsFinal != isFinal || (isFinal && state.StateHash != row.FinalStateHash))
        {
            return SoloResult.Fail<VerifiedState<GameState>>(SoloErrorCode.VerificationFailed,
                $"Tick {tick} does not reproduce the verified original.");
        }

        return SoloResult.Ok(state);
    }

    /// <summary>The single verified frame at <paramref name="tick"/> (0..final; the final tick is inspect-only).</summary>
    public async Task<Result<SoloTraceFrame, SoloError>> InspectAsync(
        IActorContext actor, TimelineId timeline, long tick, CancellationToken ct)
    {
        var chunk = await TraceAsync(actor, timeline, tick, 1, ct).ConfigureAwait(false);
        return chunk.TryGetValue(out var value)
            ? SoloResult.Ok(value.Frames[0])
            : SoloResult.Fail<SoloTraceFrame>(chunk.Error);
    }
}
