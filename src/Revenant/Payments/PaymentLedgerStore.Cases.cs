using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using Marrow.Kernel.Canonical;
using Marrow.Kernel.Messaging;
using Marrow.Kernel.Results;
using Npgsql;
using Revenant.Cases;
using Revenant.Contracts;
using Revenant.Finance;
using Revenant.Timeline;

namespace Revenant.Payments;

/// <summary>A review case action, issued by a verified staff actor. <see cref="ExpectedVersion"/> is the case version the actor read.</summary>
public sealed record CaseCommand(
    ScenarioRunId RunId, string IntentId, CaseAction Action, long ExpectedVersion, string CommandId, string? Reason);

public sealed record CaseApprovalView(int Order, string SubjectId, string Role, DateTimeOffset DecidedAt);

public sealed record CaseCommentView(long Id, string AuthorSubject, string Body, DateTimeOffset CreatedAt);

public sealed record CaseView(
    Guid CaseId,
    ScenarioRunId RunId,
    string IntentId,
    CaseStatus Status,
    long Version,
    PaymentStatus PaymentStatus,
    long PaymentVersion,
    string Currency,
    long AmountMinor,
    string PolicyVersion,
    int RequiredApprovals,
    string? AssignedSubject,
    NameScreeningResult Screening,
    IReadOnlyList<CaseApprovalView> Approvals,
    IReadOnlyList<CaseCommentView> Comments);

/// <param name="FactSequence">The ordered canonical fact this action committed (approvals and rejections), otherwise null.</param>
public sealed record CaseReceipt(CaseView Case, bool IsReplay, long? FactSequence);

/// <summary>
/// Spec 07 task 7.4: review cases and four-eyes release.
///
/// A case is opened with the hold (same transaction as Screened -> Held). Every case action takes the payment row and then the
/// case row <c>FOR UPDATE</c>, checks the command record (idempotent replay or conflicting duplicate), decides with the pure
/// <see cref="CaseMachine"/> against the persisted version and approvers, and then commits in ONE transaction: the case step,
/// the approval row, the resulting payment step (Held -> Approved on release, Held -> Rejected with the reservation released on
/// rejection), the ordered canonical <see cref="FinanceFactKind.ReviewDecision"/> fact under the run cursor, the command record
/// and the outbox events. PostgreSQL re-checks distinct approvers, the supervisor slot, MFA, slot order and the release sequence
/// (RV007/RV008), so a bug or direct SQL cannot release a payment early. Lock order: payment, case, run cursor.
/// </summary>
public sealed partial class PaymentLedgerStore
{
    public const string EventCaseOpened = "revenant.case.opened";
    public const string EventCaseUpdated = "revenant.case.updated";
    public const string EventCaseCommented = "revenant.case.commented";
    public const string ReasonReviewReleased = "review_released";
    public const string ReasonReviewRejected = "review_rejected";

    public async Task<CaseView?> GetCaseAsync(string tenantId, ScenarioRunId runId, string intentId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await SetTenantAsync(connection, tx, tenantId, ct);
        var caseId = await ScalarAsync<Guid?>(connection, tx,
            """
            SELECT c.id FROM revenant.review_case c JOIN revenant.payment p ON p.id = c.payment_id
             WHERE p.scenario_run_id = @run AND p.intent_id = @intent
            """, ct, ("run", runId.Value), ("intent", intentId));
        var view = caseId is { } id ? await ReadCaseAsync(connection, tx, id, ct) : null;
        await tx.CommitAsync(ct);
        return view;
    }

