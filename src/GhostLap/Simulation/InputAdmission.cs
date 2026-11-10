using GhostLap.Rules;
using Marrow.Kernel.Results;

namespace GhostLap.Simulation;

/// <summary>
/// An untrusted input as a client submitted it. <see cref="ClientTick"/> is the tick the client aimed at; the server
/// decides the tick it applies to. There is no position field: a client cannot claim where it is.
/// </summary>
public sealed record SubmittedInput(byte Slot, int Sequence, long ClientTick, sbyte MoveX, sbyte MoveY, bool RequestBurst);

public enum InputRejection
{
    RoundOver,
    UnknownSlot,
    InvalidVector,
    /// <summary>The sequence is not newer than the last accepted or already-pending sequence of the slot.</summary>
    StaleSequence,
    /// <summary>The client tick is more than <see cref="GameRulesV1.InputMaxLateTicks"/> behind the next server tick.</summary>
    LateTick,
    /// <summary>The client tick is more than <see cref="GameRulesV1.InputMaxLeadTicks"/> ahead of the next server tick.</summary>
    FutureTick,
    /// <summary>
    /// A commit of the next tick was attempted and has not succeeded yet, so that tick's inputs are frozen until the retry
    /// commits them (task 6.3). The client resubmits for the following tick.
    /// </summary>
    CommitPending,
}

/// <summary>
/// The <c>ghost-v1</c> transport acceptance policy, pure and server-side. A host keeps the admitted inputs of the next
/// tick and appends exactly those as the Timeline step, so replay sees the same inputs the live tick did; the reducer then
/// derives the effective input (hold, neutral expiry, Burst edge) from canonical state. Rejections are typed, never thrown.
/// </summary>
public static class InputAdmission
{
    /// <param name="current">The authoritative state; the input is admitted for tick <c>current.Tick + 1</c>.</param>
    /// <param name="pendingForNextTick">Inputs already admitted for that tick (any slot).</param>
    public static Result<GameInput, InputRejection> Admit(
        GameState current, IReadOnlyList<GameInput> pendingForNextTick, SubmittedInput submitted)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(pendingForNextTick);
        ArgumentNullException.ThrowIfNull(submitted);
        var next = current.Tick + 1;
        if (next > GameRulesV1.RoundTicks)
        {
            return Result<GameInput, InputRejection>.Failure(InputRejection.RoundOver);
        }
        if (submitted.Slot >= current.Players.Length)
        {
            return Result<GameInput, InputRejection>.Failure(InputRejection.UnknownSlot);
        }
        if (submitted.MoveX is < -1 or > 1 || submitted.MoveY is < -1 or > 1)
        {
            return Result<GameInput, InputRejection>.Failure(InputRejection.InvalidVector);
        }
        if (submitted.Sequence <= SequenceFloor(current, pendingForNextTick, submitted.Slot))
        {
            return Result<GameInput, InputRejection>.Failure(InputRejection.StaleSequence);
        }
        if (submitted.ClientTick < next - GameRulesV1.InputMaxLateTicks)
        {
            return Result<GameInput, InputRejection>.Failure(InputRejection.LateTick);
        }
        if (submitted.ClientTick > next + GameRulesV1.InputMaxLeadTicks)
        {
            return Result<GameInput, InputRejection>.Failure(InputRejection.FutureTick);
        }
        return Result<GameInput, InputRejection>.Success(new GameInput(
            next, submitted.Slot, submitted.Sequence, submitted.MoveX, submitted.MoveY, submitted.RequestBurst));
    }

    /// <summary>
    /// The server-generated input for a disconnect or blur: neutral movement and Burst released, effective on the next tick
    /// (no six-tick hold). It takes the next sequence, so a client must resume from the state's <see cref="PlayerState.LastSequence"/>.
    /// </summary>
    public static GameInput Neutralize(GameState current, IReadOnlyList<GameInput> pendingForNextTick, byte slot)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(pendingForNextTick);
        if (slot >= current.Players.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(slot), slot, "Unknown slot.");
        }
        if (current.Tick >= GameRulesV1.RoundTicks)
        {
            throw new InvalidOperationException("The round is over.");
        }
        return GameInput.Neutral(current.Tick + 1, slot, checked(SequenceFloor(current, pendingForNextTick, slot) + 1));
    }

    private static int SequenceFloor(GameState current, IReadOnlyList<GameInput> pending, byte slot)
    {
        var floor = current.Players[slot].LastSequence;
        foreach (var p in pending)
        {
            if (p.Slot == slot && p.Sequence > floor)
            {
                floor = p.Sequence;
            }
        }
        return floor;
    }
}
