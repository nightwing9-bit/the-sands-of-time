using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Revenant.Payments;

/// <summary>Settings of the LiveOps screening loop (<c>Revenant:Screening</c>).</summary>
public sealed class ScreeningWorkerOptions
{
    public const string SectionName = "Revenant:Screening";

    /// <summary>Payments screened per pass (each in its own transaction).</summary>
    public int BatchSize { get; set; } = 50;

    public int IdleDelayMs { get; set; } = 500;

    public int MaxBackoffMs { get; set; } = 30_000;
}

/// <summary>
/// The LiveOps screening loop of the Worker host (task 7.5): the production path from Initiated into Screened and then Held,
/// Cleared or Rejected, through <see cref="PaymentLedgerStore.ScreenBatchAsync"/>. It acts only for the tenant in server
/// configuration (<c>Revenant:Ledger:TenantId</c>) as the system actor <see cref="PaymentLedgerStore.ScreeningActor"/>; no HTTP
/// caller can choose a screening outcome. A database outage leaves committed screenings intact and is retried with bounded backoff.
/// </summary>
public sealed class ScreeningWorker(
    PaymentLedgerStore store,
    RevenantLedgerOptions ledger,
    ScreeningWorkerOptions options,
    ILogger<ScreeningWorker> logger) : BackgroundService
{
    public long Screened => Interlocked.Read(ref _screened);

    private long _screened;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batchSize = Math.Clamp(options.BatchSize, 1, PaymentLedgerStore.MaxSettlementBatch);
        var backoff = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            int count;
            try
            {
                var outcomes = await store.ScreenBatchAsync(ledger.TenantId, batchSize, $"screening-{Guid.NewGuid():N}", stoppingToken);
                count = outcomes.Count;
                Interlocked.Add(ref _screened, count);
                backoff = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is NpgsqlException or TimeoutException or InvalidOperationException { InnerException: NpgsqlException })
            {
                backoff = backoff == 0 ? 100 : Math.Min(backoff * 2, Math.Max(100, options.MaxBackoffMs));
                logger.LogWarning("Revenant screening pass failed ({SqlState}); retrying in {BackoffMs} ms.",
                    (ex as PostgresException)?.SqlState ?? ex.GetType().Name, backoff);
                if (!await DelayAsync(backoff, stoppingToken))
                {
                    return;
                }
                continue;
            }
            if (count < batchSize && !await DelayAsync(Math.Max(10, options.IdleDelayMs), stoppingToken))
            {
                return;
            }
        }
    }

    private static async Task<bool> DelayAsync(int milliseconds, CancellationToken ct)
    {
        try
        {
            await Task.Delay(milliseconds, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
