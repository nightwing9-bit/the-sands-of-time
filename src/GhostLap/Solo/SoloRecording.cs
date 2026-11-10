using GhostLap.Simulation;
using Marrow.Kernel.Results;
using Marrow.Timeline;

namespace GhostLap.Solo;

/// <summary>One committed tick: the Timeline receipt hashes and the authoritative state after the tick.</summary>
public sealed record SoloTickReceipt(long Tick, string StateHash, string RecordHash, GameState State);

/// <summary>
/// A complete, verified solo original: 2700 committed ticks whose Timeline replay reproduced the recorded hashes.
/// <see cref="FinalState"/> carries the score breakdown (<see cref="GameState.FinalResult"/>).
/// </summary>
public sealed record SoloOriginal(
    TimelineId Timeline,
    long Seed,
    byte MascotId,
    long FinalTick,
    string FinalStateHash,
    string LastRecordHash,
    GameState FinalState)
{
    public GameResult Result => FinalState.FinalResult
        ?? throw new InvalidOperationException("A solo original always ends on the final tick.");
}

/// <summary>
/// The live server-side recorder of one solo run. Inputs are admitted with the <c>ghost-v1</c> policy
/// (<see cref="InputAdmission"/>); <see cref="CommitTickAsync"/> appends exactly the admitted inputs of the next tick as
/// one Marrow Timeline step (an empty list on a tick with no input), and only after the commit succeeds does
/// <see cref="State"/> advance. The effective input (hold, neutral expiry, Burst edge), charge, score and candles of
/// every tick are part of the state the engine hashes for that step.
/// <para>Failure is visible and retryable: a failed commit returns a typed error, keeps the last committed state, and
/// freezes that tick's step so a retry sends the identical payload under the same operation key (idempotent). The
/// original exists only after <see cref="CompleteAsync"/> completed and replay-verified all 2700 ticks.</para>
/// <para>Driven by one tick loop: commit/complete calls must not overlap; <see cref="Submit"/> may run concurrently.</para>
/// </summary>
public sealed class SoloRecording
{
    private readonly SoloOriginalStore _store;
    private readonly TickRecorder _recorder;

    internal SoloRecording(
        ITimelineEngine<GameState, GameInput, GameContext> engine, SoloOriginalStore store, SoloOriginalRecord record,
        GameContext context, GameState state)
    {
        _store = store;
        _recorder = new TickRecorder(engine, record.Timeline, SourceKey(record.OperationKey), context, state);
    }

    public TimelineId Timeline => _recorder.Timeline;

    public GameContext Context => _recorder.Context;

    /// <summary>The last committed authoritative state.</summary>
    public GameState State => _recorder.State;

    public bool IsRoundOver => State.IsFinal;

    /// <summary>Admits a client input for the next tick, or returns why it was rejected.</summary>
    public Result<GameInput, InputRejection> Submit(SubmittedInput submitted) => _recorder.Submit(submitted);

    /// <summary>Disconnect/blur: the server-generated neutral input for the next tick.</summary>
    public Result<GameInput, InputRejection> Neutralize(byte slot) => _recorder.Neutralize(slot);

    /// <summary>
    /// Commits the next tick (with whatever inputs were admitted, possibly none) and advances <see cref="State"/> only on
    /// success. After a failure, call again to retry the same frozen step.
    /// </summary>
    public Task<Result<SoloTickReceipt, SoloError>> CommitTickAsync(CancellationToken ct) => _recorder.CommitTickAsync(ct);

    /// <summary>
    /// Completes the Timeline history (contiguous chain and terminal hash check), replays it from the database and
    /// requires the replay to reproduce the completion hashes and the live final state byte for byte, and only then marks
    /// the Ghost Lap row Complete. Before the final tick this is <see cref="SoloErrorCode.NotComplete"/>. Idempotent.
    /// </summary>
    public Task<Result<SoloOriginal, SoloError>> CompleteAsync(CancellationToken ct) =>
        _recorder.CompleteAsync(async (receipt, final, token) =>
        {
            var marked = await _store.MarkCompleteAsync(Timeline, final.Tick, receipt.FinalStateHash, receipt.LastRecordHash, token)
                .ConfigureAwait(false);
            return marked.TryGetValue(out var row)
                ? SoloResult.Ok(new SoloOriginal(Timeline, row.Seed, row.MascotId, final.Tick, receipt.FinalStateHash,
                    receipt.LastRecordHash, final))
                : SoloResult.Fail<SoloOriginal>(marked.Error);
        }, ct);

    internal static string SourceKey(string operationKey) => $"ghost-lap/solo/{operationKey}";
}
