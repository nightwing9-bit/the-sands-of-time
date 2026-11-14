using System.Globalization;
using System.Text.Json;
using Hosts.Worker;
using Marrow.Infrastructure.Hosting;
using Marrow.Timeline;
using Npgsql;
using Revenant.Counterfactual;
using Marrow.Infrastructure.Messaging;
using Marrow.Infrastructure.Timeline;
using Revenant.Api;
using Revenant.Payments;
using Revenant.Projection;
using Revenant.Timeline;

if (args is ["--ml-runtime-selftest"])
{
    return MlRuntimeSelfTest.Run();
}

if (args is ["--r5-risk-selftest"])
{
    return R5RiskRuntimeSelfTest.Run();
}

Guid replayJob = default;
if (args is ["--replay"] || (args is ["--replay", var replayId] && Guid.TryParse(replayId, out replayJob) && replayJob != Guid.Empty))
{
    var replayBuilder = WebApplication.CreateBuilder([]);
    replayBuilder.AddMarrowHostDefaults();
    if (!string.IsNullOrWhiteSpace(replayBuilder.Configuration.GetConnectionString("RevenantLedger")))
        throw new InvalidOperationException("Replay must not be configured with the live ledger credential.");
    var configured = replayBuilder.Configuration.GetConnectionString("RevenantReplay")
        ?? throw new InvalidOperationException("Replay connection is not configured.");
    var tenant = replayBuilder.Configuration["Revenant:Ledger:TenantId"]
        ?? throw new InvalidOperationException("Replay tenant is not configured.");
    var dbOptions = new NpgsqlConnectionStringBuilder(configured) { Options = $"-c revenant.tenant_id={tenant}" };
    if (dbOptions.Username != "sands_replay" || tenant.Length == 0
        || tenant.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-')))
        throw new InvalidOperationException("Replay requires its dedicated login and a valid tenant.");
    replayBuilder.Services.AddSingleton(_ => NpgsqlDataSource.Create(dbOptions.ConnectionString));
    replayBuilder.Services.AddMarrowTimeline(ObservedFinanceTimelineVersions.All, ObservedFinanceTimelineVersions.CheckpointInterval);
    replayBuilder.Services.AddSingleton<V4ReplayWorker>();
    await using var replayHost = replayBuilder.Build();
    var replay = replayHost.Services.GetRequiredService<V4ReplayWorker>();
    if (replayJob != Guid.Empty) return await replay.ProcessAsync(tenant, replayJob, CancellationToken.None) ? 0 : 2;
    using var shutdown = new CancellationTokenSource();
    using var terminate = System.Runtime.InteropServices.PosixSignalRegistration.Create(
        System.Runtime.InteropServices.PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; shutdown.Cancel(); });
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
    try
    {
        while (!shutdown.IsCancellationRequested)
        {
            foreach (var id in await replay.PendingAsync(tenant, shutdown.Token))
                await replay.ProcessAsync(tenant, id, shutdown.Token);
            await Task.Delay(500, shutdown.Token);
        }
    }
    catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
    return 0;
}
if (args.Length > 0 && args[0] == "--replay")
{
    Console.Error.WriteLine("Usage: --replay [job-uuid]");
    return 1;
}

var v4Dispatch = args.Length > 0 && args[0] == "--v4-dispatch";
var v4Advisory = args.Length > 0 && args[0] == "--v4-advisory";
Guid execution = default;
int steps = 0;
long boundary = 0;
if (v4Dispatch && (args.Length != 3 || !Guid.TryParse(args[1], out execution) || execution == Guid.Empty
    || !int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out steps)
    || steps > RevenantV4Worker.MaxStepsPerInvocation))
{
    Console.Error.WriteLine("Usage: --v4-dispatch <execution-uuid> <step-count>");
    return 1;
}
if (v4Advisory && (args.Length is < 3 or > 4 || !Guid.TryParse(args[1], out execution) || execution == Guid.Empty
    || !long.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out boundary)
    || !ObservedFinanceVersion.IsBoundary(boundary) || (args.Length == 4 && string.IsNullOrWhiteSpace(args[3]))))
{
    Console.Error.WriteLine("Usage: --v4-advisory <execution-uuid> <completed-boundary-ms> [account-id]");
    return 1;
}