    /// <summary>
    /// Applies one case action for <paramref name="actor"/> (built by the caller from the verified server-side actor only).
    /// Refusals (stale version, closed case, missing MFA, a second approval by the first approver or by a non-supervisor) change
    /// nothing and record nothing, so the same command id can be retried after the actor steps up.
    /// </summary>
    public async Task<Result<CaseReceipt, PaymentFailure>> CaseActionAsync(
        string tenantId, CaseActor actor, CaseCommand command, string correlationId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(command);
        try
        {
            return await TransientSql.RetryAsync(() => CaseActionCoreAsync(tenantId, actor, command, correlationId, ct), ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // The command key, or the approval slot / distinct-approver keys of a racing approval of the same case.
            return Fail<CaseReceipt>(PaymentFailureCode.ConflictingDuplicate, "A concurrent request already used this command or approval slot.");
        }
    }

    private async Task<Result<CaseReceipt, PaymentFailure>> CaseActionCoreAsync(
        string tenantId, CaseActor actor, CaseCommand command, string correlationId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(actor.SubjectId) || !ValidCorrelation(correlationId)
            || command.RunId.IsEmpty || string.IsNullOrWhiteSpace(command.IntentId) || !Enum.IsDefined(command.Action)
            || command.ExpectedVersion < 1 || !ValidCommandId(command.CommandId)
            || (command.Reason is { } reason && (reason.Length > MaxReasonLength || reason.Length == 0)))
        {
            return Fail<CaseReceipt>(PaymentFailureCode.InvalidRequest, "The case command is malformed.");
        }
        var run = command.RunId.Value;
        var commandKey = $"review:{command.CommandId}";
        var hash = SHA256.HashData(new CanonicalWriter()
            .WriteGuid(run).WriteString(command.IntentId).WriteByte((byte)command.Action)
            .WriteInt64(command.ExpectedVersion).WriteString(command.Reason ?? "").ToArray());

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await SetTenantAsync(connection, tx, tenantId, ct);

        var payment = await ReadPaymentForUpdateAsync(connection, tx, run, command.IntentId, ct);
        if (payment is null)
        {
            return Fail<CaseReceipt>(PaymentFailureCode.PaymentNotFound, "The payment does not exist.");
        }
        Guid caseId;
        CaseSnapshot snapshot;
        await using (var cmd = Command(connection, tx,
            "SELECT id, status, version, required_approvals FROM revenant.review_case WHERE payment_id = @p FOR UPDATE",
            ("p", payment.Id)))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct))
            {
                return Fail<CaseReceipt>(PaymentFailureCode.CaseNotFound, "The payment has no review case.");
            }
            caseId = reader.GetGuid(0);
            snapshot = new CaseSnapshot(Enum.Parse<CaseStatus>(reader.GetString(1)), reader.GetInt64(2), reader.GetInt16(3), []);
        }

        var recorded = await ReadCommandAsync(connection, tx, run, commandKey, ct);
        if (recorded is not null)
        {
            if (!CryptographicOperations.FixedTimeEquals(recorded.RequestHash, hash))
            {
                return Fail<CaseReceipt>(PaymentFailureCode.ConflictingDuplicate,
                    $"Command {command.CommandId} was already used for a different request.");
            }
            var replayed = await ReadCaseAsync(connection, tx, caseId, ct)
                ?? throw new InvalidOperationException("A locked case is not readable.");
            return Result<CaseReceipt, PaymentFailure>.Success(new CaseReceipt(replayed, IsReplay: true, null));
        }

        var approvers = new List<string>();
        await using (var cmd = Command(connection, tx,
            "SELECT subject_id FROM revenant.case_approval WHERE case_id = @c ORDER BY approval_order", ("c", caseId)))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                approvers.Add(reader.GetString(0));
            }
        }
        snapshot = snapshot with { Approvers = approvers };

        var decision = CaseMachine.Decide(snapshot, command.Action, actor, command.ExpectedVersion);
        if (decision.Kind == CaseDecisionKind.Accepted && command.Action != CaseAction.Assign && payment.Status != PaymentStatus.Held)
        {
            decision = decision with { Kind = CaseDecisionKind.Closed };
        }
        switch (decision.Kind)
        {
            case CaseDecisionKind.NotStaff:
                return Fail<CaseReceipt>(PaymentFailureCode.Forbidden, "This run is not available to the caller.");
            case CaseDecisionKind.Stale:
                return Fail<CaseReceipt>(PaymentFailureCode.StaleVersion,
                    $"Expected case version {command.ExpectedVersion}, but the case is at version {snapshot.Version}.");
            case CaseDecisionKind.Closed:
                return Fail<CaseReceipt>(PaymentFailureCode.IllegalTransition, "The case or its payment no longer accepts this action.");
            case CaseDecisionKind.MfaRequired:
                return Fail<CaseReceipt>(PaymentFailureCode.MfaRequired, "Approving or rejecting needs a fresh MFA step-up.");
            case CaseDecisionKind.DistinctSupervisorRequired:
                return Fail<CaseReceipt>(PaymentFailureCode.DistinctSupervisorRequired,
                    "The second approval must come from a different supervisor.");
        }

        var outbox = outboxFactory(tx);
        long? factSequence = null;
        var paymentStatus = payment.Status;
        var paymentVersion = payment.Version;
        switch (command.Action)
        {
            case CaseAction.Assign:
                await ExecAsync(connection, tx,
                    "UPDATE revenant.review_case SET version = @next, assigned_subject = @actor WHERE id = @c", ct,
                    ("next", decision.NextVersion), ("actor", actor.SubjectId), ("c", caseId));
                break;

            case CaseAction.Approve:
                await ExecAsync(connection, tx,
                    """
                    INSERT INTO revenant.case_approval (tenant_id, scenario_run_id, case_id, approval_order, subject_id, actor_role, mfa_verified)
                    VALUES (@tenant, @run, @c, @order, @subject, @role, @mfa)
                    """, ct,
                    ("tenant", tenantId), ("run", run), ("c", caseId), ("order", (short)decision.ApprovalOrder), ("subject", actor.SubjectId),
                    ("role", actor.IsSupervisor ? "supervisor" : "operator"), ("mfa", actor.HasFreshMfa));
                await UpdateCaseStatusAsync(connection, tx, caseId, decision, ct);
                if (decision.NextStatus == CaseStatus.Released)
                {
                    paymentVersion = checked(payment.Version + 1);
                    paymentStatus = PaymentStatus.Approved;
                    await AppendStepAsync(connection, tx, outbox, tenantId, run, payment.Id, command.IntentId, PaymentStatus.Held,
                        PaymentStatus.Approved, paymentVersion, ReasonReviewReleased, commandKey, actor.SubjectId, correlationId, ct);
                }
                factSequence = await AppendFactAsync(connection, tx, tenantId, run, payment.Id, FinanceFactKind.ReviewDecision,
                    ReviewDecisionCodec.SchemaVersion,
                    ReviewDecisionCodec.Encode(new ReviewDecisionFact(command.IntentId, ReviewDecisionAction.Approve, decision.ApprovalOrder,
                        snapshot.RequiredApprovals, decision.NextStatus.ToString(), ReviewPolicy.Version)), actor.SubjectId, correlationId,
                    paymentVersion, ct);
                break;

            case CaseAction.Reject:
                await UpdateCaseStatusAsync(connection, tx, caseId, decision, ct);
                // Releasing the reservation raises the source's available balance, so the account row is locked before the run
                // cursor (task 7.5): every availability change is then ordered by account lock and fact order consistently.
                await LockAvailableAsync(connection, tx, [payment.SourceAccountId], ct);
                await ResolveHoldAsync(connection, tx, outbox, run, payment.Id, "Released", correlationId, ct);
                paymentVersion = checked(payment.Version + 1);
                paymentStatus = PaymentStatus.Rejected;
                await AppendStepAsync(connection, tx, outbox, tenantId, run, payment.Id, command.IntentId, PaymentStatus.Held,
                    PaymentStatus.Rejected, paymentVersion, command.Reason ?? ReasonReviewRejected, commandKey, actor.SubjectId, correlationId, ct);
                factSequence = await AppendFactAsync(connection, tx, tenantId, run, payment.Id, FinanceFactKind.ReviewDecision,
                    ReviewDecisionCodec.SchemaVersion,
                    ReviewDecisionCodec.Encode(new ReviewDecisionFact(command.IntentId, ReviewDecisionAction.Reject, 0,
                        snapshot.RequiredApprovals, decision.NextStatus.ToString(), ReviewPolicy.Version)), actor.SubjectId, correlationId,
                    paymentVersion, ct);
                break;
        }

        await InsertCommandAsync(connection, tx, tenantId, run, commandKey, "Review", payment.Id, hash, "Accepted",
            paymentStatus, paymentVersion, null, actor.SubjectId, ct);
        await outbox.EnqueueAsync(CaseEvent(EventCaseUpdated, run, caseId, payment.Id, decision.NextStatus.ToString(), decision.NextVersion,
            command.Action.ToString(), factSequence, correlationId, $"v{decision.NextVersion}"), ct);
        var view = await ReadCaseAsync(connection, tx, caseId, ct) ?? throw new InvalidOperationException("A just-updated case is not readable.");
        await tx.CommitAsync(ct);
        return Result<CaseReceipt, PaymentFailure>.Success(new CaseReceipt(view, IsReplay: false, factSequence));
    }

    /// <summary>
    /// Appends one sanitized comment by <paramref name="authorSubject"/> (the verified actor) to the payment's case. Comments are
    /// append-only and do not change the case version, so they never conflict with a decision. Idempotent on the command id.
    /// </summary>
    public async Task<Result<CaseReceipt, PaymentFailure>> CommentAsync(
        string tenantId, string authorSubject, ScenarioRunId runId, string intentId, string body, string commandId,
        string correlationId, CancellationToken ct)
    {
        var text = CaseCommentText.Sanitize(body);
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(authorSubject) || runId.IsEmpty
            || string.IsNullOrWhiteSpace(intentId) || text is null || !ValidCommandId(commandId) || !ValidCorrelation(correlationId))
        {
            return Fail<CaseReceipt>(PaymentFailureCode.InvalidRequest, "The comment is malformed.");
        }
        var run = runId.Value;
        var commandKey = $"comment:{commandId}";
        var hash = SHA256.HashData(new CanonicalWriter().WriteGuid(run).WriteString(intentId).WriteString(text).ToArray());
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            await using var tx = await connection.BeginTransactionAsync(ct);
            await SetTenantAsync(connection, tx, tenantId, ct);
            Guid caseId, paymentId;
            PaymentStatus status;
            long version;
            await using (var cmd = Command(connection, tx,
                """
                SELECT c.id, p.id, p.status, p.version FROM revenant.review_case c JOIN revenant.payment p ON p.id = c.payment_id
                 WHERE p.scenario_run_id = @run AND p.intent_id = @intent
                """, ("run", run), ("intent", intentId)))
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                if (!await reader.ReadAsync(ct))
                {
                    return Fail<CaseReceipt>(PaymentFailureCode.CaseNotFound, "The payment has no review case.");
                }
                (caseId, paymentId, status, version) = (reader.GetGuid(0), reader.GetGuid(1), Enum.Parse<PaymentStatus>(reader.GetString(2)), reader.GetInt64(3));
            }
            var recorded = await ReadCommandAsync(connection, tx, run, commandKey, ct);
            if (recorded is not null)
            {
                return CryptographicOperations.FixedTimeEquals(recorded.RequestHash, hash)
                    ? Result<CaseReceipt, PaymentFailure>.Success(new CaseReceipt(
                        await ReadCaseAsync(connection, tx, caseId, ct) ?? throw new InvalidOperationException("Case vanished."), true, null))
                    : Fail<CaseReceipt>(PaymentFailureCode.ConflictingDuplicate, $"Command {commandId} was already used for a different request.");
            }
            var commentId = await ScalarAsync<long>(connection, tx,
                """
                INSERT INTO revenant.case_comment (tenant_id, scenario_run_id, case_id, author_subject, body)
                VALUES (@tenant, @run, @c, @author, @body) RETURNING id
                """, ct, ("tenant", tenantId), ("run", run), ("c", caseId), ("author", authorSubject), ("body", text));
            await InsertCommandAsync(connection, tx, tenantId, run, commandKey, "Comment", paymentId, hash, "Accepted",
                status, version, null, authorSubject, ct);
            // The chain references the comment by id and the SHA-256 of its sanitized text, never the text itself.
            await AppendAuditAsync(connection, tx, tenantId, run, "case.comment", authorSubject, correlationId, $"comment:{commentId}",
                null, SHA256.HashData(Encoding.UTF8.GetBytes(text)), ct);
            // Ids only: the comment text stays in the tenant-scoped table and never enters the shared outbox.
            await outboxFactory(tx).EnqueueAsync(new OutboxMessage(
                DeterministicId($"revenant.case-comment/{commentId}"), EventCaseCommented, 1, $"case:{caseId:N}", clock.GetUtcNow(),
                JsonSerializer.Serialize(new { runId = run, caseId, paymentId, commentId }), correlationId), ct);
            var view = await ReadCaseAsync(connection, tx, caseId, ct) ?? throw new InvalidOperationException("Case vanished.");
            await tx.CommitAsync(ct);
            return Result<CaseReceipt, PaymentFailure>.Success(new CaseReceipt(view, false, null));
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return Fail<CaseReceipt>(PaymentFailureCode.ConflictingDuplicate, "The command id is already in use.");
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Run staff (directory)
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>Adds a staff subject to the run's staff scope. False when the subject was revoked from this run earlier.</summary>
    public async Task<bool> GrantRunStaffAsync(ScenarioRunId runId, string subjectId, string role, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(
            """
            WITH added AS (
              INSERT INTO revenant.scenario_run_staff (scenario_run_id, subject_id, role) VALUES (@run, @subject, @role)
              ON CONFLICT DO NOTHING RETURNING 1)
            SELECT NOT EXISTS (SELECT 1 FROM revenant.scenario_run_staff_revocation WHERE scenario_run_id = @run AND subject_id = @subject)
            """);
        cmd.Parameters.AddWithValue("run", runId.Value);
        cmd.Parameters.AddWithValue("subject", subjectId);
        cmd.Parameters.AddWithValue("role", role);
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    /// <summary>Permanently removes a subject from the run's staff scope. Effective on the subject's next request or hub check.</summary>
    public async Task<bool> RevokeRunStaffAsync(ScenarioRunId runId, string subjectId, string revokedBy, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(
            """
            INSERT INTO revenant.scenario_run_staff_revocation (scenario_run_id, subject_id, revoked_by)
            SELECT @run, @subject, @by WHERE EXISTS (
              SELECT 1 FROM revenant.scenario_run_staff WHERE scenario_run_id = @run AND subject_id = @subject)
            ON CONFLICT DO NOTHING
            """);
        cmd.Parameters.AddWithValue("run", runId.Value);
        cmd.Parameters.AddWithValue("subject", subjectId);
        cmd.Parameters.AddWithValue("by", revokedBy);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Shared case steps
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>Opens the review case of a payment that has just been held, with its policy and screening evidence.</summary>
    private async Task OpenCaseAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, IOutboxWriter outbox, string tenantId, Guid run, LockedPayment payment,
        string correlationId, CancellationToken ct)
    {
        // The screened party is the receiving bank as named in the synthetic scenario (the only party name the intents carry).
        var screenedName = await ScalarAsync<string>(connection, tx,
            "SELECT b.name FROM revenant.account a JOIN revenant.bank b ON b.id = a.bank_id WHERE a.id = @a", ct,
            ("a", payment.DestAccountId)) ?? throw new InvalidOperationException("The destination bank of a locked payment is not visible.");
        var screening = NameScreening.Screen(screenedName);
        var required = ReviewPolicy.RequiredApprovals(payment.Currency, payment.AmountMinor);
        var caseId = await ScalarAsync<Guid>(connection, tx,
            """
            INSERT INTO revenant.review_case
              (tenant_id, scenario_run_id, payment_id, currency, amount_minor, policy_version, required_approvals, screening_version,
               screened_name, screening_entry, screening_score_permille, screening_threshold_permille, screening_match)
            VALUES (@tenant, @run, @payment, @currency, @amount, @policy, @required, @sversion, @name, @entry, @score, @threshold, @match)
            RETURNING id
            """, ct,
            ("tenant", tenantId), ("run", run), ("payment", payment.Id), ("currency", payment.Currency), ("amount", payment.AmountMinor),
            ("policy", ReviewPolicy.Version), ("required", (short)required), ("sversion", screening.Version), ("name", screening.ScreenedName),
            ("entry", (object?)screening.BestEntry ?? DBNull.Value), ("score", screening.ScorePermille),
            ("threshold", screening.ThresholdPermille), ("match", screening.IsMatch));
        await outbox.EnqueueAsync(CaseEvent(EventCaseOpened, run, caseId, payment.Id, nameof(CaseStatus.Open), 1, "Open", null,
            correlationId, "v1"), ct);
    }

    private static Task<int> UpdateCaseStatusAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid caseId, CaseDecision decision, CancellationToken ct) =>
        ExecAsync(connection, tx, "UPDATE revenant.review_case SET status = @status, version = @next WHERE id = @c", ct,
            ("status", decision.NextStatus.ToString()), ("next", decision.NextVersion), ("c", caseId));

    /// <summary>
    /// Appends one ordered canonical fact under the run cursor (row lock to commit): the next gap-free <c>fact_seq</c>, the
    /// payload bytes and their SHA-256. The intent sequence and business clock are left unchanged.
    /// </summary>
    private static async Task<long> AppendFactAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, string tenantId, Guid run, Guid paymentId, FinanceFactKind kind,
        string schema, byte[] payload, string actor, string correlationId, long paymentVersion, CancellationToken ct,
        FactEvidenceRef? evidence = null)
    {
        var sequence = await AppendFactCoreAsync(connection, tx, tenantId, run, paymentId, kind, schema, payload, ct, evidence);
        // Task 7.5: every committed fact is also a link of the run's audit chain, in the same transaction.
        await AppendAuditAsync(connection, tx, tenantId, run, $"fact.{kind.ToString().ToLowerInvariant()}", actor, correlationId,
            $"fact:{sequence}", paymentVersion, FinanceFactCodec.PayloadHash(payload), ct);
        return sequence;
    }

    private static async Task<long> AppendFactCoreAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, string tenantId, Guid run, Guid paymentId, FinanceFactKind kind,
        string schema, byte[] payload, CancellationToken ct, FactEvidenceRef? evidence = null)
    {
        var sequence = await ScalarAsync<long?>(connection, tx,
            "UPDATE revenant.run_cursor SET last_fact_seq = last_fact_seq + 1 WHERE scenario_run_id = @run RETURNING last_fact_seq", ct,
            ("run", run)) ?? throw new InvalidOperationException("The run cursor is not visible in this tenant context.");
        await ExecAsync(connection, tx,
            """
            INSERT INTO revenant.finance_fact
              (tenant_id, scenario_run_id, fact_seq, kind, schema_version, payment_id, canonical, canonical_sha256, evidence_schema, evidence_sha256)
            VALUES (@tenant, @run, @seq, @kind, @schema, @payment, @canonical, @sha, @eschema, @esha)
            """, ct,
            ("tenant", tenantId), ("run", run), ("seq", sequence), ("kind", kind.ToString()), ("schema", schema), ("payment", paymentId),
            ("canonical", payload), ("sha", FinanceFactCodec.PayloadHash(payload)),
            ("eschema", (object?)evidence?.Schema ?? DBNull.Value), ("esha", (object?)evidence?.Sha256 ?? DBNull.Value));
        return sequence;
    }

    private OutboxMessage CaseEvent(
        string eventType, Guid run, Guid caseId, Guid paymentId, string status, long version, string action, long? factSequence,
        string correlationId, string discriminator) =>
        new(DeterministicId($"revenant.case/{caseId:N}/{discriminator}"), eventType, 1, $"case:{caseId:N}", clock.GetUtcNow(),
            JsonSerializer.Serialize(new { runId = run, caseId, paymentId, status, version, action, factSequence }), correlationId);

    private static async Task<CaseView?> ReadCaseAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid caseId, CancellationToken ct)
    {
        CaseView? view;
        await using (var cmd = Command(connection, tx,
            """
            SELECT c.id, c.scenario_run_id, p.intent_id, c.status, c.version, p.status, p.version, c.currency, c.amount_minor,
                   c.policy_version, c.required_approvals, c.assigned_subject, c.screening_version, c.screened_name, c.screening_entry,
                   c.screening_score_permille, c.screening_threshold_permille, c.screening_match
              FROM revenant.review_case c JOIN revenant.payment p ON p.id = c.payment_id WHERE c.id = @c
            """, ("c", caseId)))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct))
            {
                return null;
            }
            view = new CaseView(
                reader.GetGuid(0), new ScenarioRunId(reader.GetGuid(1)), reader.GetString(2), Enum.Parse<CaseStatus>(reader.GetString(3)),
                reader.GetInt64(4), Enum.Parse<PaymentStatus>(reader.GetString(5)), reader.GetInt64(6), reader.GetString(7), reader.GetInt64(8),
                reader.GetString(9), reader.GetInt16(10), reader.IsDBNull(11) ? null : reader.GetString(11),
                new NameScreeningResult(reader.GetString(12), reader.GetString(13), reader.IsDBNull(14) ? null : reader.GetString(14),
                    reader.GetInt32(15), reader.GetInt32(16), reader.GetBoolean(17)),
                [], []);
        }
        var approvals = new List<CaseApprovalView>();
        await using (var cmd = Command(connection, tx,
            "SELECT approval_order, subject_id, actor_role, decided_at FROM revenant.case_approval WHERE case_id = @c ORDER BY approval_order",
            ("c", caseId)))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                approvals.Add(new CaseApprovalView(reader.GetInt16(0), reader.GetString(1), reader.GetString(2), Utc(reader.GetDateTime(3))));
            }
        }
        var comments = new List<CaseCommentView>();
        await using (var cmd = Command(connection, tx,
            "SELECT id, author_subject, body, created_at FROM revenant.case_comment WHERE case_id = @c ORDER BY id", ("c", caseId)))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                comments.Add(new CaseCommentView(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), Utc(reader.GetDateTime(3))));
            }
        }
        return view with { Approvals = approvals, Comments = comments };
    }

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
