using Npgsql;
using Marrow.Kernel.Results;
using Revenant.Contracts;
using Revenant.Finance;
using Revenant.Intelligence;
using Revenant.Scenario;
using Revenant.Timeline;

namespace Revenant.Payments;

/// <summary>
/// Result of screening one payment: its status and version after the step, and the hold reason when it was held.
/// <see cref="Evidence"/> is the committed inference evidence of a model-aware (V3) run, null for a rules-only V2 run.
/// </summary>
public sealed record ScreeningOutcome(
    ScenarioRunId RunId, string IntentId, Guid PaymentId, PaymentStatus Status, long Version, string? Reason, long FactSequence,
    InferenceEvidenceV1? Evidence = null);

/// <summary>
/// A model-required screening could not be scored (features incompatible, or a non-finite/out-of-range score). The transaction is
/// rolled back: the payment stays Initiated and no decision, fact, hold, case or outbox row exists (R08.08).
/// </summary>
public sealed class RiskScreeningUnavailableException(RiskModelFailure failure, RiskFeatureError? featureError = null)
    : InvalidOperationException($"Model-required screening is unavailable: {failure.Code}: {failure.Detail}")
{
    public RiskModelFailure Failure { get; } = failure;
    public RiskFeatureError? FeatureError { get; } = featureError;
}

public sealed record BlockedScreening(
    ScenarioRunId RunId, string IntentId, RiskModelFailure Failure, RiskFeatureError? FeatureError);

/// <summary>
/// Spec 07 task 7.5: the production entry point into Held. The LiveOps screening step (run by <see cref="ScreeningWorker"/>) takes
/// the oldest Initiated payment of the tenant <c>FOR UPDATE SKIP LOCKED</c>, computes its payer velocity from the run's earlier
/// <see cref="FinanceFactKind.PaymentIntent"/> facts, applies the pinned live policy (<see cref="LedgerFinanceContext.LivePolicy"/>)
/// exactly as <see cref="FinanceLedgerReducer"/> does, and commits in ONE transaction: Initiated → Screened, then Cleared, or Held
/// with its reservation and review case, or Held → Rejected when the source cannot cover the reservation; the trail rows, outbox
/// events, the ordered <see cref="FinanceFactKind.Screening"/> fact and its audit link. A source account whose availability the step
/// reads is locked before the run cursor, so fact order agrees with every availability change.
/// <para>
/// Spec 08 task 8.3, model-aware (V3) runs: the source account and then the run cursor are locked FIRST, so the committed posted
/// balance and the as-of fact sequence cannot change until commit; the <c>risk-features-v1</c> snapshot is built from them, scored
/// outside any reducer with the pinned, verified artifact, quantised once to PPM and frozen into <see cref="InferenceEvidenceV1"/>
/// for fact <c>asOf + 1</c>. The v7 decision keeps precedence; a base-clear payment at or above the threshold is held
/// (<c>ml_risk_hold</c>). The evidence bytes are the Screening fact's payload, committed with the steps, hold, case, outbox and audit
/// link. When the pinned model is unavailable, V3 payments are not selected at all (they stay Initiated) and rules-only V2 runs
/// continue; nothing falls back to rules-only within a V3 run.
/// </para>
/// </summary>
public sealed partial class PaymentLedgerStore
{
    public const string ScreeningActor = "system:screening";
    public const string ReasonMlRiskHold = "ml_risk_hold";

    /// <summary>The pinned model of model-aware runs, verified; a typed failure means V3 payments are not screened.</summary>
    public Result<RiskModelScorer, RiskModelFailure> ModelAvailability() =>
        riskModels?.Get(RiskPolicyCatalog.V1)
        ?? Result<RiskModelScorer, RiskModelFailure>.Failure(new RiskModelFailure(RiskModelError.ArtifactMissing, "no risk model provider is configured"));

    public Task<IReadOnlyList<ScreeningOutcome>> ScreenBatchAsync(
        string tenantId, int maxPayments, string correlationId, CancellationToken ct) =>
        ScreenBatchAsync(tenantId, maxPayments, correlationId, ct, onBlocked: null);

