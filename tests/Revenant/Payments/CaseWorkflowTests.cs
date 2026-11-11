using Npgsql;
using Revenant.Cases;
using Revenant.Contracts;
using Revenant.Finance;
using Revenant.Payments;
using Revenant.Tests.Ledger;
using Revenant.Timeline;
using Xunit;
using static Revenant.Tests.Payments.PaymentKit;
using LedgerSql = Revenant.Tests.Ledger.Ledger;

namespace Revenant.Tests.Payments;

/// <summary>
/// Spec 07 task 7.4 against real PostgreSQL (Testcontainers, migrated by <c>scripts/db/migrate.sh</c>), writing as a LOGIN that is
/// only a member of <c>revenant_ledger_writer</c> (not an owner, NOBYPASSRLS): review cases opened with the hold, four-eyes
/// release, optimistic concurrency, comments, reject/return facts, run-staff revocation and transaction-local RLS on pooled
/// connections. **Validates: Requirements R07.04, R07.05, R07.07, R07.08, R07.09, R07.13**
/// </summary>
[Trait("Category", "Integration")]
[Collection(PaymentCollection.Name)]
public sealed class CaseWorkflowTests(LedgerDatabase db)
{
    private static readonly CaseActor Op1 = new("staff:case-op-1", true, false, true);
    private static readonly CaseActor Op2 = new("staff:case-op-2", true, false, true);
    private static readonly CaseActor Sup1 = new("staff:case-sup-1", true, true, true);
    private static readonly CaseActor Sup2 = new("staff:case-sup-2", true, true, true);

    private sealed record HeldPayment(PaymentLedgerStore Store, string Tenant, ScenarioRunId Run, string Intent, long Amount);

    /// <summary>A run in a fresh tenant with one payment BANK-A/ACC-1 -> BANK-D/ACC-1 ("Dune Credit") moved Screened -> Held.</summary>
    private async Task<HeldPayment> HeldAsync(long amount, string? tenant = null, string owner = Operator, string intentId = "H-1", long sequence = 1,
        ScenarioRunId? run = null)
    {
        var store = Store(db.Writer);
        tenant ??= $"t-{Guid.NewGuid():N}";
        var runId = run ?? await OpenAsync(store, tenant: tenant, owner: owner);
        var intent = new PaymentIntent(runId, intentId, sequence, sequence * 1_000, "BANK-A", "BANK-A/ACC-1", "BANK-D", "BANK-D/ACC-1",
            new Money("USD", amount), SyntheticTruth.Legitimate, new PaymentFeatures(false, false));
        Assert.True((await store.SubmitIntentAsync(tenant, Operator, intent, "c", default)).IsSuccess);
        foreach (var (target, version) in new[] { (PaymentStatus.Screened, 1L), (PaymentStatus.Held, 2L) })
        {
            var step = await store.TransitionAsync(tenant, "system:screening",
                new TransitionCommand(runId, intentId, target, version, $"{intentId}-{target}", null), "c", default);
            Assert.True(step.IsSuccess, step.IsFailure ? step.Error.ToString() : null);
        }
        return new HeldPayment(store, tenant, runId, intentId, amount);
    }

    private static Task<Marrow.Kernel.Results.Result<CaseReceipt, PaymentFailure>> Act(
        HeldPayment p, CaseActor actor, CaseAction action, long expected, string commandId) =>
        p.Store.CaseActionAsync(p.Tenant, actor, new CaseCommand(p.Run, p.Intent, action, expected, commandId, null), "c", default);

    private async Task<long> FactCount(Guid run) => await Count(db.Owner, "finance_fact", run);

