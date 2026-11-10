using Marrow.Infrastructure.Hosting;
using Marrow.Infrastructure.Identity;
var builder = WebApplication.CreateBuilder(args);
builder.AddMarrowHostDefaults();
builder.Services.AddMarrowDependencies(builder.Configuration);
builder.Services.AddMarrowIdentity(builder.Configuration, builder.Environment);
var app = builder.Build();
// Hosts never run migrations; scripts/db/migrate.sh is the single executor.
app.UseMarrowIdentity();
app.MapGet("/", () => Results.Ok(new { service = "hosts-api", status = "bootstrap" }));
app.MapMarrowHealth("hosts-api");
app.MapMarrowIdentityEndpoints();
app.Run();
/// <summary>Entry-point marker so integration tests can host the real composition with <c>WebApplicationFactory</c>.</summary>
public partial class Program;
