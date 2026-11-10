using GhostLap.Rules;
using GhostLap.Simulation;
using GhostLap.Timeline;
using Marrow.Kernel.Results;
using Marrow.Timeline;

namespace GhostLap.Solo;

/// <summary>
/// The server-side tick loop over one Recording Marrow Timeline history (a solo original or a branch of one; both are
/// continued with the same <see cref="ITimelineEngine{TState,TInput,TContext}.AppendAsync"/>). Inputs are admitted with
/// the <c>ghost-v1</c> policy; <see cref="CommitTickAsync"/> appends exactly the admitted inputs of the next tick as one
/// step (empty on a tick with no input) and only advances <see cref="State"/> after the commit succeeded. A failed commit
/// freezes that tick's step so a retry sends the identical payload under the same operation key (idempotent).
/// <para>Driven by one tick loop: commit/complete calls must not overlap; <see cref="Submit"/> may run concurrently.</para>
/// </summary>
internal sealed class TickRecorder
{
    private readonly ITimelineEngine<GameState, GameInput, GameContext> _engine;
    private readonly string _keyPrefix;
    private readonly Lock _gate = new();
    private readonly List<GameInput> _pending = [];
    private StepRecord<GameInput>? _frozen;
    private GameState _state;
    private SoloTickReceipt? _lastReceipt;
    private int _busy;

    /// <param name="keyPrefix">Prefix of the per-tick append operation keys; tick keys are <c>{prefix}/tick/{n}</c>.</param>
    public TickRecorder(
        ITimelineEngine<GameState, GameInput, GameContext> engine, TimelineId timeline, string keyPrefix,
        GameContext context, GameState state)
    {
        _engine = engine;
        _keyPrefix = keyPrefix;
        Timeline = timeline;
        Context = context;
        _state = state;
    }

    public TimelineId Timeline { get; }

    public GameContext Context { get; }

    public GameState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public Result<GameInput, InputRejection> Submit(SubmittedInput submitted)
    {
        ArgumentNullException.ThrowIfNull(submitted);
        lock (_gate)
        {
            if (_frozen is not null)
            {
                return Result<GameInput, InputRejection>.Failure(InputRejection.CommitPending);
            }

            var admitted = InputAdmission.Admit(_state, _pending, submitted);
            if (admitted.TryGetValue(out var input))
            {
                _pending.Add(input);
            }

            return admitted;
        }
    }

    public Result<GameInput, InputRejection> Neutralize(byte slot)
    {
        lock (_gate)
        {
            if (_frozen is not null)
            {
                return Result<GameInput, InputRejection>.Failure(InputRejection.CommitPending);
            }

            if (_state.IsFinal)
            {
                return Result<GameInput, InputRejection>.Failure(InputRejection.RoundOver);
            }

            if (slot >= _state.Players.Length)
            {
                return Result<GameInput, InputRejection>.Failure(InputRejection.UnknownSlot);
            }

            var input = InputAdmission.Neutralize(_state, _pending, slot);
            _pending.Add(input);
            return Result<GameInput, InputRejection>.Success(input);
        }
    }

