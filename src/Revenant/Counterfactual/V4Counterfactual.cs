using Marrow.Kernel.Canonical;
using Marrow.Timeline;
using Revenant.Cases;
using Revenant.Contracts;
using Revenant.Finance;
using Revenant.Intelligence;
using Revenant.Observations;
using Revenant.Payments;
using Revenant.Timeline;

namespace Revenant.Counterfactual;

internal enum V4ComparisonMode { PolicyOnly, PolicyAndSandboxIntervention }
internal enum V4ActionDisposition { Applied, InapplicableUnderBranch }

/// <summary>An authorization receipt resolved by the trusted history reader or authenticated sandbox admission.
/// The actor, expected version and time are never supplied by an untrusted replay request.</summary>
internal sealed record V4ReplayAction(string Reference, string IntentId, long At, long ExpectedVersion, CaseActor Actor,
    CaseAction? Review, long? SourceFact, string? ReturnReason = null);
internal sealed record V4ActionEvidence(V4ReplayAction Action, V4ActionDisposition Disposition, string Code, long? BranchFact);
internal sealed record V4ReplaySource(HistoryManifest Manifest, VerifiedReplay<ObservedFinanceState> Verification,
    ObservedFinanceState Genesis, ObservedFinanceContext Context, IReadOnlyList<V4StoredFact> Facts,
    IReadOnlyList<V4ReplayAction> CommittedActions);
internal sealed record V4ReplayPlan(ObservedFinanceContext Context, ObservedFinanceState Fork, ObservedFinanceState SourceFinal,
    ObservedFinanceState AlternativeFinal, IReadOnlyList<V4StoredFact> Suffix, IReadOnlyList<V4ActionEvidence> Actions,
    V4ProjectionHistory SourceHistory, V4ProjectionHistory AlternativeHistory);

internal static class V4Counterfactual
{
    internal const string ContinuationPolicy = "analyst-continuation-v1";

