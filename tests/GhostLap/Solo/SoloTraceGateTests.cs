extern alias realtime;

using System.Diagnostics;
using System.Text.Json;
using GhostLap.Rules;
using GhostLap.Simulation;
using GhostLap.Solo;
using GhostLap.Timeline;
using Marrow.Infrastructure.Identity;
using Marrow.Timeline;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using Xunit.Abstractions;
using realtime::GhostLapHost;
using static GhostLap.Tests.Solo.SoloDatabase;

namespace GhostLap.Tests.Solo;

public sealed partial class SoloTraceTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly long[] RequiredTicks = [0, 1140, 2699, 2700];

    [Fact]
    public async Task A_restarted_runtime_reads_the_entire_verified_original_and_measures_prefetch()
    {
        await db.RestartAsync();
        await using (var create = db.Superuser.CreateCommand("CREATE EXTENSION IF NOT EXISTS pg_stat_statements"))
        {
            await create.ExecuteNonQueryAsync();
        }

        var id = db.Original.Timeline;
        var chunks = new List<SoloTraceChunk>();
        var beforeTrace = await DatabaseStatsAsync();
        await using var full = NewRealtimeServices();
        var originals = full.GetRequiredService<SoloOriginals>();
        using var process = Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        var heapBefore = GC.GetTotalMemory(false);
        var rssBefore = process.WorkingSet64;
        var allocatedBefore = GC.GetTotalAllocatedBytes(true);
        var assembly = Stopwatch.StartNew();
        var traceMs = 0d;
        var jsonMs = 0d;
        var jsonBytes = 0;
        var largestChunkBytes = 0;
        var sampledHeapPeak = heapBefore;
        long? next = 0;
        while (next is { } from)
        {
            var start = Stopwatch.GetTimestamp();
            var chunk = Ok(await originals.TraceAsync(db.OwnerA, id, from, SoloOriginals.MaxTraceTicks, Ct));
            var projected = Stopwatch.GetTimestamp();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(chunk, Json);
            traceMs += Stopwatch.GetElapsedTime(start, projected).TotalMilliseconds;
            jsonMs += Stopwatch.GetElapsedTime(projected).TotalMilliseconds;
            jsonBytes += bytes.Length;
            largestChunkBytes = Math.Max(largestChunkBytes, bytes.Length);
            sampledHeapPeak = Math.Max(sampledHeapPeak, GC.GetTotalMemory(false));
            chunks.Add(chunk);
            Assert.InRange(chunks.Count, 1, 10);
            next = chunk.NextTick;
        }

        assembly.Stop();
        var allocated = GC.GetTotalAllocatedBytes(true) - allocatedBefore;
        var heapDelta = GC.GetTotalMemory(false) - heapBefore;
        process.Refresh();
        var rssDelta = process.WorkingSet64 - rssBefore;
        var cpu = process.TotalProcessorTime - cpuBefore;
        var traceDb = await DatabaseStatsAsync() - beforeTrace;
        output.WriteLine($"FULL wall={assembly.Elapsed.TotalMilliseconds:F2}ms service={traceMs:F2}ms JSON={jsonMs:F2}ms " +
                         $"calls={traceDb.Calls} pg_exec={traceDb.ServerMs:F2}ms CPU={cpu.TotalMilliseconds:F2}ms " +
                         $"allocated={allocated} managed_heap_delta={heapDelta} sampled_heap_peak_delta={sampledHeapPeak - heapBefore} " +
                         $"working_set_delta={rssDelta} JSON_bytes={jsonBytes} largest_chunk_bytes={largestChunkBytes}");

        var store = full.GetRequiredService<SoloOriginalStore>();
        var catalog = Ok(await store.FindAsync(id, Ct));
        output.WriteLine($"SOURCE id={id.Value:D} seed={catalog.Seed} mascot={catalog.MascotId} " +
                         $"rules={catalog.RulesVersion}/{catalog.LayoutVersion} record_hash={catalog.LastRecordHash} {await SourceSizeAsync(id)}");
        var engine = full.GetRequiredService<ITimelineEngine<GameState, GameInput, GameContext>>();
        var replay = await engine.ReplayAsync(id, Ct);
        Assert.True(replay.IsSuccess, replay.IsFailure ? replay.Error.ToString() : "");
        Assert.Equal((id, GameRulesV1.RoundTicks, catalog.FinalStateHash, catalog.LastRecordHash, HistoryStatus.Verified),
            (replay.Value.Timeline, replay.Value.FinalPosition, replay.Value.FinalStateHash, replay.Value.LastRecordHash, replay.Value.Status));
        Assert.Equal(Bytes(db.Run.Trace[^1]), Bytes(replay.Value.FinalState));
        Assert.Equal((Seed, (byte)2, GameRulesV1.RulesVersion, GameRulesV1.LayoutVersion),
            (catalog.Seed, catalog.MascotId, catalog.RulesVersion, catalog.LayoutVersion));

        Assert.Equal(Enumerable.Repeat(300, 9).Append(1), chunks.Select(c => c.Frames.Length));
        var frames = chunks.SelectMany(chunk => chunk.Frames).ToArray();
        Assert.Equal(Enumerable.Range(0, 2701).Select(t => (long)t), frames.Select(f => f.Tick));
        Assert.Equal(frames.Length, frames.Select(f => f.StateHash).Distinct().Count());
        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            Assert.Equal((id, GhostLapTimelineVersions.V1, catalog.RulesVersion, catalog.LayoutVersion,
                    GameRulesV1.RoundTicks, catalog.FinalStateHash, (long)(i * 300)),
                (chunk.Timeline, chunk.Versions, chunk.RulesVersion, chunk.LayoutVersion,
                    chunk.SourceFinalTick, chunk.SourceFinalStateHash, chunk.FromTick));
            Assert.Equal(i + 1 < chunks.Count ? chunks[i + 1].FromTick : (long?)null, chunk.NextTick);
        }

        var collected = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var frame in frames)
        {
            var live = db.Run.Trace[(int)frame.Tick];
            var hash = frame.Tick == 0 ? frames[0].StateHash : db.Run.Receipts[(int)frame.Tick - 1].StateHash;
            var origin = frame.Tick % GhostLapTimelineVersions.CheckpointInterval == 0
                ? StateHashOrigin.StoredCheckpoint : StateHashOrigin.ReconstructedFromVerifiedChain;
            EqualFrame(SoloTraceFrame.Of(new VerifiedState<GameState>(id, live.Tick, live, hash, origin, HistoryStatus.Verified)), frame);

            foreach (var cue in frame.Events.Where(e => e.Kind == GameEventKind.CandleCollected))
            {
                collected.Add(cue.CandleId, frame.Tick);
            }

            Assert.Equal(live.Collected.Select(c => (c.Id, c.Tick)),
                collected.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => (pair.Key, pair.Value)));
            Assert.Equal(db.Run.Context.Plan.ActiveAt(frame.Tick).Select(c => c.Id).Where(c => !collected.ContainsKey(c)),
                frame.ActiveCandleIds);
        }

        var overlit = frames.First(f => f.IsOverlit);
        var manual = frames.First(f => f.Events.Any(e => e.Kind == GameEventKind.BurstManual));
        var automatic = frames.First(f => f.Events.Any(e => e.Kind == GameEventKind.BurstAuto));
        Assert.Contains(db.Run.Steps[(int)manual.Tick - 1], input => input.RequestBurst);
        Assert.DoesNotContain(db.Run.Steps[(int)automatic.Tick - 1], input => input.RequestBurst);
        Assert.Equal(automatic.Tick, frames[(int)automatic.Tick - 1].OverlitDeadlineTick);
        var expired = db.Run.Context.Plan.Candles.First(c => c.ExpireTick < GameRulesV1.RoundTicks && !collected.ContainsKey(c.Id));
        Assert.Contains(expired.Id, frames[expired.ExpireTick - 1].ActiveCandleIds);
        Assert.DoesNotContain(expired.Id, frames[expired.ExpireTick].ActiveCandleIds);
        output.WriteLine($"EVENTS first Overlit={overlit.Tick} deadline={overlit.OverlitDeadlineTick}, " +
                         $"manual={manual.Tick}, auto={automatic.Tick}, expired={expired.Id}@{expired.ExpireTick}; " +
                         $"manual_count={frames.Count(f => f.Events.Any(e => e.Kind == GameEventKind.BurstManual))} " +
                         $"auto_count={frames.Count(f => f.Events.Any(e => e.Kind == GameEventKind.BurstAuto))}");

        foreach (var tick in RequiredTicks)
        {
            await using var fresh = NewRealtimeServices();
            var service = fresh.GetRequiredService<SoloOriginals>();
            var cold = await MeasureAsync($"COLD inspect T{tick}", 1, () => service.InspectAsync(db.OwnerA, id, tick, Ct));
            var warm = await MeasureAsync($"WARM inspect T{tick}", 10, () => service.InspectAsync(db.OwnerA, id, tick, Ct));
            EqualFrame(frames[tick], cold);
            EqualFrame(frames[tick], warm);
            var p = db.Run.Trace[(int)tick].Players[0];
            output.WriteLine($"STATE T{tick} ({p.XFixed},{p.YFixed}) dist={p.DistanceFixed} charge={p.Charge} " +
                             $"deadline={p.OverlitDeadlineTick} collected={cold.CollectedCount} active={cold.ActiveCandleIds.Length} " +
                             $"score={p.TotalScore} parts={p.MovementPoints}+{p.PickupPoints}-{p.PenaltyPoints} hash={cold.StateHash}");
        }

        foreach (var tick in new long[] { 59, 60, 299, 300, 600, 899, 900, 1200, 1499, 1500, 2400 }
                     .Concat([overlit.Tick, manual.Tick, automatic.Tick, expired.ExpireTick]).Distinct())
        {
            var inspected = Ok(await originals.InspectAsync(db.OwnerA, id, tick, Ct));
            var restored = Ok(await originals.RestoreAsync(db.OwnerA, id, tick, Ct));
            EqualFrame(frames[tick], inspected);
            Assert.Equal(inspected.StateHash, restored.StateHash);
            Assert.Equal(Bytes(db.Run.Trace[(int)tick]), Bytes(restored.State));
        }

        await using (var fresh = NewRealtimeServices())
        {
            var service = fresh.GetRequiredService<SoloOriginals>();
            var slice = await MeasureAsync("COLD trace 60", 1, () => service.TraceAsync(db.OwnerA, id, Rich, 60, Ct));
            Assert.All(slice.Frames, f => EqualFrame(frames[f.Tick], f));
        }

        await using (var fresh = NewRealtimeServices())
        {
            var service = fresh.GetRequiredService<SoloOriginals>();
            var cold = await MeasureAsync("COLD trace 300", 1, () => service.TraceAsync(db.OwnerA, id, 900, 300, Ct));
            var warm = await MeasureAsync("WARM trace 300", 5, () => service.TraceAsync(db.OwnerA, id, 900, 300, Ct));
            var overlap = Ok(await service.TraceAsync(db.OwnerA, id, 1190, 25, Ct));
            Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(chunks[3], Json), JsonSerializer.SerializeToUtf8Bytes(cold, Json));
            Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(cold, Json), JsonSerializer.SerializeToUtf8Bytes(warm, Json));
            Assert.All(overlap.Frames, f => EqualFrame(frames[f.Tick], f));
        }

        var foreign = Guest();
        var denied = await originals.TraceAsync(foreign, id, 0, 1, Ct);
        var unknown = await originals.TraceAsync(foreign, new TimelineId(Guid.NewGuid()), 0, 1, Ct);
        Assert.Equal(SoloErrorCode.NotFound, denied.Error.Code);
        Assert.Equal(unknown.Error, denied.Error);
        Assert.Equal(denied.Error, (await originals.InspectAsync(ClaimsActorContext.Anonymous, id, 0, Ct)).Error);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Equal(SoloErrorCode.Cancelled, (await originals.TraceAsync(db.OwnerA, id, 0, 300, cancelled.Token)).Error.Code);
        Assert.Equal(SoloErrorCode.Cancelled, (await originals.InspectAsync(db.OwnerA, id, 1140, cancelled.Token)).Error.Code);

        Assert.Equal(catalog, Ok(await store.FindAsync(id, Ct)));
        var reopened = Ok(await originals.OpenAsync(db.OwnerA, id, Ct));
        Assert.Equal(db.Original.LastRecordHash, reopened.LastRecordHash);
        Assert.Equal(db.Original.FinalStateHash, reopened.FinalStateHash);
        Assert.Equal(db.Run.Trace[^1].Players[0].TotalScore, reopened.Result.Scores[0].TotalScore);

        var living = Ok(await originals.StartAsync(db.OwnerA, Seed, 2, "trace-independent-run", Ct));
        Assert.NotEqual(id, living.Timeline);
        Ok(await living.CommitTickAsync(Ct)); // neutral input, independent of the original's input at tick 1
        var livingBefore = Bytes(living.State);
        Assert.False(livingBefore.AsSpan().SequenceEqual(Bytes(db.Run.Trace[1])));
        var ghost = Ok(await originals.TraceAsync(db.OwnerA, id, 0, 60, Ct));
        EqualFrame(frames[1], ghost.Frames[1]);
        Assert.Equal(livingBefore, Bytes(living.State));
        var control = await PaceLivingTicksAsync(living, historyRead: null);
        var historyRead = originals.TraceAsync(db.OwnerA, id, 900, 300, Ct);
        var concurrent = await PaceLivingTicksAsync(living, historyRead);
        var overlappingChunk = Ok(await historyRead);
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(chunks[3], Json), JsonSerializer.SerializeToUtf8Bytes(overlappingChunk, Json));
        output.WriteLine($"LIVE_WRITE probe control max={control.MaxMs:F2}ms; " +
                         $"during probe max={concurrent.MaxMs:F2}ms, {concurrent.Overlap}/18 commits started with trace in flight");
        var storedLiving = await engine.RestoreAsync(living.Timeline, living.State.Tick, Ct);
        Assert.True(storedLiving.IsSuccess);
        Assert.Equal(Bytes(living.State), Bytes(storedLiving.Value.State));
        Assert.Equal(catalog, Ok(await store.FindAsync(id, Ct)));
    }

    private static async Task<(double MaxMs, int Overlap)> PaceLivingTicksAsync(SoloRecording living, Task? historyRead)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1d / 30));
        var maxMs = 0d;
        var overlap = 0;
        for (var i = 0; i < 18; i++)
        {
            await timer.WaitForNextTickAsync();
            if (historyRead is { IsCompleted: false })
            {
                overlap++;
            }

            var start = Stopwatch.GetTimestamp();
            Ok(await living.CommitTickAsync(Ct));
            maxMs = Math.Max(maxMs, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }

        return (maxMs, overlap);
    }

    [Fact]
    public async Task An_in_flight_trace_cancels_while_waiting_for_its_checkpoint()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        await using var admin = await db.Superuser.OpenConnectionAsync(timeout.Token);
        await using var transaction = await admin.BeginTransactionAsync(timeout.Token);
        await using var block = new NpgsqlCommand("LOCK TABLE marrow_timeline.checkpoints IN ACCESS EXCLUSIVE MODE", admin, transaction);
        await block.ExecuteNonQueryAsync(timeout.Token);

        await using var host = db.NewHost();
        var reading = host.Originals.TraceAsync(db.OwnerA, db.Original.Timeline, Rich, SoloOriginals.MaxTraceTicks, cancelled.Token);
        await using var status = db.Superuser.CreateCommand(
            "SELECT EXISTS (SELECT FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND query LIKE '%FROM marrow_timeline.checkpoints%')");
        while (!(bool)(await status.ExecuteScalarAsync(timeout.Token))!)
        {
            await Task.Delay(10, timeout.Token);
        }

        await cancelled.CancelAsync();
        Assert.Equal(SoloErrorCode.Cancelled, (await reading.WaitAsync(timeout.Token)).Error.Code);
        await transaction.RollbackAsync(timeout.Token);
        Ok(await host.Originals.InspectAsync(db.OwnerA, db.Original.Timeline, Rich, timeout.Token));
    }

    private ServiceProvider NewRealtimeServices()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["ConnectionStrings:GhostLapSolo"] = db.AppConnectionString }).Build();
        return new ServiceCollection().AddGhostLapSolo(configuration)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static void EqualFrame(SoloTraceFrame expected, SoloTraceFrame actual)
    {
        Assert.Equal(expected with { ActiveCandleIds = [], Events = [] }, actual with { ActiveCandleIds = [], Events = [] });
        Assert.Equal(expected.ActiveCandleIds.AsEnumerable(), actual.ActiveCandleIds.AsEnumerable());
        Assert.Equal(expected.Events.AsEnumerable(), actual.Events.AsEnumerable());
    }

    private async Task<T> MeasureAsync<T>(string label, int repetitions, Func<Task<Marrow.Kernel.Results.Result<T, SoloError>>> read)
    {
        var before = await DatabaseStatsAsync();
        var elapsed = new List<double>(repetitions);
        T result = default!;
        for (var i = 0; i < repetitions; i++)
        {
            var start = Stopwatch.GetTimestamp();
            result = Ok(await read());
            elapsed.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }

        var stats = await DatabaseStatsAsync() - before;
        var jsonStart = Stopwatch.GetTimestamp();
        var json = JsonSerializer.SerializeToUtf8Bytes(result, Json);
        var jsonMs = Stopwatch.GetElapsedTime(jsonStart).TotalMilliseconds;
        elapsed.Sort();
        var median = (elapsed[(repetitions - 1) / 2] + elapsed[repetitions / 2]) / 2;
        output.WriteLine($"{label} n={repetitions} median={median:F2}ms range={elapsed[0]:F2}–{elapsed[^1]:F2}ms " +
                         $"calls/read={(double)stats.Calls / repetitions:F2} pg_exec/read={stats.ServerMs / repetitions:F2}ms " +
                         $"JSON last={jsonMs:F2}ms/{json.Length}B");
        return result;
    }

    private async Task<DatabaseStats> DatabaseStatsAsync()
    {
        await using var command = db.Superuser.CreateCommand(
            """
            SELECT coalesce(sum(calls), 0)::bigint, coalesce(sum(total_exec_time), 0)
            FROM pg_stat_statements
            WHERE userid = (SELECT oid FROM pg_roles WHERE rolname = @role)
              AND dbid = (SELECT oid FROM pg_database WHERE datname = current_database())
              AND query LIKE 'SELECT %'
            """);
        command.Parameters.AddWithValue("role", new NpgsqlConnectionStringBuilder(db.AppConnectionString).Username!);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new DatabaseStats(reader.GetInt64(0), reader.GetDouble(1));
    }

    private async Task<string> SourceSizeAsync(TimelineId id)
    {
        await using var command = db.Superuser.CreateCommand(
            """
            SELECT sum(rowsize), sum(canonical)
            FROM (
                SELECT pg_column_size(t) AS rowsize, 0 AS canonical FROM ghostlap.solo_originals t WHERE timeline_id = @id
                UNION ALL
                SELECT pg_column_size(t), 0 FROM marrow_timeline.histories t WHERE timeline_id = @id
                UNION ALL
                SELECT pg_column_size(t), 0 FROM marrow_timeline.steps t WHERE timeline_id = @id
                UNION ALL
                SELECT pg_column_size(t), octet_length(payload) FROM marrow_timeline.step_inputs t WHERE timeline_id = @id
                UNION ALL
                SELECT pg_column_size(t), octet_length(state_bytes) FROM marrow_timeline.checkpoints t WHERE timeline_id = @id
                UNION ALL
                SELECT pg_column_size(t), octet_length(canonical_bytes) FROM marrow_timeline.context_artifacts t
                JOIN marrow_timeline.histories h ON t.context_ref = h.context_ref WHERE h.timeline_id = @id
            ) source
            """);
        command.Parameters.AddWithValue("id", id.Value);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return $"row_bytes={reader.GetInt64(0)} canonical_bytes={reader.GetInt64(1)}";
    }

    private readonly record struct DatabaseStats(long Calls, double ServerMs)
    {
        public static DatabaseStats operator -(DatabaseStats a, DatabaseStats b) =>
            new(a.Calls - b.Calls, a.ServerMs - b.ServerMs);
    }
}
