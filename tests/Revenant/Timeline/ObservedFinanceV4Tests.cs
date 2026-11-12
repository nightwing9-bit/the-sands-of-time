using System.Security.Cryptography;
using Marrow.Kernel.Canonical;
using Revenant.Cases;
using Revenant.Contracts;
using Revenant.Finance;
using Revenant.Scenario;
using Revenant.Timeline;
using Xunit;

namespace Revenant.Tests.Timeline;

public sealed class ObservedFinanceV4Tests
{
    internal const long Origin = 1_789_203_000_000; // 2026-09-12 08:50 UTC
    internal const long Nine = Origin + 600_000;
    internal static readonly ScenarioRunId RunId = new(Guid.Parse("00000000-0000-0000-0000-000000000904"));
    internal static readonly string EmptyDigest = new('0', 64);

    [Fact]
    public void Canonical_V4_fixtures_and_versions_are_frozen()
    {
        var (run, context, initial) = Start();
        var state = Apply(initial, Seal(initial), context);
        var contextBytes = ObservedFinanceContextCodec.Instance.EncodeCanonical(context);
        var stateBytes = ObservedFinanceStateCodec.Instance.EncodeCanonical(state);
        var seal = Seal(state);
        var sealBytes = seal.Payload.ToArray();

        Assert.Equal("revenant-finance-reducer/4", ObservedFinanceVersion.ReducerVersion);
        Assert.Equal("revenant-finance-state/4", ObservedFinanceVersion.StateSchemaVersion);
        Assert.Equal("revenant-finance-context/4", ObservedFinanceVersion.ContextSchemaVersion);
        Assert.Equal("revenant-finance-fact/4", ObservedFinanceVersion.InputSchemaVersion);
        Assert.Equal("sim-scheduler/1", ObservedFinanceVersion.SchedulerContract);
        Assert.Equal("five-minute-seal/1", ObservedFinanceVersion.ObservationContract);
        Assert.Equal("observation-seal/1", ObservedFinanceVersion.SealSchema);
        Assert.Equal(ObservedFinanceTimelineVersions.V4, Assert.Single(ObservedFinanceTimelineVersions.All).Versions);
        Assert.Equal(contextBytes, ObservedFinanceContextCodec.Instance.EncodeCanonical(ObservedFinanceContextCodec.Instance.Decode(contextBytes)));
        Assert.Equal(stateBytes, ObservedFinanceStateCodec.Instance.EncodeCanonical(ObservedFinanceStateCodec.Instance.Decode(stateBytes)));
        Assert.Equal(sealBytes, ObservationSealCodec.Encode(ObservationSealCodec.Decode(sealBytes)));
        var envelope = ObservedFinanceInputCodec.Instance.EncodeCanonical(seal);
        Assert.Equal(envelope, ObservedFinanceInputCodec.Instance.EncodeCanonical(ObservedFinanceInputCodec.Instance.Decode(envelope)));
        Assert.Equal(RunId, run.RunId);
        Assert.Equal("a902cb16638eebffbd49b114dce2eac20387d23a85789966b90e3d4280d530ea", Hash(contextBytes));
        Assert.Equal("61b8376d350ea8566547e97332c6d3c17451976e17ea544fe425b83dd7e6ae6a", Hash(stateBytes));
        Assert.Equal("cb5e296e79d74135d65f9a42c44b43d47d75d6e5bcb9cad4e3cfd2502a21253f", Hash(sealBytes));
    }