    /// <summary>Validate the verified source before creating any durable child, and compute the child suffix.</summary>
    internal static V4ReplayPlan Plan(V4ReplaySource source, long forkPosition, long horizon, ScenarioRunId child,
        ObservedFinanceContext alternate, V4ComparisonMode mode, IReadOnlyList<V4ReplayAction> interventions,
        BehavioralRiskScorerV2 model, CancellationToken ct = default)
    {
        var m = source.Manifest;
        var v = source.Verification;
        var sourceContext = source.Context;
        if (m.ParentId is not null || m.Versions != ObservedFinanceTimelineVersions.V4 || m.Id != v.Timeline
            || m.Status != HistoryStatus.Verified || v.Status != HistoryStatus.Verified || m.LastPosition != v.FinalPosition
            || m.LastRecordHash != v.LastRecordHash || m.LatestStateHash != v.FinalStateHash || source.Facts.Count != v.FinalPosition
            || m.RulesHash != V4BranchExecution.FamilyRulesHash || m.SeedHash != sourceContext.ManifestSha256
            || source.Genesis.LastFact != 0 || child.IsEmpty || child == source.Genesis.RunId
            || m.ContextHash != ContextDigest(sourceContext)
            || m.ContextArtifactRef != "sha256:" + m.ContextHash
            || sourceContext.BasePolicy != PolicyCatalog.RefOf(PolicyCatalog.V7)
            || sourceContext.RiskMode != ObservedRiskMode.ModelRequired || model.Pin != sourceContext.RiskModel)
            throw new InvalidOperationException("Unverified or incompatible V4 source.");

        var alternativeIsSource = ContextDigest(alternate) == m.ContextHash;
        if (!alternativeIsSource) V4BranchExecution.ValidateChildContext(sourceContext, alternate);
        if (!Enum.IsDefined(mode) || (mode == V4ComparisonMode.PolicyOnly) != (interventions.Count == 0))
            throw new InvalidOperationException("Sandbox interventions require the explicit intervention mode.");

        var historicalActions = source.CommittedActions.Where(a => a.SourceFact.HasValue).ToDictionary(a => a.SourceFact!.Value);
        if (historicalActions.Count != source.CommittedActions.Count
            || historicalActions.Keys.Any(p => p <= 0 || p > source.Facts.Count
                || source.Facts[(int)p - 1].Input.Kind is not (ObservedFactKind.ReviewDecision or ObservedFactKind.ReturnInstruction)))
            throw new InvalidOperationException("UnsupportedManualAction: invalid action provenance.");
        if (interventions.Any(a => a.SourceFact is not null || a.Review is not (CaseAction.Approve or CaseAction.Reject)
                || a.ReturnReason is not null || a.ExpectedVersion < 1 || !Authorized(a.Actor))
            || source.CommittedActions.Concat(interventions).Select(a => a.Reference).Distinct(StringComparer.Ordinal).Count()
                != source.CommittedActions.Count + interventions.Count
            || source.CommittedActions.Concat(interventions).Any(a => string.IsNullOrWhiteSpace(a.Reference)))
            throw new InvalidOperationException("UnsupportedManualAction: invalid intervention provenance.");

        var sourceState = source.Genesis;
        var sourceCases = new Dictionary<string, CaseSnapshot>(StringComparer.Ordinal);
        Dictionary<string, CaseSnapshot>? childCases = null;
        ObservedFinanceState? fork = null, sourceAtHorizon = null;
        foreach (var row in source.Facts)
        {
            ct.ThrowIfCancellationRequested();
            var input = row.Input;
            if (input.Kind is ObservedFactKind.ReviewDecision or ObservedFactKind.ReturnInstruction)
            {
                if (!historicalActions.TryGetValue(input.Sequence, out var action) || action.At != input.EffectiveTimeMs
                    || !TryAction(sourceState, sourceCases, sourceContext, action, out var expected, out _)
                    || expected!.Kind != input.Kind || !expected.Payload.AsSpan().SequenceEqual(input.Payload.AsSpan()))
                    throw new InvalidOperationException("UnsupportedManualAction: source review or return cannot be reproduced.");
            }
            sourceState = row.Apply(sourceState, sourceContext);
            if (input.Kind == ObservedFactKind.Screening)
                OpenCase(sourceState, sourceCases, V4ModelScreeningEvidence.Decode(input).IntentId);
            if (input.Kind != ObservedFactKind.ObservationSeal) continue;
            if (input.Sequence == forkPosition)
            {
                fork = sourceState;
                childCases = new(sourceCases, StringComparer.Ordinal);
            }
            if (input.EffectiveTimeMs == horizon) sourceAtHorizon = sourceState;
        }
        if (!SameState(v.FinalState, sourceState) || StateDigest(sourceState) != v.FinalStateHash)
            throw new InvalidOperationException("The source facts do not reproduce verified Marrow state.");
        if (fork is null || sourceAtHorizon is null || fork.LastSealedBoundaryMs >= horizon)
            throw new InvalidOperationException("The fork and horizon must be ordered completed source seals.");
        if (interventions.Any(a => a.At < fork.LastSealedBoundaryMs || a.At >= horizon))
            throw new InvalidOperationException("Sandbox intervention lies outside the comparison.");

        var inherited = new V4ProjectionSegment(sourceContext, source.Facts.Take((int)forkPosition).ToArray(), forkPosition);
        var childState = fork;
        var cases = childCases!;
        var facts = new List<V4StoredFact>();
        var evidence = new List<V4ActionEvidence>();
        var added = new Queue<V4ReplayAction>(interventions.OrderBy(x => x.At).ThenBy(x => x.Reference, StringComparer.Ordinal));
        var scheduledSettlement = source.Facts.Where(x => x.Input.Kind == ObservedFactKind.Settlement)
            .ToDictionary(x => PaymentStepCodec.Decode(x.Input.Payload.AsSpan()), x => x.Input.EffectiveTimeMs, StringComparer.Ordinal);
        var origin = new V4ForkOrigin(source.Genesis.RunId, m.Id, forkPosition, fork.LastSealedBoundaryMs,
            StateDigest(fork), fork.PrefixDigest, "");
        foreach (var row in source.Facts.Skip((int)forkPosition).Take((int)(sourceAtHorizon.LastFact - forkPosition)))
        {
            ct.ThrowIfCancellationRequested();
            var input = row.Input;
            var at = input.EffectiveTimeMs;
            // At a timestamp the completed seal comes first, then original commands in recorded order,
            // then explicit interventions in ordinal reference order.
            while (added.TryPeek(out var action) && action.At < at) ApplyAction(added.Dequeue());
            switch (input.Kind)
            {
                case ObservedFactKind.PaymentIntent:
                    Append(input with { Sequence = Next() });
                    break;
                case ObservedFactKind.Screening:
                    var id = V4ModelScreeningEvidence.Decode(input).IntentId;
                    if (V4ReviewCounts.InWarmup(alternate, at))
                        Append(V4ModelScreeningEvidence.Create(childState, alternate, id, at, [], null, null).ToInput());
                    else
                    {
                        var risk = V4ObservationProjector.RiskAt(ChildHistory(), id, at);
                        var score = model.Score(risk.Vector).Value;
                        Append(V4ModelScreeningEvidence.Create(childState, alternate, id, at, [.. risk.Vector.Encode()],
                            BitConverter.SingleToInt32Bits(score.RawScore), score.RiskPpm).ToInput());
                    }
                    OpenCase(childState, cases, id);
                    // If the original never offered a settlement after this decision, clearing creates
                    // a new derived settlement opportunity; it does not create a customer instruction.
                    if (!scheduledSettlement.ContainsKey(id)) Settle(id, at);
                    break;
                case ObservedFactKind.Settlement:
                    Settle(PaymentStepCodec.Decode(input.Payload.AsSpan()), at);
                    break;
                case ObservedFactKind.ReviewDecision:
                case ObservedFactKind.ReturnInstruction:
                    ApplyAction(historicalActions[input.Sequence]);
                    break;
                case ObservedFactKind.ObservationSeal:
                    Append(ObservedFinanceInput.Seal(new(childState.RunId, at, childState.LastSealedBoundaryMs,
                        childState.LastFact, childState.PrefixDigest, BranchActionDigest(child, evidence))));
                    break;
                default:
                    throw new InvalidOperationException("Unknown source fact.");
            }
        }
        if (added.Count != 0 || childState.LastSealedBoundaryMs != horizon)
            throw new InvalidOperationException("The child did not complete the requested horizon.");

        return new(alternate, fork, sourceAtHorizon, childState, facts.AsReadOnly(), evidence.AsReadOnly(),
            new(source.Genesis.RunId, source.Genesis, [new(sourceContext, source.Facts.Take((int)sourceAtHorizon.LastFact).ToArray(), sourceAtHorizon.LastFact)]),
            ChildHistory(snapshot: true));

        V4ProjectionHistory ChildHistory(bool snapshot = false) =>
            new(child, source.Genesis, [inherited, new(alternate, snapshot ? facts.ToArray() : facts, childState.LastFact, origin)]);

        long Next() => checked(childState.LastFact + 1);
        void Append(ObservedFinanceInput fact)
        {
            var result = ObservedFinanceReducer.Apply(childState, fact, alternate);
            childState = result.TryGetValue(out var next) ? next : throw new ObservedFinanceTransitionException(result.Error);
            facts.Add(new(fact, childState.PrefixDigest));
        }
        void Settle(string id, long at)
        {
            if (childState.Ledger.Payments[id].Status is PaymentStatus.Cleared or PaymentStatus.Approved)
                Append(Fact(childState, at, ObservedFactKind.Settlement, PaymentStepCodec.SettlementSchema, PaymentStepCodec.Encode(id)));
        }
        void ApplyAction(V4ReplayAction action)
        {
            if (!TryAction(childState, cases, alternate, action, out var input, out var code))
            {
                evidence.Add(new(action, V4ActionDisposition.InapplicableUnderBranch, code, null));
                return;
            }
            Append(input!);
            evidence.Add(new(action, V4ActionDisposition.Applied, code, input!.Sequence));
            if (action.Review == CaseAction.Approve
                && (!scheduledSettlement.TryGetValue(action.IntentId, out var due) || due <= action.At))
                Settle(action.IntentId, action.At);
        }
    }