    public async Task<Result<SoloTickReceipt, SoloError>> CommitTickAsync(CancellationToken ct)
    {
        Enter();
        try
        {
            StepRecord<GameInput> step;
            GameState before;
            lock (_gate)
            {
                before = _state;
                if (before.IsFinal)
                {
                    return SoloResult.Fail<SoloTickReceipt>(SoloErrorCode.Conflict, $"The round already ended at tick {before.Tick}.");
                }

                _frozen ??= new StepRecord<GameInput>(before.Tick + 1, [.. _pending.Select(ToOrdered)]);
                step = _frozen;
            }

            // The same pure reducer the engine applies; the engine's own result is what the step hash commits to.
            var after = GhostLapTransition.Instance.Apply(before, step.Position, step.Inputs, Context);
            var appended = await SoloResult.GuardAsync(async () =>
            {
                var r = await _engine.AppendAsync(Timeline, before.Tick, step, TickKey(step.Position), ct).ConfigureAwait(false);
                return r.TryGetValue(out var receipt) ? SoloResult.Ok(receipt) : SoloResult.Fail<AppendReceipt>(r.Error);
            }, ct).ConfigureAwait(false);
            if (!appended.TryGetValue(out var committed))
            {
                return SoloResult.Fail<SoloTickReceipt>(appended.Error);
            }

            if (committed.Position != step.Position)
            {
                return SoloResult.Fail<SoloTickReceipt>(SoloErrorCode.VerificationFailed,
                    $"Timeline committed position {committed.Position} for tick {step.Position}.");
            }

            var tick = new SoloTickReceipt(step.Position, committed.StateHash, committed.RecordHash, after);
            lock (_gate)
            {
                _state = after;
                _pending.Clear();
                _frozen = null;
                _lastReceipt = tick;
            }

            return SoloResult.Ok(tick);
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>
    /// Completes the Timeline history (contiguous chain and terminal hash check), replays it from the database and requires
    /// the replay to reproduce the completion hashes and the live final state byte for byte; only then calls
    /// <paramref name="markComplete"/> (the Ghost Lap row transition). Before the final tick this is NotComplete.
    /// </summary>
    public async Task<Result<T, SoloError>> CompleteAsync<T>(
        Func<CompletionReceipt, GameState, CancellationToken, Task<Result<T, SoloError>>> markComplete, CancellationToken ct)
    {
        Enter();
        try
        {
            var final = State;
            if (!final.IsFinal)
            {
                return SoloResult.Fail<T>(SoloErrorCode.NotComplete,
                    $"Only {final.Tick} of {GameRulesV1.RoundTicks} ticks are committed; the run is not complete.");
            }

            return await SoloResult.GuardAsync(async () =>
            {
                var completed = await _engine.CompleteAsync(Timeline, final.Tick, ct).ConfigureAwait(false);
                if (!completed.TryGetValue(out var receipt))
                {
                    return SoloResult.Fail<T>(completed.Error);
                }

                var replayed = await _engine.ReplayAsync(Timeline, ct).ConfigureAwait(false);
                if (!replayed.TryGetValue(out var replay))
                {
                    return SoloResult.Fail<T>(replayed.Error);
                }

                var live = GameStateCodec.Instance.EncodeCanonical(final);
                var lastTick = _lastReceipt;
                if (replay.Status != HistoryStatus.Verified || receipt.Status != HistoryStatus.Verified
                    || replay.FinalPosition != final.Tick || receipt.FinalPosition != final.Tick
                    || replay.FinalStateHash != receipt.FinalStateHash || replay.LastRecordHash != receipt.LastRecordHash
                    || (lastTick is not null && (lastTick.StateHash != receipt.FinalStateHash || lastTick.RecordHash != receipt.LastRecordHash))
                    || !GameStateCodec.Instance.EncodeCanonical(replay.FinalState).AsSpan().SequenceEqual(live))
                {
                    return SoloResult.Fail<T>(SoloErrorCode.VerificationFailed,
                        "The persisted history does not reproduce the recorded run; it was not marked complete.");
                }

                return await markComplete(receipt, replay.FinalState, ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            Exit();
        }
    }

    private string TickKey(long tick) => $"{_keyPrefix}/tick/{tick}";

    /// <summary>Input ids are unique per history because sequences strictly increase per slot.</summary>
    private static OrderedInput<GameInput> ToOrdered(GameInput g) => new($"t{g.Tick}-s{g.Slot}-q{g.Sequence}", g.Slot, g);

    private void Enter()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            throw new InvalidOperationException("A commit or completion of this recording is already running.");
        }
    }

    private void Exit() => Volatile.Write(ref _busy, 0);
}
