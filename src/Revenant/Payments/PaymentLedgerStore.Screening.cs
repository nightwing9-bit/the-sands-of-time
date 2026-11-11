using Revenant.Contracts;
using Revenant.Finance;
using Revenant.Scenario;
using Revenant.Timeline;

namespace Revenant.Payments;

/// <summary>Result of screening one payment: its status and version after the step, and the hold reason when it was held.</summary>
public sealed record ScreeningOutcome(ScenarioRunId RunId, string IntentId, Guid PaymentId, PaymentStatus Status, long Version, string? Reason, long FactSequence);

/// <summary>
/// Spec 07 task 7.5: the production entry point into Held. The LiveOps screening step (run by <see cref="ScreeningWorker"/>) takes
/// the oldest Initiated payment of the tenant <c>FOR UPDATE SKIP LOCKED</c>, computes its payer velocity from the run's earlier
/// <see cref="FinanceFactKind.PaymentIntent"/> facts, applies the pinned live policy (<see cref="LedgerFinanceContext.LivePolicy"/>)
/// exactly as <see cref="FinanceLedgerReducer"/> does, and commits in ONE transaction: Initiated → Screened, then Cleared, or Held
/// with its reservation and review case, or Held → Rejected when the source cannot cover the reservation; the trail rows, outbox
/// events, the ordered <see cref="FinanceFactKind.Screening"/> fact and its audit link. A source account whose availability the step
/// reads is locked before the run cursor, so fact order agrees with every availability change.
/// </summary>
public sealed partial class PaymentLedgerStore
{
    public const string ScreeningActor = "system:screening";

    public async Task<IReadOnlyList<ScreeningOutcome>> ScreenBatchAsync(
        string tenantId, int maxPayments, string correlationId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || !ValidCorrelation(correlationId) || maxPayments < 1 || maxPayments > MaxSettlementBatch)
        {
            throw new ArgumentException($"Tenant, a correlation id and a batch size of 1-{MaxSettlementBatch} are required.");
        }
        var outcomes = new List<ScreeningOutcome>();
        for (var i = 0; i < maxPayments; i++)
        {
            var outcome = await TransientSql.RetryAsync(() => ScreenOneAsync(tenantId, correlationId, ct), ct);
            if (outcome is null)
            {
                break;
            }
            outcomes.Add(outcome);
        }
        return outcomes;
    }

    private async Task<ScreeningOutcome?> ScreenOneAsync(string tenantId, string correlationId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await SetTenantAsync(connection, tx, tenantId, ct);

        LockedPayment payment;
        Guid run;
        string intentId;
        long intentFact;
        byte[] canonical;
        await using (var cmd = Command(connection, tx,
            """
            SELECT p.id, p.scenario_run_id, p.intent_id, p.version, p.source_account_id, p.dest_account_id, p.currency, p.amount_minor,
                   f.fact_seq, f.canonical
              FROM revenant.payment p
              JOIN revenant.finance_fact f ON f.payment_id = p.id AND f.kind = 'PaymentIntent'
             WHERE p.status = 'Initiated'
             ORDER BY p.scenario_run_id, f.fact_seq
             LIMIT 1
               FOR UPDATE OF p SKIP LOCKED
            """))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct))
            {
                return null;
            }
            payment = new LockedPayment(reader.GetGuid(0), PaymentStatus.Initiated, reader.GetInt64(3), reader.GetGuid(4), reader.GetGuid(5),
                reader.GetString(6), reader.GetInt64(7));
            (run, intentId, intentFact, canonical) = (reader.GetGuid(1), reader.GetString(2), reader.GetInt64(8), reader.GetFieldValue<byte[]>(9));
        }
        var intent = PaymentIntentCodec.Instance.Decode(canonical);

        // Payer velocity: the earlier intents of the same source account in this run, by fact order (as the reducer counts them).
        var earlierTimes = new List<long>();
        await using (var cmd = Command(connection, tx,
            """
            SELECT f.canonical FROM revenant.finance_fact f JOIN revenant.payment q ON q.id = f.payment_id
             WHERE f.scenario_run_id = @run AND f.kind = 'PaymentIntent' AND q.source_account_id = @source AND f.fact_seq < @seq
            """, ("run", run), ("source", payment.SourceAccountId), ("seq", intentFact)))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                earlierTimes.Add(PaymentIntentCodec.Instance.Decode(reader.GetFieldValue<byte[]>(0)).EventTimeUtcMs);
            }
        }
        var policy = LedgerFinanceContext.LivePolicy;
        var velocity = FinanceLedgerReducer.Velocity(earlierTimes, intent.EventTimeUtcMs, policy);
        var reason = FinanceLedgerReducer.ScreeningReason(intent, velocity, policy);

        var outbox = outboxFactory(tx);
        var key = $"screening:{payment.Id:N}";
        var version = checked(payment.Version + 1);
        await AppendStepAsync(connection, tx, outbox, tenantId, run, payment.Id, intentId, PaymentStatus.Initiated, PaymentStatus.Screened,
            version, null, key, ScreeningActor, correlationId, ct);
        PaymentStatus status;
        string? holdReason = null;
        if (reason == PaymentReason.None)
        {
            version = checked(version + 1);
            status = PaymentStatus.Cleared;
            await AppendStepAsync(connection, tx, outbox, tenantId, run, payment.Id, intentId, PaymentStatus.Screened, PaymentStatus.Cleared,
                version, null, key, ScreeningActor, correlationId, ct);
        }
        else
        {
            holdReason = ReasonText(reason);
            var available = await LockAvailableAsync(connection, tx, [payment.SourceAccountId], ct);
            var fits = available[payment.SourceAccountId] >= payment.AmountMinor;
            if (fits)
            {
                await ReserveAsync(connection, tx, outbox, tenantId, run, payment, correlationId, ct);
                await OpenCaseAsync(connection, tx, outbox, tenantId, run, payment, correlationId, ct);
            }
            version = checked(version + 1);
            status = PaymentStatus.Held;
            await AppendStepAsync(connection, tx, outbox, tenantId, run, payment.Id, intentId, PaymentStatus.Screened, PaymentStatus.Held,
                version, holdReason, key, ScreeningActor, correlationId, ct);
            if (!fits)
            {
                version = checked(version + 1);
                status = PaymentStatus.Rejected;
                await AppendStepAsync(connection, tx, outbox, tenantId, run, payment.Id, intentId, PaymentStatus.Held, PaymentStatus.Rejected,
                    version, ReasonInsufficientAvailable, key, ScreeningActor, correlationId, ct);
            }
        }
        var fact = await AppendFactAsync(connection, tx, tenantId, run, payment.Id, FinanceFactKind.Screening, PaymentStepCodec.ScreeningSchema,
            PaymentStepCodec.Encode(intentId), ScreeningActor, correlationId, version, ct);
        await tx.CommitAsync(ct);
        return new ScreeningOutcome(new ScenarioRunId(run), intentId, payment.Id, status, version, holdReason, fact);
    }

    private static string ReasonText(PaymentReason reason) => reason switch
    {
        PaymentReason.SanctionsHit => "sanctions_hit",
        PaymentReason.VelocityRule => "velocity_rule",
        PaymentReason.NewBeneficiaryAmountRule => "new_beneficiary_amount_rule",
        _ => reason.ToString(),
    };
}