    internal static string ContextDigest(ObservedFinanceContext context) => ObservedFinanceEncoding.Hash(new CanonicalWriter()
        .WriteString(ObservedFinanceVersion.ContextSchemaVersion).WriteBytes(ObservedFinanceContextCodec.Instance.EncodeCanonical(context)));

    internal static string StateDigest(ObservedFinanceState state) => ObservedFinanceEncoding.Hash(new CanonicalWriter()
        .WriteString(ObservedFinanceVersion.StateSchemaVersion).WriteBytes(ObservedFinanceStateCodec.Instance.EncodeCanonical(state)));

    internal static bool SameState(ObservedFinanceState a, ObservedFinanceState b) =>
        ObservedFinanceStateCodec.Instance.EncodeCanonical(a).AsSpan().SequenceEqual(ObservedFinanceStateCodec.Instance.EncodeCanonical(b));

    private static bool Authorized(CaseActor actor) => actor.IsStaff && actor.HasFreshMfa && !string.IsNullOrWhiteSpace(actor.SubjectId);

    private static void OpenCase(ObservedFinanceState state, Dictionary<string, CaseSnapshot> cases, string id)
    {
        var p = state.Ledger.Payments[id];
        if (p.Case == CaseStatus.Open) cases.Add(id, new(CaseStatus.Open, 1, p.RequiredApprovals, []));
    }

