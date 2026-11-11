using Marrow.Infrastructure.Contracts;
using Marrow.Infrastructure.Hosting;
using Marrow.Infrastructure.Identity;
using Marrow.Infrastructure.Messaging;
using Revenant.Api;
var builder = WebApplication.CreateBuilder(args);
builder.AddMarrowHostDefaults();
builder.Services.AddMarrowDependencies(builder.Configuration);
builder.Services.AddMarrowIdentity(builder.Configuration, builder.Environment);
builder.Services.AddMarrowContractMetadata();
// Spec 07: LiveOps finance writer. Registered only when ConnectionStrings:RevenantLedger names a revenant_ledger_writer login.
builder.Services.AddRevenantLedger(builder.Configuration, tx => new PostgresOutboxWriter(tx, new MessagingTables("revenant")));
// The OpenAPI document is generated at build time for web/packages/contracts (scripts/contracts/generate.sh); it is not served.
builder.Services.AddOpenApi(options => options.AddDocumentTransformer((document, _, _) =>
{
    document.Info.Title = "The Sands Of Time API";
    document.Info.Version = ContractMetadata.ApiContractVersion;
    return Task.CompletedTask;
}));
var app = builder.Build();
// Hosts never run migrations; scripts/db/migrate.sh is the single executor.
app.UseMarrowIdentity();
app.MapGet("/", () => Results.Ok(new { service = "hosts-api", status = "bootstrap" }));
app.MapMarrowHealth("hosts-api");
app.MapMarrowIdentityEndpoints();
app.MapMarrowContractMetadata();
app.MapRevenantLedgerApi(IdentityPolicies.Staff);
app.Run();
/// <summary>Entry-point marker so integration tests can host the real composition with <c>WebApplicationFactory</c>.</summary>
public partial class Program;
