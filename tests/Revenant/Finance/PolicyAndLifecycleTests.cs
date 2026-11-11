using Revenant.Contracts;
using Revenant.Finance;
using Revenant.Tests.Scenario;
using Xunit;

namespace Revenant.Tests.Finance;

/// <summary>Task 5.2. **Validates: Requirements R05.02, R05.09**</summary>
public sealed class PolicyAndLifecycleTests
{
    private static readonly (PaymentStatus From, PaymentStatus To)[] LegalPairs =
    [
        (PaymentStatus.Initiated, PaymentStatus.Screened),
        (PaymentStatus.Screened, PaymentStatus.Held),
        (PaymentStatus.Screened, PaymentStatus.Cleared),
        (PaymentStatus.Screened, PaymentStatus.Rejected),
        (PaymentStatus.Held, PaymentStatus.Approved),
        (PaymentStatus.Held, PaymentStatus.Rejected),
        (PaymentStatus.Held, PaymentStatus.Returned),
        (PaymentStatus.Approved, PaymentStatus.Cleared),
        (PaymentStatus.Approved, PaymentStatus.Returned),
        (PaymentStatus.Cleared, PaymentStatus.Settled),
        (PaymentStatus.Cleared, PaymentStatus.Returned),
        (PaymentStatus.Settled, PaymentStatus.Returned),
    ];

    [Fact]
    public void Lifecycle_allows_exactly_the_documented_transitions()
    {
        foreach (var from in Enum.GetValues<PaymentStatus>())
        {
            foreach (var to in Enum.GetValues<PaymentStatus>())
            {
                Assert.Equal(LegalPairs.Contains((from, to)), PaymentLifecycle.IsLegal(from, to));
            }
        }
        Assert.False(PaymentLifecycle.IsLegalTrail([PaymentStatus.Screened, PaymentStatus.Cleared]));
        Assert.False(PaymentLifecycle.IsLegalTrail([PaymentStatus.Initiated, PaymentStatus.Settled]));
        Assert.True(PaymentLifecycle.IsLegalTrail(
            [PaymentStatus.Initiated, PaymentStatus.Screened, PaymentStatus.Held, PaymentStatus.Approved, PaymentStatus.Cleared, PaymentStatus.Settled]));
    }

    [Fact]
    public void Policy_hashes_are_pinned_and_distinct()
    {
        // Golden values copied from this build's output: a change means a new policy version is required.
        Assert.Equal("sha256:687078f26fefcf081ee0f39e5ff185385c11130b52198e74c3861f7347b5c023", PolicyCatalog.V7.ContentHash);
        Assert.Equal("sha256:b702047a23f2e5e02efc44ef1354aec91e913159f35b69f65b112612f1cbafbd", PolicyCatalog.V8.ContentHash);
        Assert.Equal("sha256:a71bf33d3532bb58298cc637de83b8d5076174dbf1fb977ae03393c71fb90271", PolicyCatalog.FeatureSchemaHash(PolicyCatalog.V8));
        Assert.NotEqual(PolicyCatalog.V7.ContentHash, PolicyCatalog.V8.ContentHash);
        Assert.NotEqual(PolicyCatalog.V7.ContentHash, (PolicyCatalog.V7 with { VelocityWindowMs = 10 * 60 * 1000L }).ContentHash);
    }

    [Theory]
    [InlineData(8, 100_000, false, false, PaymentReason.VelocityRule)]
    [InlineData(8, 99_999, false, false, PaymentReason.None)]
    [InlineData(7, 500_000, false, false, PaymentReason.None)]
    [InlineData(1, 1, false, true, PaymentReason.SanctionsHit)]
    [InlineData(1, 900_000, true, false, PaymentReason.None)]
    public void V7_screening_boundaries(int velocity, long amount, bool newBeneficiary, bool sanctions, PaymentReason expected) =>
        Assert.Equal(expected, PolicyCatalog.V7.Screen(new ScreeningFeatures(velocity, amount, newBeneficiary, sanctions)));

    [Theory]
    [InlineData(5, 1, false, false, PaymentReason.VelocityRule)]
    [InlineData(4, 149_999, true, false, PaymentReason.None)]
    [InlineData(4, 150_000, true, false, PaymentReason.NewBeneficiaryAmountRule)]
    [InlineData(4, 900_000, false, false, PaymentReason.None)]
    [InlineData(1, 1, false, true, PaymentReason.SanctionsHit)]
    public void V8_screening_boundaries(int velocity, long amount, bool newBeneficiary, bool sanctions, PaymentReason expected) =>
        Assert.Equal(expected, PolicyCatalog.V8.Screen(new ScreeningFeatures(velocity, amount, newBeneficiary, sanctions)));

    [Fact]
    public void Pinning_fails_on_any_mismatch_instead_of_using_defaults()
    {
        var manifest = FridayRingScenarioTests.Create(42, new ScenarioRunId(Guid.NewGuid())).Manifest;
        var v8 = PolicyCatalog.RefOf(PolicyCatalog.V8);

        Assert.True(FinanceContext.Pin(manifest, v8, FinanceReducer.Version).IsSuccess);
        AssertMismatch(FinanceContext.Pin(manifest, v8 with { ConfigHash = PolicyCatalog.V7.ContentHash }, FinanceReducer.Version));
        AssertMismatch(FinanceContext.Pin(manifest, v8 with { PolicyId = "policy-v9" }, FinanceReducer.Version));
        AssertMismatch(FinanceContext.Pin(manifest, v8 with { ModelArtifactHash = "sha256:00" }, FinanceReducer.Version));
        AssertMismatch(FinanceContext.Pin(manifest, v8 with { FeatureSchemaHash = "sha256:00" }, FinanceReducer.Version));
        AssertMismatch(FinanceContext.Pin(manifest, v8, "revenant-finance-reducer/0"));
        AssertMismatch(FinanceContext.Pin(manifest with { Seed = 7 }, v8, FinanceReducer.Version));
        // Re-hashed manifest with another feature schema: hash is valid, but the policy's feature schema differs.
        var otherSchema = manifest with { FeatureSchemaVersion = "revenant-features/2" };
        otherSchema = otherSchema with { ContentHash = Revenant.Scenario.ScenarioManifestCodec.ComputeContentHash(otherSchema) };
        AssertMismatch(FinanceContext.Pin(otherSchema, v8, FinanceReducer.Version));
    }

    private static void AssertMismatch(Marrow.Kernel.Results.Result<FinanceContext, FinanceError> result)
    {
        Assert.True(result.IsFailure);
        Assert.Equal(FinanceErrorCode.VersionMismatch, result.Error.Code);
    }
}