    private static bool TryAction(ObservedFinanceState state, Dictionary<string, CaseSnapshot> cases, ObservedFinanceContext context,
        V4ReplayAction action, out ObservedFinanceInput? input, out string code)
    {
        input = null;
        code = "ActorAuthority";
        if (!Authorized(action.Actor)) return false;
        code = "PaymentNotFound";
        if (!state.Ledger.Payments.TryGetValue(action.IntentId, out var p)) return false;
        if (action.Review is { } review)
        {
            if (review is not (CaseAction.Approve or CaseAction.Reject) || action.ReturnReason is not null)
                throw new InvalidOperationException("UnsupportedManualAction: review action is not modelled.");
            code = "CaseNotFound";
            if (!cases.TryGetValue(action.IntentId, out var c)) return false;
            var decision = CaseMachine.Decide(c, review, action.Actor, action.ExpectedVersion);
            code = decision.Kind.ToString();
            if (decision.Kind != CaseDecisionKind.Accepted) return false;
            var approve = review == CaseAction.Approve;
            cases[action.IntentId] = c with { Status = decision.NextStatus, Version = decision.NextVersion,
                Approvers = approve ? [.. c.Approvers, action.Actor.SubjectId] : c.Approvers };
            input = Fact(state, action.At, ObservedFactKind.ReviewDecision, ReviewDecisionCodec.SchemaVersion,
                ReviewDecisionCodec.Encode(new(action.IntentId, approve ? ReviewDecisionAction.Approve : ReviewDecisionAction.Reject,
                    decision.ApprovalOrder, c.RequiredApprovals, decision.NextStatus.ToString(), context.ReviewPolicy)));
            return true;
        }
        if (string.IsNullOrWhiteSpace(action.ReturnReason))
            throw new InvalidOperationException("UnsupportedManualAction: return reason is absent.");
        code = "PaymentNotReturnable";
        if (p.Status != PaymentStatus.Settled || p.Trail.Length != action.ExpectedVersion
            || state.Ledger.Accounts[p.Intent.DestAccountId].AvailableMinor < p.Intent.Amount.MinorUnits) return false;
        code = "Accepted";
        input = Fact(state, action.At, ObservedFactKind.ReturnInstruction, ReturnInstructionCodec.SchemaVersion,
            ReturnInstructionCodec.Encode(new(action.IntentId, action.ReturnReason)));
        return true;
    }

    private static ObservedFinanceInput Fact(ObservedFinanceState state, long at, ObservedFactKind kind, string schema, byte[] payload) =>
        new(state.RunId, checked(state.LastFact + 1), at, kind, schema, [.. payload]);

    // The sandbox has no SQL scheduler. Its seals bind the ordered sandbox action receipts in a distinct digest domain.
    private static string BranchActionDigest(ScenarioRunId child, IReadOnlyList<V4ActionEvidence> actions)
    {
        var w = new CanonicalWriter().WriteString("revenant-v4-counterfactual-actions/1").WriteGuid(child.Value)
            .WriteString(ContinuationPolicy).WriteCount(actions.Count);
        foreach (var x in actions)
        {
            var a = x.Action;
            w.WriteString(a.Reference).WriteString(a.IntentId).WriteInt64(a.At).WriteInt64(a.ExpectedVersion)
                .WriteString(a.Actor.SubjectId).WriteBoolean(a.Actor.IsStaff).WriteBoolean(a.Actor.IsSupervisor)
                .WriteBoolean(a.Actor.HasFreshMfa).WriteString(a.Review?.ToString() ?? "").WriteInt64(a.SourceFact ?? 0)
                .WriteString(a.ReturnReason ?? "").WriteString(x.Disposition.ToString()).WriteString(x.Code).WriteInt64(x.BranchFact ?? 0);
        }
        return ObservedFinanceEncoding.Hash(w);
    }
}