var builder = WebApplication.CreateBuilder(v4Dispatch || v4Advisory ? [] : args);
builder.AddMarrowHostDefaults();
builder.Services.AddMarrowDependencies(builder.Configuration);

// Spec 07, LiveOps mode. Two database credentials, never one:
//   ConnectionStrings:RevenantLedger -> sands_worker_ledger (revenant_ledger_writer + revenant_outbox_relay): screening, settlement,
//                                       outbox relay. Registered only when configured.
//   ConnectionStrings:Postgres       -> sands_worker (marrow_timeline_runtime + revenant_replay_reader): the V2 Timeline projection
//                                       reads live facts and writes only Timeline records; it cannot write any live finance table.
// Replay uses its own child-only credential in the separate composition above.
builder.Services
    .AddRevenantLedger(builder.Configuration, tx => new PostgresOutboxWriter(tx, new MessagingTables("revenant")))
    .AddRevenantScreeningWorker(builder.Configuration)
    .AddRevenantSettlementWorker(builder.Configuration)
    .AddMarrowTimeline(FinanceLedgerTimelineVersions.All, FinanceLedgerTimelineVersions.CheckpointInterval)
    // Spec 08 task 8.3: the model-aware (V3) shape on the same restricted login; its projection replays recorded evidence only.
    .AddMarrowTimeline(ModelLedgerTimelineVersions.All, ModelLedgerTimelineVersions.CheckpointInterval)
    .AddMarrowTimeline(ObservedFinanceTimelineVersions.All, ObservedFinanceTimelineVersions.CheckpointInterval)
    .AddRevenantV4Worker()
    .AddRevenantLedgerProjection(builder.Configuration)
    .AddMarrowOutboxRelayService(builder.Configuration, "Revenant:Relay", "RevenantLedger", new MessagingTables("revenant"))
    // R07.09: the idempotent read projection of revenant:events, on the restricted ConnectionStrings:Postgres login
    // (revenant_read_projector writes only payment_view/hold_view/posting_view and the inbox). Section Revenant:ReadProjection.
    .AddMarrowInboxConsumerService(builder.Configuration, "Revenant:ReadProjection", new MessagingTables("revenant"),
        RevenantEventProjection.ConsumerId, RevenantEventProjection.ApplyAsync);
if (v4Advisory)
    builder.Services.AddSingleton(sp => new RevenantAdvisoryWorker(sp.GetRequiredService<V4SimulationStore>(),
        sp.GetRequiredService<V4BranchExecution>(), sp.GetRequiredService<RevenantLedgerOptions>()));

await using var app = builder.Build();
if (v4Advisory)
{
    var worker = app.Services.GetRequiredService<RevenantAdvisoryWorker>();
    using var stopping = new CancellationTokenSource();
    Console.CancelKeyPress += (_, evt) =>
    {
        evt.Cancel = true;
        stopping.Cancel();
    };
    var receipt = await worker.ScoreAsync(new(execution), boundary, args.Length == 4 ? args[3] : null, stopping.Token);
    Console.WriteLine(JsonSerializer.Serialize(receipt));
    return 0;
}
if (v4Dispatch)
{
    var worker = app.Services.GetRequiredService<RevenantV4Worker>();
    using var stopping = new CancellationTokenSource();
    Console.CancelKeyPress += (_, evt) =>
    {
        evt.Cancel = true;
        stopping.Cancel();
    };
    var receipt = await worker.DispatchAsync(new(execution), steps, stopping.Token);
    Console.WriteLine(JsonSerializer.Serialize(receipt));
    return receipt.BlockedCode is null ? 0 : 2;
}

// Hosts never run migrations; scripts/db/migrate.sh is the single executor.
app.MapGet("/", () => Results.Ok(new { service = "hosts-worker", status = "bootstrap" }));
app.MapMarrowHealth("hosts-worker");

app.Run();
return 0;
