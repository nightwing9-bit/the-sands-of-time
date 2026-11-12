using System.Collections.Immutable;
using Marrow.Kernel.Canonical;
using Revenant.Cases;
using Revenant.Contracts;
using Revenant.Finance;
using Revenant.Intelligence;
using Revenant.Payments;
using Revenant.Scenario;
using Revenant.Synthetic;
using Revenant.Timeline;
using Xunit;

namespace Revenant.Tests.Timeline;

public sealed class V4ModelScreeningTests
{
    // These unit tests exercise decisions on recorded PPM, without asserting any predictor output.
    private static readonly ImmutableArray<byte> Vector =
        [.. new RiskFeatureVectorV2([.. RiskFeatureVectorV2.Columns.Select(x => x.Minimum)]).Encode()];
    private static readonly int Threshold = BehavioralRiskScorerV2.PublishedPin.ThresholdPpm;

    [Fact]
    public void Warmup_is_explicit_and_its_cutoff_requires_inference()
    {
        var (run, context, state, sample) = Start();
        var cutoff = context.OriginMs + V4ReviewCounts.WarmupMs;
        while (state.LastSealedBoundaryMs < cutoff - ObservedFinanceVersion.BucketMs)
            state = Seal(state, context);

        var warm = Intent(sample, 1, cutoff - 1);
        state = Accept(state, context, warm);
        var evidence = V4ModelScreeningEvidence.Create(state, context, warm.IntentId, cutoff - 1, [], null, null);
        Assert.Equal(V4ScreeningResult.WarmupRulesOnly, evidence.Decision);
        Assert.Equal(PaymentStatus.Cleared, evidence.Status);
        Assert.Null(evidence.Ppm);
        Assert.True(ObservedFinanceReducer.Apply(state, (evidence with { RawBits = 0, VectorDigest = state.PrefixDigest, Ppm = 0 }).ToInput(), context).IsFailure);
        state = ObservedFinanceReducer.Apply(state, evidence.ToInput(), context).Value;
        Assert.Equal(default, state.ReviewCounts);

        state = Seal(state, context);
        var live = Intent(sample, 2, cutoff);
        state = Accept(state, context, live);
        Assert.True(ObservedFinanceReducer.Apply(state, evidence.ToInput() with
        {
            Sequence = state.LastFact + 1, EffectiveTimeMs = cutoff,
        }, context).IsFailure);
        var missing = new ObservedFinanceInput(run.RunId, state.LastFact + 1, cutoff, ObservedFactKind.Screening,
            PaymentStepCodec.ScreeningSchema, [.. PaymentStepCodec.Encode(live.IntentId)]);
        Assert.True(ObservedFinanceReducer.Apply(state, missing, context).IsFailure);
        var score = V4ModelScreeningEvidence.Create(state, context, live.IntentId, cutoff, Vector, 0, Threshold);
        Assert.True(score.AboveThreshold && score.Suppressed);
        Assert.Equal(1, score.Capacity.Eligible);
    }