    [Fact]
    public void Malformed_and_contradictory_contracts_fail_closed()
    {
        var (run, context, state) = Start();
        var seal = Seal(state);
        Assert.Equal(LedgerFinanceErrorCode.VersionMismatch, ObservedFinanceReducer.Initial(
            run, Context(context.ScenarioVersion, "sha256:" + EmptyDigest, context.OriginMs)).Error.Code);
        Reject(ObservedFinanceContextCodec.Instance.EncodeCanonical(context), b => ObservedFinanceContextCodec.Instance.Decode(b));
        Reject(ObservedFinanceStateCodec.Instance.EncodeCanonical(state), b => ObservedFinanceStateCodec.Instance.Decode(b));
        Reject(ObservedFinanceInputCodec.Instance.EncodeCanonical(seal), b => ObservedFinanceInputCodec.Instance.Decode(b));
        Reject(seal.Payload.ToArray(), b => ObservationSealCodec.Decode(b));
        Assert.Throws<CanonicalFormatException>(() => ObservedFinanceInputCodec.Instance.EncodeCanonical(seal with { Kind = (ObservedFactKind)255 }));
        Assert.Throws<CanonicalFormatException>(() => ObservedFinanceInputCodec.Instance.EncodeCanonical(seal with { Sequence = 2 }));
        Assert.Throws<CanonicalFormatException>(() => ObservedFinanceInputCodec.Instance.EncodeCanonical(seal with { RunId = new(Guid.Empty) }));
        Assert.Throws<CanonicalFormatException>(() => ObservedFinanceInputCodec.Instance.EncodeCanonical(seal with { EffectiveTimeMs = Origin }));
        Assert.Throws<CanonicalFormatException>(() => ObservationSealCodec.Encode(ObservationSealCodec.Decode(seal.Payload.AsSpan()) with
        {
            PrefixFactDigestSha256 = "00",
        }));
        Assert.Throws<CanonicalFormatException>(() => Context("future-scenario/2", "sha256:" + EmptyDigest, Origin + 1));
        Assert.Throws<CanonicalFormatException>(() => Context("future-scenario/2", "sha256:" + EmptyDigest, long.MaxValue - long.MaxValue % 300_000));
        Assert.Throws<CanonicalFormatException>(() => new ObservedFinanceContext(context.ScenarioVersion, context.ManifestSha256,
            Origin, context.BasePolicy, context.ReviewPolicy, ObservedRiskMode.ModelRequired, "", null, [], []));
        Assert.Throws<CanonicalFormatException>(() => new ObservedFinanceContext(context.ScenarioVersion, context.ManifestSha256,
            Origin, context.BasePolicy, context.ReviewPolicy, ObservedRiskMode.NotRequired, "test-features/1", null, [], []));

        var enhanced = new ObservedFinanceContext("future-scenario/2", "sha256:" + EmptyDigest, -300_000, PolicyCatalog.RefOf(PolicyCatalog.V8),
            ReviewPolicy.Version, ObservedRiskMode.ModelRequired, "test-features/1", new("test-risk/1", EmptyDigest, 5),
            [new("test-z/1", EmptyDigest), new("test-a/1", EmptyDigest)], []);
        var bytes = ObservedFinanceContextCodec.Instance.EncodeCanonical(enhanced);
        var decoded = ObservedFinanceContextCodec.Instance.Decode(bytes);
        Assert.Equal(new[] { "test-a/1", "test-z/1" }, decoded.AnomalyModels.Select(m => m.ModelId));
        Assert.Equal(enhanced.RiskModel, decoded.RiskModel);
        Assert.Equal(enhanced.BasePolicy, decoded.BasePolicy);
        Assert.Equal(bytes, ObservedFinanceContextCodec.Instance.EncodeCanonical(decoded));
    }

    [Fact]
    public void Boundary_ties_use_the_shared_fact_position_and_preserve_the_ledger()
    {
        var (run, context, origin) = Start();
        var state = Apply(origin, Seal(origin), context);
        state = Apply(state, Payment(run.Intents[0], 2, Nine - 1), context);

        var atBoundary = Payment(run.Intents[1], 3, Nine);
        Assert.Equal(LedgerFinanceErrorCode.OutOfOrderFact, ObservedFinanceReducer.Apply(state, atBoundary, context).Error.Code);
        var seal = Seal(state);
        var content = ObservationSealCodec.Decode(seal.Payload.AsSpan());
        Assert.Equal((Nine, Nine - 300_000, 2L, state.PrefixDigest),
            (content.BoundaryMs, content.IntervalStartMs, content.PrefixFactSeq, content.PrefixFactDigestSha256));
        Assert.Equal(LedgerFinanceErrorCode.InconsistentFact, ObservedFinanceReducer.Apply(state,
            ObservedFinanceInput.Seal(content with { PrefixFactDigestSha256 = EmptyDigest }), context).Error.Code);
        var sealedState = Apply(state, seal, context);
        Assert.Equal(state.Ledger with { LastFact = 3 }, sealedState.Ledger);

        var payment = atBoundary with { Sequence = 4 };
        Assert.Equal(LedgerFinanceErrorCode.OutOfOrderFact,
            ObservedFinanceReducer.Apply(sealedState, payment with { EffectiveTimeMs = Nine - 1 }, context).Error.Code);
        var after = Apply(sealedState, payment, context);
        Assert.Equal((Nine - 1, 2L, Nine, 3L, Nine, 4L),
            (state.LastEffectiveTimeMs, state.LastFact, seal.EffectiveTimeMs, seal.Sequence, after.LastEffectiveTimeMs, after.LastFact));
        Assert.Equal(4, after.Ledger.Payments[run.Intents[1].IntentId].IntentFact);
        Assert.Equal(5, Apply(after, Payment(run.Intents[2], 5, Nine), context).LastFact);
        Assert.Equal(LedgerFinanceErrorCode.IllegalStep, ObservedFinanceReducer.Apply(after,
            new(RunId, 5, Nine, ObservedFactKind.Screening, PaymentStepCodec.ScreeningSchema, [.. PaymentStepCodec.Encode(run.Intents[1].IntentId)]), context).Error.Code);
    }

