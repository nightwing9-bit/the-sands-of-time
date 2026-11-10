using Marrow.Timeline;
using Revenant.Contracts;
using Revenant.Finance;
using Revenant.Scenario;

namespace Revenant.Timeline;

/// <summary>
/// The Timeline reducer for Revenant: the pure <see cref="FinanceReducer"/>, applied to one step's exogenous
/// intents in the engine's canonical <c>(Ordinal, InputId)</c> order under the step segment's pinned context.
/// Each journal <c>InputId</c> must be the intent's run-scoped key. A reducer rejection throws
/// <see cref="FinanceTransitionException"/>; <see cref="FinanceTimelineAdapter"/> pre-validates every step with the
/// same reducer before appending, so a rejected step is never offered to the engine.
/// </summary>
public sealed class FinanceTransition : IDeterministicTransition<FinanceProjectionState, PaymentIntent, FinanceContext>
{
    public static FinanceTransition Instance { get; } = new();

    public FinanceProjectionState Apply(
        FinanceProjectionState previous, long position, IReadOnlyList<OrderedInput<PaymentIntent>> inputs, FinanceContext pinnedContext)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var state = previous;
        foreach (var input in inputs)
        {
            if (!string.Equals(input.InputId, input.Payload.RunScopedKey, StringComparison.Ordinal))
            {
                throw new FinanceTransitionException(new FinanceError(
                    FinanceErrorCode.RunMismatch,
                    $"Journal input id '{input.InputId}' is not the run-scoped key '{input.Payload.RunScopedKey}'."));
            }

            var next = FinanceReducer.Apply(state, input.Payload, pinnedContext);
            state = next.TryGetValue(out var value) ? value : throw new FinanceTransitionException(next.Error);
        }

        return state;
    }
}

/// <summary>A pure reducer rejection surfaced through the Timeline transition interface.</summary>
public sealed class FinanceTransitionException(FinanceError error)
    : InvalidOperationException($"Finance reducer rejected the step: {error.Code}: {error.Message}")
{
    public FinanceError Error { get; } = error;
}

/// <summary>
/// Pinned Timeline version identity of Revenant finance histories and the components registered for it.
/// Source (policy v7) and branch (policy v8) histories share this one key: the policy is part of each
/// segment's pinned <see cref="FinanceContext"/>, not a different engine or reducer.
/// </summary>
public static class FinanceTimelineVersions
{
    public const string DomainId = "revenant-finance";
    public const string StateSchemaVersion = "revenant-finance-state/1";
    public const string ContextSchemaVersion = "revenant-finance-context/1";

    /// <summary>Complete checkpoint spacing for finance histories (one exogenous intent per position).</summary>
    public const int CheckpointInterval = 16;

    public static TimelineVersionKey V1 { get; } = new(
        DomainId, FinanceReducer.Version, ScenarioSchemas.InputSchemaVersion, StateSchemaVersion, ContextSchemaVersion);

    public static TimelineComponents<FinanceProjectionState, PaymentIntent, FinanceContext> V1Components { get; } = new(
        V1, FinanceTransition.Instance, FinanceStateCodec.Instance, PaymentIntentCodec.Instance, FinanceContextCodec.Instance);

    /// <summary>Every version a stored Revenant finance history may pin. Keep old entries when adding new ones.</summary>
    public static IReadOnlyList<TimelineComponents<FinanceProjectionState, PaymentIntent, FinanceContext>> All { get; } =
        [V1Components];
}