    [Fact]
    public void Capacity_is_cumulative_inherited_and_mandatory_rules_take_precedence()
    {
        var (_, context, state, sample) = Start();
        var cutoff = context.OriginMs + V4ReviewCounts.WarmupMs;
        while (state.LastSealedBoundaryMs < cutoff)
            state = Seal(state, context);

        for (var i = 1; i <= 20; i++)
        {
            var intent = Intent(sample, i, cutoff);
            state = Accept(state, context, intent);
            var evidence = V4ModelScreeningEvidence.Create(state, context, intent.IntentId, cutoff, Vector, 0, Threshold);
            Assert.Equal(i < 20, evidence.Suppressed);
            Assert.Equal(i == 20, evidence.Admitted);
            Assert.Equal(i / 20, evidence.Allowed);
            Assert.Equal(i == 20 ? PaymentStatus.Held : PaymentStatus.Cleared, evidence.Status);
            if (i == 20)
            {
                foreach (var tampered in new[]
                {
                    evidence with { PriorDigest = new string('0', 64) },
                    evidence with { PriorSequence = evidence.PriorSequence - 1 },
                    evidence with { IntentId = "different-intent" },
                    evidence with { AboveThreshold = false },
                    evidence with { Ppm = Threshold - 1 },
                    evidence with { Available = 0 },
                    evidence with { Admitted = false },
                    evidence with { MandatoryOverload = true },
                    evidence with { BaseReason = PaymentReason.VelocityRule },
                    evidence with { FeatureSchema = "incorrect" },
                    evidence with { Model = evidence.Model with { ThresholdPpm = Threshold + 1 } },
                    evidence with { VectorDigest = new string('0', 64) },
                    evidence with { RawBits = BitConverter.SingleToInt32Bits(float.NaN) },
                })
                    Assert.True(ObservedFinanceReducer.Apply(state, tampered.ToInput(), context).IsFailure);
                var input = evidence.ToInput();
                Assert.True(ObservedFinanceReducer.Apply(state, input with { EvidenceSha256 = state.PrefixDigest }, context).IsFailure);
                Assert.Equal(input.Payload.ToArray(), V4ModelScreeningEvidence.Decode(input).ToInput().Payload.ToArray());
                var unfunded = state with { Ledger = state.Ledger with
                {
                    Accounts = state.Ledger.Accounts.SetItem(sample.SourceAccountId, state.Ledger.Accounts[sample.SourceAccountId] with
                    {
                        PostedMinor = 0,
                    }),
                } };
                var cannotAdmit = V4ModelScreeningEvidence.Create(unfunded, context, intent.IntentId, cutoff, Vector, 0, Threshold);
                Assert.False(cannotAdmit.Admitted || cannotAdmit.OpensCase);
                Assert.Equal(PaymentStatus.Cleared, cannotAdmit.Status);
            }
            state = ObservedFinanceReducer.Apply(state, evidence.ToInput(), context).Value;
        }

        state = Seal(state, context);
        var codec = ObservedFinanceStateCodec.Instance;
        var checkpoint = codec.EncodeCanonical(state);
        var trailer = new CanonicalWriter().WriteInt64(state.LastSealedBoundaryMs).WriteInt64(state.LastSealedBoundaryMs)
            .WriteBytes(Convert.FromHexString(state.PrefixDigest)).ToArray();
        Assert.Equal(trailer, checkpoint[^trailer.Length..]);
        var inherited = codec.Decode(checkpoint);
        Assert.Equal(new V4ReviewCounts(20, 0, 1), inherited.ReviewCounts);
        var at = inherited.LastSealedBoundaryMs;
        var child = ObservedFinanceV4Tests.ChildContext(context);
        V4BranchExecution.ValidateChildContext(context, child);
        var next = Intent(sample, 21, at);
        var source = Accept(inherited, context, next);
        var suffix = Accept(inherited, child, next);
        var inSource = V4ModelScreeningEvidence.Create(source, context, next.IntentId, at, Vector, 0, Threshold);
        var inChild = V4ModelScreeningEvidence.Create(suffix, child, next.IntentId, at, Vector, 0, 0);
        Assert.True(inSource.Suppressed);
        Assert.Equal(V4ScreeningResult.BaseRuleHold, inChild.Decision);
        Assert.Equal(PaymentReason.VelocityRule, inChild.BaseReason);
        Assert.False(inChild.MandatoryOverload); // One mandatory hold fits, though the inherited ML slot is already spent.
        Assert.False(inChild.AboveThreshold || inChild.Admitted || inChild.Suppressed);
        var childState = ObservedFinanceReducer.Apply(suffix, inChild.ToInput(), child).Value;
        Assert.Equal(new V4ReviewCounts(21, 1, 1), childState.ReviewCounts);
        Assert.Equal(0, childState.ReviewCounts.Available);
        Assert.Equal(new V4ReviewCounts(20, 0, 1), inherited.ReviewCounts);
    }

    private static (ScenarioRun, ObservedFinanceContext, ObservedFinanceState, PaymentIntent) Start()
    {
        var config = V4EpisodeConfig.RiskSmoke;
        var plan = V4EpisodePlanner.Plan(config, config.Roots().Single(), new(Guid.NewGuid())).Financial;
        var run = plan.Opening;
        var context = new ObservedFinanceContext(run.Manifest.ScenarioVersion, run.Manifest.ContentHash, run.Manifest.StartUtcMs,
            PolicyCatalog.RefOf(PolicyCatalog.V7), ReviewPolicy.Version, ObservedRiskMode.ModelRequired,
            BehavioralRiskScorerV2.FeatureSchema, BehavioralRiskScorerV2.PublishedPin, [], []);
        return (run, context, ObservedFinanceReducer.Initial(run, context).Value, plan.Payments[0].Intent);
    }

    private static PaymentIntent Intent(PaymentIntent sample, int i, long at) =>
        new(sample.RunId, $"r6-{i}", i, at, sample.SourceBankId, sample.SourceAccountId, sample.DestBankId,
            sample.DestAccountId, Money.Usd(10_000), SyntheticTruth.Legitimate, new(false, false));

    private static ObservedFinanceState Accept(ObservedFinanceState state, ObservedFinanceContext context, PaymentIntent intent) =>
        ObservedFinanceReducer.Apply(state, new(intent.RunId, state.LastFact + 1, intent.EventTimeUtcMs, ObservedFactKind.PaymentIntent,
            ScenarioSchemas.InputSchemaVersion, [.. PaymentIntentCodec.Instance.EncodeCanonical(intent)]), context).Value;

    private static ObservedFinanceState Seal(ObservedFinanceState state, ObservedFinanceContext context) =>
        ObservedFinanceReducer.Apply(state, ObservedFinanceInput.Seal(new(state.RunId,
            ObservedFinanceVersion.NextBoundary(state.LastSealedBoundaryMs), state.LastSealedBoundaryMs, state.LastFact,
            state.PrefixDigest, ObservedFinanceV4Tests.EmptyDigest)), context).Value;
}
