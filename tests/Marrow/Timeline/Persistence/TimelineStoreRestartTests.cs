using Marrow.Timeline;
using Xunit;
using static Marrow.Tests.Timeline.Persistence.StoreData;

namespace Marrow.Tests.Timeline.Persistence;

/// <summary>
/// Durability across a real PostgreSQL server restart: the position-0 checkpoint, pinned context bytes,
/// steps and record chain survive, and the same history continues and completes afterwards (R02.11, R02.12).
/// Uses its own container because it stops the server.
/// </summary>
[Trait("Category", "Integration")]
public sealed class TimelineStoreRestartTests(TimelinePostgresDatabase db) : IClassFixture<TimelinePostgresDatabase>
{
    [Fact]
    public async Task Initial_checkpoint_pinned_context_and_steps_survive_a_server_restart()
    {
        var ct = CancellationToken.None;
        var created = (await db.Store.CreateHistoryAsync(Source(context: 1234), ct)).Value;
        var id = created.Manifest.Id;
        await db.Store.AppendStepAsync(Step(id, 0, null, ("before-1", 1)), ct);
        await db.Store.AppendStepAsync(Step(id, 1) with { CheckpointState = State(2) }, ct);
        var before = (await db.Store.GetHistoryAsync(id, ct)).Value;
        var stepsBefore = (await db.Store.ReadStepsAsync(id, 0, 2, ct)).Value;

        await db.RestartAsync();

        var after = (await db.Store.GetHistoryAsync(id, ct)).Value;
        Assert.Equal(before, after);
        Assert.Equal(Context(1234), (await db.Store.ReadContextAsync(after.Manifest.ContextArtifactRef, ct)).Value.CanonicalContext);
        Assert.Equal(State(0), (await db.Store.ReadNearestCheckpointAsync(id, 1, ct)).Value.CanonicalState);
        Assert.Equal(State(2), (await db.Store.ReadNearestCheckpointAsync(id, 2, ct)).Value.CanonicalState);
        var stepsAfter = (await db.Store.ReadStepsAsync(id, 0, 2, ct)).Value;
        Assert.Equal(stepsBefore.Select(s => (s.Position, s.RecordHash, s.StateHash)), stepsAfter.Select(s => (s.Position, s.RecordHash, s.StateHash)));
        Assert.Equal(Payload("before-1"), stepsAfter[0].Inputs.Single().Payload);

        var continued = await db.Store.AppendStepAsync(Step(id, 2, null, ("after-3", 1)), ct);
        Assert.Equal(3, continued.Value.Position);
        Assert.Equal(HistoryStatus.Verified, (await db.Store.MarkVerifiedAsync(id, 3, StateHash(3), ct)).Value.Status);
    }
}
