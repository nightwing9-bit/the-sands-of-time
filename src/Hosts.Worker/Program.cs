using Marrow.Infrastructure.Hosting;

var builder = WebApplication.CreateBuilder(args);
builder.AddMarrowHostDefaults();
builder.Services.AddMarrowDependencies(builder.Configuration);

var app = builder.Build();
// Hosts never run migrations; scripts/db/migrate.sh is the single executor.
app.MapGet("/", () => Results.Ok(new { service = "hosts-worker", status = "bootstrap" }));
app.MapMarrowHealth("hosts-worker");
app.Run();
