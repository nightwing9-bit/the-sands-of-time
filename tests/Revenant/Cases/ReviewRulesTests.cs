using FsCheck;
using FsCheck.Fluent;
using Marrow.Kernel.Canonical;
using Revenant.Cases;
using Revenant.Timeline;
using Xunit;

namespace Revenant.Tests.Cases;

/// <summary>
/// Spec 07 task 7.4: pure four-eyes rules, the versioned high-value threshold, deterministic non-ML name screening and the
/// review/return fact payloads. **Validates: Requirements R07.05, R07.07**
/// </summary>
public sealed class ReviewRulesTests
{
    private static readonly CaseActor Operator1 = new("staff:op-1", true, false, true);
    private static readonly CaseActor Operator2 = new("staff:op-2", true, false, true);
    private static readonly CaseActor Supervisor1 = new("staff:sup-1", true, true, true);
    private static readonly CaseActor Supervisor2 = new("staff:sup-2", true, true, true);

    private static CaseSnapshot Open(int required) => new(CaseStatus.Open, 1, required, []);

    [Theory]
    [InlineData("USD", 999_999, 1)]
    [InlineData("USD", 1_000_000, 2)]
    [InlineData("USD", 25_000_00, 2)]
    [InlineData("USD", 1, 1)]
    [InlineData("EUR", 1, 2)]
    public void The_high_value_threshold_is_USD_10000_inclusive_and_other_currencies_fail_safe(string currency, long amount, int required)
    {
        Assert.Equal(required, ReviewPolicy.RequiredApprovals(currency, amount));
        Assert.Equal("revenant-review-policy/1", ReviewPolicy.Version);
    }

    [Fact]
    public void A_high_value_release_needs_an_mfa_approval_then_a_different_mfa_supervisor()
    {
        var noMfa = Operator1 with { HasFreshMfa = false };
        Assert.Equal(CaseDecisionKind.MfaRequired, CaseMachine.Decide(Open(2), CaseAction.Approve, noMfa, 1).Kind);

        var first = CaseMachine.Decide(Open(2), CaseAction.Approve, Operator1, 1);
        Assert.Equal((CaseDecisionKind.Accepted, CaseStatus.AwaitingSecondApproval, 2L, 1), (first.Kind, first.NextStatus, first.NextVersion, first.ApprovalOrder));

        var awaiting = new CaseSnapshot(CaseStatus.AwaitingSecondApproval, 2, 2, [Operator1.SubjectId]);
        // Self-approval, even if the first approver is also a supervisor, and a non-supervisor second approver are refused.
        Assert.Equal(CaseDecisionKind.DistinctSupervisorRequired, CaseMachine.Decide(awaiting, CaseAction.Approve, Operator1, 2).Kind);
        Assert.Equal(CaseDecisionKind.DistinctSupervisorRequired, CaseMachine.Decide(awaiting, CaseAction.Approve, Operator2, 2).Kind);
        var selfSupervisor = new CaseSnapshot(CaseStatus.AwaitingSecondApproval, 2, 2, [Supervisor1.SubjectId]);
        Assert.Equal(CaseDecisionKind.DistinctSupervisorRequired, CaseMachine.Decide(selfSupervisor, CaseAction.Approve, Supervisor1, 2).Kind);
        Assert.Equal(CaseDecisionKind.MfaRequired, CaseMachine.Decide(awaiting, CaseAction.Approve, Supervisor1 with { HasFreshMfa = false }, 2).Kind);
        Assert.Equal(CaseDecisionKind.Stale, CaseMachine.Decide(awaiting, CaseAction.Approve, Supervisor1, 1).Kind);

        var second = CaseMachine.Decide(awaiting, CaseAction.Approve, Supervisor1, 2);
        Assert.Equal((CaseDecisionKind.Accepted, CaseStatus.Released, 3L, 2), (second.Kind, second.NextStatus, second.NextVersion, second.ApprovalOrder));

        var released = new CaseSnapshot(CaseStatus.Released, 3, 2, [Operator1.SubjectId, Supervisor1.SubjectId]);
        Assert.Equal(CaseDecisionKind.Closed, CaseMachine.Decide(released, CaseAction.Approve, Supervisor2, 3).Kind);
        Assert.Equal(CaseDecisionKind.Closed, CaseMachine.Decide(released, CaseAction.Reject, Supervisor2, 3).Kind);
    }

