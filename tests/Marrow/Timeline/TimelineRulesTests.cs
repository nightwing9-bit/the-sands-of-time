using Marrow.Timeline;
using Marrow.Timeline.Internal;
using Xunit;

namespace Marrow.Tests.Timeline;

/// <summary>Typed failure mapping for position, ordering and lifecycle preconditions (R02.01, R02.11, R02.14).</summary>
public sealed class TimelineRulesTests
{
    private static readonly TimelineId Id = new(Guid.Parse("00000000-0000-0000-0000-00000000000a"));

    [Theory]
    [InlineData(0L, 1L)]
    [InlineData(41L, 42L)]
    public void Contiguous_next_step_is_accepted(long expectedLast, long stepPosition)
    {
        Assert.True(TimelineRules.CanonicalizeNextStep(expectedLast, new StepRecord<int>(stepPosition, [])).IsSuccess);
    }

    [Theory]
    [InlineData(0L, 0L)]   // position 0 is the initial state, never an appended step
    [InlineData(0L, 2L)]   // gap
    [InlineData(5L, 5L)]   // repeat
    [InlineData(5L, 4L)]   // reorder
    [InlineData(-1L, 0L)]  // negative expected position
    [InlineData(long.MaxValue, long.MinValue)] // overflow cannot wrap into a valid position
    public void Noncontiguous_step_is_InvalidPosition(long expectedLast, long stepPosition)
    {
        var result = TimelineRules.CanonicalizeNextStep(expectedLast, new StepRecord<int>(stepPosition, []));
        Assert.Equal(TimelineErrorCode.InvalidPosition, result.Error.Code);
    }

    [Fact]
    public void Duplicate_input_id_in_a_step_is_Conflict()
    {
        var step = new StepRecord<int>(1, [new("x", 0, 1), new("x", 1, 2)]);
        Assert.Equal(TimelineErrorCode.Conflict, TimelineRules.CanonicalizeNextStep(0, step).Error.Code);
    }

    [Fact]
    public void Inputs_are_ordered_by_ordinal_then_ordinal_input_id_without_mutating_the_step()
    {
        OrderedInput<int>[] supplied = [new("b", 1, 1), new("a", 1, 2), new("Z", 1, 3), new("z", 0, 4)];
        var step = new StepRecord<int>(1, supplied);

        var ordered = TimelineRules.CanonicalizeNextStep(0, step).Value;

        Assert.Equal(new[] { "z", "Z", "a", "b" }, ordered.Select(i => i.InputId));
        Assert.Equal(new[] { "b", "a", "Z", "z" }, step.Inputs.Select(i => i.InputId));
    }

    [Fact]
    public void Append_requires_recording_status_and_current_expected_position()
    {
        Assert.Null(TimelineRules.CheckAppendable(Manifest(HistoryStatus.Recording, 3), 3));
        Assert.Equal(TimelineErrorCode.Conflict, TimelineRules.CheckAppendable(Manifest(HistoryStatus.Recording, 3), 2)!.Code);
        Assert.Equal(TimelineErrorCode.Conflict, TimelineRules.CheckAppendable(Manifest(HistoryStatus.Verified, 3), 3)!.Code);
        Assert.Equal(TimelineErrorCode.Conflict, TimelineRules.CheckAppendable(Manifest(HistoryStatus.Failed, 3), 3)!.Code);
    }

    [Theory]
    [InlineData(0L, true)]
    [InlineData(3L, true)]
    [InlineData(-1L, false)]
    [InlineData(4L, false)]
    public void Readable_positions_are_zero_through_last(long position, bool readable)
    {
        var error = TimelineRules.CheckReadablePosition(Manifest(HistoryStatus.Recording, 3), position);
        if (readable) Assert.Null(error);
        else Assert.Equal(TimelineErrorCode.InvalidPosition, error!.Code);
    }

    [Fact]
    public void Fork_requires_verified_source_and_allows_zero_through_final_position()
    {
        var verified = Manifest(HistoryStatus.Verified, 3);
        Assert.Null(TimelineRules.CheckForkable(verified, 0));
        Assert.Null(TimelineRules.CheckForkable(verified, 3));
        Assert.Equal(TimelineErrorCode.InvalidPosition, TimelineRules.CheckForkable(verified, 4)!.Code);
        Assert.Equal(TimelineErrorCode.Conflict, TimelineRules.CheckForkable(Manifest(HistoryStatus.Recording, 3), 1)!.Code);
    }

    private static HistoryManifest Manifest(HistoryStatus status, long lastPosition) =>
        TimelineContractTests.Manifest(Id, parent: null, forkPosition: null, status, lastPosition);
}
