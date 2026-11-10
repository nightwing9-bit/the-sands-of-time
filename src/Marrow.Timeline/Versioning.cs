using Marrow.Kernel.Results;

namespace Marrow.Timeline;

/// <summary>Pinned version identity of one history segment. Compared ordinally and case-sensitively.</summary>
public sealed record TimelineVersionKey(
    string DomainId,
    string ReducerVersion,
    string InputSchemaVersion,
    string StateSchemaVersion,
    string ContextSchemaVersion)
{
    public string DomainId { get; init; } = Require(DomainId, nameof(DomainId));
    public string ReducerVersion { get; init; } = Require(ReducerVersion, nameof(ReducerVersion));
    public string InputSchemaVersion { get; init; } = Require(InputSchemaVersion, nameof(InputSchemaVersion));
    public string StateSchemaVersion { get; init; } = Require(StateSchemaVersion, nameof(StateSchemaVersion));
    public string ContextSchemaVersion { get; init; } = Require(ContextSchemaVersion, nameof(ContextSchemaVersion));

    private static string Require(string value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException($"{name} must be non-blank.", name) : value;
}

/// <summary>The reducer and codecs registered for exactly one <see cref="TimelineVersionKey"/>.</summary>
public sealed record TimelineComponents<TState, TInput, TContext>(
    TimelineVersionKey Versions,
    IDeterministicTransition<TState, TInput, TContext> Transition,
    IStateCodec<TState> StateCodec,
    IInputCodec<TInput> InputCodec,
    IContextCodec<TContext> ContextCodec);

/// <summary>
/// Selects the historically compatible reducer/codecs for a pinned version key. A missing historical
/// implementation fails closed with <see cref="TimelineErrorCode.IncompatibleVersion"/>; there is no fallback to "current".
/// </summary>
public interface ITimelineVersionResolver<TState, TInput, TContext>
{
    Result<TimelineComponents<TState, TInput, TContext>, TimelineError> Resolve(TimelineVersionKey versions);
}

/// <summary>Immutable exact-match registry. Duplicate keys are rejected at construction.</summary>
public sealed class TimelineVersionRegistry<TState, TInput, TContext> : ITimelineVersionResolver<TState, TInput, TContext>
{
    private readonly Dictionary<TimelineVersionKey, TimelineComponents<TState, TInput, TContext>> _byKey = new();

    public TimelineVersionRegistry(IEnumerable<TimelineComponents<TState, TInput, TContext>> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        foreach (var registration in registrations)
        {
            ArgumentNullException.ThrowIfNull(registration);
            if (!_byKey.TryAdd(registration.Versions, registration))
            {
                throw new ArgumentException($"Duplicate Timeline version registration: {registration.Versions}.", nameof(registrations));
            }
        }
    }

    public Result<TimelineComponents<TState, TInput, TContext>, TimelineError> Resolve(TimelineVersionKey versions)
    {
        ArgumentNullException.ThrowIfNull(versions);
        return _byKey.TryGetValue(versions, out var components)
            ? TimelineResult.Ok(components)
            : TimelineResult.Fail<TimelineComponents<TState, TInput, TContext>>(
                TimelineErrorCode.IncompatibleVersion,
                $"No registered reducer/codec for {versions}.");
    }
}
