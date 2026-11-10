using System.Net;
using System.Text.Json;
using Integration.Tests.Support;
using Marrow.Infrastructure.Contracts;
using Marrow.Timeline;
using Xunit;
namespace Integration.Tests.Contracts;

/// <summary>
/// R04.08: what the real Hosts.Api composition sends must match the documented DTOs that generate the TypeScript client
/// (<c>web/packages/contracts</c>). The OpenAPI file itself is checked for staleness by <c>scripts/contracts/generate.sh --check</c>.
/// </summary>
[Collection(IdentityCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ContractMetadataTests(IdentityDatabase db)
{
    [Fact]
    public async Task Meta_contracts_is_anonymous_and_lists_the_live_versions_and_every_timeline_error_code_by_name()
    {
        await using var factory = new IdentityApiFactory(db);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/meta/contracts");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal(["apiContract", "realtimeSchemaVersion", "timelineErrorCodes"], root.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal(ContractMetadata.ApiContractVersion, root.GetProperty("apiContract").GetString());
        Assert.Equal(JsonValueKind.Number, root.GetProperty("realtimeSchemaVersion").ValueKind);
        var codes = root.GetProperty("timelineErrorCodes").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(Enum.GetNames<TimelineErrorCode>(), codes);
    }

    [Fact]
    public async Task Real_denial_bodies_use_exactly_the_documented_ApiProblem_fields()
    {
        await using var factory = new IdentityApiFactory(db);
        using var client = factory.CreateClient();
        var documented = typeof(ApiProblem).GetProperties().Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name)).ToHashSet();

        using var unauthenticated = await client.GetAsync("/auth/staff/me");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        AssertProblemShape(await unauthenticated.Content.ReadAsStringAsync(), documented, "session_inactive", 401);

        // The Origin guard is middleware, a different code path from endpoint handlers; its body must match too.
        using var foreign = new HttpRequestMessage(HttpMethod.Post, "/auth/guest/signout");
        foreign.Headers.Add("Origin", "https://evil.example");
        using var rejected = await client.SendAsync(foreign);
        Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        AssertProblemShape(await rejected.Content.ReadAsStringAsync(), documented, "origin_rejected", 403);
    }

    private static void AssertProblemShape(string body, HashSet<string> documented, string code, int status)
    {
        using var doc = JsonDocument.Parse(body);
        var names = doc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet();
        Assert.True(names.IsSubsetOf(documented), $"undocumented fields in {body}");
        Assert.Contains("code", names);
        Assert.Contains("status", names);
        Assert.Equal(code, doc.RootElement.GetProperty("code").GetString());
        Assert.Equal(status, doc.RootElement.GetProperty("status").GetInt32());
        Assert.False(names.Contains("timelineError"), "identity failures are not timeline failures");
    }
}
