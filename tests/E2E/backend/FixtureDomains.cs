using System.Globalization;
using Marrow.Infrastructure.Timeline;
using Marrow.Kernel.Results;
using Marrow.Tests.TestDomains;
using Marrow.Timeline;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
namespace Sands.E2E.Backend;
/// <summary>How the seeded history is prepared for one browser test. Every scenario uses the real engine and real storage.</summary>
public enum FixtureScenario
{
    /// <summary>Verified source under the version the host can restore.</summary>
    Standard,
    /// <summary>Verified source pinned to a reducer version the host has no registration for: Restore/Replay give IncompatibleVersion.</summary>
    Incompatible,
    /// <summary>Verified source whose stored checkpoint at position <see cref="FixtureDomains.TamperedCheckpoint"/> was altered in PostgreSQL.</summary>
    CorruptCheckpoint,
}
public sealed record MarkerDto(string Position, string Label);
public sealed record DescribeFacts(long LastPosition, HistoryStatus Status);
public sealed record PreviewFacts(long Position, object State, string StateHash, StateHashOrigin HashOrigin, HistoryStatus Status);
/// <summary>One synthetic domain shape the fixture host serves. Hides the engine's generic type arguments from the routes.</summary>
public interface IFixtureDomain
{
    /// <summary>The <c>DomainId</c> pinned in every history of this shape.</summary>
    string DomainId { get; }
    /// <summary>Name the browser test uses to ask for this shape: <c>revenant</c> (event-shaped) or <c>ghost-lap</c> (tick-shaped).</summary>
    string Profile { get; }
    /// <summary>Number of steps in a seeded source, so the last position is <see cref="SeedSteps"/>.</summary>
    long SeedSteps { get; }
    IReadOnlyList<MarkerDto> Markers(FixtureScenario scenario);
    Task<Result<TimelineId, TimelineError>> SeedAsync(string ownerId, FixtureScenario scenario, CancellationToken ct);
    Task<Result<DescribeFacts, TimelineError>> DescribeAsync(TimelineId id, CancellationToken ct);
    Task<Result<PreviewFacts, TimelineError>> RestoreAsync(TimelineId id, long position, CancellationToken ct);
    Task<Result<TimelineId, TimelineError>> ForkAsync(TimelineId id, long position, string operationKey, CancellationToken ct);
}
/// <summary>Everything that differs between two fixture shapes; the engine calls are shared.</summary>
public sealed record FixtureSpec<TState, TInput, TContext>(
    string Profile,
    TimelineVersionKey Current,
    TimelineVersionKey Retired,
    Func<TimelineVersionKey, TimelineComponents<TState, TInput, TContext>> Components,
    TState Initial,
    TContext SourceContext,
    TContext BranchContext,
    int Steps,
    Func<long, StepRecord<TInput>> Step,
    Func<TState, string, object> Project,
    IReadOnlyList<MarkerDto> Markers);