    [Fact]
    public void A_low_value_release_needs_one_mfa_approval_and_assigning_needs_no_mfa_but_a_current_version()
    {
        var single = CaseMachine.Decide(Open(1), CaseAction.Approve, Operator1, 1);
        Assert.Equal((CaseStatus.Released, 2L, 1), (single.NextStatus, single.NextVersion, single.ApprovalOrder));
        var assign = CaseMachine.Decide(Open(1), CaseAction.Assign, Operator1 with { HasFreshMfa = false }, 1);
        Assert.Equal((CaseDecisionKind.Accepted, CaseStatus.Open, 2L), (assign.Kind, assign.NextStatus, assign.NextVersion));
        Assert.Equal(CaseDecisionKind.Stale, CaseMachine.Decide(Open(1), CaseAction.Assign, Operator1, 2).Kind);
        Assert.Equal(CaseDecisionKind.NotStaff, CaseMachine.Decide(Open(1), CaseAction.Approve, Operator1 with { IsStaff = false }, 1).Kind);
        Assert.Equal(CaseDecisionKind.MfaRequired, CaseMachine.Decide(Open(1), CaseAction.Reject, Operator1 with { HasFreshMfa = false }, 1).Kind);
        Assert.Equal(CaseStatus.Rejected, CaseMachine.Decide(Open(2), CaseAction.Reject, Operator1, 1).NextStatus);
    }

    /// <summary>
    /// Property: over random sequences of assign/approve/reject commands by random staff (operators and supervisors, with or
    /// without MFA, current or stale version), a case is Released only after exactly its required number of MFA-verified approvals
    /// by distinct people with a supervisor in slot 2, every accepted command advances the version by exactly one, refusals change
    /// nothing, and nothing is accepted after Released or Rejected.
    /// </summary>
    [Fact]
    public void Release_happens_only_after_the_proper_sequence_for_any_command_sequence()
    {
        CaseActor[] actors = [Operator1, Operator2, Supervisor1, Supervisor2];
        var command =
            from action in Gen.Elements(CaseAction.Assign, CaseAction.Approve, CaseAction.Approve, CaseAction.Approve, CaseAction.Reject)
            from who in Gen.Choose(0, actors.Length - 1)
            from mfa in Gen.Frequency((4, Gen.Constant(true)), (1, Gen.Constant(false)))
            from stale in Gen.Frequency((5, Gen.Constant(false)), (1, Gen.Constant(true)))
            select (action, who, mfa, stale);
        var cases = from required in Gen.Choose(1, 2) from n in Gen.Choose(1, 12) from list in Gen.ArrayOf(command, n) select (required, list);

        Prop.ForAll(cases.ToArbitrary(), input =>
        {
            var (required, commands) = input;
            var state = Open(required);
            var mfaOfApprovers = new List<bool>();
            var supervisorOfApprovers = new List<bool>();
            foreach (var (action, who, mfa, stale) in commands)
            {
                var actor = actors[who] with { HasFreshMfa = mfa };
                var terminal = state.Status is CaseStatus.Released or CaseStatus.Rejected;
                var decision = CaseMachine.Decide(state, action, actor, stale ? state.Version + 1 : state.Version);
                if (decision.Kind != CaseDecisionKind.Accepted)
                {
                    if (decision.NextVersion != state.Version || decision.NextStatus != state.Status) { return false; }
                    continue;
                }
                if (terminal || stale || decision.NextVersion != state.Version + 1) { return false; }
                var approvers = state.Approvers;
                if (action == CaseAction.Approve)
                {
                    approvers = [.. approvers, actor.SubjectId];
                    mfaOfApprovers.Add(actor.HasFreshMfa);
                    supervisorOfApprovers.Add(actor.IsSupervisor);
                }
                state = new CaseSnapshot(decision.NextStatus, decision.NextVersion, required, approvers);
                if (state.Status == CaseStatus.Released
                    && (state.Approvers.Count != required || state.Approvers.Distinct().Count() != required
                        || mfaOfApprovers.Contains(false) || (required == 2 && !supervisorOfApprovers[1])))
                {
                    return false;
                }
                if (state.Status == CaseStatus.AwaitingSecondApproval && (required != 2 || state.Approvers.Count != 1)) { return false; }
            }
            return true;
        }).Check(Config.QuickThrowOnFailure.WithMaxTest(500));
    }

