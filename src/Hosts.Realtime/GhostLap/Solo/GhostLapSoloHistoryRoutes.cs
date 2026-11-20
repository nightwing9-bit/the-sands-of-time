using GhostLap.Contracts.Solo;
using GhostLap.Rules;
using GhostLap.Solo;
using Marrow.Infrastructure.Identity;
using Marrow.Timeline;

namespace GhostLapHost;

/// <summary>
/// Task 11.5 owner-only history reads over the existing solo services: bounded trace and single-tick inspect of an owned original,
/// the catalog of completed Challenge/Fork children and the same-tick comparison. Every handler derives the owner from the verified
/// guest principal (<see cref="ClaimsActorContext"/>), reuses <see cref="SoloOriginals"/>/<see cref="SoloChallenges"/> (which authorize per
/// resource and verify the stored history), and answers a foreign, unknown or unfinished id with the same 404. The routes share the
/// history-read limiter with the other replaying reads; none of them writes anything or sits on a tick loop.
/// </summary>
internal static class GhostLapSoloHistoryRoutes
{
    public static void Map(WebApplication app, RouteGroupBuilder originals)
    {
        originals.MapGet("/{id}/inspect/{tick}", InspectAsync);
        originals.MapGet("/{id}/trace", TraceAsync);

        var children = app.MapGroup(GhostLapSoloWire.ChildrenRoute).RequireAuthorization(GhostLapPolicies.Connect);
        children.MapGet("/", ListChildrenAsync);
        children.MapGet("/{id}", GetChildAsync);
        children.MapGet("/{id}/comparison", CompareAsync);
        children.MapGet("/{id}/trace", ChildTraceAsync);
    }

    private static async Task<IResult> InspectAsync(string id, string tick, string? v, HttpContext http, SoloRunRegistry runs, CancellationToken ct)
    {
        if (ContractRefusal(v) is { } refused)
        {
            return refused;
        }

        if (!Guid.TryParseExact(id, "D", out var guid))
        {
            return NotFound();
        }

        if (!long.TryParse(tick, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var at)
            || at > GameRulesV1.RoundTicks)
        {
            return Results.Json(new { code = "invalid_tick" }, statusCode: StatusCodes.Status400BadRequest);
        }

        using var slot = runs.TryEnterHistoryRead();
        if (slot is null)
        {
            return Busy();
        }

        var chunk = await runs.Originals.TraceAsync(ClaimsActorContext.From(http.User), new TimelineId(guid), at, 1, ct);
        return chunk.TryGetValue(out var value)
            ? Results.Ok(new SoloInspectView(GhostLapSoloWire.HistoryContractVersion, Identity(value),
                ChallengeMapping.ToWire(value.Frames[0], CandlePlan.Create(value.Seed))))
            : Failure(chunk.Error);
    }

    private static async Task<IResult> TraceAsync(
        string id, long? from, int? count, string? v, HttpContext http, SoloRunRegistry runs, CancellationToken ct)
    {
        if (ContractRefusal(v) is { } refused)
        {
            return refused;
        }

        if (!Guid.TryParseExact(id, "D", out var guid))
        {
            return NotFound();
        }

        var start = from ?? 0;
        var size = count ?? GhostLapSoloWire.MaxTraceCount;
        if (size is < 1 or > GhostLapSoloWire.MaxTraceCount)
        {
            return Results.Json(new { code = SoloFailure.InvalidRequest }, statusCode: StatusCodes.Status400BadRequest);
        }

        if (start is < 0 or > GameRulesV1.RoundTicks)
        {
            return Results.Json(new { code = "invalid_tick" }, statusCode: StatusCodes.Status400BadRequest);
        }

        using var slot = runs.TryEnterHistoryRead();
        if (slot is null)
        {
            return Busy();
        }

        var chunk = await runs.Originals.TraceAsync(ClaimsActorContext.From(http.User), new TimelineId(guid), start, size, ct);
        return chunk.TryGetValue(out var value)
            ? Results.Ok(new SoloTraceView(
                GhostLapSoloWire.HistoryContractVersion, Identity(value), value.FromTick, value.Frames.Length, value.NextTick,
                TraceFrames(value)))
            : Failure(chunk.Error);
    }

