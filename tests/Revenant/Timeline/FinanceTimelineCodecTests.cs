using FsCheck;
using FsCheck.Fluent;
using Marrow.Kernel.Canonical;
using Marrow.Timeline;
using Revenant.Contracts;
using Revenant.Finance;
using Revenant.Tests.Scenario;
using Revenant.Timeline;
using Xunit;

namespace Revenant.Tests.Timeline;

/// <summary>Task 5.3 pure components. **Validates: Requirements R05.01, R05.05, R05.09, R05.10**</summary>
public sealed class FinanceTimelineCodecTests
{
    private static readonly ScenarioRunId RunA = new(Guid.Parse("00000000-0000-0000-0000-0000000005a3"));
    private static readonly ScenarioRunId RunB = new(Guid.Parse("00000000-0000-0000-0000-0000000005b3"));

    [Fact]
    public void State_codec_round_trips_every_position_byte_identically_under_both_policies()
    {
        var run = FridayRingScenarioTests.Create(42, RunA);
        foreach (var policy in new[] { PolicyCatalog.V7, PolicyCatalog.V8 })
        {
            foreach (var state in Fold(run, Pin(run, policy), run.Intents.Length))
            {
                var bytes = FinanceStateCodec.Instance.EncodeCanonical(state);
                Assert.Equal(bytes, FinanceStateCodec.Instance.EncodeCanonical(FinanceStateCodec.Instance.Decode(bytes)));
            }
        }
    }

    [Fact]
    public void Same_seed_and_run_give_byte_identical_state_and_another_run_differs_only_by_namespace()
    {
        var first = Encode(FridayRingScenarioTests.Create(42, RunA));
        var again = Encode(FridayRingScenarioTests.Create(42, RunA));
        var otherRun = FridayRingScenarioTests.Create(42, RunB);
        Assert.Equal(first, again);
        Assert.NotEqual(first, Encode(otherRun)); // run-scoped ids and keys are part of the state
        Assert.Equal(FridayRingScenarioTests.Create(42, RunA).ExogenousFactsHash, otherRun.ExogenousFactsHash);

        static byte[] Encode(ScenarioRun run) =>
            FinanceStateCodec.Instance.EncodeCanonical(Fold(run, Pin(run, PolicyCatalog.V7), run.Intents.Length)[^1]);
    }

    [Fact]
    public void State_codec_rejects_unknown_format_and_trailing_bytes()
    {
        var run = FridayRingScenarioTests.Create(7, RunA);
        var bytes = FinanceStateCodec.Instance.EncodeCanonical(Fold(run, Pin(run, PolicyCatalog.V7), 3)[^1]);
        var wrongVersion = (byte[])bytes.Clone();
        wrongVersion[3] = 9;
        Assert.Throws<CanonicalFormatException>(() => FinanceStateCodec.Instance.Decode(wrongVersion));
        Assert.Throws<CanonicalFormatException>(() => FinanceStateCodec.Instance.Decode([.. bytes, 0]));
    }

    [Fact]
    public void Context_codec_round_trips_both_policies_and_fails_closed_on_an_unreproducible_pin()
    {
        var run = FridayRingScenarioTests.Create(42, RunA);
        foreach (var policy in new[] { PolicyCatalog.V7, PolicyCatalog.V8 })
        {
            var context = Pin(run, policy);
            var bytes = FinanceContextCodec.Instance.EncodeCanonical(context);
            var decoded = FinanceContextCodec.Instance.Decode(bytes);
            Assert.Equal(context.PolicyRef, decoded.PolicyRef);
            Assert.Same(policy, decoded.Policy);
            Assert.Equal(bytes, FinanceContextCodec.Instance.EncodeCanonical(decoded));
        }

        // A stored context that names policy-v8 with v7's config hash cannot be reproduced by this build.
        var v8 = Pin(run, PolicyCatalog.V8);
        var tampered = new CanonicalWriter().WriteInt32(1)
            .WriteString(v8.ScenarioVersion).WriteString(v8.ManifestContentHash).WriteString(v8.InputSchemaVersion)
            .WriteString(v8.FeatureSchemaVersion).WriteString(v8.ReducerVersion)
            .WriteString(PolicyCatalog.V8Id).WriteString(PolicyCatalog.V7.ContentHash)
            .WriteString(v8.PolicyRef.ModelArtifactHash).WriteString(v8.PolicyRef.FeatureSchemaHash)
            .ToArray();
        Assert.Throws<CanonicalFormatException>(() => FinanceContextCodec.Instance.Decode(tampered));
    }

