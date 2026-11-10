using System.Net;
using System.Text.Json;
using Integration.Tests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Sands.E2E.Backend;
using Xunit;
namespace Integration.Tests.Timeline;
/// <summary>
/// The fixture Timeline surface exists only in the test-only host. The product <c>Hosts.Api</c> composition serves none of it in any
/// environment, its OpenAPI document lists none of it, and the fixture itself refuses to start in Production.
/// </summary>
[Collection(IdentityCollection.Name)]
[Trait("Category", "Integration")]
public sealed class TimelineSurfaceIsolationTests(IdentityDatabase db)
{
    private static readonly string[] FixturePaths =
    [
        "/timeline/00000000-0000-0000-0000-000000000001",
        "/timeline/00000000-0000-0000-0000-000000000001/restore?position=0",
        "/revenant/runs/00000000-0000-0000-0000-000000000001",
        "/revenant/runs/00000000-0000-0000-0000-000000000001/restore?position=0",
        "/revenant/branches/00000000-0000-0000-0000-000000000001/comparison",
        "/__e2e/info",
        "/__e2e/facts/00000000-0000-0000-0000-000000000001",
        "/__e2e/log",
    ];
    [Theory]
    [InlineData("Production")]
    [InlineData("Testing")]
    [InlineData("Development")]
    public async Task The_product_api_serves_no_timeline_or_fixture_route_in_any_environment(string environment)
    {
        await using var factory = new IdentityApiFactory(db, environment: environment);
        using var client = factory.CreateClient();
        foreach (var path in FixturePaths)
        {
            using var response = await client.GetAsync(path);
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{environment} GET {path} -> {(int)response.StatusCode}");
        }
        foreach (var path in new[] { "/timeline/00000000-0000-0000-0000-000000000001/fork", "/revenant/runs", "/revenant/runs/00000000-0000-0000-0000-000000000001/branches", "/__e2e/provision" })
        {
            using var response = await client.PostAsync(path, new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{environment} POST {path} -> {(int)response.StatusCode}");
        }
    }
    [Fact]
    public void The_committed_OpenAPI_document_and_the_product_assemblies_know_nothing_of_the_fixture_surface()
    {
        var root = RepositoryRoot();
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "web", "packages", "contracts", "openapi", "hosts-api.json")));
        var paths = document.RootElement.GetProperty("paths").EnumerateObject().Select(p => p.Name).ToArray();
        Assert.NotEmpty(paths);
        Assert.DoesNotContain(paths, p => p.StartsWith("/timeline", StringComparison.Ordinal) || p.StartsWith("/revenant", StringComparison.Ordinal) || p.StartsWith("/__e2e", StringComparison.Ordinal));
        foreach (var assembly in new[] { typeof(Program).Assembly, typeof(Marrow.Infrastructure.Timeline.TimelineServiceCollectionExtensions).Assembly, typeof(Marrow.Timeline.TimelineId).Assembly })
        {
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), a => a.Name is "E2E.Backend" || (a.Name?.EndsWith(".Tests", StringComparison.Ordinal) ?? false));
        }
    }
    [Fact]
    public void The_fixture_refuses_to_configure_itself_in_Production()
    {
        var production = new FakeEnvironment(Environments.Production);
        Assert.Throws<InvalidOperationException>(() => new E2eFixtureStartupFilter(production).Configure(_ => { }));
        _ = new E2eFixtureStartupFilter(new FakeEnvironment("Testing")).Configure(_ => { });
        Assert.True(E2eTimelineEndpoints.IsFixturePath("/timeline/x"));
        Assert.True(E2eTimelineEndpoints.IsFixturePath("/__e2e/info"));
        Assert.True(E2eTimelineEndpoints.IsFixturePath("/revenant/runs"));
        Assert.False(E2eTimelineEndpoints.IsFixturePath("/auth/guest/start"));
        Assert.False(E2eTimelineEndpoints.IsFixturePath("/timelines"));
    }
    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "TheSandsOfTime.slnx")))
            {
                return dir.FullName;
            }
        }
        throw new InvalidOperationException("Repository root not found.");
    }
    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "fake";
        public string ContentRootPath { get; set; } = "/";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