    [Fact]
    public async Task A_high_value_hold_is_released_only_after_an_mfa_approval_and_then_a_distinct_mfa_supervisor_and_then_settles()
    {
        var p = await HeldAsync(1_500_000);
        var opened = (await p.Store.GetCaseAsync(p.Tenant, p.Run, p.Intent, default))!;
        Assert.Equal((CaseStatus.Open, 1L, 2, PaymentStatus.Held, ReviewPolicy.Version), (opened.Status, opened.Version, opened.RequiredApprovals, opened.PaymentStatus, opened.PolicyVersion));
        // Deterministic screening evidence on the case: receiving bank "Dune Credit" vs synthetic entry "Dune Kredit".
        Assert.Equal(("Dune Credit", "Dune Kredit", 909, true), (opened.Screening.ScreenedName, opened.Screening.BestEntry, opened.Screening.ScorePermille, opened.Screening.IsMatch));
        var bankA = (await p.Store.GetRunAsync(p.Tenant, p.Run, default))!.Banks.Single(b => b.BankId == "BANK-A");
        Assert.Equal(1_500_000, bankA.ReservedMinor); // held: available lower, posted unchanged
        Assert.Equal(bankA.OpeningMinor, bankA.PostedMinor);

        // No MFA: refused, nothing changes and nothing is recorded (the same command id works after stepping up).
        Assert.Equal(PaymentFailureCode.MfaRequired, (await Act(p, Op1 with { HasFreshMfa = false }, CaseAction.Approve, 1, "a1")).Error.Code);
        Assert.Equal(1L, await FactCount(p.Run.Value));

        var first = await Act(p, Op1, CaseAction.Approve, 1, "a1");
        Assert.Equal((CaseStatus.AwaitingSecondApproval, 2L, PaymentStatus.Held, (long?)2), (first.Value.Case.Status, first.Value.Case.Version, first.Value.Case.PaymentStatus, first.Value.FactSequence));

        Assert.Equal(PaymentFailureCode.DistinctSupervisorRequired, (await Act(p, Op1, CaseAction.Approve, 2, "a2-self")).Error.Code);
        Assert.Equal(PaymentFailureCode.DistinctSupervisorRequired, (await Act(p, Op2, CaseAction.Approve, 2, "a2-op")).Error.Code);
        Assert.Equal(PaymentFailureCode.MfaRequired, (await Act(p, Sup1 with { HasFreshMfa = false }, CaseAction.Approve, 2, "a2")).Error.Code);
        Assert.Equal(PaymentFailureCode.StaleVersion, (await Act(p, Sup1, CaseAction.Approve, 1, "a2-stale")).Error.Code);
        // The generic transition can never release a held payment.
        var generic = await p.Store.TransitionAsync(p.Tenant, Sup1.SubjectId,
            new TransitionCommand(p.Run, p.Intent, PaymentStatus.Approved, 3, "generic-release", null), "c", default);
        Assert.Equal(PaymentFailureCode.ReviewRequired, generic.Error.Code);
        Assert.Equal(PaymentStatus.Held, (await p.Store.GetPaymentAsync(p.Tenant, p.Run, p.Intent, default))!.Status);

        var second = await Act(p, Sup1, CaseAction.Approve, 2, "a2");
        Assert.Equal((CaseStatus.Released, 3L, PaymentStatus.Approved, 4L, (long?)3),
            (second.Value.Case.Status, second.Value.Case.Version, second.Value.Case.PaymentStatus, second.Value.Case.PaymentVersion, second.Value.FactSequence));
        Assert.Equal(new[] { (1, Op1.SubjectId, "operator"), (2, Sup1.SubjectId, "supervisor") },
            second.Value.Case.Approvals.Select(a => (a.Order, a.SubjectId, a.Role)).ToArray());
        var payment = (await p.Store.GetPaymentAsync(p.Tenant, p.Run, p.Intent, default))!;
        Assert.Equal((PaymentStatus.Approved, PaymentLedgerStore.ReasonReviewReleased, Sup1.SubjectId), (payment.Trail[^1].To, payment.Trail[^1].Reason, payment.Trail[^1].Actor));

        // Idempotent replay, conflicting reuse of a command id.
        var replay = await Act(p, Sup1, CaseAction.Approve, 2, "a2");
        Assert.True(replay.Value.IsReplay);
        Assert.Equal(PaymentFailureCode.ConflictingDuplicate, (await Act(p, Sup1, CaseAction.Reject, 3, "a2")).Error.Code);
        Assert.Equal(PaymentFailureCode.IllegalTransition, (await Act(p, Sup2, CaseAction.Reject, 3, "late-reject")).Error.Code);

        // Ordered canonical facts in the live transactions: intent, approval 1, approval 2 (gap-free, hashes match, decodable).
        await using (var cmd = db.Owner.CreateCommand(
            "SELECT fact_seq, kind, schema_version, canonical, canonical_sha256 FROM revenant.finance_fact WHERE scenario_run_id = @r ORDER BY fact_seq"))
        {
            cmd.Parameters.AddWithValue("r", p.Run.Value);
            var facts = new List<(long, string, string, byte[], byte[])>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                facts.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), (byte[])reader[3], (byte[])reader[4]));
            }
            Assert.Equal(new[] { 1L, 2L, 3L }, facts.Select(f => f.Item1).ToArray());
            Assert.Equal(new[] { "PaymentIntent", "ReviewDecision", "ReviewDecision" }, facts.Select(f => f.Item2).ToArray());
            Assert.All(facts, f => Assert.Equal(FinanceFactCodec.PayloadHash(f.Item4), f.Item5));
            Assert.Equal(new ReviewDecisionFact(p.Intent, ReviewDecisionAction.Approve, 2, 2, "Released", ReviewPolicy.Version), ReviewDecisionCodec.Decode(facts[2].Item4));
        }
        Assert.Equal(3L, await Scalar<long>(db.Owner,
            "SELECT count(*) FROM revenant.outbox WHERE aggregate_id = 'case:' || replace(@c::text, '-', '')", ("c", opened.CaseId)));

        // Held -> decision -> settlement: the reservation is consumed exactly once by the posting.
        var cleared = await p.Store.TransitionAsync(p.Tenant, "system:release",
            new TransitionCommand(p.Run, p.Intent, PaymentStatus.Cleared, 4, "clear-1", null), "c", default);
        Assert.True(cleared.IsSuccess);
        var batch = await p.Store.SettleBatchAsync(p.Tenant, "worker:test", 10, "c", default);
        Assert.Equal(PaymentStatus.Settled, Assert.Single(batch.Outcomes).Status);
        bankA = (await p.Store.GetRunAsync(p.Tenant, p.Run, default))!.Banks.Single(b => b.BankId == "BANK-A");
        Assert.Equal((0L, bankA.OpeningMinor - 1_500_000), (bankA.ReservedMinor, bankA.PostedMinor));
    }

    private static async Task<string?> Attempt(NpgsqlDataSource source, string tenant, params (string Sql, (string, object)[] Args)[] statements)
    {
        await using var conn = await source.OpenConnectionAsync();
        await using var tx = await LedgerSql.BeginTenantAsync(conn, tenant);
        try
        {
            foreach (var (sql, args) in statements)
            {
                await LedgerSql.ExecAsync(conn, sql, args);
            }
            await tx.CommitAsync();
            return null;
        }
        catch (PostgresException ex)
        {
            return ex.SqlState;
        }
    }

    [Fact]
    public async Task The_database_itself_refuses_an_early_release_self_approval_and_case_tampering()
    {
        var p = await HeldAsync(2_000_000);
        var c = (await p.Store.GetCaseAsync(p.Tenant, p.Run, p.Intent, default))!.CaseId;
        var paymentId = (await p.Store.GetPaymentAsync(p.Tenant, p.Run, p.Intent, default))!.PaymentId;
        (string, object)[] ids = [("t", p.Tenant), ("r", p.Run.Value), ("c", c), ("p", paymentId)];
        string Approval(int order, string subject, string role, bool mfa = true) =>
            $"INSERT INTO revenant.case_approval (tenant_id, scenario_run_id, case_id, approval_order, subject_id, actor_role, mfa_verified) " +
            $"VALUES (@t, @r, @c, {order}, '{subject}', '{role}', {(mfa ? "true" : "false")})";
        const string ReleasePayment = "UPDATE revenant.payment SET status = 'Approved', version = version + 1 WHERE id = @p";

        // Release without any review: refused at commit.
        Assert.Equal("RV007", await Attempt(db.Writer, p.Tenant, (ReleasePayment, ids)));
        Assert.Equal("RV008", await Attempt(db.Writer, p.Tenant, ("UPDATE revenant.review_case SET status = 'Released', version = 2 WHERE id = @c", ids)));
        Assert.Equal("RV008", await Attempt(db.Writer, p.Tenant, ("UPDATE revenant.review_case SET version = 3 WHERE id = @c", ids)));
        // The writer has no UPDATE privilege on policy columns at all; even the owner cannot change them (trigger).
        Assert.Equal("42501", await Attempt(db.Writer, p.Tenant, ("UPDATE revenant.review_case SET required_approvals = 1, version = 2 WHERE id = @c", ids)));
        Assert.Equal("RV001", await Attempt(db.Owner, p.Tenant, ("UPDATE revenant.review_case SET required_approvals = 1, version = 2 WHERE id = @c", ids)));
        Assert.Equal("23514", await Attempt(db.Writer, p.Tenant, (Approval(1, "staff:x", "operator", mfa: false), ids)));
        Assert.Equal("RV008", await Attempt(db.Writer, p.Tenant, (Approval(2, "staff:x", "supervisor"), ids)));

        // A consistent first approval commits.
        Assert.Null(await Attempt(db.Writer, p.Tenant, (Approval(1, "staff:x", "operator"), ids),
            ("UPDATE revenant.review_case SET status = 'AwaitingSecondApproval', version = 2 WHERE id = @c", ids)));
        // Self-approval of slot 2, a non-supervisor slot 2, an approval the case status does not account for, a Released case
        // whose payment is still Held, and deleting an approval are all refused.
        Assert.Equal("23505", await Attempt(db.Writer, p.Tenant, (Approval(2, "staff:x", "supervisor"), ids),
            ("UPDATE revenant.review_case SET status = 'Released', version = 3 WHERE id = @c", ids), (ReleasePayment, ids)));
        Assert.Equal("23514", await Attempt(db.Writer, p.Tenant, (Approval(2, "staff:y", "operator"), ids)));
        Assert.Equal("RV008", await Attempt(db.Writer, p.Tenant, (Approval(2, "staff:z", "supervisor"), ids)));
        Assert.Equal("RV007", await Attempt(db.Writer, p.Tenant, (Approval(2, "staff:z", "supervisor"), ids),
            ("UPDATE revenant.review_case SET status = 'Released', version = 3 WHERE id = @c", ids)));
        Assert.Equal("42501", await Attempt(db.Writer, p.Tenant, ("DELETE FROM revenant.case_approval WHERE case_id = @c", ids)));
        Assert.Equal("RV001", await Attempt(db.Owner, p.Tenant, ("DELETE FROM revenant.case_approval WHERE case_id = @c", ids)));
        Assert.Equal(PaymentStatus.Held, (await p.Store.GetPaymentAsync(p.Tenant, p.Run, p.Intent, default))!.Status);

        // The proper sequence, even written by hand, commits.
        Assert.Null(await Attempt(db.Writer, p.Tenant, (Approval(2, "staff:z", "supervisor"), ids),
            ("UPDATE revenant.review_case SET status = 'Released', version = 3 WHERE id = @c", ids), (ReleasePayment, ids)));
        Assert.Equal("RV008", await Attempt(db.Writer, p.Tenant, ("UPDATE revenant.review_case SET status = 'Open', version = 4 WHERE id = @c", ids)));
    }

    [Fact]
    public async Task Concurrent_case_edits_from_one_version_have_exactly_one_winner_and_no_lost_update()
    {
        var p = await HeldAsync(3_000_000);
        var assigners = Enumerable.Range(0, 8).Select(i => new CaseActor($"staff:assign-{i}", true, false, false)).ToArray();
        var results = await Task.WhenAll(assigners.Select((a, i) => Task.Run(() => Act(p, a, CaseAction.Assign, 1, $"assign-{i}"))));
        var winners = results.Where(r => r.IsSuccess).ToArray();
        var winner = Assert.Single(winners);
        Assert.All(results.Where(r => r.IsFailure), r => Assert.Equal(PaymentFailureCode.StaleVersion, r.Error.Code));
        var view = (await p.Store.GetCaseAsync(p.Tenant, p.Run, p.Intent, default))!;
        Assert.Equal((2L, winner.Value.Case.AssignedSubject), (view.Version, view.AssignedSubject));

        Assert.True((await Act(p, Op1, CaseAction.Approve, 2, "first")).IsSuccess);
        var racing = await Task.WhenAll(
            Task.Run(() => Act(p, Sup1, CaseAction.Approve, 3, "second-a")),
            Task.Run(() => Act(p, Sup2, CaseAction.Approve, 3, "second-b")),
            Task.Run(() => Act(p, Sup2, CaseAction.Reject, 3, "second-c")));
        Assert.Single(racing, r => r.IsSuccess);
        Assert.All(racing.Where(r => r.IsFailure), r => Assert.Contains(r.Error.Code, new[] { PaymentFailureCode.StaleVersion, PaymentFailureCode.IllegalTransition }));
        view = (await p.Store.GetCaseAsync(p.Tenant, p.Run, p.Intent, default))!;
        Assert.Equal(4L, view.Version);
        var payment = (await p.Store.GetPaymentAsync(p.Tenant, p.Run, p.Intent, default))!;
        Assert.Equal(4L, payment.Version); // exactly one payment step from the race
        Assert.Equal(3L, await FactCount(p.Run.Value));
    }

    [Fact]
    public async Task Rejecting_a_held_payment_releases_its_reservation_once_and_commits_the_decision_fact()
    {
        var p = await HeldAsync(200_000);
        var view = (await p.Store.GetCaseAsync(p.Tenant, p.Run, p.Intent, default))!;
        Assert.Equal(1, view.RequiredApprovals);
        var rejected = await p.Store.CaseActionAsync(p.Tenant, Op1,
            new CaseCommand(p.Run, p.Intent, CaseAction.Reject, 1, "reject-1", "mule_pattern"), "c", default);
        Assert.Equal((CaseStatus.Rejected, PaymentStatus.Rejected, (long?)2), (rejected.Value.Case.Status, rejected.Value.Case.PaymentStatus, rejected.Value.FactSequence));
        Assert.Equal("Released", await Scalar<string>(db.Owner, "SELECT h.status FROM revenant.hold h JOIN revenant.payment p ON p.id = h.payment_id WHERE p.scenario_run_id = @r", ("r", p.Run.Value)));
        Assert.Equal(0L, (await p.Store.GetRunAsync(p.Tenant, p.Run, default))!.Banks.Single(b => b.BankId == "BANK-A").ReservedMinor);
        Assert.Equal("mule_pattern", (await p.Store.GetPaymentAsync(p.Tenant, p.Run, p.Intent, default))!.Trail[^1].Reason);
        Assert.Equal(PaymentFailureCode.IllegalTransition, (await Act(p, Op2, CaseAction.Approve, 2, "late")).Error.Code);
        Assert.True((await p.Store.CaseActionAsync(p.Tenant, Op1,
            new CaseCommand(p.Run, p.Intent, CaseAction.Reject, 1, "reject-1", "mule_pattern"), "c", default)).Value.IsReplay);
        Assert.Equal(2L, await FactCount(p.Run.Value));
    }

    private static TestActor StaffActor(string role = "operator", string? tenant = TenantA) =>
        TestActor.Staff($"staff:{Guid.NewGuid():D}", role, tenant);

    [Fact]
    public async Task Comments_are_sanitized_attributed_to_the_verified_actor_append_only_and_limited_to_current_run_staff()
    {
        var directory = new RevenantRunDirectory(db.Writer);
        var service = new RevenantLedgerService(Store(db.Writer), directory, new RevenantStaffAuthorizer(directory, directory));
        var owner = StaffActor();
        var member = StaffActor("supervisor");
        var outsider = StaffActor("supervisor");
        var opened = await service.OpenRunAsync(owner, 42, null, default);
        var run = opened.Value.Run.RunId;
        Assert.True((await service.GrantStaffAsync(owner, run, member.SubjectId, "supervisor", default)).IsSuccess);
        Assert.Equal(PaymentFailureCode.Forbidden, (await service.GrantStaffAsync(member, run, outsider.SubjectId, "operator", default)).Error.Code);
        var p = await HeldAsync(150_000, TenantA, owner.SubjectId, run: run);

        var posted = await service.CommentAsync(member, run, p.Intent, "  checked\u0007 the beneficiary\r\nhistory  ", "comment-1", "c", default);
        var comment = Assert.Single(posted.Value.Case.Comments);
        Assert.Equal((member.SubjectId, "checked the beneficiary\nhistory"), (comment.AuthorSubject, comment.Body));
        Assert.True((await service.CommentAsync(member, run, p.Intent, "  checked\u0007 the beneficiary\r\nhistory  ", "comment-1", "c", default)).Value.IsReplay);
        Assert.Equal(PaymentFailureCode.ConflictingDuplicate, (await service.CommentAsync(member, run, p.Intent, "other", "comment-1", "c", default)).Error.Code);
        Assert.Equal(PaymentFailureCode.InvalidRequest, (await service.CommentAsync(member, run, p.Intent, "\u0001 ", "comment-2", "c", default)).Error.Code);

        var refused = new[]
        {
            (await service.CommentAsync(outsider, run, p.Intent, "x", "c-out", "c", default)).Error.Code,
            (await service.CommentAsync(TestActor.Staff(member.SubjectId, "supervisor", TenantB), run, p.Intent, "x", "c-tenant", "c", default)).Error.Code,
            (await service.CommentAsync(TestActor.Tenantless(member.SubjectId, "supervisor"), run, p.Intent, "x", "c-none", "c", default)).Error.Code,
            (await service.CommentAsync(new TestActor("guest:1", Marrow.Kernel.Security.PrincipalKind.Guest, TenantA, ["guest"]), run, p.Intent, "x", "c-guest", "c", default)).Error.Code,
            (await service.GetCaseAsync(outsider, run, p.Intent, default)).Error.Code,
        };
        Assert.All(refused, code => Assert.Equal(PaymentFailureCode.Forbidden, code));
        Assert.Equal(1L, await Count(db.Owner, "case_comment", run.Value));
        // The text never enters the shared outbox; the comment rows cannot be rewritten.
        Assert.Equal(0L, await Scalar<long>(db.Owner, "SELECT count(*) FROM revenant.outbox WHERE payload LIKE '%beneficiary%'"));
        Assert.Equal("42501", await Attempt(db.Writer, TenantA, ("UPDATE revenant.case_comment SET body = 'x' WHERE scenario_run_id = @r", [("r", run.Value)])));
        Assert.Equal("RV001", await Attempt(db.Owner, TenantA, ("DELETE FROM revenant.case_comment WHERE scenario_run_id = @r", [("r", run.Value)])));

        // Revoking a member denies the next request; a revoked member cannot be re-granted.
        Assert.True((await service.GetCaseAsync(member, run, p.Intent, default)).IsSuccess);
        Assert.Equal(PaymentFailureCode.InvalidRequest, (await service.RevokeStaffAsync(owner, run, owner.SubjectId, default)).Error.Code);
        Assert.True((await service.RevokeStaffAsync(owner, run, member.SubjectId, default)).IsSuccess);
        Assert.Equal(PaymentFailureCode.Forbidden, (await service.GetCaseAsync(member, run, p.Intent, default)).Error.Code);
        Assert.Equal(PaymentFailureCode.Forbidden, (await service.CommentAsync(member, run, p.Intent, "x", "c-revoked", "c", default)).Error.Code);
        Assert.Equal(PaymentFailureCode.ConflictingDuplicate, (await service.GrantStaffAsync(owner, run, member.SubjectId, "supervisor", default)).Error.Code);
    }

    [Fact]
    public async Task A_compensating_return_needs_a_run_supervisor_with_mfa_and_commits_a_return_instruction_fact()
    {
        var directory = new RevenantRunDirectory(db.Writer);
        var store = Store(db.Writer);
        var service = new RevenantLedgerService(store, directory, new RevenantStaffAuthorizer(directory, directory));
        var tenant = $"t-{Guid.NewGuid():N}";
        var owner = StaffActor("operator", tenant);
        var supervisor = StaffActor("supervisor", tenant);
        var run = (await service.OpenRunAsync(owner, 42, null, default)).Value.Run.RunId;
        Assert.True((await service.GrantStaffAsync(owner, run, supervisor.SubjectId, "supervisor", default)).IsSuccess);
        var intent = new PaymentIntent(run, "R-1", 1, 1_000, "BANK-A", "BANK-A/ACC-1", "BANK-B", "BANK-B/ACC-1",
            new Money("USD", 50_000), SyntheticTruth.Legitimate, new PaymentFeatures(false, false));
        Assert.True((await store.SubmitIntentAsync(tenant, owner.SubjectId, intent, "c", default)).IsSuccess);
        foreach (var (target, version) in new[] { (PaymentStatus.Screened, 1L), (PaymentStatus.Cleared, 2L) })
        {
            Assert.True((await store.TransitionAsync(tenant, "system", new TransitionCommand(run, "R-1", target, version, $"r-{target}", null), "c", default)).IsSuccess);
        }
        Assert.Equal(PaymentStatus.Settled, Assert.Single((await store.SettleBatchAsync(tenant, "worker", 5, "c", default)).Outcomes).Status);

        Assert.Equal(PaymentFailureCode.Forbidden, (await service.ReturnSettledAsync(owner, run, "R-1", 4, "ret-1", "recall", "c", default)).Error.Code);
        var noMfa = new TestActor(supervisor.SubjectId, Marrow.Kernel.Security.PrincipalKind.Staff, tenant, ["supervisor"]);
        Assert.Equal(PaymentFailureCode.MfaRequired,
            (await service.ReturnSettledAsync(new NoMfaActor(noMfa), run, "R-1", 4, "ret-1", "recall", "c", default)).Error.Code);
        var returned = await service.ReturnSettledAsync(supervisor, run, "R-1", 4, "ret-1", "recall", "c", default);
        Assert.Equal((PaymentStatus.Returned, 5L), (returned.Value.AppliedStatus, returned.Value.AppliedVersion));
        var lastFact = await Scalar<byte[]>(db.Owner,
            "SELECT canonical FROM revenant.finance_fact WHERE scenario_run_id = @r AND kind = 'ReturnInstruction'", ("r", run.Value));
        Assert.Equal(new ReturnInstructionFact("R-1", "recall"), ReturnInstructionCodec.Decode(lastFact));
        // PaymentIntent, Settlement (ordering fact since task 7.5) and ReturnInstruction.
        Assert.Equal(3L, await FactCount(run.Value));
    }

    /// <summary>The verified actor of a supervisor whose session has no fresh MFA step-up.</summary>
    private sealed class NoMfaActor(TestActor inner) : Marrow.Kernel.Security.IActorContext
    {
        public string SubjectId => inner.SubjectId;
        public Marrow.Kernel.Security.PrincipalKind PrincipalKind => inner.PrincipalKind;
        public string? TenantId => inner.TenantId;
        public IReadOnlyCollection<string> Roles => inner.Roles;
        public IReadOnlyCollection<string> Claims => [];
        public bool IsAuthenticated => true;
    }

    [Fact]
    public async Task Tenant_context_is_transaction_local_on_one_pooled_connection_A_then_B_then_A_with_a_non_owner_non_bypass_role()
    {
        var tenantA = $"ta-{Guid.NewGuid():N}";
        var tenantB = $"tb-{Guid.NewGuid():N}";
        var a = await HeldAsync(120_000, tenantA);
        await HeldAsync(130_000, tenantB);
        var caseA = (await a.Store.GetCaseAsync(tenantA, a.Run, a.Intent, default))!.CaseId;
        await using var pool = db.CreateWriterPool(1);

        async Task<(int Pid, long Cases, long Payments, long Approvals, string? Setting)> Observe(string? tenant)
        {
            await using var conn = await pool.OpenConnectionAsync();
            await using var tx = await LedgerSql.BeginTenantAsync(conn, tenant);
            var result = (
                await LedgerSql.ScalarAsync<int>(conn, "SELECT pg_backend_pid()"),
                await LedgerSql.ScalarAsync<long>(conn, "SELECT count(*) FROM revenant.review_case"),
                await LedgerSql.ScalarAsync<long>(conn, "SELECT count(*) FROM revenant.payment"),
                await LedgerSql.ScalarAsync<long>(conn, "SELECT count(*) FROM revenant.case_comment") + await LedgerSql.ScalarAsync<long>(conn, "SELECT count(*) FROM revenant.case_approval"),
                await LedgerSql.ScalarAsync<object>(conn, "SELECT current_setting('revenant.tenant_id', true)") as string);
            await tx.CommitAsync();
            return result;
        }

        var first = await Observe(tenantA);
        var other = await Observe(tenantB);
        var none = await Observe(null);
        var again = await Observe(tenantA);
        Assert.Equal((1L, 1L), (first.Cases, first.Payments));
        Assert.Equal((1L, 1L), (other.Cases, other.Payments));
        Assert.Equal((0L, 0L, 0L), (none.Cases, none.Payments, none.Approvals));
        Assert.True(string.IsNullOrEmpty(none.Setting)); // SET LOCAL did not leak into the next transaction
        Assert.Equal(first, again);
        Assert.Single(new[] { first.Pid, other.Pid, none.Pid, again.Pid }.Distinct()); // one physical connection

        // Writing into tenant A's case from tenant B's context fails (the case is invisible, and WITH CHECK refuses A's tenant id).
        Assert.NotNull(await Attempt(pool, tenantB,
            ("INSERT INTO revenant.case_comment (tenant_id, scenario_run_id, case_id, author_subject, body) VALUES (@t, @r, @c, 'staff:x', 'x')",
                [("t", tenantA), ("r", a.Run.Value), ("c", caseA)])));
        // An UPDATE of tenant A's case from tenant B's context matches no row (RLS), so nothing changes.
        Assert.Null(await Attempt(pool, tenantB, ("UPDATE revenant.review_case SET version = version + 1 WHERE id = @c", [("c", caseA)])));
        Assert.Equal(1L, await Scalar<long>(db.Owner, "SELECT version FROM revenant.review_case WHERE id = @c", ("c", caseA)));

        // Catalog: the runtime role is neither owner nor BYPASSRLS, and RLS is forced on the tenant-owned case/payment tables.
        Assert.False(await Scalar<bool>(db.Owner, "SELECT rolbypassrls FROM pg_roles WHERE rolname = 'revenant_ledger_writer'"));
        Assert.False(await Scalar<bool>(db.Owner,
            "SELECT bool_or(r.rolbypassrls OR r.rolsuper) FROM pg_roles r WHERE pg_has_role('revenant_writer_test', r.oid, 'member')"));
        Assert.Equal(0L, await Scalar<long>(db.Owner,
            "SELECT count(*) FROM pg_tables WHERE schemaname = 'revenant' AND tableowner IN ('revenant_ledger_writer', 'revenant_replay_reader')"));
        Assert.Equal(5L, await Scalar<long>(db.Owner,
            """
            SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
             WHERE n.nspname = 'revenant' AND c.relname IN ('review_case', 'case_approval', 'case_comment', 'payment', 'hold')
               AND c.relrowsecurity AND c.relforcerowsecurity
            """));
    }
}
