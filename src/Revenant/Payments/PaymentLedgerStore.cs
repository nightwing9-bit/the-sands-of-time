using Revenant.Timeline;
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
using Revenant.Intelligence;
using Revenant.Scenario;

namespace Revenant.Payments;

/// <summary>
/// The durable payment machine and idempotent intake over the <c>revenant</c> schema. One command is one PostgreSQL
/// transaction that carries its state step, trail row, canonical fact, command record and outbox event together:
/// either all of it commits or none of it does, and a repeat of the same command is answered from the recorded result
/// without a second effect. Tenant isolation uses the transaction-local setting <c>revenant.tenant_id</c> (RLS), so a
/// pooled connection never carries a previous caller's tenant. The tenant passed in is resolved by the server from
/// persisted run state, never from a request.
///
/// This type decides nothing about who may call it: <see cref="RevenantLedgerService"/> authorizes first.
/// It runs as the least-privileged <c>revenant_ledger_writer</c> role (SELECT/INSERT, UPDATE of payment status/version
/// and the run cursor only), so even a bug here cannot rewrite a trail, fact, command or posting row.
/// </summary>
public sealed partial class PaymentLedgerStore(
    NpgsqlDataSource dataSource,
    Func<NpgsqlTransaction, IOutboxWriter> outboxFactory,
    TimeProvider clock,
    RiskModelProvider? riskModels = null)
{
    public const string FinanceVersion = "revenant-finance-reducer/2";

    /// <summary>
    /// Spec 08 task 8.3: the model-required finance family (<see cref="Revenant.Timeline.ModelLedgerVersion.ReducerVersion"/>), chosen
    /// explicitly when a run is opened and pinned to risk policy <see cref="RiskPolicyCatalog.V1Id"/>. Never switched after opening.
    /// </summary>
    public const string ModelAwareFinanceVersion = "revenant-finance-reducer/3";
    public const int MaxCommandIdLength = 128;
    public const int MaxReasonLength = 200;
    public const string EventPaymentInitiated = "revenant.payment.initiated";
    public const string EventPaymentTransitioned = "revenant.payment.transitioned";

    // ---------------------------------------------------------------------------------------------------------
    // Runs
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Creates the run directory entry, its six banks, their accounts with the opening balances, and the fact cursor in
    /// one transaction. Every row is keyed by <paramref name="runId"/>, so two runs with the same seed hold equal logical
    /// ids and equal balances in disjoint namespaces and neither ever touches the other. Idempotent on the run id:
    /// the same owner and seed replay; another owner is <see cref="PaymentFailureCode.Forbidden"/>; another seed conflicts.
    /// </summary>
    public async Task<Result<RunReceipt, PaymentFailure>> OpenRunAsync(
        string tenantId, string ownerSubjectId, string ownerRole, long seed, ScenarioRunId runId, CancellationToken ct,
        string financeVersion = FinanceVersion)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(ownerSubjectId) || runId.IsEmpty
            || ownerRole is not ("operator" or "supervisor") || financeVersion is not (FinanceVersion or ModelAwareFinanceVersion))
        {
            return Fail<RunReceipt>(PaymentFailureCode.InvalidRequest, "Tenant, owner, role, run id and a supported finance version are required.");
        }
        var created = FridayRingScenario.Create(seed, ScenarioSchemas.FridayRingVersion, runId);
        if (!created.TryGetValue(out var run))
        {
            return Fail<RunReceipt>(PaymentFailureCode.InvalidRequest, created.Error.Message);
        }

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await SetTenantAsync(connection, tx, tenantId, ct);

        var inserted = await ScalarAsync<int?>(connection, tx,
            """
            INSERT INTO revenant.scenario_run (id, tenant_id, owner_subject_id, seed, scenario_version, manifest_hash, finance_version)
            VALUES (@id, @tenant, @owner, @seed, @version, @hash, @finance)
            ON CONFLICT (id) DO NOTHING
            RETURNING 1
            """, ct,
            ("id", runId.Value), ("tenant", tenantId), ("owner", ownerSubjectId), ("seed", seed),
            ("version", run.Manifest.ScenarioVersion), ("hash", run.Manifest.ContentHash), ("finance", financeVersion));

        if (inserted is null)
        {
            // The run id is taken (the conflicting insert waited for the other transaction's outcome).
            var existing = await ReadRunAsync(connection, tx, runId, ct);
            await tx.RollbackAsync(CancellationToken.None);
            if (existing is null || !string.Equals(existing.OwnerSubjectId, ownerSubjectId, StringComparison.Ordinal)
                || !string.Equals(existing.TenantId, tenantId, StringComparison.Ordinal))
            {
                return Fail<RunReceipt>(PaymentFailureCode.Forbidden, "This run is not available to the caller.");
            }
            return existing.Seed == seed && string.Equals(existing.FinanceVersion, financeVersion, StringComparison.Ordinal)
                ? Result<RunReceipt, PaymentFailure>.Success(new RunReceipt(existing, IsReplay: true))
                : Fail<RunReceipt>(PaymentFailureCode.ConflictingDuplicate, "The run id is already used with another seed or finance version.");
        }

        await ExecAsync(connection, tx,
            "INSERT INTO revenant.run_cursor (scenario_run_id, tenant_id) VALUES (@run, @tenant)", ct,
            ("run", runId.Value), ("tenant", tenantId));

        _ = await InsertOpeningAccountsAsync(connection, tx, tenantId, ownerSubjectId, ownerRole, runId.Value, run, null, ct);

        var view = await ReadRunAsync(connection, tx, runId, ct)
            ?? throw new InvalidOperationException("The run row written in this transaction is not visible.");
        await tx.CommitAsync(ct);
        return Result<RunReceipt, PaymentFailure>.Success(new RunReceipt(view, IsReplay: false));
    }

    internal static async Task<Dictionary<string, Guid>> InsertOpeningAccountsAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, string tenantId, string ownerSubjectId, string ownerRole,
        Guid execution, ScenarioRun run, LedgerFinanceState? snapshot, CancellationToken ct)
    {
        await ExecAsync(connection, tx,
            "INSERT INTO revenant.scenario_run_staff (scenario_run_id, subject_id, role) VALUES (@run, @subject, @role)", ct,
            ("run", execution), ("subject", ownerSubjectId), ("role", ownerRole));
        var bankIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var bank in run.Manifest.Banks)
        {
            bankIds[bank.BankId] = await ScalarAsync<Guid>(connection, tx,
                """
                INSERT INTO revenant.bank (tenant_id, scenario_run_id, local_bank_id, name)
                VALUES (@tenant, @run, @bank, @name) RETURNING id
                """, ct,
                ("tenant", tenantId), ("run", execution), ("bank", bank.BankId), ("name", bank.Name));
        }
        var accountIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var account in run.Manifest.Accounts)
        {
            accountIds[account.AccountId] = await ScalarAsync<Guid>(connection, tx,
                """
                INSERT INTO revenant.account (tenant_id, scenario_run_id, bank_id, local_account_id, currency, opening_balance_minor)
                VALUES (@tenant, @run, @bank, @account, @currency, @opening) RETURNING id
                """, ct,
                ("tenant", tenantId), ("run", execution), ("bank", bankIds[account.BankId]), ("account", account.AccountId),
                ("currency", account.OpeningBalance.Currency), ("opening", snapshot?.Accounts[account.AccountId].PostedMinor ?? account.OpeningBalance.MinorUnits));
        }
        return accountIds;
    }

    public async Task<RunView?> GetRunAsync(string tenantId, ScenarioRunId runId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await SetTenantAsync(connection, tx, tenantId, ct);
        var view = await ReadRunAsync(connection, tx, runId, ct);
        await tx.CommitAsync(ct);
        // The run directory is not row-level-secured, so another tenant's run is hidden here, like every tenant table.
        return view is not null && string.Equals(view.TenantId, tenantId, StringComparison.Ordinal) ? view : null;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Intake
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Accepts one exogenous intent into <paramref name="intent"/>'s run: one Initiated payment, one trail row, one ordered
    /// canonical fact, one command record and one outbox event, atomically. Idempotent on <c>(ScenarioRunId, intentId)</c>:
    /// the same facts replay the recorded receipt with no new effect; different facts under the same id are
    /// <see cref="PaymentFailureCode.ConflictingDuplicate"/>. Intake into one run is serialized on the run cursor, and a
    /// new intent must carry a higher logical sequence and a business time not earlier than the last accepted one.
    /// </summary>
    public async Task<Result<IntakeReceipt, PaymentFailure>> SubmitIntentAsync(
        string tenantId, string actor, PaymentIntent intent, string correlationId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(actor) || !ValidCorrelation(correlationId))
        {
            return Fail<IntakeReceipt>(PaymentFailureCode.InvalidRequest, "Tenant, actor and a correlation id of 1-200 characters are required.");
        }
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await SetTenantAsync(connection, tx, tenantId, ct);
        var result = await SubmitIntentCoreAsync(connection, tx, tenantId, actor, intent, correlationId, ct);
        if (result.IsSuccess)
        {
            await tx.CommitAsync(ct);
        }
        return result;
    }

    private async Task<Result<IntakeReceipt, PaymentFailure>> SubmitIntentCoreAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, string tenantId, string actor, PaymentIntent intent,
        string correlationId, CancellationToken ct, V4FinanceDispatch? dispatch = null)
    {
        var canonical = PaymentIntentCodec.Instance.EncodeCanonical(intent);
        var hash = SHA256.HashData(canonical);
        var commandKey = $"intake:{intent.IntentId}";
        var run = dispatch?.Execution.Value ?? intent.RunId.Value;

        // Serializes intake into this run: the lock is held to commit, so fact order is commit order.
        var cursor = await ReadCursorForUpdateAsync(connection, tx, run, ct);
        if (cursor is null)
        {
            return Fail<IntakeReceipt>(PaymentFailureCode.RunNotFound, "The run does not exist.");
        }

        if ((cursor.FinanceVersion == ObservedFinanceVersion.ReducerVersion) != (dispatch is not null))
        {
            return Fail<IntakeReceipt>(PaymentFailureCode.InvalidRequest, "V4 intake requires its scheduler transaction.");
        }

        var recorded = await ReadCommandAsync(connection, tx, run, commandKey, ct);
        if (recorded is not null)
        {
            if (!CryptographicOperations.FixedTimeEquals(recorded.RequestHash, hash))
            {
                return Fail<IntakeReceipt>(PaymentFailureCode.ConflictingDuplicate,
                    $"Intent {intent.IntentId} was already accepted with different facts.");
            }
            var existing = await ReadPaymentByIdAsync(connection, tx, recorded.PaymentId, ct)
                ?? throw new InvalidOperationException("A recorded intake command has no payment.");
            var factSeq = await ScalarAsync<long>(connection, tx,
                "SELECT fact_seq FROM revenant.finance_fact WHERE payment_id = @p AND kind = 'PaymentIntent'", ct, ("p", recorded.PaymentId));
            return Result<IntakeReceipt, PaymentFailure>.Success(new IntakeReceipt(existing, IsReplay: true, factSeq));
        }

        if (intent.Sequence <= cursor.LastIntentSequence || intent.EventTimeUtcMs < cursor.LastEventTimeMs)
        {
            return Fail<IntakeReceipt>(PaymentFailureCode.OutOfOrderInput,
                $"Intent {intent.IntentId} (sequence {intent.Sequence}, time {intent.EventTimeUtcMs}) does not follow the last accepted intent " +
                $"(sequence {cursor.LastIntentSequence}, time {cursor.LastEventTimeMs}).");
        }

        var accounts = await ReadAccountsAsync(connection, tx, run, [intent.SourceAccountId, intent.DestAccountId], ct);
        if (!accounts.TryGetValue(intent.SourceAccountId, out var source) || source.BankId != intent.SourceBankId
            || !accounts.TryGetValue(intent.DestAccountId, out var dest) || dest.BankId != intent.DestBankId)
        {
            return Fail<IntakeReceipt>(PaymentFailureCode.UnknownAccount, "An account or bank named by the intent is not part of this run.");
        }
        if (!string.Equals(source.Currency, intent.Amount.Currency, StringComparison.Ordinal)
            || !string.Equals(dest.Currency, intent.Amount.Currency, StringComparison.Ordinal))
        {
            return Fail<IntakeReceipt>(PaymentFailureCode.UnsupportedCurrency,
                "The amount currency differs from the account currency; there is no FX path.");
        }

        if (cursor.FinanceVersion == ModelAwareFinanceVersion)
        {
            // Postings commit with a run fact under this cursor. Read without an account row lock to preserve the
            // account-before-cursor lock order. Screening rechecks the posted balance at its own later boundary.
            var posted = await ScalarAsync<long>(connection, tx,
                "SELECT posted_minor FROM revenant.account_available_balance WHERE account_id = @a", ct, ("a", source.AccountId));
            var features = RiskFeaturesV1.Build(intent.Amount.Currency, intent.Amount.MinorUnits, posted);
            if (features.IsFailure)
            {
                return Fail<IntakeReceipt>(PaymentFailureCode.InvalidRequest, $"Model-required intake: {features.Error}.");
            }
        }

        var paymentId = await ScalarAsync<Guid>(connection, tx,
            """
            INSERT INTO revenant.payment (tenant_id, scenario_run_id, intent_id, source_account_id, dest_account_id, currency, amount_minor, status)
            VALUES (@tenant, @run, @intent, @source, @dest, @currency, @amount, 'Initiated')
            RETURNING id
            """, ct,
            ("tenant", tenantId), ("run", run), ("intent", intent.IntentId), ("source", source.AccountId), ("dest", dest.AccountId),
            ("currency", intent.Amount.Currency), ("amount", intent.Amount.MinorUnits));

        await ExecAsync(connection, tx,
            """
            INSERT INTO revenant.payment_transition (tenant_id, scenario_run_id, payment_id, version, from_status, to_status, reason, command_key, actor)
            VALUES (@tenant, @run, @payment, 1, NULL, 'Initiated', NULL, @key, @actor)
            """, ct,
            ("tenant", tenantId), ("run", run), ("payment", paymentId), ("key", commandKey), ("actor", actor));

        long factSequence;
        if (dispatch is not null)
        {
            factSequence = await AppendV4FactAsync(connection, tx, tenantId, paymentId, FinanceFactKind.PaymentIntent,
                ScenarioSchemas.InputSchemaVersion, canonical, dispatch, intent.Sequence, ct);
        }
        else
        {
            factSequence = await ScalarAsync<long>(connection, tx,
                """
                UPDATE revenant.run_cursor
                   SET last_fact_seq = last_fact_seq + 1, last_intent_sequence = @sequence, last_event_time_ms = @time
                 WHERE scenario_run_id = @run
                RETURNING last_fact_seq
                """, ct, ("run", run), ("sequence", intent.Sequence), ("time", intent.EventTimeUtcMs));

            await ExecAsync(connection, tx,
                """
                INSERT INTO revenant.finance_fact (tenant_id, scenario_run_id, fact_seq, kind, schema_version, payment_id, canonical, canonical_sha256)
                VALUES (@tenant, @run, @seq, 'PaymentIntent', @schema, @payment, @canonical, @sha)
                """, ct,
                ("tenant", tenantId), ("run", run), ("seq", factSequence), ("schema", ScenarioSchemas.InputSchemaVersion),
                ("payment", paymentId), ("canonical", canonical), ("sha", hash));

        }

        await InsertCommandAsync(connection, tx, tenantId, run, commandKey, "Intake", paymentId, hash, "Accepted",
            PaymentStatus.Initiated, 1, null, actor, ct);
        await AppendAuditAsync(connection, tx, tenantId, run, "fact.paymentintent", actor, correlationId, $"fact:{factSequence}", 1, hash, ct);

        await outboxFactory(tx).EnqueueAsync(
            PaymentEvent(EventPaymentInitiated, run, paymentId, intent.IntentId, null, PaymentStatus.Initiated, 1, factSequence, correlationId), ct);

        var view = new PaymentView(
            paymentId, new(run), intent.IntentId, PaymentStatus.Initiated, 1, intent.Amount.Currency, intent.Amount.MinorUnits,
            [new PaymentTrailEntry(1, null, PaymentStatus.Initiated, null, actor)]);
        return Result<IntakeReceipt, PaymentFailure>.Success(new IntakeReceipt(view, IsReplay: false, factSequence));
    }

    // ---------------------------------------------------------------------------------------------------------
    // Transitions
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Applies one transition command under the payment's row lock. Exactly one of these happens: the command is a
    /// replay and answers from its recorded result; the expected version is stale (nothing recorded); the step is illegal
    /// or needs a posting group (recorded as a rejected command result, no trail row, no event); or the payment moves one
    /// legal step to <c>version + 1</c> with its trail row, command record and outbox event in the same transaction.
    /// Steps that move money (into or out of Settled) belong to the settlement path and are refused here.
    /// Who may issue a given step (screening, case workflow) is the caller's contract; this method is not an HTTP surface.
    /// </summary>
    public async Task<Result<TransitionReceipt, PaymentFailure>> TransitionAsync(
        string tenantId, string actor, TransitionCommand command, string correlationId, CancellationToken ct)
    {
        try
        {
            // The whole transaction is retried, with the same command id, only on serialization failure or deadlock.
            return await TransientSql.RetryAsync(() => TransitionCoreAsync(tenantId, actor, command, correlationId, ct), ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // The only unique key a transition can collide on is its command key (another request raced the same command id).
            return Fail<TransitionReceipt>(PaymentFailureCode.ConflictingDuplicate, "The command id is already in use.");
        }
    }

    private async Task<Result<TransitionReceipt, PaymentFailure>> TransitionCoreAsync(
        string tenantId, string actor, TransitionCommand command, string correlationId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(actor) || !ValidCorrelation(correlationId)
            || command.RunId.IsEmpty || string.IsNullOrWhiteSpace(command.IntentId) || !Enum.IsDefined(command.Target)
            || command.ExpectedVersion < 1 || !ValidCommandId(command.CommandId)
            || (command.Reason is { } reason && (reason.Length > MaxReasonLength || reason.Length == 0)))
        {
            return Fail<TransitionReceipt>(PaymentFailureCode.InvalidRequest, "The transition command is malformed.");
        }
        var run = command.RunId.Value;
        var commandKey = $"transition:{command.CommandId}";
        var hash = SHA256.HashData(new CanonicalWriter()
            .WriteGuid(run).WriteString(command.IntentId).WriteByte((byte)command.Target)
            .WriteInt64(command.ExpectedVersion).WriteString(command.Reason ?? "").ToArray());

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await SetTenantAsync(connection, tx, tenantId, ct);
        if (await IsV4RunAsync(connection, tx, command.RunId.Value, ct))
        {
            return Fail<TransitionReceipt>(PaymentFailureCode.InvalidRequest, "V4 requires its scheduler transaction.");
        }


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

        var decision = PaymentMachine.Decide(locked.Status, locked.Version, command.Target, command.ExpectedVersion);
        if (decision.Kind == PaymentDecisionKind.Accepted && locked.Status == PaymentStatus.Held && command.Target == PaymentStatus.Approved)
        {
            // Releasing a held payment is the review case's privileged sequence (task 7.4), never a generic step. RV007 in the
            // database refuses it as well; this records the refusal as a rejected command result with no effect.
            await InsertCommandAsync(connection, tx, tenantId, run, commandKey, "Transition", locked.Id, hash, "Rejected",
                locked.Status, locked.Version, nameof(PaymentFailureCode.ReviewRequired), actor, ct);
            await tx.CommitAsync(ct);
            return Fail<TransitionReceipt>(PaymentFailureCode.ReviewRequired,
                "A held payment is released only by its review case after the required approvals.");
        }
        switch (decision.Kind)
        {
            case PaymentDecisionKind.Stale:
                return Fail<TransitionReceipt>(PaymentFailureCode.StaleVersion,
                    $"Expected version {command.ExpectedVersion}, but the payment is at version {locked.Version}.");

            case PaymentDecisionKind.Illegal or PaymentDecisionKind.PostingRequired:
                var rejection = decision.Kind == PaymentDecisionKind.Illegal
                    ? PaymentFailureCode.IllegalTransition
                    : PaymentFailureCode.PostingRequired;
                await InsertCommandAsync(connection, tx, tenantId, run, commandKey, "Transition", locked.Id, hash, "Rejected",
                    locked.Status, locked.Version, rejection.ToString(), actor, ct);
                await tx.CommitAsync(ct);
                return Fail<TransitionReceipt>(rejection, rejection == PaymentFailureCode.IllegalTransition
                    ? $"{locked.Status} to {command.Target} is not a legal payment transition."
                    : $"{locked.Status} to {command.Target} moves money and requires its balanced posting group.");
        }

        // Liquidity (task 7.3). Entering Held reserves the amount on the source account, checked against the locked account's
        // committed available balance (no cache). If it does not fit, the command records the V1 outcome Held -> Rejected
        // (insufficient_available) atomically. Leaving the pending states to Rejected/Returned releases the hold once.
        var outbox = outboxFactory(tx);
        var insufficient = false;
        if (locked.Status == PaymentStatus.Screened && command.Target == PaymentStatus.Held)
        {
            var available = await LockAvailableAsync(connection, tx, [locked.SourceAccountId], ct);
            if (available[locked.SourceAccountId] >= locked.AmountMinor)
            {
                await ReserveAsync(connection, tx, outbox, tenantId, run, locked, correlationId, ct);
                await OpenCaseAsync(connection, tx, outbox, tenantId, run, locked, correlationId, ct);
            }
            else
            {
                insufficient = true;
            }
        }
        else if (command.Target is PaymentStatus.Rejected or PaymentStatus.Returned)
        {
            await ResolveHoldAsync(connection, tx, outbox, run, locked.Id, "Released", correlationId, ct);
        }

        var next = decision.NextVersion;
        await AppendStepAsync(connection, tx, outbox, tenantId, run, locked.Id, command.IntentId, locked.Status, command.Target, next,
            command.Reason, commandKey, actor, correlationId, ct);
        var (appliedStatus, appliedVersion) = (command.Target, next);
        if (insufficient)
        {
            appliedVersion = checked(next + 1);
            appliedStatus = PaymentStatus.Rejected;
            await AppendStepAsync(connection, tx, outbox, tenantId, run, locked.Id, command.IntentId, PaymentStatus.Held,
                PaymentStatus.Rejected, appliedVersion, ReasonInsufficientAvailable, commandKey, actor, correlationId, ct);
        }
        await InsertCommandAsync(connection, tx, tenantId, run, commandKey, "Transition", locked.Id, hash, "Accepted",
            appliedStatus, appliedVersion, null, actor, ct);

        var view = await ReadPaymentByIdAsync(connection, tx, locked.Id, ct)
            ?? throw new InvalidOperationException("A just-updated payment is not readable.");
        await tx.CommitAsync(ct);
        return Result<TransitionReceipt, PaymentFailure>.Success(new TransitionReceipt(view, appliedStatus, appliedVersion, IsReplay: false));
    }

    // ---------------------------------------------------------------------------------------------------------
    // Reads
    // ---------------------------------------------------------------------------------------------------------

    public async Task<PaymentView?> GetPaymentAsync(string tenantId, ScenarioRunId runId, string intentId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await SetTenantAsync(connection, tx, tenantId, ct);
        var id = await ScalarAsync<Guid?>(connection, tx,
            "SELECT id FROM revenant.payment WHERE scenario_run_id = @run AND intent_id = @intent", ct,
            ("run", runId.Value), ("intent", intentId));
        var view = id is { } paymentId ? await ReadPaymentByIdAsync(connection, tx, paymentId, ct) : null;
        await tx.CommitAsync(ct);
        return view;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Internals
    // ---------------------------------------------------------------------------------------------------------

    private static Result<T, PaymentFailure> Fail<T>(PaymentFailureCode code, string message) =>
        Result<T, PaymentFailure>.Failure(new PaymentFailure(code, message));

    private static bool ValidCorrelation(string value) => !string.IsNullOrEmpty(value) && value.Length <= 200;

    private static bool ValidCommandId(string value) =>
        !string.IsNullOrEmpty(value) && value.Length <= MaxCommandIdLength
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':');

    private static Task SetTenantAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string tenantId, CancellationToken ct) =>
        ExecAsync(connection, tx, "SELECT set_config('revenant.tenant_id', @tenant, true)", ct, ("tenant", tenantId));

    private OutboxMessage PaymentEvent(
        string eventType, Guid run, Guid paymentId, string intentId, PaymentStatus? from, PaymentStatus to, long version,
        long? factSequence, string correlationId)
    {
        var payload = JsonSerializer.Serialize(new
        {
            runId = run,
            paymentId,
            intentId,
            fromStatus = from?.ToString(),
            toStatus = to.ToString(),
            version,
            factSequence,
        });
        // Deterministic id: a given (payment, version) can produce exactly one event row, so a duplicate fails the insert.
        var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"revenant.payment/{paymentId:N}/{version}"))[..16]);
        return new OutboxMessage(id, eventType, 1, $"payment:{paymentId:N}", clock.GetUtcNow(), payload, correlationId);
    }

    private sealed record Cursor(long LastFactSeq, long LastIntentSequence, long LastEventTimeMs, string FinanceVersion);

    private sealed record StoredCommand(
        byte[] RequestHash, Guid PaymentId, PaymentStatus ResultStatus, long ResultVersion, string? RejectCode);

    private sealed record LockedPayment(
        Guid Id, PaymentStatus Status, long Version, Guid SourceAccountId, Guid DestAccountId, string Currency, long AmountMinor);

    private sealed record AccountRow(Guid AccountId, string BankId, string Currency);

    private static async Task<Cursor?> ReadCursorForUpdateAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid run, CancellationToken ct)
    {
        await using var cmd = Command(connection, tx,
            """
            SELECT c.last_fact_seq, c.last_intent_sequence, c.last_event_time_ms, r.finance_version
              FROM revenant.run_cursor c JOIN revenant.scenario_run r ON r.id = c.scenario_run_id
             WHERE c.scenario_run_id = @run FOR UPDATE OF c
            """,
            ("run", run));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new Cursor(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3))
            : null;
    }

    private static async Task<StoredCommand?> ReadCommandAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid run, string key, CancellationToken ct)
    {
        await using var cmd = Command(connection, tx,
            """
            SELECT request_sha256, payment_id, result_status, result_version, reject_code
              FROM revenant.payment_command WHERE scenario_run_id = @run AND command_key = @key
            """, ("run", run), ("key", key));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new StoredCommand(
                reader.GetFieldValue<byte[]>(0), reader.GetGuid(1), Enum.Parse<PaymentStatus>(reader.GetString(2)),
                reader.GetInt64(3), reader.IsDBNull(4) ? null : reader.GetString(4))
            : null;
    }

    private static Task InsertCommandAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, string tenantId, Guid run, string key, string kind, Guid paymentId,
        byte[] hash, string outcome, PaymentStatus resultStatus, long resultVersion, string? rejectCode, string actor, CancellationToken ct) =>
        ExecAsync(connection, tx,
            """
            INSERT INTO revenant.payment_command
              (tenant_id, scenario_run_id, command_key, kind, payment_id, request_sha256, outcome, result_status, result_version, reject_code, actor)
            VALUES (@tenant, @run, @key, @kind, @payment, @hash, @outcome, @status, @version, @reject, @actor)
            """, ct,
            ("tenant", tenantId), ("run", run), ("key", key), ("kind", kind), ("payment", paymentId), ("hash", hash),
            ("outcome", outcome), ("status", resultStatus.ToString()), ("version", resultVersion),
            ("reject", (object?)rejectCode ?? DBNull.Value), ("actor", actor));

    private static async Task<LockedPayment?> ReadPaymentForUpdateAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid run, string intentId, CancellationToken ct)
    {
        await using var cmd = Command(connection, tx,
            """
            SELECT id, status, version, source_account_id, dest_account_id, currency, amount_minor
              FROM revenant.payment WHERE scenario_run_id = @run AND intent_id = @intent FOR UPDATE
            """,
            ("run", run), ("intent", intentId));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new LockedPayment(reader.GetGuid(0), Enum.Parse<PaymentStatus>(reader.GetString(1)), reader.GetInt64(2),
                reader.GetGuid(3), reader.GetGuid(4), reader.GetString(5), reader.GetInt64(6))
            : null;
    }

    private static async Task<Dictionary<string, AccountRow>> ReadAccountsAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid run, string[] localIds, CancellationToken ct)
    {
        await using var cmd = Command(connection, tx,
            """
            SELECT a.id, a.local_account_id, b.local_bank_id, a.currency
              FROM revenant.account a JOIN revenant.bank b ON b.id = a.bank_id
             WHERE a.scenario_run_id = @run AND a.local_account_id = ANY(@ids)
            """, ("run", run));
        cmd.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = localIds });
        var rows = new Dictionary<string, AccountRow>(StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows[reader.GetString(1)] = new AccountRow(reader.GetGuid(0), reader.GetString(2), reader.GetString(3));
        }
        return rows;
    }

    private static async Task<PaymentView?> ReadPaymentByIdAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid paymentId, CancellationToken ct)
    {
        string intentId;
        Guid run;
        PaymentStatus status;
        long version;
        string currency;
        long amount;
        await using (var cmd = Command(connection, tx,
            "SELECT scenario_run_id, intent_id, status, version, currency, amount_minor FROM revenant.payment WHERE id = @id",
            ("id", paymentId)))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct))
            {
                return null;
            }
            (run, intentId, status, version, currency, amount) = (
                reader.GetGuid(0), reader.GetString(1), Enum.Parse<PaymentStatus>(reader.GetString(2)),
                reader.GetInt64(3), reader.GetString(4), reader.GetInt64(5));
        }
        var trail = new List<PaymentTrailEntry>();
        await using (var cmd = Command(connection, tx,
            "SELECT version, from_status, to_status, reason, actor FROM revenant.payment_transition WHERE payment_id = @id ORDER BY version",
            ("id", paymentId)))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                trail.Add(new PaymentTrailEntry(
                    reader.GetInt64(0), reader.IsDBNull(1) ? null : Enum.Parse<PaymentStatus>(reader.GetString(1)),
                    Enum.Parse<PaymentStatus>(reader.GetString(2)), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4)));
            }
        }
        return new PaymentView(paymentId, new ScenarioRunId(run), intentId, status, version, currency, amount, trail);
    }

    private static async Task<RunView?> ReadRunAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, ScenarioRunId runId, CancellationToken ct)
    {
        string tenant, owner, version, hash, finance;
        long seed;
        await using (var cmd = Command(connection, tx,
            "SELECT tenant_id, owner_subject_id, seed, scenario_version, manifest_hash, finance_version FROM revenant.scenario_run WHERE id = @id",
            ("id", runId.Value)))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct))
            {
                return null;
            }
            (tenant, owner, seed, version, hash, finance) = (
                reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3), reader.GetString(4), reader.GetString(5));
        }
        var banks = new List<BankBalance>();
        await using (var cmd = Command(connection, tx,
            """
            SELECT b.local_bank_id, b.name, sum(a.opening_balance_minor)::bigint, sum(p.posted_minor)::bigint,
                   sum(p.reserved_minor)::bigint
              FROM revenant.bank b
              JOIN revenant.account a ON a.bank_id = b.id
              JOIN revenant.account_available_balance p ON p.account_id = a.id
             WHERE b.scenario_run_id = @run
             GROUP BY b.local_bank_id, b.name
             ORDER BY b.local_bank_id
            """, ("run", runId.Value)))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                banks.Add(new BankBalance(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4)));
            }
        }
        return new RunView(runId, tenant, owner, seed, version, hash, finance, banks);
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction tx, string sql, params (string Name, object Value)[] args)
    {
        var cmd = new NpgsqlCommand(sql, connection, tx);
        foreach (var (name, value) in args)
        {
            cmd.Parameters.AddWithValue(name, value);
        }
        return cmd;
    }

    private static async Task<int> ExecAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, string sql, CancellationToken ct, params (string Name, object Value)[] args)
    {
        await using var cmd = Command(connection, tx, sql, args);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<T> ScalarAsync<T>(
        NpgsqlConnection connection, NpgsqlTransaction tx, string sql, CancellationToken ct, params (string Name, object Value)[] args)
    {
        await using var cmd = Command(connection, tx, sql, args);
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is null or DBNull ? default! : (T)value;
    }
}
