using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
namespace Sands.E2E.Backend;
/// <summary>
/// The real <c>Hosts.Api</c> composition (its own <c>Program</c>: host defaults, Postgres, identity, contract metadata) running in
/// the Testing environment against the fixture database, plus the test-only Timeline surface of this assembly. Used in memory by
/// the integration tests and over Kestrel by <see cref="E2eBackendEntry"/>.
/// </summary>
public sealed class E2eBackendFactory(E2eDatabase database, string fixtureToken) : WebApplicationFactory<Program>
{
    public string MasterSecret { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        // A plain executable has no test-project manifest for the factory to find the app's content root, so name it.
        builder.UseContentRoot(Path.Combine(E2eDatabase.RepositoryRoot(), "src", "Hosts.Api"));
        builder.UseSetting("ConnectionStrings:Postgres", database.ApiConnectionString);
        // The API only touches Redis for readiness; the fixture never calls it.
        builder.UseSetting("ConnectionStrings:Redis", "localhost:6399,abortConnect=false");
        builder.UseSetting("Identity:MasterSecret", MasterSecret);
        if (database.R8Ledger.Length > 0)
        {
            builder.UseSetting("ConnectionStrings:RevenantLedger", database.R8Ledger);
            builder.UseSetting("ConnectionStrings:RevenantTimeline", database.ApiConnectionString);
            builder.UseSetting("Identity:AllowedOrigins:0", "http://localhost:5175");
        }
        builder.ConfigureTestServices(services => services.AddE2eTimelineFixture(fixtureToken, database.Owner));
    }
}
