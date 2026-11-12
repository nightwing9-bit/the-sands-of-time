using Npgsql;
using Revenant.Cases;
using Revenant.Contracts;
using Revenant.Scenario;
using Revenant.Timeline;

namespace Revenant.Payments;

public sealed partial class PaymentLedgerStore
{
    internal static async Task MaterializeV4OpeningAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string tenant,
        string owner, ScenarioRun run, V4ExecutionBinding binding, ObservedFinanceState checkpoint, CancellationToken ct)
    {
        var ledger = checkpoint.Ledger;
        var execution = binding.ExecutionRunId.Value;
        if (checkpoint.RunId != binding.LineageRunId || checkpoint.LastFact != (binding.Fork?.FactSequence ?? 0)
            || checkpoint.PrefixDigest != (binding.Fork?.PrefixDigest ?? ObservedFinanceReducer.InitialDigest(binding.LineageRunId)))
        {
            throw new InvalidOperationException("The physical opening disagrees with its canonical origin.");
        }

        var accounts = await InsertOpeningAccountsAsync(connection, tx, tenant, owner, "operator", execution, run, ledger, ct);
        if (binding.Fork is { } fork)
        {
            foreach (var payment in ledger.Payments.Values)
            {
                await MaterializePaymentAsync(connection, tx, tenant, execution, fork, payment, accounts, ct);
            }
        }

        await VerifyV4BalancesAsync(connection, tx, execution, ledger, ct);
    }

    private static async Task VerifyV4BalancesAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid execution,
        LedgerFinanceState ledger, CancellationToken ct)
    {
        await using var query = Command(connection, tx, """
            SELECT local_account_id, posted_minor, reserved_minor, available_minor
            FROM revenant.account_available_balance WHERE scenario_run_id = @run ORDER BY local_account_id
            """, ("run", execution));
        await using var reader = await query.ExecuteReaderAsync(ct);
        var count = 0;
        while (await reader.ReadAsync(ct))
        {
            var expected = ledger.Accounts[reader.GetString(0)];
            if ((reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3)) !=
                (expected.PostedMinor, expected.ReservedMinor, checked(expected.PostedMinor - expected.ReservedMinor)))
            {
                throw new InvalidOperationException("The child SQL balances disagree with the verified opening.");
            }
            count++;
        }
        if (count != ledger.Accounts.Count)
        {
            throw new InvalidOperationException("The V4 opening has missing accounts.");
        }
    }

    private static async Task MaterializePaymentAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string tenant,
        Guid execution, V4ForkOrigin fork, LedgerPayment payment, Dictionary<string, Guid> accounts, CancellationToken ct)
    {
        var intent = payment.Intent;
        var sourceExecution = fork.SourceExecutionRunId.Value;
        var source = accounts[intent.SourceAccountId];
        var dest = accounts[intent.DestAccountId];
        var id = await ScalarAsync<Guid>(connection, tx, """
            INSERT INTO revenant.payment
              (tenant_id, scenario_run_id, intent_id, source_account_id, dest_account_id, currency, amount_minor, status)
            VALUES (@tenant, @run, @intent, @source, @dest, @currency, @amount, 'Initiated') RETURNING id
            """, ct, ("tenant", tenant), ("run", execution), ("intent", intent.IntentId), ("source", source), ("dest", dest),
            ("currency", intent.Amount.Currency), ("amount", intent.Amount.MinorUnits));

        var count = await ExecAsync(connection, tx, """
            INSERT INTO revenant.payment_transition
              (tenant_id, scenario_run_id, payment_id, version, from_status, to_status, reason, command_key, actor)
            SELECT @tenant, @run, @payment, t.version, t.from_status, t.to_status, t.reason, t.command_key, t.actor
            FROM revenant.payment p JOIN revenant.payment_transition t ON t.payment_id = p.id
            WHERE p.scenario_run_id = @parent AND p.intent_id = @intent AND t.version <= @count
            ORDER BY t.version
            """, ct, ("tenant", tenant), ("run", execution), ("payment", id), ("parent", sourceExecution),
            ("intent", intent.IntentId), ("count", payment.Trail.Length));
        if (count != payment.Trail.Length)
        {
            throw new InvalidOperationException("The source payment trail disagrees with the verified opening.");
        }
        for (var index = 1; index < payment.Trail.Length; index++)
        {
            await ExecAsync(connection, tx, "UPDATE revenant.payment SET status = @status, version = @version WHERE id = @payment",
                ct, ("status", payment.Trail[index].ToString()), ("version", index + 1L), ("payment", id));
        }
        if (payment.ReservedMinor > 0)
        {
            await ExecAsync(connection, tx, """
                INSERT INTO revenant.hold (tenant_id, scenario_run_id, payment_id, account_id, currency, amount_minor)
                VALUES (@tenant, @run, @payment, @account, @currency, @amount)
                """, ct, ("tenant", tenant), ("run", execution), ("payment", id), ("account", source),
                ("currency", intent.Amount.Currency), ("amount", payment.ReservedMinor));
        }
        if (payment.SettlementPosted)
        {
            var settlement = await InheritPostingAsync("Settlement", "");
            if (payment.ReturnPosted)
            {
                _ = await InheritPostingAsync("Return", settlement.ToString("D"));
            }
        }
        if (payment.Case is { } status)
        {
            await MaterializeCaseAsync(connection, tx, tenant, execution, sourceExecution, payment, id, status, ct);
        }

        async Task<Guid> InheritPostingAsync(string purpose, string compensates)
        {
            var inherited = await ScalarAsync<Guid>(connection, tx, """
                INSERT INTO revenant.ledger_group
                  (tenant_id, scenario_run_id, payment_id, currency, purpose, compensates_group_id, inherited_from_group_id)
                SELECT @tenant, @run, @payment, g.currency, g.purpose, CAST(NULLIF(@compensates, '') AS uuid), g.id
                FROM revenant.ledger_group g JOIN revenant.payment p ON p.id = g.payment_id
                WHERE p.scenario_run_id = @parent AND p.intent_id = @intent AND g.purpose = @purpose RETURNING id
                """, ct, ("tenant", tenant), ("run", execution), ("payment", id), ("compensates", compensates),
                ("parent", sourceExecution), ("intent", intent.IntentId), ("purpose", purpose));
            return inherited != Guid.Empty ? inherited : throw new InvalidOperationException("The source posting is absent.");
        }
    }

    private static async Task MaterializeCaseAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string tenant,
        Guid execution, Guid sourceExecution, LedgerPayment payment, Guid id, CaseStatus status, CancellationToken ct)
    {
        var caseId = await ScalarAsync<Guid>(connection, tx, """
            INSERT INTO revenant.review_case
              (tenant_id, scenario_run_id, payment_id, currency, amount_minor, policy_version, required_approvals, screening_version,
               screened_name, screening_entry, screening_score_permille, screening_threshold_permille, screening_match)
            SELECT @tenant, @run, @payment, c.currency, c.amount_minor, c.policy_version, c.required_approvals, c.screening_version,
                   c.screened_name, c.screening_entry, c.screening_score_permille, c.screening_threshold_permille, c.screening_match
            FROM revenant.review_case c JOIN revenant.payment p ON p.id = c.payment_id
            WHERE p.scenario_run_id = @parent AND p.intent_id = @intent AND c.required_approvals = @required RETURNING id
            """, ct, ("tenant", tenant), ("run", execution), ("payment", id), ("parent", sourceExecution),
            ("intent", payment.Intent.IntentId), ("required", payment.RequiredApprovals));
        if (caseId == Guid.Empty)
        {
            throw new InvalidOperationException("The source case disagrees with the verified opening.");
        }
        for (var order = 1; order <= payment.Approvals; order++)
        {
            var inserted = await ExecAsync(connection, tx, """
                INSERT INTO revenant.case_approval
                  (tenant_id, scenario_run_id, case_id, approval_order, subject_id, actor_role, mfa_verified)
                SELECT @tenant, @run, @case, a.approval_order, a.subject_id, a.actor_role, a.mfa_verified
                FROM revenant.case_approval a JOIN revenant.review_case c ON c.id = a.case_id
                JOIN revenant.payment p ON p.id = c.payment_id
                WHERE p.scenario_run_id = @parent AND p.intent_id = @intent AND a.approval_order = @order
                """, ct, ("tenant", tenant), ("run", execution), ("case", caseId), ("parent", sourceExecution),
                ("intent", payment.Intent.IntentId), ("order", order));
            if (inserted != 1)
            {
                throw new InvalidOperationException("The source approvals disagree with the verified opening.");
            }
            var next = order == payment.RequiredApprovals ? CaseStatus.Released : CaseStatus.AwaitingSecondApproval;
            await ExecAsync(connection, tx, "UPDATE revenant.review_case SET status = @status, version = version + 1 WHERE id = @case",
                ct, ("status", next.ToString()), ("case", caseId));
        }
        if (status == CaseStatus.Rejected)
        {
            await ExecAsync(connection, tx, "UPDATE revenant.review_case SET status = 'Rejected', version = version + 1 WHERE id = @case",
                ct, ("case", caseId));
        }
    }
}
