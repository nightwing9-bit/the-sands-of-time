using GhostLap.Rules;
using GhostLap.Simulation;
using GhostLap.Solo;
using GhostLap.Tests.Simulation;
using Marrow.Timeline;
using Xunit;
using static GhostLap.Tests.Solo.SoloDatabase;

namespace GhostLap.Tests.Solo;

/// <summary>
/// Task 6.4 fixture: its own migrated PostgreSQL with one complete solo original (<see cref="SoloDatabase"/>), a fingerprint
/// of every persisted source row taken before any branch exists, and one real branch forked at a tick with charge, an
/// active Overlit/hold timer, a sub-point distance remainder and collected candles, then played to the final tick with a
/// different route (<see cref="AlternateRouteDriver"/>) through the real recorder and completed.
/// </summary>
public sealed class BranchFixture : IAsyncLifetime
{
    public SoloDatabase Db { get; } = new();

    public long ForkTick { get; private set; }

    /// <summary>Persisted source rows before any fork (see <see cref="SourceFingerprintAsync"/>).</summary>
    public string SourceBefore { get; private set; } = "";

    public SoloBranchResult Branch { get; private set; } = null!;

    /// <summary>Branch world per tick (index = tick); ticks 0..T are the source prefix as restored by the branch.</summary>
    public IReadOnlyList<GameState> BranchTrace { get; private set; } = [];

    /// <summary>Admitted branch inputs per tick (index = tick - T - 1).</summary>
    public IReadOnlyList<GameInput[]> BranchSteps { get; private set; } = [];

    public async Task InitializeAsync()
    {
        await Db.InitializeAsync();
        var trace = Db.Run.Trace;
        bool Rich(GameState s) => s.Tick is > 0 and < GameRulesV1.RoundTicks - 1 && s.Collected.Length > 0
            && s.Players[0] is { Charge: > 0 } p && p.HoldUntilTick > s.Tick && p.DistanceFixed % GameRulesV1.MovementScoreUnitFixed != 0;
        ForkTick = (trace.FirstOrDefault(s => Rich(s) && s.Players[0].IsOverlit) ?? trace.First(Rich)).Tick;
        SourceBefore = await SourceFingerprintAsync();

        await using var host = Db.NewHost();
        var branch = Ok(await host.Branches.ForkAsync(Db.OwnerA, Db.Original.Timeline, ForkTick, "fixture-branch", CancellationToken.None));
        var states = new List<GameState>(trace.Take((int)ForkTick)) { branch.State };
        var steps = new List<GameInput[]>();
        var driver = new AlternateRouteDriver();
        while (!branch.IsRoundOver)
        {
            var inputs = driver.Inputs(branch.State, branch.Context);
            foreach (var g in inputs)
            {
                var admitted = branch.Submit(new SubmittedInput(g.Slot, g.Sequence, g.Tick, g.MoveX, g.MoveY, g.RequestBurst));
                Assert.True(admitted.IsSuccess, $"tick {g.Tick}: {admitted}");
            }

            states.Add(Ok(await branch.CommitTickAsync(CancellationToken.None)).State);
            steps.Add(inputs);
        }

        Branch = Ok(await branch.CompleteAsync(CancellationToken.None));
        BranchTrace = states;
        BranchSteps = steps;
    }

    public Task DisposeAsync() => Db.DisposeAsync();

    /// <summary>
    /// Every persisted fact of the source original, read as the superuser: manifest lifecycle and hashes (including
    /// <c>updated_at</c>), all step/record hashes and operation keys, all checkpoint hashes and bytes, all input payloads, and
    /// the Ghost Lap row. Any write to the source would change this string.
    /// </summary>
    public async Task<string> SourceFingerprintAsync()
    {
        await using var cmd = Db.Superuser.CreateCommand("""
            SELECT h.last_position || '|' || h.latest_state_hash || '|' || h.last_record_hash || '|' || h.status || '|' || h.updated_at::text
              || '|' || (SELECT count(*) || ':' || md5(string_agg(position || state_hash || record_hash || operation_key, ',' ORDER BY position))
                         FROM marrow_timeline.steps WHERE timeline_id = @id)
              || '|' || (SELECT count(*) || ':' || md5(string_agg(position || state_hash || encode(sha256(state_bytes), 'hex'), ',' ORDER BY position))
                         FROM marrow_timeline.checkpoints WHERE timeline_id = @id)
              || '|' || (SELECT count(*) || ':' || md5(string_agg(input_id || encode(sha256(payload), 'hex'), ',' ORDER BY position, input_index))
                         FROM marrow_timeline.step_inputs WHERE timeline_id = @id)
              || '|' || (SELECT status || final_tick || final_state_hash || last_record_hash FROM ghostlap.solo_originals WHERE timeline_id = @id)
            FROM marrow_timeline.histories h WHERE h.timeline_id = @id
            """);
        cmd.Parameters.AddWithValue("id", Db.Original.Timeline.Value);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    public static IReadOnlyList<OrderedInput<GameInput>> Ordered(GameInput[] inputs) =>
        [.. inputs.Select(g => new OrderedInput<GameInput>($"t{g.Tick}-s{g.Slot}-q{g.Sequence}", g.Slot, g))];
}
