using GhostLap.Rules;
using GhostLap.Simulation;
using GhostLap.Timeline;
using Marrow.Kernel.Results;
using Marrow.Kernel.Security;
using Marrow.Timeline;

namespace GhostLap.Solo;

/// <summary>What a stored solo branch semantically is. A child is never an original: originals live only in <c>solo_originals</c>.</summary>
public enum SoloBranchKind
{
    /// <summary>A continuation from a chosen tick (Task 11.4).</summary>
    Fork,
    /// <summary>A fresh full 2,700-tick race against the original's ghost, a branch at tick 0.</summary>
    Challenge,
}

/// <summary>
/// Classification of a branch from the durable metadata it already has (fork tick and the server-minted operation key
/// prefix, per the Task 11.0 decision). No new column or table: the key is unique per owner and stored in <c>solo_branches</c>.
/// </summary>
public static class SoloBranchKinds
{
    public const string ChallengeKeyPrefix = "challenge.";

    public static string ChallengeKey(string clientKey) => ChallengeKeyPrefix + clientKey;

    public static SoloBranchKind Of(SoloBranchRecord branch)
    {
        ArgumentNullException.ThrowIfNull(branch);
        return branch.ForkTick == 0 && branch.OperationKey.StartsWith(ChallengeKeyPrefix, StringComparison.Ordinal)
            ? SoloBranchKind.Challenge
            : SoloBranchKind.Fork;
    }
}

public enum SoloChallengePhase
{
    /// <summary>The original is being authenticated and its full trace prefetched. No gameplay exists yet.</summary>
    Preparing,
    /// <summary>A verified prepared trace is held. Gameplay can start immediately.</summary>
    Ready,
    /// <summary>The living challenge is being played against the local ghost trace.</summary>
    Running,
    /// <summary>All 2,700 ticks were committed and the child verified.</summary>
    Completed,
    /// <summary>Preparation or the run failed. <see cref="SoloChallenge.Error"/> says why; nothing was invented.</summary>
    Failed,
    /// <summary>Cancelled before completion. An unfinished child is never a result.</summary>
    Cancelled,
}

/// <summary>The sealed start of the living challenge: identical seed/layout/mascot to the original, and both worlds at tick 0.</summary>
public sealed record SoloChallengeStarted(
    TimelineId Child,
    TimelineId Original,
    string OperationKey,
    long Seed,
    byte MascotId,
    string RulesVersion,
    string LayoutVersion,
    WorldFrame Living,
    SoloTraceFrame Ghost);

/// <summary>
/// One committed living tick and the historical ghost at the same tick (a local array lookup). <see cref="Delta"/> is the
/// living minus the source running score at this tick.
/// </summary>
public sealed record SoloChallengeTick(
    long Tick, WorldFrame Living, SoloTraceFrame Ghost, int Delta, string StateHash, string RecordHash, bool IsFinal);

/// <summary>A finished, verified, owner-bound challenge child. It is a branch of <see cref="Original"/> at tick 0, never an original.</summary>
public sealed record SoloChallengeResult(
    TimelineId Child,
    TimelineId Original,
    SoloBranchKind Kind,
    string OperationKey,
    long ForkTick,
    long FinalTick,
    string ChildFinalStateHash,
    string ChildLastRecordHash,
    GameResult LivingResult,
    int LivingScore,
    int SourceScore,
    int Delta,
    string SourceFinalStateHash,
    string SourceLastRecordHash);

