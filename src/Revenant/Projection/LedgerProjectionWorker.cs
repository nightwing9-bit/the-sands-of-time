using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Revenant.Payments;

namespace Revenant.Projection;

public sealed class LedgerProjectionOptions
{
    public const string SectionName = "Revenant:Projection";

    public int IntervalMs { get; set; } = 1000;
}

/// <summary>
/// LiveOps loop that keeps every V2 run of the configured tenant projected into its Timeline source (task 7.5). It only catches up;
/// it never completes a source (<see cref="LedgerProjector.VerifySourceAsync"/> is an explicit operation once a run is closed for new
/// facts). A failure for one run is logged and retried on the next pass; the projection is idempotent, so nothing is lost or doubled.
/// </summary>
public sealed class LedgerProjectionWorker(
    LedgerProjector projector, RevenantLedgerOptions ledger, LedgerProjectionOptions options, ILogger<LedgerProjectionWorker> logger,
    ModelLedgerProjector? modelProjector = null)
    : BackgroundService
{
    public long Appended => Interlocked.Read(ref _appended);

    private long _appended;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var run in await projector.ListRunsAsync(ledger.TenantId, stoppingToken))
                {
                    var result = await projector.CatchUpAsync(ledger.TenantId, run, stoppingToken);
                    if (result.TryGetValue(out var status))
                    {
                        Interlocked.Add(ref _appended, status.Appended);
                    }
                    else if (result.Error.Code != ProjectionErrorCode.FactsAfterVerification)
                    {
                        logger.LogWarning("Revenant projection of run {RunId} failed: {Code}.", run, result.Error.Code);
                    }
                }
                // Spec 08 task 8.3: model-aware (V3) runs, when the host registered their engine shape.
                IReadOnlyList<Revenant.Contracts.ScenarioRunId> modelRuns =
                    modelProjector is null ? [] : await modelProjector.ListRunsAsync(ledger.TenantId, stoppingToken);
                foreach (var run in modelRuns)
                {
                    var result = await modelProjector!.CatchUpAsync(ledger.TenantId, run, stoppingToken);
                    if (result.TryGetValue(out var status))
                    {
                        Interlocked.Add(ref _appended, status.Appended);
                    }
                    else if (result.Error.Code != ProjectionErrorCode.FactsAfterVerification)
                    {
                        logger.LogWarning("Revenant model-aware projection of run {RunId} failed: {Code}.", run, result.Error.Code);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
            {
                logger.LogWarning("Revenant projection pass failed ({Error}); retrying.", (ex as PostgresException)?.SqlState ?? ex.GetType().Name);
            }
            try
            {
                await Task.Delay(Math.Max(50, options.IntervalMs), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
