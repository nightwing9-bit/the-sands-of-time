using Marrow.Kernel.Canonical;
using Npgsql;
using Revenant.Cases;
using Revenant.Contracts;
using Revenant.Timeline;
using Revenant.Finance;
using Revenant.Scenario;

namespace Revenant.Payments;

public sealed partial class PaymentLedgerStore
{
    private static Task<bool> IsV4RunAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid run, CancellationToken ct) =>
        ScalarAsync<bool>(connection, tx, """
            SELECT EXISTS (SELECT 1 FROM revenant.scenario_run WHERE id = @run AND finance_version = @version)
            """, ct, ("run", run), ("version", ObservedFinanceVersion.ReducerVersion));

    internal async Task<V4CommandOutcome?> ExecuteV4Async(NpgsqlConnection connection, NpgsqlTransaction tx, string tenant,
        V4FinanceDispatch dispatch, CancellationToken ct)
    {
        var request = dispatch.Command.Request;
        if (!ValidCommandId(request.Key))
        {
            return Rejected("invalid-command-key");
        }
        var execution = dispatch.Execution.Value;
        if (request.Kind == nameof(FinanceFactKind.PaymentIntent) && request.Class == V4CommandClass.Exogenous
            && request.PayloadSchema == ScenarioSchemas.InputSchemaVersion)
        {
            PaymentIntent intent;
            try
            {
                intent = PaymentIntentCodec.Instance.Decode(request.CanonicalPayload.AsSpan());
            }
            catch (CanonicalFormatException)
            {
                return Rejected("malformed-intent");
            }
            if (intent.RunId != dispatch.Prior.RunId || intent.EventTimeUtcMs != dispatch.EffectiveMs)
            {
                return Rejected("wrong-lineage-or-effective-time");
            }
            if (dispatch.Prior.Ledger.Payments.ContainsKey(intent.IntentId))
            {
                return Rejected("intent-already-recorded");
            }
            var intake = await SubmitIntentCoreAsync(connection, tx, tenant, ScreeningActor, intent, request.Key, ct, dispatch);
            return intake.TryGetValue(out var result) && !result.IsReplay
                ? Done(nameof(PaymentStatus.Initiated), result.FactSequence)
                : Rejected(intake.IsSuccess ? "intent-already-recorded" : intake.Error.Code.ToString());
        }
        if (request.Kind is not (nameof(FinanceFactKind.Screening) or nameof(FinanceFactKind.Settlement)
            or nameof(FinanceFactKind.ReviewDecision) or nameof(FinanceFactKind.ReturnInstruction)))
        {
            return null;
        }

        var decision = request.Kind is nameof(FinanceFactKind.ReviewDecision) or nameof(FinanceFactKind.ReturnInstruction);
        if (decision ? request.Class != V4CommandClass.ConditionalHumanRequest || request.PayloadSchema != "v4-human-request/1"
            : request.Class != V4CommandClass.DerivedOpportunity || request.PayloadSchema != "v4-payment-opportunity/1")
        {
            return Rejected("invalid-command-class-or-schema");
        }

        string intentId;
        string actor = ScreeningActor;
        long version = 0;
        bool mfa = false;
        CaseAction action = CaseAction.Assign;
        try
        {
            var reader = new CanonicalReader(request.CanonicalPayload.AsSpan());
            if (reader.ReadInt32() != 1)
            {
                return Rejected("invalid-command-format");
            }
            intentId = reader.ReadString();
            if (decision)
            {
                actor = reader.ReadString();
                version = reader.ReadInt64();
                mfa = reader.ReadBoolean();
                action = (CaseAction)reader.ReadByte();
            }
            reader.EnsureEnd();
        }
        catch (CanonicalFormatException)
        {
            return Rejected("malformed-command");
        }

        if (decision)
        {
            var role = await ScalarAsync<string?>(connection, tx, """
                SELECT s.role FROM revenant.scenario_run_staff s
                WHERE s.scenario_run_id = @run AND s.subject_id = @actor AND NOT EXISTS (
                  SELECT 1 FROM revenant.scenario_run_staff_revocation r
                  WHERE r.scenario_run_id = s.scenario_run_id AND r.subject_id = s.subject_id)
                """, ct, ("run", execution), ("actor", actor));
            if (role is not ("operator" or "supervisor") || !mfa || version < 1)
            {
                return Rejected("inapplicable-authorization");
            }
            if (request.Kind == nameof(FinanceFactKind.ReviewDecision))
            {
                if (action is not (CaseAction.Approve or CaseAction.Reject))
                {
                    return Rejected("inapplicable-review-action");
                }
                var reviewed = await ApplyCaseActionAsync(connection, tx, tenant, new(actor, true, role == "supervisor", true),
                    new(dispatch.Execution, intentId, action, version, request.Key, null), request.Key, ct, dispatch);
                return reviewed.TryGetValue(out var applied) && applied.FactSequence is { } sequence
                    ? Done("reviewed", sequence) : Rejected(reviewed.IsSuccess ? "review-already-recorded" : $"inapplicable-{reviewed.Error.Code}");
            }
            var returned = await ApplyReturnAsync(connection, tx, tenant, actor,
                new(dispatch.Execution, intentId, version, request.Key, "authorized-return"), request.Key, ct, dispatch);
            return returned.TryGetValue(out var receipt) && !receipt.IsReplay
                ? Done(nameof(PaymentStatus.Returned), dispatch.Prior.LastFact + 1)
                : Rejected(returned.IsSuccess ? "return-already-recorded" : returned.Error.Code.ToString());
        }

        var locked = await ReadPaymentForUpdateAsync(connection, tx, execution, intentId, ct);
        if (locked is null || !dispatch.Prior.Ledger.Payments.TryGetValue(intentId, out var payment) || locked.Status != payment.Status)
        {
            return Rejected("payment-not-applicable");
        }
        if (request.Kind == nameof(FinanceFactKind.Screening) && locked.Status == PaymentStatus.Initiated)
        {
            var policy = dispatch.Context.LegacyContext().Value.Policy;
            var reason = FinanceLedgerReducer.ScreeningReason(payment.Intent, payment.Velocity15m, policy);
            var screened = await ApplyScreeningAsync(connection, tx, tenant, execution, intentId, locked, reason, null, null,
                request.Key, ct, dispatch);
            return Done(screened.Status.ToString(), dispatch.Prior.LastFact + 1);
        }
        if (request.Kind == nameof(FinanceFactKind.Settlement) && locked.Status is PaymentStatus.Cleared or PaymentStatus.Approved)
        {
            var available = await LockAvailableAsync(connection, tx, [locked.SourceAccountId, locked.DestAccountId], ct);
            var own = await ScalarAsync<long?>(connection, tx,
                "SELECT amount_minor FROM revenant.hold WHERE payment_id = @p AND status = 'Active'", ct, ("p", locked.Id)) ?? 0;
            var claimed = new Claimed(locked.Id, execution, intentId, locked.Version, locked.SourceAccountId,
                locked.DestAccountId, locked.Currency, locked.AmountMinor, locked.Status);
            var settled = await ApplySettlementAsync(connection, tx, outboxFactory(tx), tenant, actor, claimed, available, own,
                request.Key, ct, dispatch);
            return Done(settled.Status.ToString(), dispatch.Prior.LastFact + 1);
        }
        return Rejected("payment-not-applicable");
    }

    private static V4CommandOutcome Done(string code, long sequence) =>
        new(V4CommandStatus.Completed, code, [.. new CanonicalWriter().WriteInt64(sequence).ToArray()]);

    private static V4CommandOutcome Rejected(string code) =>
        new(V4CommandStatus.Cancelled, code, [.. new CanonicalWriter().WriteString(code).ToArray()]);

    private static async Task<long> AppendV4FactAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string tenant,
        Guid paymentId, FinanceFactKind kind, string schema, byte[] payload, V4FinanceDispatch dispatch, long? intentSequence,
        CancellationToken ct)
    {
        var sequence = checked(dispatch.Prior.LastFact + 1);
        var input = new ObservedFinanceInput(dispatch.Prior.RunId, sequence, dispatch.EffectiveMs,
            (ObservedFactKind)kind, schema, [.. payload]);
        var next = ObservedFinanceReducer.Apply(dispatch.Prior, input, dispatch.Context);
        if (next.IsFailure)
        {
            throw new ObservedFinanceTransitionException(next.Error);
        }
        var run = dispatch.Execution.Value;
        await VerifyV4BalancesAsync(connection, tx, run, next.Value.Ledger, ct);
        var changed = await ExecAsync(connection, tx, """
            UPDATE revenant.run_cursor
               SET last_fact_seq = @seq, last_event_time_ms = @time,
                   last_intent_sequence = coalesce(nullif(@intent, 0), last_intent_sequence)
             WHERE scenario_run_id = @run AND last_fact_seq = @prior
            """, ct, ("seq", sequence), ("time", dispatch.EffectiveMs), ("intent", intentSequence ?? 0),
            ("run", run), ("prior", dispatch.Prior.LastFact));
        if (changed != 1)
        {
            throw new InvalidOperationException("V4 finance cursor changed under the scheduler lock.");
        }
        await ExecAsync(connection, tx, """
            INSERT INTO revenant.finance_fact
              (tenant_id, scenario_run_id, fact_seq, kind, schema_version, payment_id, canonical, canonical_sha256,
               effective_time_ms, v4_command_seq)
            VALUES (@tenant, @run, @seq, @kind, @schema, @payment, @canonical, @sha, @time, @command)
            """, ct, ("tenant", tenant), ("run", run), ("seq", sequence), ("kind", kind.ToString()), ("schema", schema),
            ("payment", paymentId), ("canonical", payload), ("sha", FinanceFactCodec.PayloadHash(payload)),
            ("time", dispatch.EffectiveMs), ("command", dispatch.Command.Sequence));
        return sequence;
    }
}