    private static async Task<IResult> ListChildrenAsync(
        string? source, string? after, int? limit, HttpContext http, SoloRunRegistry runs, SoloChallenges challenges, CancellationToken ct)
    {
        TimelineId? sourceId = null;
        TimelineId? afterId = null;
        if (source is not null)
        {
            if (!Guid.TryParseExact(source, "D", out var g))
            {
                return Results.Json(new { code = SoloFailure.InvalidRequest }, statusCode: StatusCodes.Status400BadRequest);
            }

            sourceId = new TimelineId(g);
        }

        if (after is not null)
        {
            if (!Guid.TryParseExact(after, "D", out var g))
            {
                return Results.Json(new { code = SoloFailure.InvalidRequest }, statusCode: StatusCodes.Status400BadRequest);
            }

            afterId = new TimelineId(g);
        }

        using var slot = runs.TryEnterHistoryRead();
        if (slot is null)
        {
            return Busy();
        }

        var page = await challenges.ListChildrenAsync(ClaimsActorContext.From(http.User), sourceId, afterId, limit ?? 10, ct);
        return page.TryGetValue(out var value)
            ? Results.Ok(new SoloChildPageView([.. value.Items.Select(ToView)], value.NextAfter?.Value.ToString("D")))
            : Failure(page.Error);
    }

    private static async Task<IResult> GetChildAsync(string id, HttpContext http, SoloRunRegistry runs, SoloChallenges challenges, CancellationToken ct)
    {
        if (!Guid.TryParseExact(id, "D", out var guid))
        {
            return NotFound();
        }

        using var slot = runs.TryEnterHistoryRead();
        if (slot is null)
        {
            return Busy();
        }

        var found = await challenges.GetChildAsync(ClaimsActorContext.From(http.User), new TimelineId(guid), ct);
        return found.TryGetValue(out var result) ? Results.Ok(ChallengeMapping.ToView(result)) : Failure(found.Error);
    }

    private static async Task<IResult> CompareAsync(
        string id, long? from, int? count, int? stride, string? v, HttpContext http, SoloRunRegistry runs, SoloChallenges challenges, CancellationToken ct)
    {
        if (ContractRefusal(v) is { } refused)
        {
            return refused;
        }

        if (!Guid.TryParseExact(id, "D", out var guid))
        {
            return NotFound();
        }

        using var slot = runs.TryEnterHistoryRead();
        if (slot is null)
        {
            return Busy();
        }

        var compared = await challenges.CompareChildAsync(
            ClaimsActorContext.From(http.User), new TimelineId(guid), from ?? 0, count ?? SoloChallenges.MaxComparisonPoints, stride ?? 1, ct);
        return compared.TryGetValue(out var c)
            ? Results.Ok(new SoloComparisonView(
                GhostLapSoloWire.HistoryContractVersion, c.Child.Value.ToString("D"), c.Source.Value.ToString("D"), ChallengeMapping.KindName(c.Kind),
                c.ForkTick, ChallengeMapping.Pinned(c.Versions, c.RulesVersion, c.LayoutVersion), c.FinalTick, c.SourceFinalStateHash,
                c.ChildFinalStateHash, c.FromTick, c.Stride, c.NextTick,
                [.. c.Points.Select(p => new SoloComparisonPointView(
                    p.Tick, p.SourceScore, p.ChildScore, p.Delta, Parts(p.Source), Parts(p.Child), p.AfterFork, p.IsFinal,
                    p.SourceStateHash, p.ChildStateHash))]))
            : Failure(compared.Error);
    }

