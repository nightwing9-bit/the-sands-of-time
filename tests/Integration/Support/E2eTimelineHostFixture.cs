using Microsoft.AspNetCore.Mvc.Testing;
using Sands.E2E.Backend;
using Xunit;
namespace Integration.Tests.Support;
/// <summary>
/// The test-only Timeline host (<c>tests/E2E/backend</c>) in memory: the real Hosts.Api composition, real PostgreSQL 18 migrated by
/// <c>scripts/db/migrate.sh</c>, the real Timeline engine and the fixture routes. The browser E2E suite runs the same classes over Kestrel.
/// </summary>
public sealed class E2eTimelineHostFixture : IAsyncLifetime
{
    public const string FixtureToken = "xunit-fixture-token-0123456789";
    public E2eDatabase Database { get; private set; } = null!;
    public E2eBackendFactory Factory { get; private set; } = null!;
    public async Task InitializeAsync()
    {
        Database = await E2eDatabase.StartAsync(CancellationToken.None);
        Factory = new E2eBackendFactory(Database, FixtureToken);
        _ = Factory.Server; // build the host now so a composition failure is reported here, not by the first test
    }
    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        await Database.DisposeAsync();
    }
    public TestBrowser NewBrowser() => new(
        Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = false,
        }),
        "https://localhost");
}
[CollectionDefinition(Name)]
public sealed class E2eTimelineHostCollection : ICollectionFixture<E2eTimelineHostFixture>
{
    public const string Name = "e2e-timeline-host";
}
