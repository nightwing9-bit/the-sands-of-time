using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Marrow.Kernel.Canonical;
using Marrow.Kernel.Messaging;
using Marrow.Kernel.Results;
using Npgsql;
using NpgsqlTypes;
using Revenant.Contracts;
using Revenant.Finance;
using Revenant.Timeline;

namespace Revenant.Payments;

/// <summary>
/// Spec 07 task 7.3: holds, batch settlement and the compensating return.
///
/// Locking discipline (it is what makes "settle once, never overspend" hold under concurrency):
/// <list type="number">
/// <item>payment rows first: a transition or return takes its one payment <c>FOR UPDATE</c>; the settlement worker claims
/// Cleared payments <c>FOR UPDATE SKIP LOCKED</c>, so two workers never claim the same payment and never wait on each other's
/// claims. SKIP LOCKED is used only on this queue, never to skip a funds check;</item>
/// <item>then every account the transaction lowers or raises, <c>FOR NO KEY UPDATE</c> in database id order, in one
/// statement, followed by a separate statement that reads the committed available balance (READ COMMITTED takes a new
/// snapshot per statement, so the read sees everything committed before the lock was granted). There is no cached
/// balance anywhere.</item>
/// </list>
/// Account locks are always taken in one global order after the payment locks, and no transaction waits for a payment
/// lock while holding an account lock, so these paths cannot deadlock each other. PostgreSQL re-checks every hold and
/// credit leg under the same row lock (RV005) and the payment/hold/posting links at commit (RV003, RV006).
/// </summary>
public sealed partial class PaymentLedgerStore
{
    public const string EventHoldReserved = "revenant.hold.reserved";
    public const string EventHoldResolved = "revenant.hold.resolved";
    public const string EventLedgerPosted = "revenant.ledger.posted";
    public const string ReasonInsufficientAvailable = "insufficient_available";
    public const int MaxSettlementBatch = 500;

