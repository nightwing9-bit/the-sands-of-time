using System.Diagnostics;
using System.Text.Json.Serialization;
using Marrow.Timeline;
using Microsoft.AspNetCore.Http;
namespace Sands.E2E.Backend;
/// <summary>
/// TEST-ONLY failure body of the fixture Timeline surface. Same JSON shape as the generated <c>ApiProblem</c>
/// (<c>code</c>, <c>status</c>, <c>traceId</c>, <c>timelineError</c>) plus <c>requestedPosition</c>, which echoes the canonical
/// decimal position the server parsed. The echo exists so a browser test can prove the server read a value above 2^53 exactly.
/// </summary>
public sealed class E2eTimelineProblem
{
    public required string Code { get; init; }
    public required int Status { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TraceId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TimelineErrorCode? TimelineError { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RequestedPosition { get; init; }
}
/// <summary>
/// Maps every Spec 02 <see cref="TimelineErrorCode"/> to one explicit HTTP status and snake_case code. The statuses are a fixture
/// choice: the spec that owns a real domain endpoint decides its own. The switch has no default arm that returns a value, so a new
/// enum member throws here (and fails <c>TimelineHttpErrorsTests</c>) instead of silently becoming a 200 or a 500.
/// </summary>
public static class TimelineHttpErrors
{
    /// <summary>Non-standard "client closed request"; nobody reads this response, it only labels the request log.</summary>
    public const int ClientClosedRequest = 499;
    public static int Status(TimelineErrorCode code) => code switch
    {
        TimelineErrorCode.NotFound => StatusCodes.Status404NotFound,
        TimelineErrorCode.Forbidden => StatusCodes.Status403Forbidden,
        TimelineErrorCode.InvalidPosition => StatusCodes.Status400BadRequest,
        TimelineErrorCode.IncompatibleVersion => StatusCodes.Status422UnprocessableEntity,
        TimelineErrorCode.CorruptHistory => StatusCodes.Status500InternalServerError,
        TimelineErrorCode.Conflict => StatusCodes.Status409Conflict,
        TimelineErrorCode.Cancelled => ClientClosedRequest,
        _ => throw new UnreachableException($"Unmapped Timeline error code {code}."),
    };
    public static string Code(TimelineErrorCode code) => code switch
    {
        TimelineErrorCode.NotFound => "timeline_not_found",
        TimelineErrorCode.Forbidden => "forbidden",
        TimelineErrorCode.InvalidPosition => "timeline_invalid_position",
        TimelineErrorCode.IncompatibleVersion => "timeline_incompatible_version",
        TimelineErrorCode.CorruptHistory => "timeline_corrupt_history",
        TimelineErrorCode.Conflict => "timeline_conflict",
        TimelineErrorCode.Cancelled => "timeline_cancelled",
        _ => throw new UnreachableException($"Unmapped Timeline error code {code}."),
    };
    public static E2eTimelineProblem Problem(HttpContext http, TimelineErrorCode code, string? requestedPosition = null) => new()
    {
        Code = Code(code),
        Status = Status(code),
        TraceId = Activity.Current?.TraceId.ToString() ?? http.TraceIdentifier,
        TimelineError = code,
        RequestedPosition = requestedPosition,
    };
    public static IResult ToResult(HttpContext http, TimelineErrorCode code, string? requestedPosition = null) =>
        Results.Json(Problem(http, code, requestedPosition), statusCode: Status(code));
}
