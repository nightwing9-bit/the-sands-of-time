using Marrow.Timeline;
using Marrow.Timeline.Internal;
using Xunit;

namespace Marrow.Tests.Timeline;

/// <summary>State hash, record chain and chain verification rules used by the persistence adapter (R02.03, R02.08).</summary>
public sealed class TimelineHashingTests
{
    private static readonly byte[] Bytes = [1, 2, 3];

    [Fact]
    public void State_hash_is_bound_to_the_schema_version_and_is_lowercase_sha256_hex()
    {
        var v1 = TimelineHashing.StateHash("state-1", Bytes);

        Assert.True(TimelineHashing.IsHash(v1));
        Assert.Equal(v1, TimelineHashing.StateHash("state-1", [1, 2, 3]));
        Assert.NotEqual(v1, TimelineHashing.StateHash("state-2", Bytes));
    }

    [Fact]
    public void Chain_verification_accepts_an_intact_chain_and_detects_any_altered_field()
    {
        var steps = Chain(TimelineHashing.GenesisAnchor, 3);
        Assert.Null(TimelineHashing.VerifyChain(0, TimelineHashing.GenesisAnchor, steps));

        StoredStep[][] tampered =
        [
            [steps[0], steps[1] with { Inputs = [new("i-1", 1, [9])] }, steps[2]],
            [steps[0], steps[1] with { StateHash = steps[2].StateHash }, steps[2]],
            [steps[0], steps[2]],
            [steps[0], steps[1] with { PreviousRecordHash = TimelineHashing.GenesisAnchor }, steps[2]],
        ];

        Assert.All(tampered, t =>
            Assert.Equal(TimelineErrorCode.CorruptHistory, TimelineHashing.VerifyChain(0, TimelineHashing.GenesisAnchor, t)?.Code));
    }

    private static StoredStep[] Chain(string anchor, int count)
    {
        var result = new StoredStep[count];
        var previous = anchor;
        for (var i = 0; i < count; i++)
        {
            var position = i + 1L;
            IReadOnlyList<OrderedInput<byte[]>> inputs = [new($"i-{i}", 1, [(byte)i])];
            var state = TimelineHashing.StateHash("state-1", [(byte)position]);
            var record = TimelineHashing.ChainHash(previous, TimelineHashing.StepEnvelope(position, inputs, state));
            result[i] = new StoredStep(position, inputs, state, previous, record);
            previous = record;
        }

        return result;
    }
}