    // ---------------------------------------------------------------------------------------------------------
    // Settlement
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Claims up to <paramref name="maxPayments"/> Cleared or Approved (released, which first step to Cleared) payments of
    /// <paramref name="tenantId"/>, each also recorded as an ordered Settlement fact (task 7.5) (skipping any another worker
    /// holds), and settles them in one transaction, in run then run-local fact order. For each payment: when the source's
    /// available balance plus the payment's own reservation covers the amount, its hold (if any) is consumed and one balanced
    /// Settlement group (credit source, debit destination) is posted with the Settled step; otherwise the payment is Returned
    /// with reason <c>insufficient_available</c> and nothing is posted (the V1 rule). Every step writes its trail row and outbox
    /// events in the same transaction. Re-running after a crash or retry finds nothing left to settle for those payments.
    /// </summary>
    public async Task<SettlementBatch> SettleBatchAsync(
        string tenantId, string actor, int maxPayments, string correlationId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(actor) || !ValidCorrelation(correlationId)
            || maxPayments < 1 || maxPayments > MaxSettlementBatch)
        {
            throw new ArgumentException($"Tenant, actor, a correlation id and a batch size of 1-{MaxSettlementBatch} are required.");
        }
        return await TransientSql.RetryAsync(() => SettleBatchCoreAsync(tenantId, actor, maxPayments, correlationId, ct), ct);
    }

    private sealed record Claimed(
        Guid Id, Guid RunId, string IntentId, long Version, Guid SourceAccountId, Guid DestAccountId, string Currency, long AmountMinor,
        PaymentStatus Status);

    private async Task<SettlementBatch> SettleBatchCoreAsync(
        string tenantId, string actor, int maxPayments, string correlationId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await SetTenantAsync(connection, tx, tenantId, ct);

        var claimed = new List<Claimed>();
        await using (var cmd = Command(connection, tx,
            """
            SELECT p.id, p.scenario_run_id, p.intent_id, p.version, p.source_account_id, p.dest_account_id, p.currency, p.amount_minor,
                   p.status
              FROM revenant.payment p
              JOIN revenant.finance_fact f ON f.payment_id = p.id AND f.kind = 'PaymentIntent'
             WHERE p.status IN ('Cleared', 'Approved') AND NOT EXISTS (SELECT 1 FROM revenant.scenario_run r
                 WHERE r.id = p.scenario_run_id AND r.finance_version = 'revenant-finance-reducer/4')
             ORDER BY p.scenario_run_id, f.fact_seq
             LIMIT @n
               FOR UPDATE OF p SKIP LOCKED
            """, ("n", maxPayments)))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                claimed.Add(new Claimed(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetInt64(3),
                    reader.GetGuid(4), reader.GetGuid(5), reader.GetString(6), reader.GetInt64(7), Enum.Parse<PaymentStatus>(reader.GetString(8))));
            }
        }
        if (claimed.Count == 0)
        {
            await tx.RollbackAsync(CancellationToken.None);
            return new SettlementBatch([]);
        }

        var available = await LockAvailableAsync(connection, tx,
            [.. claimed.SelectMany(c => new[] { c.SourceAccountId, c.DestAccountId }).Distinct()], ct);
        var reserved = new Dictionary<Guid, long>();
        await using (var cmd = Command(connection, tx,
            "SELECT payment_id, amount_minor FROM revenant.hold WHERE payment_id = ANY(@ids) AND status = 'Active'"))
        {
            cmd.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = claimed.Select(c => c.Id).ToArray() });
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                reserved[reader.GetGuid(0)] = reader.GetInt64(1);
            }
        }

        var outbox = outboxFactory(tx);
        var outcomes = new List<SettlementOutcome>(claimed.Count);
        foreach (var payment in claimed)
        {
            outcomes.Add(await ApplySettlementAsync(connection, tx, outbox, tenantId, actor, payment, available,
                reserved.GetValueOrDefault(payment.Id), correlationId, ct));
        }

        await tx.CommitAsync(ct);
        return new SettlementBatch(outcomes);
    }

    private async Task<SettlementOutcome> ApplySettlementAsync(NpgsqlConnection connection, NpgsqlTransaction tx,
        IOutboxWriter outbox, string tenantId, string actor, Claimed payment, Dictionary<Guid, long> available,
        long own, string correlationId, CancellationToken ct, V4FinanceDispatch? dispatch = null)
    {
        SettlementOutcome outcome;
        var next = checked(payment.Version + 1);
        var key = $"settlement:{payment.Id:N}";
        if (payment.Status == PaymentStatus.Approved)
        {
            // A released payment clears in the same transaction that settles it (task 7.5), so release never waits on a second
            // manual step and the settlement fact covers both.
            await AppendStepAsync(connection, tx, outbox, tenantId, payment.RunId, payment.Id, payment.IntentId,
                PaymentStatus.Approved, PaymentStatus.Cleared, next, null, key, actor, correlationId, ct);
            next = checked(next + 1);
        }
        if (own > 0)
        {
            // The reservation is resolved first so the credit leg's RV005 check sees the funds it protected.
            available[payment.SourceAccountId] = checked(available[payment.SourceAccountId] + own);
        }
        if (available[payment.SourceAccountId] >= payment.AmountMinor)
        {
            if (own > 0)
            {
                await ResolveHoldAsync(connection, tx, outbox, payment.RunId, payment.Id, "Consumed", correlationId, ct);
            }
            var group = await PostGroupAsync(connection, tx, outbox, tenantId, payment.RunId, payment.Id, payment.Currency,
                "Settlement", null, credit: payment.SourceAccountId, debit: payment.DestAccountId, payment.AmountMinor, correlationId, ct);
            available[payment.SourceAccountId] -= payment.AmountMinor;
            available[payment.DestAccountId] = checked(available[payment.DestAccountId] + payment.AmountMinor);
            await AppendStepAsync(connection, tx, outbox, tenantId, payment.RunId, payment.Id, payment.IntentId,
                PaymentStatus.Cleared, PaymentStatus.Settled, next, null, key, actor, correlationId, ct);
            outcome = new SettlementOutcome(new ScenarioRunId(payment.RunId), payment.IntentId, payment.Id, PaymentStatus.Settled, next, group);
        }
        else
        {
            if (own > 0)
            {
                await ResolveHoldAsync(connection, tx, outbox, payment.RunId, payment.Id, "Released", correlationId, ct);
            }
            await AppendStepAsync(connection, tx, outbox, tenantId, payment.RunId, payment.Id, payment.IntentId,
                PaymentStatus.Cleared, PaymentStatus.Returned, next, ReasonInsufficientAvailable, key, actor, correlationId, ct);
            outcome = new SettlementOutcome(new ScenarioRunId(payment.RunId), payment.IntentId, payment.Id, PaymentStatus.Returned, next, null);
        }
        // The commit order of settlements is decided here, by whichever worker holds these rows; the ordered Settlement fact
        // records it so the V2 projection replays outcomes in their authoritative order (never input or claim order).
        await AppendFactAsync(connection, tx, tenantId, payment.RunId, payment.Id, FinanceFactKind.Settlement, PaymentStepCodec.SettlementSchema,
            PaymentStepCodec.Encode(payment.IntentId), actor, correlationId, next, ct, dispatch: dispatch);
        return outcome;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Compensating return
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Returns a Settled payment by posting a new Return group that mirrors its Settlement group (debit source, credit
    /// destination) together with the Settled -> Returned step; the original posting rows are never touched. Idempotent on
    /// <see cref="ReturnCommand.CommandId"/> (shared with transition command ids). A payment that is not Settled is
    /// <see cref="PaymentFailureCode.IllegalTransition"/>; a destination whose available balance no longer covers the amount
    /// is <see cref="PaymentFailureCode.InsufficientAvailable"/>; both are recorded as rejected command results.
    /// Who may return a payment (case workflow, four-eyes) is the caller's contract (task 7.4).
    /// </summary>
    public async Task<Result<TransitionReceipt, PaymentFailure>> ReturnSettledAsync(
        string tenantId, string actor, ReturnCommand command, string correlationId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        try
        {
            return await TransientSql.RetryAsync(() => ReturnSettledCoreAsync(tenantId, actor, command, correlationId, ct), ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return Fail<TransitionReceipt>(PaymentFailureCode.ConflictingDuplicate, "The command id is already in use.");
        }
    }

    private async Task<Result<TransitionReceipt, PaymentFailure>> ReturnSettledCoreAsync(
        string tenantId, string actor, ReturnCommand command, string correlationId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(actor) || !ValidCorrelation(correlationId)
            || command.RunId.IsEmpty || string.IsNullOrWhiteSpace(command.IntentId) || command.ExpectedVersion < 1
            || !ValidCommandId(command.CommandId)
            || (command.Reason is { } reason && (reason.Length > MaxReasonLength || reason.Length == 0)))
        {
            return Fail<TransitionReceipt>(PaymentFailureCode.InvalidRequest, "The return command is malformed.");
        }
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await SetTenantAsync(connection, tx, tenantId, ct);
        if (await IsV4RunAsync(connection, tx, command.RunId.Value, ct))
        {
            return Fail<TransitionReceipt>(PaymentFailureCode.InvalidRequest, "V4 requires its scheduler transaction.");
        }

        var result = await ApplyReturnAsync(connection, tx, tenantId, actor, command, correlationId, ct);
        await tx.CommitAsync(ct);
        return result;
    }

    private async Task<Result<TransitionReceipt, PaymentFailure>> ApplyReturnAsync(NpgsqlConnection connection,
        NpgsqlTransaction tx, string tenantId, string actor, ReturnCommand command, string correlationId,
        CancellationToken ct, V4FinanceDispatch? dispatch = null)
    {
        var run = command.RunId.Value;
        var commandKey = $"transition:{command.CommandId}";
        // Same request digest as a transition to Returned, so one command id names one request in either entry point.
        var hash = SHA256.HashData(new CanonicalWriter()
            .WriteGuid(run).WriteString(command.IntentId).WriteByte((byte)PaymentStatus.Returned)
            .WriteInt64(command.ExpectedVersion).WriteString(command.Reason ?? "").ToArray());

        var locked = await ReadPaymentForUpdateAsync(connection, tx, run, command.IntentId, ct);
        if (locked is null)
        {
            return Fail<TransitionReceipt>(PaymentFailureCode.PaymentNotFound, "The payment does not exist.");
        }
        var recorded = await ReadCommandAsync(connection, tx, run, commandKey, ct);
        if (recorded is not null)
        {
            if (!CryptographicOperations.FixedTimeEquals(recorded.RequestHash, hash))
            {
                return Fail<TransitionReceipt>(PaymentFailureCode.ConflictingDuplicate,
                    $"Command {command.CommandId} was already used for a different request.");
            }
            if (recorded.RejectCode is { } code)
            {
                return Fail<TransitionReceipt>(Enum.Parse<PaymentFailureCode>(code), "The command was rejected when first issued.");
            }
            var current = await ReadPaymentByIdAsync(connection, tx, locked.Id, ct)
                ?? throw new InvalidOperationException("A locked payment is not readable.");
            return Result<TransitionReceipt, PaymentFailure>.Success(
                new TransitionReceipt(current, recorded.ResultStatus, recorded.ResultVersion, IsReplay: true));
        }
        if (command.ExpectedVersion != locked.Version)
        {
            return Fail<TransitionReceipt>(PaymentFailureCode.StaleVersion,
                $"Expected version {command.ExpectedVersion}, but the payment is at version {locked.Version}.");
        }

        async Task<Result<TransitionReceipt, PaymentFailure>> RejectAsync(PaymentFailureCode code, string message)
        {
            await InsertCommandAsync(connection, tx, tenantId, run, commandKey, "Transition", locked.Id, hash, "Rejected",
                locked.Status, locked.Version, code.ToString(), actor, ct);
            return Fail<TransitionReceipt>(code, message);
        }

        if (locked.Status != PaymentStatus.Settled)
        {
            return await RejectAsync(PaymentFailureCode.IllegalTransition,
                $"Only a Settled payment is returned by a compensating posting; this payment is {locked.Status}.");
        }
        var settlement = await ScalarAsync<Guid>(connection, tx,
            "SELECT id FROM revenant.ledger_group WHERE payment_id = @p AND purpose = 'Settlement'", ct, ("p", locked.Id));
        var available = await LockAvailableAsync(connection, tx, [locked.SourceAccountId, locked.DestAccountId], ct);
        if (available[locked.DestAccountId] < locked.AmountMinor)
        {
            return await RejectAsync(PaymentFailureCode.InsufficientAvailable,
                "The destination account no longer has the available balance to return this payment.");
        }

        var outbox = outboxFactory(tx);
        await PostGroupAsync(connection, tx, outbox, tenantId, run, locked.Id, locked.Currency, "Return", settlement,
            credit: locked.DestAccountId, debit: locked.SourceAccountId, locked.AmountMinor, correlationId, ct);
        var next = checked(locked.Version + 1);
        await AppendStepAsync(connection, tx, outbox, tenantId, run, locked.Id, command.IntentId, PaymentStatus.Settled,
            PaymentStatus.Returned, next, command.Reason, commandKey, actor, correlationId, ct);
        // The return instruction is an externally committed fact (task 7.4): ordered under the run cursor, in this transaction.
        await AppendFactAsync(connection, tx, tenantId, run, locked.Id, FinanceFactKind.ReturnInstruction, ReturnInstructionCodec.SchemaVersion,
            ReturnInstructionCodec.Encode(new ReturnInstructionFact(command.IntentId, command.Reason ?? "unspecified")), actor, correlationId,
            next, ct, dispatch: dispatch);
        await InsertCommandAsync(connection, tx, tenantId, run, commandKey, "Transition", locked.Id, hash, "Accepted",
            PaymentStatus.Returned, next, null, actor, ct);
        var view = await ReadPaymentByIdAsync(connection, tx, locked.Id, ct)
            ?? throw new InvalidOperationException("A just-returned payment is not readable.");
        return Result<TransitionReceipt, PaymentFailure>.Success(new TransitionReceipt(view, PaymentStatus.Returned, next, IsReplay: false));
    }

    // ---------------------------------------------------------------------------------------------------------
    // Shared steps
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Locks the accounts <c>FOR NO KEY UPDATE</c> in database id order (one statement), then reads their committed
    /// available balances in a second statement. Callers must hold no account lock yet.
    /// </summary>
    private static async Task<Dictionary<Guid, long>> LockAvailableAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid[] accounts, CancellationToken ct)
    {
        var ids = accounts.Distinct().ToArray();
        await using (var cmd = Command(connection, tx,
            "SELECT id FROM revenant.account WHERE id = ANY(@ids) ORDER BY id FOR NO KEY UPDATE"))
        {
            cmd.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ids });
            var locked = 0;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                locked++;
            }
            if (locked != ids.Length)
            {
                throw new InvalidOperationException("An account of a locked payment is not visible in this tenant context.");
            }
        }
        var balances = new Dictionary<Guid, long>(ids.Length);
        await using (var cmd = Command(connection, tx,
            "SELECT account_id, available_minor FROM revenant.account_available_balance WHERE account_id = ANY(@ids)"))
        {
            cmd.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ids });
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                balances[reader.GetGuid(0)] = reader.GetInt64(1);
            }
        }
        return balances;
    }

    private async Task ReserveAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, IOutboxWriter outbox, string tenantId, Guid run, LockedPayment payment,
        string correlationId, CancellationToken ct)
    {
        var holdId = await ScalarAsync<Guid>(connection, tx,
            """
            INSERT INTO revenant.hold (tenant_id, scenario_run_id, payment_id, account_id, currency, amount_minor)
            VALUES (@tenant, @run, @payment, @account, @currency, @amount) RETURNING id
            """, ct,
            ("tenant", tenantId), ("run", run), ("payment", payment.Id), ("account", payment.SourceAccountId),
            ("currency", payment.Currency), ("amount", payment.AmountMinor));
        await outbox.EnqueueAsync(HoldEvent(EventHoldReserved, run, holdId, payment.Id, "Active", payment.AmountMinor, correlationId), ct);
    }

    /// <summary>Resolves the payment's Active hold, if it has one, exactly once (the UPDATE matches only an Active row).</summary>
    private async Task ResolveHoldAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, IOutboxWriter outbox, Guid run, Guid paymentId, string resolution,
        string correlationId, CancellationToken ct)
    {
        await using var cmd = Command(connection, tx,
            """
            UPDATE revenant.hold SET status = @status, resolved_at = now()
             WHERE payment_id = @payment AND status = 'Active'
            RETURNING id, amount_minor
            """, ("status", resolution), ("payment", paymentId));
        Guid holdId;
        long amount;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct))
            {
                return;
            }
            (holdId, amount) = (reader.GetGuid(0), reader.GetInt64(1));
        }
        await outbox.EnqueueAsync(HoldEvent(EventHoldResolved, run, holdId, paymentId, resolution, amount, correlationId), ct);
    }

    private async Task<Guid> PostGroupAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, IOutboxWriter outbox, string tenantId, Guid run, Guid paymentId,
        string currency, string purpose, Guid? compensates, Guid credit, Guid debit, long amount, string correlationId, CancellationToken ct)
    {
        var group = await ScalarAsync<Guid>(connection, tx,
            """
            INSERT INTO revenant.ledger_group (tenant_id, scenario_run_id, payment_id, currency, purpose, compensates_group_id)
            VALUES (@tenant, @run, @payment, @currency, @purpose, @compensates) RETURNING id
            """, ct,
            ("tenant", tenantId), ("run", run), ("payment", paymentId), ("currency", currency), ("purpose", purpose),
            ("compensates", compensates is { } c ? c : DBNull.Value));
        foreach (var (account, direction) in new[] { (credit, "Credit"), (debit, "Debit") })
        {
            await ExecAsync(connection, tx,
                """
                INSERT INTO revenant.ledger_entry (group_id, tenant_id, scenario_run_id, account_id, currency, direction, amount_minor)
                VALUES (@group, @tenant, @run, @account, @currency, @direction, @amount)
                """, ct,
                ("group", group), ("tenant", tenantId), ("run", run), ("account", account), ("currency", currency),
                ("direction", direction), ("amount", amount));
        }
        var payload = JsonSerializer.Serialize(new { runId = run, paymentId, groupId = group, purpose, currency, amountMinor = amount, compensatesGroupId = compensates });
        await outbox.EnqueueAsync(new OutboxMessage(
            DeterministicId($"revenant.ledger/{group:N}"), EventLedgerPosted, 1, $"ledger:{group:N}", clock.GetUtcNow(), payload, correlationId), ct);
        return group;
    }

    /// <summary>One version step: the guarded UPDATE (legal edge, version + 1), its trail row and its outbox event.</summary>
    private async Task AppendStepAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, IOutboxWriter outbox, string tenantId, Guid run, Guid paymentId,
        string intentId, PaymentStatus from, PaymentStatus to, long version, string? reason, string commandKey, string actor,
        string correlationId, CancellationToken ct)
    {
        var updated = await ExecAsync(connection, tx,
            "UPDATE revenant.payment SET status = @status, version = @next WHERE id = @id AND version = @expected AND status = @from", ct,
            ("status", to.ToString()), ("next", version), ("id", paymentId), ("expected", version - 1), ("from", from.ToString()));
        if (updated != 1)
        {
            throw new InvalidOperationException("The locked payment row was not updated exactly once.");
        }
        await ExecAsync(connection, tx,
            """
            INSERT INTO revenant.payment_transition (tenant_id, scenario_run_id, payment_id, version, from_status, to_status, reason, command_key, actor)
            VALUES (@tenant, @run, @payment, @version, @from, @to, @reason, @key, @actor)
            """, ct,
            ("tenant", tenantId), ("run", run), ("payment", paymentId), ("version", version), ("from", from.ToString()),
            ("to", to.ToString()), ("reason", (object?)reason ?? DBNull.Value), ("key", commandKey), ("actor", actor));
        await outbox.EnqueueAsync(PaymentEvent(EventPaymentTransitioned, run, paymentId, intentId, from, to, version, null, correlationId), ct);
    }

    private OutboxMessage HoldEvent(
        string eventType, Guid run, Guid holdId, Guid paymentId, string status, long amount, string correlationId)
    {
        var payload = JsonSerializer.Serialize(new { runId = run, holdId, paymentId, status, amountMinor = amount });
        return new OutboxMessage(
            DeterministicId($"revenant.hold/{holdId:N}/{status}"), eventType, 1, $"hold:{holdId:N}", clock.GetUtcNow(), payload, correlationId);
    }

    private static Guid DeterministicId(string name) => new(SHA256.HashData(Encoding.UTF8.GetBytes(name))[..16]);
}
