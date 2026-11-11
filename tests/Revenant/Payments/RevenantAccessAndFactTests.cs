using FsCheck;
using FsCheck.Fluent;
using Marrow.Kernel.Canonical;
using Marrow.Kernel.Security;
using Revenant.Contracts;
using Revenant.Payments;
using Revenant.Tests.Ledger;
using Revenant.Timeline;
using Xunit;
using static Revenant.Tests.Payments.PaymentKit;

namespace Revenant.Tests.Payments;

/// <summary>
/// Spec 07 task 7.2: staff workflow authorization composed from the shared ownership and membership ports over the real run
/// directory, with no request-supplied tenant or role. **Validates: Requirements R07.07, R07.08**
/// </summary>
[Trait("Category", "Integration")]
[Collection(PaymentCollection.Name)]
public sealed class RevenantAccessTests(LedgerDatabase db)
{
    [Fact]
    public async Task Only_a_staff_member_enrolled_in_the_run_may_use_it_and_every_refusal_is_a_denial_without_detail()
    {
        var store = PaymentKit.Store(db.Writer);
        var directory = new RevenantRunDirectory(db.Writer);
        var authorizer = new RevenantStaffAuthorizer(directory, directory);
        var run = await OpenAsync(store);
        var resource = RevenantResources.Run(run);

        async Task<AccessDecision> Check(IActorContext actor, ResourceRef? r = null, ResourceOperation op = ResourceOperation.Write) =>
            await authorizer.AuthorizeAsync(actor, r ?? resource, op, default);

        Assert.True((await Check(TestActor.Staff(Operator))).IsAllowed);
        Assert.True((await Check(TestActor.Staff(Operator, "supervisor"), op: ResourceOperation.Read)).IsAllowed);
        Assert.True((await Check(TestActor.Staff(Operator, tenant: TenantA))).IsAllowed);

        // A supervisor of nothing: the role alone gives no access to someone else's run (no role bypass).
        Assert.Equal(AccessDenial.NotMember, (await Check(TestActor.Staff("staff:other-supervisor", "supervisor"))).Denial);
        Assert.Equal(AccessDenial.TenantMismatch, (await Check(TestActor.Staff(Operator, tenant: TenantB))).Denial);
        // Task 7.4: an actor without a server-side tenant (never granted or revoked) is denied, even as the run owner.
        Assert.Equal(AccessDenial.TenantMismatch, (await Check(TestActor.Tenantless(Operator))).Denial);
        Assert.Equal(AccessDenial.NotMember, (await Check(new TestActor(Operator, PrincipalKind.Staff, null, []))).Denial);
        Assert.Equal(AccessDenial.NotMember, (await Check(new TestActor(Operator, PrincipalKind.Staff, null, ["guest"]))).Denial);
        Assert.Equal(AccessDenial.OwnerKindMismatch, (await Check(new TestActor(Operator, PrincipalKind.Guest, null, ["guest"]))).Denial);
        Assert.Equal(AccessDenial.Unauthenticated, (await Check(new TestActor("", PrincipalKind.Staff, null, ["operator"], IsAuthenticated: false))).Denial);
        Assert.Equal(AccessDenial.UnknownResource, (await Check(TestActor.Staff(Operator), RevenantResources.Run(new ScenarioRunId(Guid.NewGuid())))).Denial);
        Assert.Equal(AccessDenial.NoPolicy, (await Check(TestActor.Staff(Operator), new ResourceRef("history", run.ToString()))).Denial);
        Assert.Equal(AccessDenial.UnknownResource, (await Check(TestActor.Staff(Operator), new ResourceRef(RevenantResources.RunKind, "not-a-guid"))).Denial);
    }

    [Fact]
    public async Task The_service_derives_tenant_and_owner_from_server_state_and_gives_outsiders_one_indistinguishable_answer()
    {
        var store = PaymentKit.Store(db.Writer);
        var directory = new RevenantRunDirectory(db.Writer);
        var service = new RevenantLedgerService(store, directory, new RevenantStaffAuthorizer(directory, directory));
        var owner = TestActor.Staff("staff:service-owner");
        var outsider = TestActor.Staff("staff:service-outsider", "supervisor");

        var opened = await service.OpenRunAsync(owner, 42, null, default);
        Assert.False(opened.Value.IsReplay);
        var run = opened.Value.Run.RunId;
        Assert.Equal(TenantA, opened.Value.Run.TenantId);
        Assert.Equal(owner.SubjectId, opened.Value.Run.OwnerSubjectId);

        var request = Request(Intents(run)[0]);
        Assert.True((await service.SubmitPaymentAsync(owner, run, request, "c", default)).IsSuccess);
        Assert.True((await service.GetPaymentAsync(owner, run, request.IntentId, default)).IsSuccess);

        // Outsider, unknown run and tenant-mismatched actor all get the same typed failure, not 404 vs 403.
        var unknownRun = new ScenarioRunId(Guid.NewGuid());
        var denied = new[]
        {
            (await service.SubmitPaymentAsync(outsider, run, request, "c", default)).Error.Code,
            (await service.GetPaymentAsync(outsider, run, request.IntentId, default)).Error.Code,
            (await service.GetRunAsync(outsider, run, default)).Error.Code,
            (await service.GetRunAsync(owner, unknownRun, default)).Error.Code,
            (await service.GetRunAsync(TestActor.Staff(owner.SubjectId, tenant: TenantB), run, default)).Error.Code,
        };
        Assert.All(denied, c => Assert.Equal(PaymentFailureCode.Forbidden, c));
        Assert.Equal(PaymentFailureCode.Forbidden, (await service.OpenRunAsync(new TestActor("guest:1", PrincipalKind.Guest, null, ["guest"]), 42, null, default)).Error.Code);
        Assert.Equal(PaymentFailureCode.Forbidden, (await service.OpenRunAsync(TestActor.Tenantless("staff:x"), 42, null, default)).Error.Code);

        // Only the owner's single intake happened.
        Assert.Equal(1, await Count(db.Owner, "payment", run.Value));
        // A malformed intent is a typed failure, never an exception.
        var bad = await service.SubmitPaymentAsync(owner, run, request with { Currency = "usd" }, "c", default);
        Assert.Equal(PaymentFailureCode.InvalidRequest, bad.Error.Code);
    }