public sealed class FixtureDomain<TState, TInput, TContext>(
    FixtureSpec<TState, TInput, TContext> spec,
    ITimelineEngine<TState, TInput, TContext> serving,
    ITimelineEngine<TState, TInput, TContext> seeding) : IFixtureDomain
{
    public string DomainId => spec.Current.DomainId;
    public string Profile => spec.Profile;
    public long SeedSteps => spec.Steps;
    public IReadOnlyList<MarkerDto> Markers(FixtureScenario scenario) => scenario == FixtureScenario.CorruptCheckpoint
        ? [.. spec.Markers, new MarkerDto(FixtureDomains.TamperedCheckpoint.ToString(CultureInfo.InvariantCulture), "Altered checkpoint")]
        : spec.Markers;
    public async Task<Result<TimelineId, TimelineError>> SeedAsync(string ownerId, FixtureScenario scenario, CancellationToken ct)
    {
        var key = scenario == FixtureScenario.Incompatible ? spec.Retired : spec.Current;
        var operation = Guid.NewGuid().ToString("N");
        var create = await seeding.CreateSourceAsync(
            new CreateSourceRequest<TState, TContext>(ownerId, key, spec.Initial, spec.SourceContext, "e2e-rules-1", "e2e-seed-1"),
            $"{operation}:create", ct);
        if (!create.TryGetValue(out var id))
        {
            return create;
        }
        for (var position = 1L; position <= spec.Steps; position++)
        {
            var appended = await seeding.AppendAsync(id, position - 1, spec.Step(position), $"{operation}:{position}", ct);
            if (appended.TryGetError(out var appendError))
            {
                return TimelineResult.Fail<TimelineId>(appendError);
            }
        }
        var completed = await seeding.CompleteAsync(id, spec.Steps, ct);
        return completed.TryGetError(out var completeError) ? TimelineResult.Fail<TimelineId>(completeError) : TimelineResult.Ok(id);
    }
    public async Task<Result<DescribeFacts, TimelineError>> DescribeAsync(TimelineId id, CancellationToken ct)
    {
        // Full verified replay: the bounds shown to the browser are the last position of a history whose whole chain verified.
        var replay = await serving.ReplayAsync(id, ct);
        return replay.TryGetValue(out var r)
            ? TimelineResult.Ok(new DescribeFacts(r.FinalPosition, r.Status))
            : TimelineResult.Fail<DescribeFacts>(replay.Error);
    }
    public async Task<Result<PreviewFacts, TimelineError>> RestoreAsync(TimelineId id, long position, CancellationToken ct)
    {
        var restored = await serving.RestoreAsync(id, position, ct);
        return restored.TryGetValue(out var v)
            ? TimelineResult.Ok(new PreviewFacts(v.Position, spec.Project(v.State, v.StateHash), v.StateHash, v.HashOrigin, v.HistoryStatus))
            : TimelineResult.Fail<PreviewFacts>(restored.Error);
    }
    public Task<Result<TimelineId, TimelineError>> ForkAsync(TimelineId id, long position, string operationKey, CancellationToken ct) =>
        serving.ForkAsync(id, position, operationKey, spec.BranchContext, ct);
}
/// <summary>
/// The two synthetic fixture shapes and their pinned versions. The reducers and codecs are the Spec 02 test domains
/// (<c>tests/Marrow/TestDomains</c>, linked into this project); they are not finance or game rules.
/// </summary>
public sealed class FixtureDomains
{
    /// <summary>Complete checkpoints every 4 positions (plus position 0).</summary>
    public const int CheckpointInterval = 4;
    /// <summary>The stored checkpoint the <see cref="FixtureScenario.CorruptCheckpoint"/> scenario alters.</summary>
    public const long TamperedCheckpoint = 8;
    public static readonly TimelineVersionKey RevenantKey = new("e2e-revenant-events", "counter-reducer-1", "counter-input-1", "counter-state-1", "counter-context-1");
    public static readonly TimelineVersionKey RevenantRetiredKey = RevenantKey with { ReducerVersion = "counter-reducer-0" };
    public static readonly TimelineVersionKey GhostLapKey = new("e2e-ghost-lap-ticks", "tick-reducer-7", "tick-input-1", "tick-state-1", "tick-context-7");
    public static readonly TimelineVersionKey GhostLapRetiredKey = GhostLapKey with { ReducerVersion = "tick-reducer-6" };
    private static readonly long BaseTicks = new DateTime(2026, 3, 2, 9, 0, 0, DateTimeKind.Utc).Ticks;
    private readonly Dictionary<string, IFixtureDomain> _byDomainId;
    private readonly Dictionary<string, IFixtureDomain> _byProfile;
    public FixtureDomains(NpgsqlDataSource dataSource, IServiceProvider host)
    {
        var domains = new IFixtureDomain[]
        {
            Build(RevenantSpec(), dataSource, host),
            Build(GhostLapSpec(), dataSource, host),
        };
        _byDomainId = domains.ToDictionary(d => d.DomainId, StringComparer.Ordinal);
        _byProfile = domains.ToDictionary(d => d.Profile, StringComparer.Ordinal);
    }
    public IFixtureDomain? Find(string domainId) => _byDomainId.GetValueOrDefault(domainId);
    public IFixtureDomain? FindByProfile(string profile) => _byProfile.GetValueOrDefault(profile);
    /// <summary>
    /// The serving engines only know the current version of each shape, as a deployed host would. Seeding engines are built
    /// privately over the same pool with the retired version added, so a history can be recorded under it and later be refused.
    /// </summary>
    public static IServiceCollection AddFixtureEngines(IServiceCollection services)
    {
        services.AddMarrowTimeline([CounterComponents(RevenantKey)], CheckpointInterval);
        services.AddMarrowTimeline([TickComponents(GhostLapKey)], CheckpointInterval);
        return services;
    }
    private static IFixtureDomain Build<TState, TInput, TContext>(
        FixtureSpec<TState, TInput, TContext> spec, NpgsqlDataSource dataSource, IServiceProvider host)
    {
        var seedServices = new ServiceCollection()
            .AddSingleton(dataSource)
            .AddMarrowTimeline([spec.Components(spec.Current), spec.Components(spec.Retired)], CheckpointInterval)
            .BuildServiceProvider();
        return new FixtureDomain<TState, TInput, TContext>(
            spec,
            host.GetRequiredService<ITimelineEngine<TState, TInput, TContext>>(),
            seedServices.GetRequiredService<ITimelineEngine<TState, TInput, TContext>>());
    }
    private static TimelineComponents<CounterState, CounterEvent, CounterContext> CounterComponents(TimelineVersionKey key) =>
        new(key, new CounterReducer(), new CounterStateCodec(), new CounterEventCodec(), new CounterContextCodec());
    private static TimelineComponents<TickWorld, TickInput, TickContext> TickComponents(TimelineVersionKey key) =>
        new(key, new TickReducer(), new TickWorldCodec(), new TickInputCodec(), new TickContextCodec());
    /// <summary>Business time of the event that closes step <paramref name="position"/>: 2026-03-02T09:00Z + p*(p+7) minutes.</summary>
    public static DateTime BusinessTime(long position) =>
        new(BaseTicks + TimeSpan.FromMinutes(position * (position + 7)).Ticks, DateTimeKind.Utc);
    private static FixtureSpec<CounterState, CounterEvent, CounterContext> RevenantSpec() => new(
        "revenant",
        RevenantKey,
        RevenantRetiredKey,
        CounterComponents,
        new CounterState(0, 0, BaseTicks),
        new CounterContext(2),
        new CounterContext(3),
        Steps: 24,
        Step: position =>
        {
            // Every step ends with one event stamped BusinessTime(position); steps divisible by 4 carry an earlier event first.
            var inputs = new List<OrderedInput<CounterEvent>>();
            if (position % 4 == 0)
            {
                inputs.Add(new OrderedInput<CounterEvent>($"ev-{position:D4}-a", 0, new CounterEvent(BusinessTime(position).AddMinutes(-1).Ticks, (int)(100 + position))));
            }
            inputs.Add(new OrderedInput<CounterEvent>($"ev-{position:D4}-b", 1, new CounterEvent(BusinessTime(position).Ticks, (int)position)));
            return new StepRecord<CounterEvent>(position, inputs);
        },
        Project: (state, hash) => new
        {
            businessTime = new DateTime(state.LastBusinessTimeTicks, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            stateHash = hash,
            eventsApplied = state.EventsApplied,
            value = state.Value,
        },
        Markers:
        [
            new MarkerDto("5", "Payroll batch"),
            new MarkerDto("12", "Settlement cut-off"),
            new MarkerDto("20", "Month end"),
        ]);
    private static FixtureSpec<TickWorld, TickInput, TickContext> GhostLapSpec() => new(
        "ghost-lap",
        GhostLapKey,
        GhostLapRetiredKey,
        TickComponents,
        TickWorld.Initial(timer: 90),
        new TickContext(3),
        new TickContext(5),
        Steps: 60,
        Step: position =>
        {
            // Odd ticks burst, every third tick collects; ticks like 2 and 4 carry no inputs at all.
            var inputs = new List<OrderedInput<TickInput>>();
            if (position % 2 == 1)
            {
                inputs.Add(new OrderedInput<TickInput>($"t{position:D4}-b", 0, new TickInput(TickAction.Burst, "")));
            }
            if (position % 3 == 0)
            {
                inputs.Add(new OrderedInput<TickInput>($"t{position:D4}-c", 1, new TickInput(TickAction.Collect, $"item-{position % 4}")));
            }
            return new StepRecord<TickInput>(position, inputs);
        },
        Project: (state, hash) => new
        {
            tick = state.Tick,
            stateHash = hash,
            timer = state.Timer,
            cooldown = state.Cooldown,
            bursts = state.Bursts,
            collected = state.Collected.Count,
        },
        Markers:
        [
            new MarkerDto("30", "Candle"),
            new MarkerDto("45", "Burst window"),
        ]);
}