    public async Task<IReadOnlyList<ScreeningOutcome>> ScreenBatchAsync(
        string tenantId, int maxPayments, string correlationId, CancellationToken ct, Action<BlockedScreening>? onBlocked)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || !ValidCorrelation(correlationId) || maxPayments < 1 || maxPayments > MaxSettlementBatch)
        {
            throw new ArgumentException($"Tenant, a correlation id and a batch size of 1-{MaxSettlementBatch} are required.");
        }
        // Resolved once per batch: a verified scorer or none. Without one, model-required runs are skipped (fail closed).
        var scorer = ModelAvailability().TryGetValue(out var s) ? s : null;
        var outcomes = new List<ScreeningOutcome>();
        // After a blocked head, leave the run untouched for this pass and look for work in other runs.
        var excludedRuns = new HashSet<Guid>();
        while (outcomes.Count < maxPayments)
        {
            var attempt = await TransientSql.RetryAsync(
                () => ScreenOneAsync(tenantId, correlationId, scorer, [.. excludedRuns], ct), ct);
            if (attempt.Outcome is { } outcome)
            {
                outcomes.Add(outcome);
            }
            else if (attempt.Blocked is { } blocked)
            {
                excludedRuns.Add(blocked.RunId.Value);
                onBlocked?.Invoke(blocked);
            }
            else
            {
                break;
            }
        }
        return outcomes;
    }

    private async Task<(ScreeningOutcome? Outcome, BlockedScreening? Blocked)> ScreenOneAsync(
        string tenantId, string correlationId, RiskModelScorer? scorer, Guid[] excludedRuns, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await SetTenantAsync(connection, tx, tenantId, ct);

        LockedPayment payment;
        Guid run;
        string intentId;
        long intentFact;
        byte[] canonical;
        bool modelAware;
        string[] versions = scorer is null ? [FinanceVersion] : [FinanceVersion, ModelAwareFinanceVersion];
        // For V3, SKIP LOCKED may skip a locked head run but cannot move past its head within the run.
        await using (var cmd = Command(connection, tx,
            """
            SELECT p.id, p.scenario_run_id, p.intent_id, p.version, p.source_account_id, p.dest_account_id, p.currency, p.amount_minor,
                   f.fact_seq, f.canonical, r.finance_version
              FROM revenant.payment p
              JOIN revenant.finance_fact f ON f.payment_id = p.id AND f.kind = 'PaymentIntent'
              JOIN revenant.scenario_run r ON r.id = p.scenario_run_id
             WHERE p.status = 'Initiated' AND r.finance_version = ANY(@versions)
               AND NOT (p.scenario_run_id = ANY(@excluded))
               AND (r.finance_version <> @modelAware OR NOT EXISTS (
                   SELECT 1 FROM revenant.payment q
                   JOIN revenant.finance_fact g ON g.payment_id = q.id AND g.kind = 'PaymentIntent'
                    WHERE q.scenario_run_id = p.scenario_run_id AND q.status = 'Initiated' AND g.fact_seq < f.fact_seq
               ))
             ORDER BY p.scenario_run_id, f.fact_seq
             LIMIT 1
               FOR UPDATE OF p SKIP LOCKED
            """, ("versions", versions), ("excluded", excludedRuns), ("modelAware", ModelAwareFinanceVersion)))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct))
            {
                return (null, null);
            }
            payment = new LockedPayment(reader.GetGuid(0), PaymentStatus.Initiated, reader.GetInt64(3), reader.GetGuid(4), reader.GetGuid(5),
                reader.GetString(6), reader.GetInt64(7));
            (run, intentId, intentFact, canonical) = (reader.GetGuid(1), reader.GetString(2), reader.GetInt64(8), reader.GetFieldValue<byte[]>(9));
            modelAware = reader.GetString(10) == ModelAwareFinanceVersion;
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

        long? available = null;
        InferenceEvidenceV1? evidence = null;
        if (modelAware)
        {
            try
            {
                (evidence, available) = await InferAsync(connection, tx, scorer!, new ScenarioRunId(run), intentId, payment, reason, ct);
            }
            catch (RiskScreeningUnavailableException ex)
            {
                await tx.RollbackAsync(ct);
                return (null, new BlockedScreening(new ScenarioRunId(run), intentId, ex.Failure, ex.FeatureError));
            }
            reason = ModelLedgerReducer.EffectiveReason(reason, evidence.FinalDecision);
        }

        var outcome = await ApplyScreeningAsync(connection, tx, tenantId, run, intentId, payment, reason, available, evidence,
            correlationId, ct);
        await tx.CommitAsync(ct);
        return (outcome, null);
    }

    private async Task<ScreeningOutcome> ApplyScreeningAsync(NpgsqlConnection connection, NpgsqlTransaction tx,
        string tenantId, Guid run, string intentId, LockedPayment payment, PaymentReason reason, long? available,
        InferenceEvidenceV1? evidence, string correlationId, CancellationToken ct, V4FinanceDispatch? dispatch = null)
    {
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
            available ??= (await LockAvailableAsync(connection, tx, [payment.SourceAccountId], ct))[payment.SourceAccountId];
            var fits = available >= payment.AmountMinor;
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
        long fact;
        if (evidence is null)
        {
            fact = await AppendFactAsync(connection, tx, tenantId, run, payment.Id, FinanceFactKind.Screening, PaymentStepCodec.ScreeningSchema,
                PaymentStepCodec.Encode(intentId), ScreeningActor, correlationId, version, ct, dispatch: dispatch);
        }
        else
        {
            var bytes = evidence.ToCanonicalBytes();
            fact = await AppendFactAsync(connection, tx, tenantId, run, payment.Id, FinanceFactKind.Screening, ModelLedgerVersion.ScreeningSchema,
                bytes, ScreeningActor, correlationId, version, ct, new FactEvidenceRef(InferenceEvidenceV1.SchemaId, FinanceFactCodec.PayloadHash(bytes)));
            if (fact != evidence.ScreeningFactSeq)
            {
                // Impossible while the cursor lock is held; refuse rather than commit evidence for another position.
                throw new InvalidOperationException($"Screening fact {fact} is not the evidence position {evidence.ScreeningFactSeq}.");
            }
        }
        return new ScreeningOutcome(new ScenarioRunId(run), intentId, payment.Id, status, version, holdReason, fact, evidence);
    }

    /// <summary>
    /// Locks the source account (FOR NO KEY UPDATE) and the run cursor (FOR UPDATE), in the documented lock order, then reads the
    /// committed posted balance and the as-of fact sequence they protect, builds the feature snapshot, scores it with the pinned model
    /// and freezes the evidence for the next fact position. Because both locks are held to commit, no fact or posting can change the
    /// inputs between scoring and commit (the revalidation is by lock, not by a later re-read).
    /// </summary>
    private static async Task<(InferenceEvidenceV1 Evidence, long Available)> InferAsync(
        Npgsql.NpgsqlConnection connection, Npgsql.NpgsqlTransaction tx, RiskModelScorer scorer, ScenarioRunId run, string intentId,
        LockedPayment payment, PaymentReason baseReason, CancellationToken ct)
    {
        var available = (await LockAvailableAsync(connection, tx, [payment.SourceAccountId], ct))[payment.SourceAccountId];
        var cursor = await ReadCursorForUpdateAsync(connection, tx, run.Value, ct)
            ?? throw new InvalidOperationException("The run cursor is not visible in this tenant context.");
        var posted = await ScalarAsync<long>(connection, tx,
            "SELECT posted_minor FROM revenant.account_available_balance WHERE account_id = @a", ct, ("a", payment.SourceAccountId));
        var asOf = cursor.LastFactSeq;
        var built = RiskFeaturesV1.BuildFromLedger(payment.Currency, payment.AmountMinor,
            new SourceBalanceObservation(posted, BalanceMoment.BeforeScreening, asOf), new ScreeningBoundary(asOf));
        if (!built.TryGetValue(out var features))
        {
            throw new RiskScreeningUnavailableException(
                new RiskModelFailure(RiskModelError.FeaturesIncompatible, built.Error.ToString()), built.Error);
        }
        var risk = RiskPolicyCatalog.V1;
        var identity = scorer.Identity;
        if (identity != risk.Model)
        {
            throw new RiskScreeningUnavailableException(new RiskModelFailure(RiskModelError.PinnedIdentityMismatch, $"{identity}"));
        }
        var scored = scorer.Score(features.Vector);
        if (!scored.TryGetValue(out var scorePpm))
        {
            throw new RiskScreeningUnavailableException(scored.Error);
        }
        var evidence = new InferenceEvidenceV1(run, intentId, asOf, checked(asOf + 1), features.Raw, identity.ModelId, identity.ArtifactSha256,
            identity.ManifestSha256, identity.MlNetPackageVersion, risk.PolicyId, identity.ThresholdPpm, scorePpm,
            ModelLedgerReducer.BaseDecision(baseReason));
        return (evidence, available);
    }

    internal static string ReasonText(PaymentReason reason) => reason switch
    {
        PaymentReason.SanctionsHit => "sanctions_hit",
        PaymentReason.VelocityRule => "velocity_rule",
        PaymentReason.NewBeneficiaryAmountRule => "new_beneficiary_amount_rule",
        PaymentReason.MlRiskHold => ReasonMlRiskHold,
        _ => reason.ToString(),
    };
}
