using System.Collections.Immutable;
using GhostLap.Rules;
using GhostLap.Simulation;
using Marrow.Kernel.Results;
using Marrow.Kernel.Security;
using Marrow.Timeline;

namespace GhostLap.Solo;

/// <summary>
/// Read-only view of the solo player of one world at one tick. For the source it is the historical ghost: data taken from
/// the verified source history, never fed into the living world's reducer, so it has no collider and cannot collect,
/// contact or score there. <see cref="ActiveCandleIds"/> are the candles still uncollected in that world.
/// </summary>
public sealed record WorldFrame(
    long Tick,
    int XFixed,
    int YFixed,
    int Charge,
    long OverlitDeadlineTick,
    int MovementPoints,
    int PickupPoints,
    int PenaltyPoints,
    int TotalScore,
    int Pickups,
    ImmutableArray<string> ActiveCandleIds,
    ImmutableArray<GameEvent> Events)
{
    public static WorldFrame Of(GameState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var p = state.Players[0];
        return new WorldFrame(state.Tick, p.XFixed, p.YFixed, p.Charge, p.OverlitDeadlineTick, p.MovementPoints, p.PickupPoints,
            p.PenaltyPoints, p.TotalScore, p.Pickups, state.ActiveCandleIds, state.Events);
    }
}

/// <summary>
/// Source ghost and current branch at the same tick, from the Timeline's aligned verified states. <see cref="Delta"/> is
/// current minus source score at this tick (not either final score); <see cref="IsFinal"/> only at the final tick, where
/// the totals are the final results. <see cref="CurrentStatus"/> is Recording while the branch is still being played.
/// </summary>
public sealed record AlignedScore(long Tick, WorldFrame Ghost, WorldFrame Current, int Delta, bool IsFinal, HistoryStatus CurrentStatus);

/// <summary>A branch that reached the final tick and whose Timeline replay reproduced the recorded hashes.</summary>
public sealed record SoloBranchResult(
    TimelineId Timeline,
    TimelineId Source,
    long ForkTick,
    long FinalTick,
    string FinalStateHash,
    string LastRecordHash,
    GameState FinalState);

/// <summary>
/// The living world of one branch: a Recording Marrow Timeline child of a solo original that shares the source's verified
/// prefix through <see cref="ForkTick"/> and owns only ticks <c>ForkTick + 1..</c>. It is played with exactly the same
/// admission policy, reducer and commit loop as the original (<see cref="TickRecorder"/>), on the same pinned candle
/// schedule; its collected-candle set, charge and score are its own. The source history is never written.
/// </summary>
public sealed class SoloBranch
{
    private readonly SoloOriginalStore _store;
    private readonly TickRecorder _recorder;

    internal SoloBranch(
        ITimelineEngine<GameState, GameInput, GameContext> engine, SoloOriginalStore store, SoloBranchRecord record,
        GameContext context, GameState state)
    {
        _store = store;
        Record = record;
        _recorder = new TickRecorder(engine, record.Timeline, SoloBranches.BranchKey(record.OperationKey), context, state);
    }

    public SoloBranchRecord Record { get; }

    public TimelineId Timeline => Record.Timeline;

    public TimelineId Source => Record.Source;

    public long ForkTick => Record.ForkTick;

    public GameContext Context => _recorder.Context;

    /// <summary>The last committed authoritative state of the branch (the source state at <see cref="ForkTick"/> before any branch tick).</summary>
    public GameState State => _recorder.State;

    public bool IsRoundOver => State.IsFinal;

    public Result<GameInput, InputRejection> Submit(SubmittedInput submitted) => _recorder.Submit(submitted);

    public Result<GameInput, InputRejection> Neutralize(byte slot) => _recorder.Neutralize(slot);

    public Task<Result<SoloTickReceipt, SoloError>> CommitTickAsync(CancellationToken ct) => _recorder.CommitTickAsync(ct);

    /// <summary>Completes and replay-verifies the branch history, then marks the branch row Complete. Idempotent.</summary>
    public Task<Result<SoloBranchResult, SoloError>> CompleteAsync(CancellationToken ct) =>
        _recorder.CompleteAsync(async (receipt, final, token) =>
        {
            var marked = await _store.MarkBranchCompleteAsync(Timeline, final.Tick, receipt.FinalStateHash, receipt.LastRecordHash, token)
                .ConfigureAwait(false);
            return marked.TryGetValue(out _)
                ? SoloResult.Ok(new SoloBranchResult(Timeline, Source, ForkTick, final.Tick, receipt.FinalStateHash,
                    receipt.LastRecordHash, final))
                : SoloResult.Fail<SoloBranchResult>(marked.Error);
        }, ct);
}