    private static async Task<IResult> ChildTraceAsync(
        string id, long? from, int? count, string? v, HttpContext http, SoloRunRegistry runs, SoloChallenges challenges, CancellationToken ct)
    {
        if (ContractRefusal(v) is { } refused) return refused;
        if (!Guid.TryParseExact(id, "D", out var guid)) return NotFound();
        using var slot = runs.TryEnterHistoryRead();
        if (slot is null) return Busy();
        var result = await challenges.TraceChildAsync(ClaimsActorContext.From(http.User), new TimelineId(guid), from ?? 0,
            count ?? GhostLapSoloWire.MaxTraceCount, ct);
        if (!result.TryGetValue(out var child)) return Failure(result.Error);
        var c = child.Trace;
        return Results.Ok(new SoloChildTraceView(GhostLapSoloWire.HistoryContractVersion, id, child.Source.Value.ToString("D"),
            ChallengeMapping.KindName(child.Kind), child.ForkTick, child.SourceFinalStateHash, c.SourceFinalStateHash,
            ChallengeMapping.Pinned(c.Versions, c.RulesVersion, c.LayoutVersion), c.SourceFinalTick, c.FromTick, c.Frames.Length,
            c.NextTick, TraceFrames(c)));
    }

    private static SoloGhostFrame[] TraceFrames(SoloTraceChunk chunk)
    {
        var plan = CandlePlan.Create(chunk.Seed);
        return [.. chunk.Frames.Select(frame => ChallengeMapping.ToWire(frame, plan))];
    }

    private static SoloSourceIdentity Identity(SoloTraceChunk c) => new(
        c.Timeline.Value.ToString("D"), c.SourceFinalStateHash, c.SourceFinalTick, ChallengeMapping.Pinned(c.Versions, c.RulesVersion, c.LayoutVersion));

    private static SoloScorePartsView Parts(SoloScoreParts p) =>
        new(p.Movement, p.Pickup, p.Penalty, p.Total, p.Pickups, p.Bursts, p.Charge, p.Overlit, p.DistanceFixed);

    private static SoloChildView ToView(SoloChildSummary c) => new(
        c.Child.Value.ToString("D"), c.Source.Value.ToString("D"), ChallengeMapping.KindName(c.Kind), c.ClientKey, c.ForkTick, c.FinalTick,
        c.FinalStateHash, c.LastRecordHash, c.Score, c.Verified, ChallengeFinishStatuses.Completed, c.Seed, c.MascotId,
        ChallengeMapping.Pinned(c.Versions, c.RulesVersion, c.LayoutVersion));

    private static IResult? ContractRefusal(string? requested) =>
        requested is null || requested == GhostLapSoloWire.HistoryContractVersion
            ? null
            : Results.Json(new { code = SoloFailure.ContractUnsupported, supported = GhostLapSoloWire.HistoryContractVersion },
                statusCode: StatusCodes.Status400BadRequest);

    private static IResult NotFound() => Results.NotFound(new { code = SoloFailure.NotFound });

    private static IResult Busy() => Results.Json(new { code = SoloFailure.ServerBusy }, statusCode: StatusCodes.Status429TooManyRequests);

    /// <summary>Foreign, unknown and unfinished ids are one 404 (no existence oracle). Malformed requests are 400; everything else is a typed 409/503.</summary>
    private static IResult Failure(SoloError error) => error.Code switch
    {
        SoloErrorCode.NotFound or SoloErrorCode.NotComplete => NotFound(),
        SoloErrorCode.Unauthenticated => Results.Unauthorized(),
        SoloErrorCode.Unavailable or SoloErrorCode.Cancelled =>
            Results.Json(new { code = SoloFailure.Unavailable }, statusCode: StatusCodes.Status503ServiceUnavailable),
        SoloErrorCode.InvalidRequest or SoloErrorCode.InvalidTick =>
            Results.Json(new { code = SoloMapping.Code(error) }, statusCode: StatusCodes.Status400BadRequest),
        _ => Results.Json(new { code = SoloMapping.Code(error) }, statusCode: StatusCodes.Status409Conflict),
    };
}
