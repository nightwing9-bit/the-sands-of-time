using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Marrow.Infrastructure.Identity;
using Marrow.Infrastructure.Identity.Policy;
using Marrow.Kernel.Security;
using Marrow.Timeline;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Marrow.Infrastructure.Timeline;
using Revenant.Contracts;
using Revenant.Finance;
using Revenant.Timeline;
namespace Sands.E2E.Backend;
/// <summary>Per-run secret that gates the fixture control routes (<c>/__e2e/provision|facts|log</c>).</summary>
public sealed record E2eFixtureOptions(string FixtureToken);
/// <summary>Superuser access to the fixture database. Deliberately a distinct type so it can never be resolved as the API's <see cref="NpgsqlDataSource"/>.</summary>
public sealed class E2eOwnerAccess(NpgsqlDataSource source)
{
    public NpgsqlDataSource Source { get; } = source;
}
/// <summary>Faults a browser test asked for when it provisioned a history. Held in memory for the life of the host.</summary>
public sealed class FixtureEntry(FixtureScenario scenario, IReadOnlyDictionary<long, int> slowRestoreMs, bool dropFirstForkResponse)
{
    private int _forkResponseDrops = dropFirstForkResponse ? 1 : 0;
    public FixtureScenario Scenario { get; } = scenario;
    public IReadOnlyDictionary<long, int> SlowRestoreMs { get; } = slowRestoreMs;
    /// <summary>True exactly once per provisioned history that asked for it.</summary>
    public bool TakeForkResponseDrop() => Interlocked.Exchange(ref _forkResponseDrops, 0) == 1;
}
public sealed class FixtureRegistry
{
    private readonly ConcurrentDictionary<Guid, FixtureEntry> _entries = new();
    public void Add(Guid rootId, FixtureEntry entry) => _entries[rootId] = entry;
    public FixtureEntry? Find(Guid rootId) => _entries.GetValueOrDefault(rootId);
}
public sealed record E2eLogEntry(long Seq, string Method, string Path, string Query, int? Status, string Outcome, long ElapsedMs);
/// <summary>Method, path, query, outcome and timing of Timeline requests. No headers, cookies or bodies, so nothing secret is kept.</summary>
public sealed class E2eRequestLog
{
    private const int Capacity = 4000;
    private readonly ConcurrentQueue<E2eLogEntry> _entries = new();
    private long _seq;
    public void Add(string method, string path, string query, int? status, string outcome, long elapsedMs)
    {
        _entries.Enqueue(new E2eLogEntry(Interlocked.Increment(ref _seq), method, path, query, status, outcome, elapsedMs));
        while (_entries.Count > Capacity && _entries.TryDequeue(out _))
        {
        }
    }
    public IReadOnlyList<E2eLogEntry> Query(string? pathContains) =>
        [.. _entries.Where(e => pathContains is null || e.Path.Contains(pathContains, StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.Seq)];
}
public sealed record ProvisionRequest(string? Domain, string? Scenario, Dictionary<string, int>? SlowRestoreMs, bool? DropFirstForkResponse);
public sealed record ForkBody(string? Position, string? OperationKey, JsonElement? Options);
public sealed record BoundsDto(string Start, string End);
public sealed record DescriptorDto(
    string SourceId, string? BranchId, BoundsDto Bounds, bool ReadOnly, IReadOnlyList<MarkerDto> Markers,
    string HistoryStatus, string? ParentId, string? ForkPosition);
/// <summary>
/// TEST-ONLY Timeline HTTP surface for the Spec 04 exit gate. It is mapped by <see cref="E2eFixtureStartupFilter"/> in this
/// assembly's host and nowhere else: <c>Hosts.Api</c> serves none of these routes (asserted by tests). It is not a Revenant or
/// Ghost Lap API; the Timeline operations behind it are the real <see cref="ITimelineEngine{TState,TInput,TContext}"/> over
/// PostgreSQL. Authentication is the real guest session; authorization is the real Spec 03 owner-match policy over the owner
/// persisted in the manifest.
/// </summary>
public static class E2eTimelineEndpoints
{
    public const string FixtureTokenHeader = "X-E2E-Fixture-Token";
    public static bool IsFixturePath(PathString path) =>
        path.StartsWithSegments("/timeline", StringComparison.Ordinal) || path.StartsWithSegments("/__e2e", StringComparison.Ordinal)
        || RevenantSliceEndpoints.IsSlicePath(path);
    public static void Map(IEndpointRouteBuilder routes)
    {
        var timeline = routes.MapGroup("/timeline/{id:guid}").RequireAuthorization(IdentityPolicies.Guest);
        timeline.MapGet("", Describe);
        timeline.MapGet("/restore", Restore);
        timeline.MapPost("/fork", Fork);
        RevenantSliceEndpoints.Map(routes);
        var control = routes.MapGroup("/__e2e");
        control.MapGet("/info", Info);
        control.MapPost("/provision", Provision).RequireAuthorization(IdentityPolicies.Guest);
        control.MapGet("/facts/{id:guid}", Facts);
        control.MapGet("/log", Log);
    }
    // ---- Timeline routes -------------------------------------------------------------------------------------------
    private static async Task<IResult> Describe(
        Guid id, HttpContext http, IActorContext actor, IResourceAuthorizer authorizer,
        FixtureCatalog catalog, FixtureDomains domains, FixtureRegistry registry)
    {
        var ct = http.RequestAborted;
        if (await DenyAsync(http, actor, authorizer, id, ResourceOperation.Read) is { } denied)
        {
            return denied;
        }
        var row = await catalog.FindAsync(id, ct);
        var domain = row is null ? null : domains.Find(row.DomainId);
        if (row is null || domain is null)
        {
            return TimelineHttpErrors.ToResult(http, TimelineErrorCode.Forbidden);
        }
        var facts = await domain.DescribeAsync(new TimelineId(id), ct);
        if (facts.TryGetError(out var error))
        {
            return TimelineHttpErrors.ToResult(http, error.Code);
        }
        var scenario = registry.Find(row.RootId)?.Scenario ?? FixtureScenario.Standard;
        var markers = domain.Markers(scenario)
            .Where(m => long.Parse(m.Position, CultureInfo.InvariantCulture) <= facts.Value.LastPosition)
            .ToList();
        return Results.Json(new DescriptorDto(
            SourceId: row.RootId.ToString("D"),
            BranchId: row.ParentId is null ? null : id.ToString("D"),
            Bounds: new BoundsDto("0", facts.Value.LastPosition.ToString(CultureInfo.InvariantCulture)),
            ReadOnly: facts.Value.Status == HistoryStatus.Failed,
            Markers: markers,
            HistoryStatus: facts.Value.Status.ToString(),
            ParentId: row.ParentId?.ToString("D"),
            ForkPosition: row.ForkPosition?.ToString(CultureInfo.InvariantCulture)));
    }
    private static async Task<IResult> Restore(
        Guid id, string? position, HttpContext http, IActorContext actor, IResourceAuthorizer authorizer,
        FixtureCatalog catalog, FixtureDomains domains, FixtureRegistry registry)
    {
        var ct = http.RequestAborted;
        if (await DenyAsync(http, actor, authorizer, id, ResourceOperation.Read) is { } denied)
        {
            return denied;
        }
        // The position is parsed as a canonical decimal int64 (no double, no leading zeros, no sign). Anything else is not a position.
        if (!TimelinePosition.TryParseWire(position, out var parsed))
        {
            return TimelineHttpErrors.ToResult(http, TimelineErrorCode.InvalidPosition);
        }
        var row = await catalog.FindAsync(id, ct);
        var domain = row is null ? null : domains.Find(row.DomainId);
        if (row is null || domain is null)
        {
            return TimelineHttpErrors.ToResult(http, TimelineErrorCode.Forbidden);
        }
        if (registry.Find(row.RootId) is { } entry && entry.SlowRestoreMs.TryGetValue(parsed.Value, out var delayMs))
        {
            // Fixture latency before the engine call; aborted with the request, so a superseded browser request frees the server.
            await Task.Delay(delayMs, ct);
        }
        var restored = await domain.RestoreAsync(new TimelineId(id), parsed.Value, ct);
        if (restored.TryGetError(out var error))
        {
            return TimelineHttpErrors.ToResult(http, error.Code, parsed.ToWireString());
        }
        var facts = restored.Value;
        return Results.Json(new
        {
            position = facts.Position.ToString(CultureInfo.InvariantCulture),
            state = facts.State,
            hashOrigin = facts.HashOrigin.ToString(),
            historyStatus = facts.Status.ToString(),
        });
    }
    private static async Task<IResult> Fork(
        Guid id, HttpContext http, IActorContext actor, IResourceAuthorizer authorizer,
        FixtureCatalog catalog, FixtureDomains domains, FixtureRegistry registry)
    {
        var ct = http.RequestAborted;
        if (await DenyAsync(http, actor, authorizer, id, ResourceOperation.Write) is { } denied)
        {
            return denied;
        }
        ForkBody? body;
        try
        {
            body = http.Request.ContentLength is > 4096 ? null : await http.Request.ReadFromJsonAsync<ForkBody>(ct);
        }
        catch (JsonException)
        {
            body = null;
        }
        var header = http.Request.Headers["Idempotency-Key"].ToString();
        if (body is null || !ValidKey(body.OperationKey) || header != body.OperationKey
            || (body.Options is { } options && options.ValueKind is not (JsonValueKind.Null or JsonValueKind.Object)))
        {
            return Results.Json(new E2eTimelineProblem { Code = "invalid_request", Status = 400, TraceId = Activity.Current?.TraceId.ToString() }, statusCode: 400);
        }
        if (!TimelinePosition.TryParseWire(body.Position, out var parsed))
        {
            return TimelineHttpErrors.ToResult(http, TimelineErrorCode.InvalidPosition);
        }
        var row = await catalog.FindAsync(id, ct);
        var domain = row is null ? null : domains.Find(row.DomainId);
        if (row is null || domain is null)
        {
            return TimelineHttpErrors.ToResult(http, TimelineErrorCode.Forbidden);
        }
        // The engine owns idempotency: the same (owner, source, position, key) returns the same child, never a second one.
        var forked = await domain.ForkAsync(new TimelineId(id), parsed.Value, body.OperationKey!, ct);
        if (forked.TryGetError(out var error))
        {
            return TimelineHttpErrors.ToResult(http, error.Code, parsed.ToWireString());
        }
        if (registry.Find(row.RootId) is { } entry && entry.TakeForkResponseDrop())
        {
            // Fixture fault: the child is committed, then the connection is cut so the browser never sees the answer.
            http.Abort();
            return Results.Empty;
        }
        return Results.Json(new
        {
            branchId = forked.Value.Value.ToString("D"),
            sourceId = row.RootId.ToString("D"),
            forkPosition = parsed.ToWireString(),
        });
    }
    // ---- Fixture control routes -----------------------------------------------------------------------------------------
    private static IResult Info(IHostEnvironment environment, FixtureDomains domains) => Results.Json(new
    {
        fixture = "sands-e2e-timeline-host",
        notAProductionApi = true,
        environment = environment.EnvironmentName,
        checkpointInterval = FixtureDomains.CheckpointInterval,
        profiles = new[] { "revenant", "ghost-lap" }.Select(p => new { profile = p, steps = domains.FindByProfile(p)!.SeedSteps }),
    });
    private static async Task<IResult> Provision(
        HttpContext http, IActorContext actor, E2eFixtureOptions options, FixtureDomains domains, FixtureRegistry registry)
    {
        if (!TokenOk(http, options))
        {
            return TokenRejected();
        }
        ProvisionRequest? request;
        try
        {
            request = await http.Request.ReadFromJsonAsync<ProvisionRequest>(http.RequestAborted);
        }
        catch (JsonException)
        {
            request = null;
        }
        var domain = request?.Domain is null ? null : domains.FindByProfile(request.Domain);
        var scenario = ParseScenario(request?.Scenario);
        if (request is null || domain is null || scenario is null || !TryReadSlow(request.SlowRestoreMs, domain.SeedSteps, out var slow))
        {
            return Results.Json(new E2eTimelineProblem { Code = "invalid_request", Status = 400 }, statusCode: 400);
        }
        // The owner is the verified guest subject of this request, exactly as a domain handler would use it.
        var seeded = await domain.SeedAsync(actor.SubjectId, scenario.Value, http.RequestAborted);
        if (seeded.TryGetError(out var error))
        {
            return TimelineHttpErrors.ToResult(http, error.Code);
        }
        var id = seeded.Value.Value;
        if (scenario == FixtureScenario.CorruptCheckpoint)
        {
            await TamperCheckpointAsync(http.RequestServices.GetRequiredService<E2eOwnerAccess>().Source, id, http.RequestAborted);
        }
        registry.Add(id, new FixtureEntry(scenario.Value, slow, request.DropFirstForkResponse ?? false));
        return Results.Json(new
        {
            timelineId = id.ToString("D"),
            domain = domain.Profile,
            scenario = scenario.Value.ToString(),
            lastPosition = domain.SeedSteps.ToString(CultureInfo.InvariantCulture),
            owner = actor.SubjectId,
        });
    }
    private static async Task<IResult> Facts(Guid id, string? position, HttpContext http, E2eFixtureOptions options, E2eOwnerAccess owner)
    {
        if (!TokenOk(http, options))
        {
            return TokenRejected();
        }
        var ct = http.RequestAborted;
        await using var connection = await owner.Source.OpenConnectionAsync(ct);
        var history = await ReadHistoryAsync(connection, id, ct);
        if (history is null)
        {
            return Results.Json(new E2eTimelineProblem { Code = "not_found", Status = 404 }, statusCode: 404);
        }
        var children = new List<object>();
        await using (var command = new NpgsqlCommand(
            "SELECT timeline_id, operation_key, owner_id, fork_position, last_position, status FROM marrow_timeline.histories " +
            "WHERE parent_timeline_id = @id ORDER BY created_at, timeline_id", connection))
        {
            command.Parameters.AddWithValue("id", id);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                children.Add(new
                {
                    id = reader.GetGuid(0).ToString("D"),
                    operationKey = reader.GetString(1),
                    ownerId = reader.GetString(2),
                    forkPosition = reader.GetInt64(3).ToString(CultureInfo.InvariantCulture),
                    lastPosition = reader.GetInt64(4).ToString(CultureInfo.InvariantCulture),
                    status = reader.GetString(5),
                });
            }
        }
        string? stateHashAt = null;
        if (position is not null && TimelinePosition.TryParseWire(position, out var parsed))
        {
            stateHashAt = await StateHashAtAsync(connection, id, parsed.Value, ct);
        }
        return Results.Json(new { history, children, stateHashAt });
    }
    private static IResult Log(string? timeline, HttpContext http, E2eFixtureOptions options, E2eRequestLog log) =>
        TokenOk(http, options) ? Results.Json(log.Query(timeline)) : TokenRejected();
    // ---- Helpers ---------------------------------------------------------------------------------------------------
    internal static async Task<IResult?> DenyAsync(
        HttpContext http, IActorContext actor, IResourceAuthorizer authorizer, Guid id, ResourceOperation operation)
    {
        var decision = await authorizer.AuthorizeAsync(
            actor, new ResourceRef(FixtureOwnershipLookup.HistoryKind, id.ToString("D")), operation, http.RequestAborted);
        // Not the owner, unknown id and a non-fixture history are deliberately indistinguishable.
        return decision.IsAllowed ? null : TimelineHttpErrors.ToResult(http, TimelineErrorCode.Forbidden);
    }
    private static bool ValidKey(string? key) => !string.IsNullOrWhiteSpace(key) && key.Length <= 128;
    private static bool TokenOk(HttpContext http, E2eFixtureOptions options)
    {
        var supplied = Encoding.UTF8.GetBytes(http.Request.Headers[FixtureTokenHeader].ToString());
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(supplied), SHA256.HashData(Encoding.UTF8.GetBytes(options.FixtureToken)));
    }
    private static IResult TokenRejected() =>
        Results.Json(new E2eTimelineProblem { Code = "fixture_token_required", Status = 403 }, statusCode: 403);
    private static FixtureScenario? ParseScenario(string? text) => text switch
    {
        null or "standard" => FixtureScenario.Standard,
        "incompatible" => FixtureScenario.Incompatible,
        "corrupt-checkpoint" => FixtureScenario.CorruptCheckpoint,
        _ => null,
    };
    private static bool TryReadSlow(Dictionary<string, int>? input, long steps, out IReadOnlyDictionary<long, int> slow)
    {
        var result = new Dictionary<long, int>();
        slow = result;
        if (input is null)
        {
            return true;
        }
        if (input.Count > 8)
        {
            return false;
        }
        foreach (var (key, ms) in input)
        {
            if (!TimelinePosition.TryParseWire(key, out var position) || position.Value > steps || ms is < 1 or > 5000)
            {
                return false;
            }
            result[position.Value] = ms;
        }
        return true;
    }
    /// <summary>
    /// Storage tamper as the superuser, like the Spec 02 engine tests: flips one byte of the stored checkpoint without
    /// recomputing its hash. Restores at 8..11 start from this checkpoint and must fail verification; positions 0..7 and 12+ and
    /// the full-chain replay behind the descriptor do not read it.
    /// </summary>
    private static async Task TamperCheckpointAsync(NpgsqlDataSource owner, Guid id, CancellationToken ct)
    {
        await using var command = owner.CreateCommand(
            "SET session_replication_role = replica; " +
            "UPDATE marrow_timeline.checkpoints SET state_bytes = set_byte(state_bytes, 0, get_byte(state_bytes, 0) # 255) " +
            "WHERE timeline_id = @id AND position = @position; " +
            "SET session_replication_role = origin;");
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("position", FixtureDomains.TamperedCheckpoint);
        await command.ExecuteNonQueryAsync(ct);
    }
    private static async Task<object?> ReadHistoryAsync(NpgsqlConnection connection, Guid id, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            "SELECT h.domain_id, h.owner_id, h.status, h.last_position, h.parent_timeline_id, h.fork_position, h.last_record_hash, " +
            "(SELECT count(*) FROM marrow_timeline.steps s WHERE s.timeline_id = h.timeline_id), " +
            "(SELECT count(*) FROM marrow_timeline.checkpoints c WHERE c.timeline_id = h.timeline_id) " +
            "FROM marrow_timeline.histories h WHERE h.timeline_id = @id", connection);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }
        return new
        {
            id = id.ToString("D"),
            domainId = reader.GetString(0),
            ownerId = reader.GetString(1),
            status = reader.GetString(2),
            lastPosition = reader.GetInt64(3).ToString(CultureInfo.InvariantCulture),
            parentId = reader.IsDBNull(4) ? null : reader.GetGuid(4).ToString("D"),
            forkPosition = reader.IsDBNull(5) ? null : reader.GetInt64(5).ToString(CultureInfo.InvariantCulture),
            lastRecordHash = reader.GetString(6),
            ownStepRows = reader.GetInt64(7),
            ownCheckpointRows = reader.GetInt64(8),
        };
    }
    /// <summary>The state hash stored in PostgreSQL for a position: its own step row, the base checkpoint at 0, or the parent's row at or below the fork.</summary>
    private static async Task<string?> StateHashAtAsync(NpgsqlConnection connection, Guid id, long position, CancellationToken ct)
    {
        var current = id;
        while (true)
        {
            await using var parentCommand = new NpgsqlCommand(
                "SELECT parent_timeline_id, fork_position FROM marrow_timeline.histories WHERE timeline_id = @id", connection);
            parentCommand.Parameters.AddWithValue("id", current);
            Guid? parent;
            long? fork;
            await using (var reader = await parentCommand.ExecuteReaderAsync(ct))
            {
                if (!await reader.ReadAsync(ct))
                {
                    return null;
                }
                parent = reader.IsDBNull(0) ? null : reader.GetGuid(0);
                fork = reader.IsDBNull(1) ? null : reader.GetInt64(1);
            }
            if (parent is not null && position <= fork)
            {
                current = parent.Value;
                continue;
            }
            await using var hashCommand = new NpgsqlCommand(
                position == 0
                    ? "SELECT state_hash FROM marrow_timeline.checkpoints WHERE timeline_id = @id AND position = 0"
                    : "SELECT state_hash FROM marrow_timeline.steps WHERE timeline_id = @id AND position = @position", connection);
            hashCommand.Parameters.AddWithValue("id", current);
            if (position != 0)
            {
                hashCommand.Parameters.AddWithValue("position", position);
            }
            return await hashCommand.ExecuteScalarAsync(ct) as string;
        }
    }
}
/// <summary>
/// Puts the fixture routes in a path-scoped branch in front of the real <c>Hosts.Api</c> pipeline. The branch reuses
/// <c>UseMarrowIdentity</c> (CSRF/origin checks, authentication, authorization) from Marrow.Infrastructure. Startup fails in the
/// Production environment, so the surface cannot be enabled there even by mistake.
/// </summary>
public sealed class E2eFixtureStartupFilter(IHostEnvironment environment) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        if (environment.IsProduction())
        {
            throw new InvalidOperationException("The E2E Timeline fixture surface is test-only and must never run in Production.");
        }
        return app =>
        {
            app.MapWhen(context => E2eTimelineEndpoints.IsFixturePath(context.Request.Path), branch =>
            {
                branch.Use(async (context, nextMiddleware) =>
                {
                    var log = context.RequestServices.GetRequiredService<E2eRequestLog>();
                    var watch = Stopwatch.StartNew();
                    try
                    {
                        await nextMiddleware(context);
                    }
                    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
                    {
                        // The browser abandoned the request; nobody is left to answer.
                    }
                    finally
                    {
                        if (!context.Request.Path.StartsWithSegments("/__e2e", StringComparison.Ordinal))
                        {
                            var aborted = context.RequestAborted.IsCancellationRequested;
                            log.Add(context.Request.Method, context.Request.Path.Value ?? "", context.Request.QueryString.Value ?? "",
                                aborted ? null : context.Response.StatusCode, aborted ? "aborted" : "completed", watch.ElapsedMilliseconds);
                        }
                    }
                });
                branch.UseRouting();
                branch.UseMarrowIdentity();
                branch.UseEndpoints(E2eTimelineEndpoints.Map);
            });
            next(app);
        };
    }
}
public static class E2eFixtureServiceCollectionExtensions
{
    /// <summary>
    /// Adds the fixture engines, the owner-match authorization over the persisted owner, the request log and the path-scoped
    /// routes to a host composed by <c>Hosts.Api</c>'s <c>Program</c>. Test hosts only.
    /// </summary>
    public static IServiceCollection AddE2eTimelineFixture(this IServiceCollection services, string fixtureToken, NpgsqlDataSource ownerSource)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fixtureToken);
        FixtureDomains.AddFixtureEngines(services);
        // Task 5.4: the real Revenant finance engine (the one FinanceTimelineVersions key) over the same PostgreSQL pool, and the adapter on it.
        services.AddMarrowTimeline(FinanceTimelineVersions.All, FinanceTimelineVersions.CheckpointInterval);
        services.AddSingleton(sp => new FinanceTimelineAdapter(
            sp.GetRequiredService<Marrow.Timeline.ITimelineEngine<FinanceProjectionState, PaymentIntent, FinanceContext>>()));
        services.AddSingleton<SliceRunRegistry>();
        services.AddSingleton(new E2eFixtureOptions(fixtureToken));
        services.AddSingleton(new E2eOwnerAccess(ownerSource));
        services.AddSingleton<E2eRequestLog>();
        services.AddSingleton<FixtureRegistry>();
        services.AddSingleton(sp => new FixtureCatalog(sp.GetRequiredService<NpgsqlDataSource>()));
        services.AddSingleton(sp => new FixtureDomains(sp.GetRequiredService<NpgsqlDataSource>(), sp));
        services.AddMarrowResourceOwnership<FixtureOwnershipLookup>();
        services.AddTransient<IStartupFilter, E2eFixtureStartupFilter>();
        return services;
    }
}
