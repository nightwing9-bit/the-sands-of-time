using Marrow.Kernel.Results;
using Npgsql;
using Revenant.Contracts;

namespace Revenant.Payments;

/// <summary>The run's closing point: no financial fact with a higher sequence can ever commit.</summary>
public sealed record RunClosure(ScenarioRunId RunId, long ClosedFactSequence, bool IsReplay);

/// <summary>
/// Spec 07 exit review: run closure. A run is verified against its Timeline source only after it is closed for new financial
/// facts, so the verified source and the live ledger describe the same, final set of facts. Closing serializes on the run cursor
/// row (the row every fact append advances): an action that commits first is part of the run; one that arrives later is refused
/// by PostgreSQL (RV009, mapped to <see cref="PaymentFailureCode.RunClosed"/>) and leaves no fact, step, posting or outbox event.
/// </summary>
public sealed partial class PaymentLedgerStore
{
    public const string AuditRunClosed = "run.closed";

    /// <summary>
    /// Closes <paramref name="runId"/> at its last committed fact. Refused with <see cref="PaymentFailureCode.RunNotQuiescent"/>
    /// while any payment is still in a LiveOps-owned state (Initiated, Screened, Approved or Cleared): those must still produce
    /// ordered facts, so they would be cut off. Held payments (awaiting review) and terminal ones do not block closing; a held
    /// payment of a closed run stays Held (no decision can be recorded). Idempotent: closing again returns the recorded point.
    /// </summary>
    public async Task<Result<RunClosure, PaymentFailure>> CloseRunAsync(
        string tenantId, string actor, ScenarioRunId runId, string correlationId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(actor) || runId.IsEmpty || !ValidCorrelation(correlationId))
        {
            return Fail<RunClosure>(PaymentFailureCode.InvalidRequest, "Tenant, actor, run and a correlation id of 1-200 characters are required.");
        }
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await SetTenantAsync(connection, tx, tenantId, ct);

        // The lock every fact append needs: in-flight actions finish first, later ones see the closed cursor.
        if (await ReadCursorForUpdateAsync(connection, tx, runId.Value, ct) is null)
        {
            return Fail<RunClosure>(PaymentFailureCode.RunNotFound, "The run does not exist.");
        }
        var closed = await ScalarAsync<long?>(connection, tx,
            "SELECT closed_fact_seq FROM revenant.run_cursor WHERE scenario_run_id = @run", ct, ("run", runId.Value));
        if (closed is { } already)
        {
            return Result<RunClosure, PaymentFailure>.Success(new RunClosure(runId, already, IsReplay: true));
        }
        var inFlight = await ScalarAsync<long>(connection, tx,
            """
            SELECT count(*) FROM revenant.payment
             WHERE scenario_run_id = @run AND status IN ('Initiated', 'Screened', 'Approved', 'Cleared')
            """, ct, ("run", runId.Value));
        if (inFlight > 0)
        {
            return Fail<RunClosure>(PaymentFailureCode.RunNotQuiescent,
                $"{inFlight} payment(s) are still Initiated, Screened, Approved or Cleared; let LiveOps finish them before closing the run.");
        }
        var point = await ScalarAsync<long>(connection, tx,
            """
            UPDATE revenant.run_cursor SET closed_fact_seq = last_fact_seq
             WHERE scenario_run_id = @run RETURNING closed_fact_seq
            """, ct, ("run", runId.Value));
        await AppendAuditAsync(connection, tx, tenantId, runId.Value, AuditRunClosed, actor, correlationId, $"fact:{point}", null, null, ct);
        await tx.CommitAsync(ct);
        return Result<RunClosure, PaymentFailure>.Success(new RunClosure(runId, point, IsReplay: false));
    }

    /// <summary>True when <paramref name="ex"/> is the database refusing a write into a closed run.</summary>
    public static bool IsRunClosed(PostgresException ex) => ex.SqlState == "RV009";
}