/// <summary>
/// Race the original ghost: prepares a verified, owner-authenticated, full T0..T2700 trace of a completed <c>ghost-v1</c>
/// original, then starts a fresh living challenge (a Timeline branch at tick 0 through <see cref="SoloBranches"/>). It adds no
/// Marrow port, schema or archive: the history comes from <see cref="SoloOriginals.TraceAsync"/>, the living history is a
/// <see cref="SoloBranch"/>, and the trace is kept only in process memory (<see cref="SoloGhostTraceCache"/>).
/// </summary>
public sealed class SoloChallenges(
    SoloOriginals originals,
    SoloBranches branches,
    SoloOriginalStore store,
    IResourceAuthorizer authorizer,
    SoloGhostTraceCache? cache = null)
{
    internal SoloOriginals Originals { get; } = originals ?? throw new ArgumentNullException(nameof(originals));

    internal SoloBranches Branches { get; } = branches ?? throw new ArgumentNullException(nameof(branches));

    internal SoloOriginalStore Store { get; } = store ?? throw new ArgumentNullException(nameof(store));

    internal IResourceAuthorizer Authorizer { get; } = authorizer ?? throw new ArgumentNullException(nameof(authorizer));

    public SoloGhostTraceCache Cache { get; } = cache ?? new SoloGhostTraceCache();

    /// <summary>
    /// Creates a challenge in <see cref="SoloChallengePhase.Preparing"/>. No I/O happens until <see cref="SoloChallenge.PrepareAsync"/>.
    /// A retry after failure or cancellation is a new challenge (it reuses a matching prepared trace, never a partial one).
    /// </summary>
    public SoloChallenge Begin(IActorContext actor, TimelineId original)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return new SoloChallenge(this, actor, original);
    }

    /// <summary>
    /// The owner's finished challenge by its client key, without a live session (lost response, refresh, host restart): the
    /// stored child must be a Complete <see cref="SoloBranchKind.Challenge"/> whose verified final state reproduces the recorded
    /// hash, and the source original must still replay to its own. An unfinished challenge is <see cref="SoloErrorCode.NotComplete"/>.
    /// </summary>
    public Task<Result<SoloChallengeResult, SoloError>> ResolveAsync(IActorContext actor, string clientKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var key = clientKey is null ? "" : SoloBranchKinds.ChallengeKey(clientKey);
        if (!actor.IsAuthenticated || string.IsNullOrEmpty(actor.SubjectId))
        {
            return Task.FromResult(SoloResult.Fail<SoloChallengeResult>(SoloErrorCode.Unauthenticated, "A verified actor is required."));
        }

        if (string.IsNullOrEmpty(clientKey) || !SoloOriginals.IsValidOperationKey(key))
        {
            return Task.FromResult(SoloResult.Fail<SoloChallengeResult>(SoloErrorCode.InvalidRequest, "Invalid challenge key."));
        }

        return SoloResult.GuardAsync(async () =>
        {
            var row = await Store.ReadBranchByKeyAsync(actor.SubjectId, key, ct).ConfigureAwait(false);
            if (row is null || SoloBranchKinds.Of(row) != SoloBranchKind.Challenge)
            {
                return SoloResult.Fail<SoloChallengeResult>(SoloErrorCode.NotFound, "No such challenge.");
            }

            if (row.Status != SoloOriginalStatus.Complete || row.FinalTick != GameRulesV1.RoundTicks)
            {
                return SoloResult.Fail<SoloChallengeResult>(SoloErrorCode.NotComplete, "This challenge was not completed and verified.");
            }

            var source = await Originals.OpenAsync(actor, row.Source, ct).ConfigureAwait(false);
            if (!source.TryGetValue(out var original))
            {
                return SoloResult.Fail<SoloChallengeResult>(source.Error);
            }

            var restored = await Branches.RestoreAsync(actor, row.Timeline, GameRulesV1.RoundTicks, ct).ConfigureAwait(false);
            if (!restored.TryGetValue(out var final))
            {
                return SoloResult.Fail<SoloChallengeResult>(restored.Error);
            }

            return final.HistoryStatus == HistoryStatus.Verified && final.State.IsFinal && final.StateHash == row.FinalStateHash
                ? SoloResult.Ok(Build(row, final.State, original))
                : SoloResult.Fail<SoloChallengeResult>(SoloErrorCode.VerificationFailed, "The stored challenge does not reproduce its recorded hash.");
        }, ct);
    }

    internal static SoloChallengeResult Build(SoloBranchRecord row, GameState finalState, SoloOriginal original)
    {
        var living = finalState.FinalResult ?? throw new InvalidOperationException("A final state has a result.");
        var livingScore = living.Scores[0].TotalScore;
        var sourceScore = original.Result.Scores[0].TotalScore;
        return new SoloChallengeResult(
            row.Timeline, row.Source, SoloBranchKinds.Of(row), row.OperationKey, row.ForkTick, row.FinalTick!.Value,
            row.FinalStateHash!, row.LastRecordHash!, living, livingScore, sourceScore, checked(livingScore - sourceScore),
            original.FinalStateHash, original.LastRecordHash);
    }
}