    private static SubmitPaymentRequest Request(PaymentIntent i) => new(
        i.IntentId, i.Sequence, i.EventTimeUtcMs, i.SourceBankId, i.SourceAccountId, i.DestBankId, i.DestAccountId,
        i.Amount.Currency, i.Amount.MinorUnits, i.Truth, i.Features.NewBeneficiary, i.Features.SanctionsHit);
}

/// <summary>Spec 07 task 7.2: the ordered canonical fact envelope and the version identity of the additive finance version.</summary>
public sealed class FinanceFactCodecTests
{
    private static readonly byte[] Hash = new byte[32];

    [Fact]
    public void Version_identifiers_are_additive_and_leave_the_v1_key_untouched()
    {
        Assert.Equal("revenant-finance-reducer/2", FinanceLedgerVersion.ReducerVersion);
        Assert.NotEqual(FinanceLedgerVersion.ReducerVersion, FinanceTimelineVersions.V1.ReducerVersion);
        Assert.Equal(FinanceTimelineVersions.DomainId, FinanceLedgerVersion.DomainId);
        Assert.Equal(PaymentLedgerStore.FinanceVersion, FinanceLedgerVersion.ReducerVersion);
        Assert.Equal("revenant-finance-reducer/1", FinanceTimelineVersions.V1.ReducerVersion);
        Assert.Single(FinanceTimelineVersions.All); // V1 stays registered and V2 is not registered before it has a reducer
    }

    [Fact]
    public void A_fact_round_trips_and_malformed_bytes_fail_closed()
    {
        var fact = new FinanceFact(FinanceFactKind.PaymentIntent, 7, "revenant-payment-intent/1", [1, 2, 3], null);
        var bytes = FinanceFactCodec.Encode(fact);
        var decoded = FinanceFactCodec.Decode(bytes);
        Assert.Equal((fact.Kind, fact.Sequence, fact.PayloadSchema), (decoded.Kind, decoded.Sequence, decoded.PayloadSchema));
        Assert.Equal(fact.Payload, decoded.Payload);
        Assert.Null(decoded.Evidence);

        var withEvidence = fact with { Evidence = new FactEvidenceRef("opaque-evidence/0", Hash) };
        var decodedEvidence = FinanceFactCodec.Decode(FinanceFactCodec.Encode(withEvidence));
        Assert.Equal("opaque-evidence/0", decodedEvidence.Evidence!.Schema);
        Assert.Equal(Hash, decodedEvidence.Evidence.Sha256);

        Assert.Throws<CanonicalFormatException>(() => FinanceFactCodec.Decode(bytes[..^1]));
        Assert.Throws<CanonicalFormatException>(() => FinanceFactCodec.Decode([.. bytes, 0]));
        var wrongFormat = (byte[])bytes.Clone();
        wrongFormat[3] = 9;
        Assert.Throws<CanonicalFormatException>(() => FinanceFactCodec.Decode(wrongFormat));
        var unknownKind = (byte[])bytes.Clone();
        unknownKind[4] = 200;
        Assert.Throws<CanonicalFormatException>(() => FinanceFactCodec.Decode(unknownKind));
        Assert.Throws<CanonicalFormatException>(() => FinanceFactCodec.Encode(fact with { Sequence = 0 }));
        Assert.Throws<CanonicalFormatException>(() => FinanceFactCodec.Encode(fact with { Payload = [] }));
        Assert.Throws<CanonicalFormatException>(() => FinanceFactCodec.Encode(fact with { Evidence = new FactEvidenceRef("e", new byte[31]) }));
    }

    [Fact]
    public void Encoding_is_canonical_for_random_facts()
    {
        var facts =
            from sequence in Gen.Choose(1, 1_000_000)
            from length in Gen.Choose(1, 64)
            from payload in Gen.ArrayOf(Gen.Choose(0, 255).Select(b => (byte)b), length)
            from withEvidence in Gen.Elements(false, true)
            select new FinanceFact(
                FinanceFactKind.PaymentIntent, sequence, "revenant-payment-intent/1", payload,
                withEvidence ? new FactEvidenceRef("opaque-evidence/0", FinanceFactCodec.PayloadHash(payload)) : null);
        Prop.ForAll(facts.ToArbitrary(), fact =>
        {
            var bytes = FinanceFactCodec.Encode(fact);
            var decoded = FinanceFactCodec.Decode(bytes);
            return bytes.SequenceEqual(FinanceFactCodec.Encode(decoded)) && decoded.Payload.SequenceEqual(fact.Payload)
                && decoded.Sequence == fact.Sequence;
        }).Check(Config.QuickThrowOnFailure.WithMaxTest(100));
    }
}
