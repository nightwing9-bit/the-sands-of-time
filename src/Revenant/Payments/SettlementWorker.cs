using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Revenant.Payments;

/// <summary>Settings of the LiveOps settlement loop (<c>Revenant:Settlement</c>).</summary>
public sealed class SettlementWorkerOptions
{
    public const string SectionName = "Revenant:Settlement";

    /// <summary>Payments claimed per transaction (1..<see cref="PaymentLedgerStore.MaxSettlementBatch"/>).</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>Wait after an empty queue.</summary>
    public int IdleDelayMs { get; set; } = 1000;

    /// <summary>Upper bound of the exponential backoff after a database failure.</summary>
    public int MaxBackoffMs { get; set; } = 30_000;
}

/// <summary>
/// The LiveOps settlement loop of the Worker host. It settles the configured tenant's Cleared payments through
/// <see cref="PaymentLedgerStore.SettleBatchAsync"/> until the queue is empty, then waits. Several instances may run against
/// one database: the queue claim is <c>FOR UPDATE SKIP LOCKED</c> and every effect is guarded by the payment row lock and
/// PostgreSQL's commit-time checks. A database outage (connection refused, terminated session, failed login) never ends the
/// loop: the batch's transaction was rolled back or committed as a whole, so the loop waits with bounded exponential
/// backoff and claims again; a committed batch is not claimable twice. The tenant comes from server configuration only.
/// </summary>
public sealed class SettlementWorker(
    PaymentLedgerStore store,
    RevenantLedgerOptions ledger,
    SettlementWorkerOptions options,
    ILogger<SettlementWorker> logger) : BackgroundService
{
    public const string Actor = "system:settlement-worker";

    /// <summary>Payments resolved by this instance since start (observability and tests).</summary>
    public long Resolved => Interlocked.Read(ref _resolved);

    /// <summary>Database failures this instance recovered from since start.</summary>
    public long Failures => Interlocked.Read(ref _failures);

    private long _resolved;
    private long _failures;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batchSize = Math.Clamp(options.BatchSize, 1, PaymentLedgerStore.MaxSettlementBatch);
        var backoff = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            int count;
            try
            {
                var batch = await store.SettleBatchAsync(ledger.TenantId, Actor, batchSize, $"settlement-{Guid.NewGuid():N}", stoppingToken);
                count = batch.Outcomes.Count;
                Interlocked.Add(ref _resolved, count);
                backoff = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is NpgsqlException or TimeoutException or InvalidOperationException { InnerException: NpgsqlException })
            {
                Interlocked.Increment(ref _failures);
                backoff = backoff == 0 ? 100 : Math.Min(backoff * 2, Math.Max(100, options.MaxBackoffMs));
                logger.LogWarning("Revenant settlement batch failed ({SqlState}); retrying in {BackoffMs} ms.",
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