    [Fact]
    public void Twelve_idle_hours_replay_identically_under_either_suffix_policy()
    {
        var (run, v7, state) = Start();
        var v8 = new ObservedFinanceContext(v7.ScenarioVersion, v7.ManifestSha256, Origin, PolicyCatalog.RefOf(PolicyCatalog.V8),
            ReviewPolicy.Version, ObservedRiskMode.NotRequired, "", null, [], []);
        var replay = ObservedFinanceReducer.Initial(run, v8).Value;
        for (var i = 0; i < 144; i++)
        {
            var fact = Seal(state);
            state = Apply(state, fact, v7);
            var copied = ObservedFinanceInputCodec.Instance.Decode(ObservedFinanceInputCodec.Instance.EncodeCanonical(fact));
            replay = Apply(replay, copied, v8);
        }
        Assert.Equal(144, state.LastFact);
        Assert.Equal(Origin + 144 * 300_000L, state.LastSealedBoundaryMs);
        Assert.Empty(state.Ledger.Payments);
        Assert.All(state.Ledger.Accounts.Values, a => Assert.Equal(0, a.ReservedMinor));
        Assert.Equal(ObservedFinanceStateCodec.Instance.EncodeCanonical(state), ObservedFinanceStateCodec.Instance.EncodeCanonical(replay));
    }

    internal static (ScenarioRun Run, ObservedFinanceContext Context, ObservedFinanceState State) Start(ScenarioRunId? id = null, long origin = Origin)
    {
        var run = FridayRingScenario.Create(42, ScenarioSchemas.FridayRingVersion, id ?? RunId).Value;
        var context = Context(run.Manifest.ScenarioVersion, run.Manifest.ContentHash, origin);
        return (run, context, ObservedFinanceReducer.Initial(run, context).Value);
    }

    private static ObservedFinanceContext Context(string scenario, string manifest, long origin) =>
        new(scenario, manifest, origin, PolicyCatalog.RefOf(PolicyCatalog.V7), ReviewPolicy.Version, ObservedRiskMode.NotRequired, "", null, [], []);

    internal static ObservedFinanceState Apply(ObservedFinanceState state, ObservedFinanceInput input, ObservedFinanceContext context) =>
        ObservedFinanceTransition.Instance.Apply(state, input.Sequence,
            [new(FinanceLedgerTransition.InputId(state.RunId, input.Sequence), 0, input)], context);

    private static ObservedFinanceInput Seal(ObservedFinanceState state) => ObservedFinanceInput.Seal(
        new(state.RunId, state.LastSealedBoundaryMs + 300_000, state.LastSealedBoundaryMs, state.LastFact, state.PrefixDigest, EmptyDigest));

    private static ObservedFinanceInput Payment(PaymentIntent intent, long sequence, long effective) => new(
        intent.RunId, sequence, effective, ObservedFactKind.PaymentIntent, ScenarioSchemas.InputSchemaVersion,
        [.. PaymentIntentCodec.Instance.EncodeCanonical(intent)]);

    private static void Reject(byte[] bytes, Action<byte[]> decode)
    {
        Assert.Throws<CanonicalFormatException>(() => decode(bytes[..^1]));
        Assert.Throws<CanonicalFormatException>(() => decode([.. bytes, 0]));
        Assert.Throws<CanonicalFormatException>(() => decode([255, .. bytes[1..]]));
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