/// <summary>
/// One challenge attempt: <c>Preparing -&gt; Ready -&gt; Running -&gt; Completed</c>, or <c>Failed</c>/<c>Cancelled</c> at any point
/// before completion. Gameplay can start only from <see cref="SoloChallengePhase.Ready"/> and therefore never with incomplete or
/// invented history. While running, the ghost is read by <see cref="GhostAt"/>/<see cref="CommitTickAsync"/> from the prepared
/// array only; the living world is a separate <see cref="SoloBranch"/> that never sees a ghost frame.
/// <para>Driven by one tick loop: <see cref="CommitTickAsync"/> and <see cref="CompleteAsync"/> must not overlap.
/// <see cref="Submit"/> and <see cref="Cancel"/> may run concurrently.</para>
/// </summary>
public sealed class SoloChallenge
{
    private readonly SoloChallenges _services;
    private readonly IActorContext _actor;
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _gate = new();
    private SoloChallengePhase _phase = SoloChallengePhase.Preparing;
    private SoloError? _error;
    private bool _prepareStarted;
    private bool _starting;
    private PreparedTraceLease? _lease;
    private PreparedGhostTrace? _trace;
    private SoloBranch? _branch;
    private SoloChallengeResult? _result;

    internal SoloChallenge(SoloChallenges services, IActorContext actor, TimelineId original)
    {
        _services = services;
        _actor = actor;
        Original = original;
    }

    public TimelineId Original { get; }

    public SoloChallengePhase Phase
    {
        get
        {
            lock (_gate)
            {
                return _phase;
            }
        }
    }

    /// <summary>Why the challenge is Failed (or the transient cause of a cancellation); null otherwise.</summary>
    public SoloError? Error
    {
        get
        {
            lock (_gate)
            {
                return _error;
            }
        }
    }

    /// <summary>The living child's Timeline id once running (a branch at tick 0), otherwise null.</summary>
    public TimelineId? Child => _branch?.Timeline;

    /// <summary>The living world's pinned context (the original's seed, mascot and candle plan); null before running.</summary>
    public GameContext? LivingContext => _branch?.Context;

    /// <summary>The last committed living state; null before the challenge is running.</summary>
    public GameState? LivingState => _branch?.State;

    /// <summary>The final verified result after <see cref="SoloChallengePhase.Completed"/>.</summary>
    public SoloChallengeResult? Result
    {
        get
        {
            lock (_gate)
            {
                return _result;
            }
        }
    }

    /// <summary>True while a prepared trace is held in memory (Ready or Running).</summary>
    public bool HoldsTrace
    {
        get
        {
            lock (_gate)
            {
                return _trace is not null;
            }
        }
    }

