using GhostLap.Contracts.Solo;
using GhostLap.Solo;
using GhostLap.Timeline;
using Marrow.Infrastructure.Identity;
using Marrow.Infrastructure.Identity.Policy;
using Marrow.Infrastructure.Timeline;
using Marrow.Timeline;
using Microsoft.AspNetCore.Http.Connections;

namespace GhostLapHost;

public static class GhostLapSoloRegistration
{
    public const string ConnectionStringName = "GhostLapSolo";

    /// <summary>
    /// Registers solo recording and the owner library when <c>ConnectionStrings:GhostLapSolo</c> is set (login <c>sands_realtime_solo</c>:
    /// <c>marrow_timeline_runtime</c> + <c>ghostlap_runtime</c>). Not set means solo is not offered by this host (no routes, no hub),
    /// like the Revenant ledger. The Timeline engine for (GameState, GameInput, GameContext) runs on this dedicated pool only.
    /// </summary>
    public static IServiceCollection AddGhostLapSolo(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return services;
        }

        services.AddSingleton(_ => GhostLapSoloDataSource.Create(connectionString));
        services.AddMarrowTimeline(GhostLapTimelineVersions.All, GhostLapTimelineVersions.CheckpointInterval,
            sp => sp.GetRequiredService<GhostLapSoloDataSource>().DataSource);
        services.AddSingleton(sp => new SoloOriginalStore(sp.GetRequiredService<GhostLapSoloDataSource>().DataSource));
        // The solo kinds are decided by the owner policy over the persisted row, never by the shared kind router (which would deny them).
        services.AddSingleton(sp =>
        {
            var store = sp.GetRequiredService<SoloOriginalStore>();
            return new SoloOriginals(
                sp.GetRequiredService<ITimelineEngine<GhostLap.Simulation.GameState, GhostLap.Simulation.GameInput, GhostLap.Simulation.GameContext>>(),
                store, new OwnerResourceAuthorizer(new SoloOwnershipLookup(store)));
        });
        services.AddSingleton<GhostLapSoloLog>();
        services.AddSingleton<SoloRunRegistry>();
        return services;
    }

    public static bool IsEnabled(this IServiceProvider services) => services.GetService<SoloRunRegistry>() is not null;

    /// <summary>Maps the solo hub and the owner library routes. Call only when <see cref="IsEnabled"/>.</summary>
    public static void MapGhostLapSolo(this WebApplication app)
    {
        app.MapHub<GhostLapSoloHub>(GhostLapSoloWire.HubPath, o => o.Transports = HttpTransportType.WebSockets);
        var library = app.MapGroup(GhostLapSoloWire.OriginalsRoute).RequireAuthorization(GhostLapPolicies.Connect);
        library.MapGet("/", ListAsync);
        library.MapGet("/{id}", GetAsync);
    }

    private static async Task<IResult> ListAsync(HttpContext http, SoloRunRegistry runs, CancellationToken ct)
    {
        var actor = ClaimsActorContext.From(http.User);
        var list = await runs.Originals.ListAsync(actor, SoloOriginals.MaxListLimit, ct);
        return list.TryGetValue(out var items)
            ? Results.Ok(items.Select(SoloMapping.ToView).ToArray())
            : Failure(list.Error);
    }

    private static async Task<IResult> GetAsync(string id, HttpContext http, SoloRunRegistry runs, CancellationToken ct)
    {
        if (!Guid.TryParseExact(id, "D", out var guid))
        {
            return Results.NotFound(new { code = SoloFailure.NotFound });
        }

        using var slot = runs.TryEnterHistoryRead();
        if (slot is null)
        {
            return Results.Json(new { code = SoloFailure.ServerBusy }, statusCode: StatusCodes.Status429TooManyRequests);
        }

        var actor = ClaimsActorContext.From(http.User);
        var found = await runs.Originals.GetAsync(actor, new TimelineId(guid), ct);
        return found.TryGetValue(out var original) ? Results.Ok(SoloMapping.ToView(original)) : Failure(found.Error);
    }

    /// <summary>Foreign, unknown and never-completed ids are one 404 (no existence oracle).</summary>
    private static IResult Failure(SoloError error) => error.Code switch
    {
        SoloErrorCode.NotFound or SoloErrorCode.NotComplete => Results.NotFound(new { code = SoloFailure.NotFound }),
        SoloErrorCode.Unauthenticated => Results.Unauthorized(),
        SoloErrorCode.Unavailable or SoloErrorCode.Cancelled =>
            Results.Json(new { code = SoloFailure.Unavailable }, statusCode: StatusCodes.Status503ServiceUnavailable),
        _ => Results.Json(new { code = SoloMapping.Code(error) }, statusCode: StatusCodes.Status409Conflict),
    };
}