    [Fact]
    public void Transition_rejects_a_journal_input_id_that_is_not_the_run_scoped_key()
    {
        var run = FridayRingScenarioTests.Create(42, RunA);
        var context = Pin(run, PolicyCatalog.V7);
        var initial = FinanceReducer.Initial(run, context).Value;
        var intent = run.Intents[0];
        var ex = Assert.Throws<FinanceTransitionException>(() => FinanceTransition.Instance.Apply(
            initial, 1, [new OrderedInput<PaymentIntent>(intent.IntentId, 0, intent)], context));
        Assert.Equal(FinanceErrorCode.RunMismatch, ex.Error.Code);
    }

    [Fact]
    public void Business_time_selection_maps_0900_to_the_last_intent_at_or_before_it()
    {
        var run = FridayRingScenarioTests.Create(42, RunA);
        var nine = run.Manifest.StartUtcMs + (60 * 60 * 1000L);
        var position = FinanceTimelineAdapter.PositionAtOrBefore(run, nine);
        Assert.InRange(position, 1, run.Intents.Length - 1);
        Assert.True(run.Intents[(int)position - 1].EventTimeUtcMs <= nine);
        Assert.True(run.Intents[(int)position].EventTimeUtcMs > nine);
        Assert.All(run.Intents.Where(i => i.Truth == SyntheticTruth.FraudMuleRing), i => Assert.True(i.Sequence > position));
        Assert.Equal(0, FinanceTimelineAdapter.PositionAtOrBefore(run, run.Manifest.StartUtcMs));
    }

    [Fact]
    public void Property_transition_fold_equals_the_pure_reducer_and_every_prefix_state_round_trips()
    {
        var gen = from seed in Gen.Choose(int.MinValue, int.MaxValue)
                  from v8 in Gen.Elements(false, true)
                  from length in Gen.Choose(0, 90)
                  select (seed, v8, length);
        Prop.ForAll(gen.ToArbitrary(), input =>
        {
            var run = FridayRingScenarioTests.Create(input.seed, RunA);
            var context = Pin(run, input.v8 ? PolicyCatalog.V8 : PolicyCatalog.V7);
            var length = Math.Min(input.length, run.Intents.Length);
            var viaTransition = Fold(run, context, length)[^1];
            var viaReducer = FinanceReducer.ApplyAll(FinanceReducer.Initial(run, context).Value, run.Intents.Take(length), context).Value;
            var bytes = FinanceStateCodec.Instance.EncodeCanonical(viaTransition);
            return bytes.AsSpan().SequenceEqual(FinanceStateCodec.Instance.EncodeCanonical(viaReducer))
                && bytes.AsSpan().SequenceEqual(FinanceStateCodec.Instance.EncodeCanonical(FinanceStateCodec.Instance.Decode(bytes)));
        }).Check(Config.QuickThrowOnFailure.WithMaxTest(50));
    }

    internal static FinanceContext Pin(ScenarioRun run, PolicyConfig policy) =>
        FinanceContext.Pin(run.Manifest, PolicyCatalog.RefOf(policy), FinanceReducer.Version).Value;

    /// <summary>States 0..length folded through the Timeline transition, one intent per step.</summary>
    internal static List<FinanceProjectionState> Fold(ScenarioRun run, FinanceContext context, int length)
    {
        var state = FinanceReducer.Initial(run, context).Value;
        var states = new List<FinanceProjectionState> { state };
        foreach (var intent in run.Intents.Take(length))
        {
            state = FinanceTransition.Instance.Apply(
                state, intent.Sequence, [new OrderedInput<PaymentIntent>(intent.RunScopedKey, 0, intent)], context);
            states.Add(state);
        }
        return states;
    }
}