/// <summary>
/// Branching of a solo original through the one shared Marrow Timeline (<see cref="ITimelineEngine{TState,TInput,TContext}.ForkAsync"/>,
/// <see cref="ITimelineEngine{TState,TInput,TContext}.RestoreAsync"/> and <see cref="ITimelineEngine{TState,TInput,TContext}.CompareAlignedAsync"/>);
/// there is no second replay engine. The owner is always the verified <see cref="IActorContext"/>; a fork is allowed only
/// from the actor's own Complete original at a nonterminal tick, and every read of a branch goes through the owner policy
/// (resource kind <see cref="SoloOwnershipLookup.BranchResourceKind"/>) with the same NotFound for strangers as for unknown ids.
/// </summary>
public sealed class SoloBranches(
    ITimelineEngine<GameState, GameInput, GameContext> engine,
    SoloOriginalStore store,
    IResourceAuthorizer authorizer)
{
    /// <summary>Upper bound of ticks per comparison request (each tick is two verified restores).</summary>
    public const int MaxCompareTicks = 512;

    private readonly ITimelineEngine<GameState, GameInput, GameContext> _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    private readonly SoloOriginalStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IResourceAuthorizer _authorizer = authorizer ?? throw new ArgumentNullException(nameof(authorizer));

    /// <summary>
    /// Forks the actor's original at <paramref name="forkTick"/> (0 &lt;= T &lt; final tick) and returns the living branch
    /// positioned at its last committed tick (T for a new branch). The same operation key with the same source and tick
    /// resumes the same branch (idempotent retry); the same key for a different source or tick is a Conflict and creates
    /// nothing. The final tick is inspect-only (<see cref="SoloErrorCode.ForkNotAllowed"/>).
    /// </summary>
    public Task<Result<SoloBranch, SoloError>> ForkAsync(
        IActorContext actor, TimelineId source, long forkTick, string operationKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!actor.IsAuthenticated || string.IsNullOrEmpty(actor.SubjectId))
        {
            return Task.FromResult(SoloResult.Fail<SoloBranch>(SoloErrorCode.Unauthenticated, "Branching requires a verified actor."));
        }

        if (!SoloOriginals.IsValidOperationKey(operationKey))
        {
            return Task.FromResult(SoloResult.Fail<SoloBranch>(SoloErrorCode.InvalidRequest,
                $"Operation key must be 1-{SoloOriginals.MaxOperationKeyLength} characters of letters, digits, '-', '_' or '.'."));
        }

        return SoloResult.GuardAsync(async () =>
        {
            var readable = await SoloOriginals.ReadableOriginalAsync(_authorizer, _store, actor, source, ct).ConfigureAwait(false);
            if (!readable.TryGetValue(out var original))
            {
                return SoloResult.Fail<SoloBranch>(readable.Error);
            }

            var final = original.FinalTick ?? GameRulesV1.RoundTicks;
            if (forkTick == final)
            {
                return SoloResult.Fail<SoloBranch>(SoloErrorCode.ForkNotAllowed,
                    $"Tick {forkTick} is the final tick; it can be inspected but not forked.");
            }

            if (forkTick < 0 || forkTick > final)
            {
                return SoloResult.Fail<SoloBranch>(SoloErrorCode.InvalidTick, $"Fork tick must be 0-{final - 1}.");
            }

            var request = new SoloBranchRecord(
                default, source, forkTick, actor.SubjectId, actor.PrincipalKind, actor.TenantId, operationKey,
                original.RulesVersion, original.LayoutVersion, original.Seed, original.MascotId, SoloOriginalStatus.Recording);

            // Checked before forking so a conflicting duplicate never creates a Timeline child.
            var existing = await _store.ReadBranchByKeyAsync(actor.SubjectId, operationKey, ct).ConfigureAwait(false);
            if (existing is not null && !existing.SameRequest(request with { Timeline = existing.Timeline }))
            {
                return SoloResult.Fail<SoloBranch>(SoloErrorCode.Conflict,
                    "This operation key already names a branch of a different original or fork tick.");
            }

            var context = SoloOriginals.ContextOf(original);
            var forked = await _engine.ForkAsync(source, forkTick, BranchKey(operationKey), context, ct).ConfigureAwait(false);
            if (!forked.TryGetValue(out var child))
            {
                return SoloResult.Fail<SoloBranch>(forked.Error);
            }

            var registered = await _store.RegisterBranchAsync(request with { Timeline = child }, ct).ConfigureAwait(false);
            if (!registered.TryGetValue(out var row))
            {
                return SoloResult.Fail<SoloBranch>(registered.Error);
            }

            // Resume point: the verified branch prefix the database holds (exactly the source state at T for a new branch).
            var current = await _engine.ReplayAsync(child, ct).ConfigureAwait(false);
            if (!current.TryGetValue(out var replay))
            {
                return SoloResult.Fail<SoloBranch>(current.Error);
            }

            if (replay.Status == HistoryStatus.Failed || replay.FinalPosition < forkTick)
            {
                return SoloResult.Fail<SoloBranch>(SoloErrorCode.Conflict, "This branch failed verification and cannot continue.");
            }

            return SoloResult.Ok(new SoloBranch(_engine, _store, row, context, replay.FinalState));
        }, ct);
    }

    /// <summary>
    /// The verified state of the actor's branch after exactly ticks 1..<paramref name="tick"/>: the shared source prefix
    /// for ticks &lt;= fork tick, the branch's own ticks after it. A Recording branch reports its committed prefix.
    /// </summary>
    public Task<Result<VerifiedState<GameState>, SoloError>> RestoreAsync(
        IActorContext actor, TimelineId branch, long tick, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return SoloResult.GuardAsync(async () =>
        {
            var row = await ReadableBranchAsync(actor, branch, ct).ConfigureAwait(false);
            if (row.IsFailure)
            {
                return SoloResult.Fail<VerifiedState<GameState>>(row.Error);
            }

            var restored = await _engine.RestoreAsync(branch, tick, ct).ConfigureAwait(false);
            if (!restored.TryGetValue(out var state))
            {
                return SoloResult.Fail<VerifiedState<GameState>>(restored.Error);
            }

            return state.HistoryStatus == HistoryStatus.Failed
                ? SoloResult.Fail<VerifiedState<GameState>>(SoloErrorCode.VerificationFailed, "The branch history failed verification.")
                : SoloResult.Ok(state);
        }, ct);
    }

    /// <summary>
    /// Source ghost and current branch at each requested tick (in request order) from the Timeline's aligned verified
    /// states, with the signed score delta at that tick. Ticks must be within both histories (the branch's committed prefix).
    /// </summary>
    public Task<Result<IReadOnlyList<AlignedScore>, SoloError>> CompareAsync(
        IActorContext actor, TimelineId branch, IReadOnlyList<long> ticks, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(ticks);
        if (ticks.Count is 0 or > MaxCompareTicks)
        {
            return Task.FromResult(SoloResult.Fail<IReadOnlyList<AlignedScore>>(SoloErrorCode.InvalidRequest,
                $"Request 1-{MaxCompareTicks} ticks."));
        }

        return SoloResult.GuardAsync(async () =>
        {
            var readable = await ReadableBranchAsync(actor, branch, ct).ConfigureAwait(false);
            if (!readable.TryGetValue(out var row))
            {
                return SoloResult.Fail<IReadOnlyList<AlignedScore>>(readable.Error);
            }

            var aligned = await _engine.CompareAlignedAsync(row.Source, branch, ticks, ct).ConfigureAwait(false);
            if (!aligned.TryGetValue(out var states))
            {
                return SoloResult.Fail<IReadOnlyList<AlignedScore>>(aligned.Error);
            }

            var result = new List<AlignedScore>(states.Count);
            foreach (var a in states)
            {
                if (a.Source.HistoryStatus != HistoryStatus.Verified || a.Alternative.HistoryStatus == HistoryStatus.Failed)
                {
                    return SoloResult.Fail<IReadOnlyList<AlignedScore>>(SoloErrorCode.VerificationFailed,
                        "The source original is not Verified or the branch failed verification.");
                }

                var ghost = WorldFrame.Of(a.Source.State);
                var current = WorldFrame.Of(a.Alternative.State);
                result.Add(new AlignedScore(a.Position, ghost, current, checked(current.TotalScore - ghost.TotalScore),
                    a.Position == GameRulesV1.RoundTicks, a.Alternative.HistoryStatus));
            }

            return SoloResult.Ok<IReadOnlyList<AlignedScore>>(result);
        }, ct);
    }

    internal static string BranchKey(string operationKey) => $"ghost-lap/branch/{operationKey}";

    private async Task<Result<SoloBranchRecord, SoloError>> ReadableBranchAsync(IActorContext actor, TimelineId branch, CancellationToken ct)
    {
        var decision = await _authorizer.AuthorizeAsync(actor, SoloOwnershipLookup.BranchResourceOf(branch), ResourceOperation.Read, ct)
            .ConfigureAwait(false);
        return decision.IsAllowed
            ? await _store.FindBranchAsync(branch, ct).ConfigureAwait(false)
            : SoloResult.Fail<SoloBranchRecord>(SoloErrorCode.NotFound, "No such branch.");
    }
}