    [Fact]
    public void Name_screening_is_a_deterministic_fuzzy_rule_over_the_synthetic_list()
    {
        // Fixture: the synthetic receiving bank "Dune Credit" is one edit from the synthetic entry "Dune Kredit" (score 909 >= 850).
        var hit = NameScreening.Screen("Dune Credit");
        Assert.Equal(("revenant-name-screen/1", "Dune Kredit", 909, 850, true),
            (hit.Version, hit.BestEntry, hit.ScorePermille, hit.ThresholdPermille, hit.IsMatch));
        Assert.Equal(hit, NameScreening.Screen("Dune Credit"));
        // Case, punctuation and token order do not matter; unrelated names stay below the threshold.
        Assert.True(NameScreening.Screen("  holdings, OMBRA ").IsMatch);
        Assert.Equal(1000, NameScreening.Screen("vesper-mutual exchange").ScorePermille);
        foreach (var name in new[] { "Aster Bank", "Bramble Trust", "Cinder Savings", "Ember Bank", "Fjord Union", "" })
        {
            Assert.False(NameScreening.Screen(name).IsMatch, name);
        }
        Assert.Equal(0, NameScreening.Score("", "DUNE"));
        Assert.Equal("DUNE KREDIT", NameScreening.Normalize("dune—kredit!"));
    }

    [Fact]
    public void Comment_text_is_sanitized_and_bounded()
    {
        Assert.Equal("line one\nline two", CaseCommentText.Sanitize("  line one\r\nline\u0007 two \u0000 "));
        Assert.Null(CaseCommentText.Sanitize(" \u0001\t "));
        Assert.Null(CaseCommentText.Sanitize(new string('x', CaseCommentText.MaxLength + 1)));
        Assert.Equal(CaseCommentText.MaxLength, CaseCommentText.Sanitize(new string('x', CaseCommentText.MaxLength))!.Length);
    }

    [Fact]
    public void Review_and_return_fact_payloads_round_trip_canonically_and_fail_closed()
    {
        var fact = new ReviewDecisionFact("P-1", ReviewDecisionAction.Approve, 2, 2, "Released", ReviewPolicy.Version);
        var bytes = ReviewDecisionCodec.Encode(fact);
        Assert.Equal(fact, ReviewDecisionCodec.Decode(bytes));
        Assert.Equal(bytes, ReviewDecisionCodec.Encode(ReviewDecisionCodec.Decode(bytes)));
        Assert.Throws<CanonicalFormatException>(() => ReviewDecisionCodec.Decode(bytes[..^1]));
        Assert.Throws<CanonicalFormatException>(() => ReviewDecisionCodec.Decode([.. bytes, 1]));
        Assert.Throws<CanonicalFormatException>(() => ReviewDecisionCodec.Encode(fact with { ApprovalOrder = 3 }));
        Assert.Throws<CanonicalFormatException>(() => ReviewDecisionCodec.Encode(fact with { Action = ReviewDecisionAction.Reject }));

        var ret = new ReturnInstructionFact("P-1", "customer_recall");
        Assert.Equal(ret, ReturnInstructionCodec.Decode(ReturnInstructionCodec.Encode(ret)));
        Assert.Throws<CanonicalFormatException>(() => ReturnInstructionCodec.Encode(ret with { Reason = "" }));

        // The new kinds travel in the existing envelope.
        var envelope = FinanceFactCodec.Decode(FinanceFactCodec.Encode(
            new FinanceFact(FinanceFactKind.ReviewDecision, 3, ReviewDecisionCodec.SchemaVersion, bytes, null)));
        Assert.Equal(FinanceFactKind.ReviewDecision, envelope.Kind);
    }
}
