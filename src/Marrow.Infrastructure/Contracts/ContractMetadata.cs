using System.Text.Json.Serialization;
using Marrow.Kernel.Realtime;
using Marrow.Timeline;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
namespace Marrow.Infrastructure.Contracts;

/// <summary>
/// Versions a browser needs before it may call this host. A different <see cref="ApiContract"/> major version means the
/// generated TypeScript client in <c>web/packages/contracts</c> cannot be trusted against this host.
/// <see cref="TimelineErrorCodes"/> is the live <see cref="TimelineErrorCode"/> enum, so the generated client's union type
/// is derived from the real C# members instead of a hand-written copy.
/// </summary>
public sealed record ContractVersions(
    string ApiContract,
    int RealtimeSchemaVersion,
    IReadOnlyList<TimelineErrorCode> TimelineErrorCodes);

/// <summary>
/// Typed JSON failure body shared by every module: <c>{ "code": "snake_case", "status": 403, "traceId": "..." }</c>
/// (the shape <c>AuthProblem</c> already writes). <see cref="TimelineError"/> is set only when the failure came from a
/// Marrow Timeline operation, and then holds the exact <see cref="TimelineErrorCode"/> member name.
/// </summary>
public sealed class ApiProblem
{
    public required string Code { get; init; }
    public required int Status { get; init; }
    public string? TraceId { get; init; }
    public TimelineErrorCode? TimelineError { get; init; }
}

public static class ContractMetadata
{
    /// <summary>
    /// Version of the HTTP and OpenAPI surface (semver). Bump the major when a generated DTO changes incompatibly.
    /// Also published as the OpenAPI <c>info.version</c>, so the generated client knows what it was built from.
    /// </summary>
    public const string ApiContractVersion = "1.0.0";

    public static ContractVersions Current { get; } = new(
        ApiContractVersion,
        RealtimeEnvelope<object>.CurrentSchemaVersion,
        Enum.GetValues<TimelineErrorCode>());

    /// <summary>
    /// Pins the JSON rules the OpenAPI document and the TypeScript client rely on: enums travel as member names and
    /// numbers are strictly numbers (no quoted-number leniency), so a generated <c>number</c> is exactly what is sent.
    /// No request DTO carries a number today, so the stricter reading changes no accepted request.
    /// </summary>
    public static IServiceCollection AddMarrowContractMetadata(this IServiceCollection services) =>
        services.Configure<JsonOptions>(o =>
        {
            o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
            o.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
        });

    /// <summary>Anonymous <c>GET /meta/contracts</c>: public, static, secret-free version handshake.</summary>
    public static IEndpointRouteBuilder MapMarrowContractMetadata(this IEndpointRouteBuilder app)
    {
        app.MapGet("/meta/contracts", () => Results.Ok(Current))
            .WithName("GetContractVersions")
            .Produces<ContractVersions>(StatusCodes.Status200OK);
        return app;
    }
}
