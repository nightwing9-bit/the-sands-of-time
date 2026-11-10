using Marrow.Timeline;
using Marrow.Timeline.Internal;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Marrow.Infrastructure.Timeline;

/// <summary>
/// Host composition for the shared Timeline engine. A domain adapter supplies its pinned version
/// registrations (reducer + state/input/context codecs per <see cref="TimelineVersionKey"/>); the host
/// gets one <see cref="ITimelineEngine{TState,TInput,TContext}"/> per domain shape, all backed by the same
/// engine implementation and the same <c>marrow_timeline</c> Postgres store. Requires an
/// <see cref="NpgsqlDataSource"/> registration (for example from <c>AddMarrowDependencies</c>) whose login
/// is a member of <c>marrow_timeline_runtime</c>. Never runs migrations.
/// </summary>
public static class TimelineServiceCollectionExtensions
{
    /// <param name="registrations">Every historical and current version the domain must still restore. Duplicate keys throw.</param>
    /// <param name="checkpointInterval">
    /// Complete checkpoint spacing in positions (position 0 always has one). Each domain chooses its own;
    /// there is no universal tick assumption. Must be at least 1.
    /// </param>
    public static IServiceCollection AddMarrowTimeline<TState, TInput, TContext>(
        this IServiceCollection services,
        IEnumerable<TimelineComponents<TState, TInput, TContext>> registrations,
        int checkpointInterval)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentOutOfRangeException.ThrowIfLessThan(checkpointInterval, 1);
        if (services.Any(d => d.ServiceType == typeof(ITimelineEngine<TState, TInput, TContext>)))
        {
            throw new InvalidOperationException(
                $"A Timeline engine for ({typeof(TState).Name}, {typeof(TInput).Name}, {typeof(TContext).Name}) is already registered.");
        }

        // Built eagerly so duplicate version keys fail at composition time, not on first request.
        var registry = new TimelineVersionRegistry<TState, TInput, TContext>(registrations);
        var options = new TimelineEngineOptions { CheckpointInterval = checkpointInterval };

        services.AddSingleton<ITimelineEngine<TState, TInput, TContext>>(sp =>
            new PersistentTimelineEngine<TState, TInput, TContext>(
                new PostgresTimelineStore(sp.GetRequiredService<NpgsqlDataSource>()), registry, options));
        return services;
    }
}
