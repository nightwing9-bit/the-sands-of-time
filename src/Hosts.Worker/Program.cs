using Hosts.Worker;
using Marrow.Infrastructure.Hosting;
using Marrow.Infrastructure.Messaging;
using Marrow.Infrastructure.Timeline;
using Revenant.Api;
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

var builder = WebApplication.CreateBuilder(args);
builder.AddMarrowHostDefaults();
builder.Services.AddMarrowDependencies(builder.Configuration);

// Spec 07, LiveOps mode. Two database credentials, never one:
//   ConnectionStrings:RevenantLedger -> sands_worker_ledger (revenant_ledger_writer + revenant_outbox_relay): screening, settlement,
//                                       outbox relay. Registered only when configured.
//   ConnectionStrings:Postgres       -> sands_worker (marrow_timeline_runtime + revenant_replay_reader): the V2 Timeline projection
//                                       reads live facts and writes only Timeline records; it cannot write any live finance table.
// The Replay process mode (Spec 09) reuses the second, restricted credential only.
builder.Services
    .AddRevenantLedger(builder.Configuration, tx => new PostgresOutboxWriter(tx, new MessagingTables("revenant")))
    .AddRevenantScreeningWorker(builder.Configuration)
    .AddRevenantSettlementWorker(builder.Configuration)
    .AddMarrowTimeline(FinanceLedgerTimelineVersions.All, FinanceLedgerTimelineVersions.CheckpointInterval)
    // Spec 08 task 8.3: the model-aware (V3) shape on the same restricted login; its projection replays recorded evidence only.
    .AddMarrowTimeline(ModelLedgerTimelineVersions.All, ModelLedgerTimelineVersions.CheckpointInterval)
    .AddRevenantLedgerProjection(builder.Configuration)
    .AddMarrowOutboxRelayService(builder.Configuration, "Revenant:Relay", "RevenantLedger", new MessagingTables("revenant"))
    // R07.09: the idempotent read projection of revenant:events, on the restricted ConnectionStrings:Postgres login
    // (revenant_read_projector writes only payment_view/hold_view/posting_view and the inbox). Section Revenant:ReadProjection.
    .AddMarrowInboxConsumerService(builder.Configuration, "Revenant:ReadProjection", new MessagingTables("revenant"),
        RevenantEventProjection.ConsumerId, RevenantEventProjection.ApplyAsync);

var app = builder.Build();

// Hosts never run migrations; scripts/db/migrate.sh is the single executor.
app.MapGet("/", () => Results.Ok(new { service = "hosts-worker", status = "bootstrap" }));
app.MapMarrowHealth("hosts-worker");

app.Run();
return 0;
