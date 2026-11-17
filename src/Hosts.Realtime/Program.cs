using GhostLapHost;
using Marrow.Infrastructure.Hosting;
using Marrow.Infrastructure.Identity;
using Marrow.Infrastructure.Realtime;
using Revenant.Api;

var builder = WebApplication.CreateBuilder(args);
builder.AddMarrowHostDefaults();
builder.Logging.AddMarrowRealtimeLogFilters();
builder.Services.AddMarrowDependencies(builder.Configuration);
builder.Services.AddMarrowIdentity(builder.Configuration, builder.Environment);
// Single process by design: no SignalR backplane, so room/stream authority cannot be split across replicas.
// A second instance against the same database refuses to start (RealtimeInstanceGuard advisory lock).
builder.Services.AddMarrowRealtime(builder.Configuration);
// Spec 07: revenant-run streams, authorized by the Revenant staff policy. Registered only when ConnectionStrings:RevenantLedger is set.
builder.Services.AddRevenantRealtime(builder.Configuration);
// Spec 10 tasks 10.1/10.2: Ghost Lap lobby owner, hub, room stream, match registry and durable process generation (registered
// after the instance guard above holds its lock). MUST come after AddRevenantRealtime: it wraps the authorizers
// registered so far in a kind router instead of replacing them, and the startup verifier refuses to run if anything replaces the router.
builder.Services.AddGhostLapRealtime(builder.Configuration);
var app = builder.Build();

// Hosts never run migrations; scripts/db/migrate.sh is the single executor.
app.UseMarrowRealtime();
app.UseGhostLapRealtime();
app.UseMarrowIdentity();
app.MapGet("/", () => Results.Ok(new { service = "hosts-realtime", status = "bootstrap" }));
app.MapMarrowHealth("hosts-realtime");
app.MapMarrowRealtime();
app.MapGhostLapRealtime();
// Resolved before Run, which disposes the service provider on return.
var instanceGuard = app.Services.GetRequiredService<RealtimeInstanceGuard>();
try
{
    app.Run();
}
catch (RealtimeInstanceRefusedException)
{
    // Already logged (event 3208 and the host's start failure); exit with a readable code instead of an abort.
    return RealtimeInstanceRefusedException.ExitCode;
}
// Non-zero when the room-authority lock was lost while running, so `restart: on-failure` restarts and re-acquires it.
return instanceGuard.ExitCode;

/// <summary>Entry-point marker so integration tests can host the real composition with <c>WebApplicationFactory</c>.</summary>
public partial class Program;