    /// <summary>
    /// Authenticates the owner's completed original, verifies it, and prefetches the full T0..T2700 trace in bounded chunks
    /// (or reuses a prepared trace whose original id, pinned rules/codecs and verified final hash match the freshly read row).
    /// Any failure leaves the challenge Failed (cancellation: Cancelled) with the typed error and starts no gameplay.
    /// </summary>
    public async Task<Result<PreparedGhostTrace, SoloError>> PrepareAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_phase != SoloChallengePhase.Preparing || _prepareStarted)
            {
                return SoloResult.Fail<PreparedGhostTrace>(SoloErrorCode.Conflict, $"The challenge is {_phase}; preparation cannot start.");
            }

            _prepareStarted = true;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        var prepared = await SoloResult.GuardAsync(() => PrepareCoreAsync(linked.Token), linked.Token).ConfigureAwait(false);
        lock (_gate)
        {
            if (_phase != SoloChallengePhase.Preparing)
            {
                // Cancelled while preparing: whatever was prepared is released, not started.
                prepared.TryGetValue(out var late);
                late?.Dispose();
                return SoloResult.Fail<PreparedGhostTrace>(SoloErrorCode.Cancelled, "The challenge was cancelled during preparation.");
            }

            if (prepared.TryGetValue(out var lease))
            {
                _lease = lease;
                _trace = lease.Trace;
                _phase = SoloChallengePhase.Ready;
                return SoloResult.Ok(lease.Trace);
            }

            _error = prepared.Error;
            _phase = prepared.Error.Code == SoloErrorCode.Cancelled ? SoloChallengePhase.Cancelled : SoloChallengePhase.Failed;
            return SoloResult.Fail<PreparedGhostTrace>(prepared.Error);
        }
    }

    private async Task<Result<PreparedTraceLease, SoloError>> PrepareCoreAsync(CancellationToken ct)
    {
        var readable = await SoloOriginals.ReadableOriginalAsync(_services.Authorizer, _services.Store, _actor, Original, ct).ConfigureAwait(false);
        if (!readable.TryGetValue(out var row))
        {
            return SoloResult.Fail<PreparedTraceLease>(readable.Error);
        }

        if (row.RulesVersion != GameRulesV1.RulesVersion || row.LayoutVersion != GameRulesV1.LayoutVersion)
        {
            return SoloResult.Fail<PreparedTraceLease>(SoloErrorCode.IncompatibleVersion, "Unsupported solo rules or layout.");
        }

        if (row.FinalTick != GameRulesV1.RoundTicks || string.IsNullOrEmpty(row.FinalStateHash))
        {
            return SoloResult.Fail<PreparedTraceLease>(SoloErrorCode.VerificationFailed, "The original has no valid final tick or hash.");
        }

        var reused = _services.Cache.TryLease(_actor.SubjectId, row);
        if (reused is not null)
        {
            return SoloResult.Ok(reused);
        }

        // Authenticate and verify the stored history once: replay must reproduce the hashes recorded at completion.
        var opened = await _services.Originals.OpenAsync(_actor, Original, ct).ConfigureAwait(false);
        if (!opened.TryGetValue(out var original))
        {
            return SoloResult.Fail<PreparedTraceLease>(opened.Error);
        }

        var frames = new SoloTraceFrame[GameRulesV1.RoundTicks + 1];
        long from = 0;
        while (from <= GameRulesV1.RoundTicks)
        {
            ct.ThrowIfCancellationRequested();
            var read = await _services.Originals.TraceAsync(_actor, Original, from, SoloOriginals.MaxTraceTicks, ct).ConfigureAwait(false);
            if (!read.TryGetValue(out var chunk))
            {
                return SoloResult.Fail<PreparedTraceLease>(read.Error);
            }

            var expected = (int)Math.Min(SoloOriginals.MaxTraceTicks, GameRulesV1.RoundTicks + 1 - from);
            long? expectedNext = from + expected <= GameRulesV1.RoundTicks ? from + expected : null;
            if (chunk.Timeline != Original || chunk.Versions != GhostLapTimelineVersions.V1
                || chunk.RulesVersion != row.RulesVersion || chunk.LayoutVersion != row.LayoutVersion
                || chunk.SourceFinalTick != GameRulesV1.RoundTicks || chunk.SourceFinalStateHash != original.FinalStateHash
                || chunk.FromTick != from || chunk.Frames.Length != expected
                || chunk.NextTick != expectedNext)
            {
                return SoloResult.Fail<PreparedTraceLease>(SoloErrorCode.VerificationFailed,
                    $"The trace chunk at tick {from} does not belong to the requested verified original.");
            }

            for (var i = 0; i < chunk.Frames.Length; i++)
            {
                if (chunk.Frames[i].Tick != from + i)
                {
                    return SoloResult.Fail<PreparedTraceLease>(SoloErrorCode.VerificationFailed, $"Trace frame {from + i} is misaligned.");
                }

                frames[from + i] = chunk.Frames[i];
            }

            from += expected;
        }

        var last = frames[GameRulesV1.RoundTicks];
        if (!last.IsFinal || last.StateHash != original.FinalStateHash || last.TotalScore != original.Result.Scores[0].TotalScore
            || frames[0].Tick != 0)
        {
            return SoloResult.Fail<PreparedTraceLease>(SoloErrorCode.VerificationFailed, "The prepared trace does not end at the verified final state.");
        }

        var trace = new PreparedGhostTrace(Original, row.RulesVersion, row.LayoutVersion, original.FinalStateHash, frames);
        var lease = _services.Cache.TryAdd(_actor.SubjectId, trace);
        return lease is null
            ? SoloResult.Fail<PreparedTraceLease>(SoloErrorCode.Unavailable, "Too many prepared ghost traces are active; retry shortly.")
            : SoloResult.Ok(lease);
    }

    /// <summary>The source frame at <paramref name="tick"/> from the prepared trace (a local lookup).</summary>
    public SoloTraceFrame GhostAt(long tick)
    {
        PreparedGhostTrace? trace;
        lock (_gate)
        {
            trace = _trace;
        }

        return (trace ?? throw new InvalidOperationException("No prepared trace is held.")).At(tick);
    }

    /// <summary>
    /// Starts the living challenge from the original's exact tick-zero seed, layout, mascot and candle schedule as a branch at
    /// tick 0 under the owner's server-minted <c>challenge.</c> key. Allowed only when <see cref="SoloChallengePhase.Ready"/>.
    /// A key that already names a branch with committed ticks is refused (an unfinished run is never continued).
    /// </summary>
    public async Task<Result<SoloChallengeStarted, SoloError>> StartAsync(string clientKey, CancellationToken ct)
    {
        var key = clientKey is null ? "" : SoloBranchKinds.ChallengeKey(clientKey);
        if (string.IsNullOrEmpty(clientKey) || !SoloOriginals.IsValidOperationKey(key))
        {
            return SoloResult.Fail<SoloChallengeStarted>(SoloErrorCode.InvalidRequest,
                $"Challenge key must be 1-{SoloOriginals.MaxOperationKeyLength - SoloBranchKinds.ChallengeKeyPrefix.Length} characters of letters, digits, '-', '_' or '.'.");
        }

        PreparedGhostTrace trace;
        lock (_gate)
        {
            if (_phase != SoloChallengePhase.Ready || _trace is null || _starting)
            {
                return SoloResult.Fail<SoloChallengeStarted>(SoloErrorCode.Conflict,
                    $"The challenge is {_phase}; only a Ready challenge with a verified prepared trace can start.");
            }

            _starting = true;
            trace = _trace;
        }

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            var forked = await _services.Branches.ForkAsync(_actor, Original, 0, key, linked.Token).ConfigureAwait(false);
            if (!forked.TryGetValue(out var branch))
            {
                return Refused(forked.Error);
            }

            var start = trace.At(0);
            var living = branch.State;
            var p = living.Players[0];
            if (living.Tick != 0)
            {
                return Refused(new SoloError(SoloErrorCode.Conflict, "This challenge key already has committed ticks; use a new key."));
            }

            // The branch context is the source row's own seed/mascot/rules by construction; tick zero is checked against the ghost.
            if (branch.Record.Source != Original || branch.ForkTick != 0 || p.XFixed != start.XFixed || p.YFixed != start.YFixed
                || p.DistanceFixed != start.DistanceFixed || !living.ActiveCandleIds.SequenceEqual(start.ActiveCandleIds))
            {
                return Fail(new SoloError(SoloErrorCode.VerificationFailed, "The living world does not start from the original's tick-zero state."));
            }

            lock (_gate)
            {
                if (_phase != SoloChallengePhase.Ready)
                {
                    return SoloResult.Fail<SoloChallengeStarted>(SoloErrorCode.Cancelled, "The challenge was cancelled while starting.");
                }

                _branch = branch;
                _phase = SoloChallengePhase.Running;
            }

            return SoloResult.Ok(new SoloChallengeStarted(
                branch.Timeline, Original, branch.Record.OperationKey, branch.Record.Seed, branch.Record.MascotId,
                branch.Record.RulesVersion, branch.Record.LayoutVersion, WorldFrame.Of(living), start));
        }
        finally
        {
            lock (_gate)
            {
                _starting = false;
            }
        }
    }

    /// <summary>A start that did not begin gameplay: the challenge stays Ready for a retry, unless it was cancelled.</summary>
    private Result<SoloChallengeStarted, SoloError> Refused(SoloError error)
    {
        if (error.Code == SoloErrorCode.Cancelled)
        {
            Cancel();
        }

        return SoloResult.Fail<SoloChallengeStarted>(error);
    }

    private Result<SoloChallengeStarted, SoloError> Fail(SoloError error)
    {
        Terminate(SoloChallengePhase.Failed, error);
        return SoloResult.Fail<SoloChallengeStarted>(error);
    }

    /// <summary>Admits a client input for the living player (slot 0) with the unchanged <c>ghost-v1</c> admission policy.</summary>
    public Result<GameInput, InputRejection> Submit(SubmittedInput submitted)
    {
        var branch = RunningBranch();
        return branch is null ? Result<GameInput, InputRejection>.Failure(InputRejection.RoundOver) : branch.Submit(submitted);
    }

    /// <summary>The server-generated neutral input for the living player (disconnect/blur).</summary>
    public Result<GameInput, InputRejection> Neutralize(byte slot)
    {
        var branch = RunningBranch();
        return branch is null ? Result<GameInput, InputRejection>.Failure(InputRejection.RoundOver) : branch.Neutralize(slot);
    }

    private SoloBranch? RunningBranch()
    {
        lock (_gate)
        {
            return _phase == SoloChallengePhase.Running ? _branch : null;
        }
    }

    /// <summary>
    /// Commits the next living tick (the unchanged recorder) and pairs it with the source ghost frame at the same tick by direct
    /// array indexing. No restore, trace, inspect or compare call is made here. A failed commit keeps the frozen step for a retry.
    /// </summary>
    public async Task<Result<SoloChallengeTick, SoloError>> CommitTickAsync(CancellationToken ct)
    {
        SoloBranch branch;
        PreparedGhostTrace trace;
        lock (_gate)
        {
            if (_phase != SoloChallengePhase.Running || _branch is null || _trace is null)
            {
                return SoloResult.Fail<SoloChallengeTick>(SoloErrorCode.Conflict, $"The challenge is {_phase}; it is not running.");
            }

            (branch, trace) = (_branch, _trace);
        }

        var committed = await branch.CommitTickAsync(ct).ConfigureAwait(false);
        if (!committed.TryGetValue(out var receipt))
        {
            return SoloResult.Fail<SoloChallengeTick>(committed.Error);
        }

        var ghost = trace.At(receipt.Tick);
        var living = WorldFrame.Of(receipt.State);
        return Phase == SoloChallengePhase.Running
            ? SoloResult.Ok(new SoloChallengeTick(receipt.Tick, living, ghost, checked(living.TotalScore - ghost.TotalScore),
                receipt.StateHash, receipt.RecordHash, receipt.State.IsFinal))
            : SoloResult.Fail<SoloChallengeTick>(SoloErrorCode.Cancelled, "The challenge was cancelled.");
    }

    /// <summary>
    /// After all 2,700 ticks: completes and replay-verifies the child, requires its stored row to be a Complete
    /// <see cref="SoloBranchKind.Challenge"/> with the same hashes, and re-verifies that the source original still reproduces its
    /// recorded final hash. Idempotent. A transient failure leaves the challenge Running so the call can be repeated.
    /// </summary>
    public async Task<Result<SoloChallengeResult, SoloError>> CompleteAsync(CancellationToken ct)
    {
        SoloBranch branch;
        PreparedGhostTrace trace;
        lock (_gate)
        {
            if (_phase == SoloChallengePhase.Completed && _result is not null)
            {
                return SoloResult.Ok(_result);
            }

            if (_phase != SoloChallengePhase.Running || _branch is null || _trace is null)
            {
                return SoloResult.Fail<SoloChallengeResult>(SoloErrorCode.Conflict, $"The challenge is {_phase}; it is not running.");
            }

            (branch, trace) = (_branch, _trace);
        }

        if (!branch.IsRoundOver)
        {
            return SoloResult.Fail<SoloChallengeResult>(SoloErrorCode.NotComplete,
                $"Only {branch.State.Tick} of {GameRulesV1.RoundTicks} ticks are committed.");
        }

        var completed = await branch.CompleteAsync(ct).ConfigureAwait(false);
        if (!completed.TryGetValue(out var child))
        {
            return Rejected<SoloChallengeResult>(completed.Error);
        }

        var stored = await _services.Store.FindBranchAsync(child.Timeline, ct).ConfigureAwait(false);
        if (!stored.TryGetValue(out var row))
        {
            return Rejected<SoloChallengeResult>(stored.Error);
        }

        if (row.Status != SoloOriginalStatus.Complete || row.Source != Original || row.ForkTick != 0
            || SoloBranchKinds.Of(row) != SoloBranchKind.Challenge
            || row.FinalStateHash != child.FinalStateHash || row.LastRecordHash != child.LastRecordHash)
        {
            return Rejected<SoloChallengeResult>(new SoloError(SoloErrorCode.VerificationFailed,
                "The stored child is not the verified Challenge that was played."));
        }

        var source = await _services.Originals.OpenAsync(_actor, Original, ct).ConfigureAwait(false);
        if (!source.TryGetValue(out var original))
        {
            return Rejected<SoloChallengeResult>(source.Error);
        }

        if (original.FinalStateHash != trace.FinalStateHash)
        {
            return Rejected<SoloChallengeResult>(new SoloError(SoloErrorCode.VerificationFailed,
                "The original no longer reproduces the hash the ghost was prepared from."));
        }

        var result = SoloChallenges.Build(row, child.FinalState, original);
        lock (_gate)
        {
            _result = result;
            _phase = SoloChallengePhase.Completed;
            Release();
        }

        return SoloResult.Ok(result);
    }

    private Result<T, SoloError> Rejected<T>(SoloError error)
    {
        // Transient: the same call can be repeated. Anything else is a verified failure and ends the challenge truthfully.
        if (error.Code is not (SoloErrorCode.Unavailable or SoloErrorCode.Cancelled))
        {
            Terminate(SoloChallengePhase.Failed, error);
        }

        return SoloResult.Fail<T>(error);
    }

    /// <summary>
    /// Cancels preparation or play and releases the trace. A cancelled run is never a result: its child row stays an unfinished
    /// recording and is never an original. Idempotent; no effect after Completed.
    /// </summary>
    public void Cancel() => Terminate(SoloChallengePhase.Cancelled, null);

    private void Terminate(SoloChallengePhase phase, SoloError? error)
    {
        lock (_gate)
        {
            if (_phase is SoloChallengePhase.Completed or SoloChallengePhase.Failed or SoloChallengePhase.Cancelled)
            {
                return;
            }

            _phase = phase;
            _error ??= error;
            Release();
        }

        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>Drops the in-memory trace and its cache lease. Caller holds the gate.</summary>
    private void Release()
    {
        _lease?.Dispose();
        _lease = null;
        _trace = null;
    }
}
