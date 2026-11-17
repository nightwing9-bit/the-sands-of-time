using System.Text.Json;
using GhostLap.Multiplayer;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace GhostLapHost;

/// <summary>Process-wide runtime numbers (GC, CPU, thread pool) as deltas since the probe was created. Per process, never attributable to one room.</summary>
public sealed record ProcessLoad(
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    double GcPauseMilliseconds,
    long AllocatedBytes,
    double CpuMilliseconds,
    double ElapsedMilliseconds,
    int Pid,
    long WorkingSetBytes,
    int ProcessorCount,
    int ThreadPoolThreads,
    long ThreadPoolPendingWork);

/// <summary>Captures process-wide GC and CPU as deltas from construction, so a profile starts at zero regardless of host start-up cost.</summary>
public sealed class ProcessProbe
{
    private readonly int _gen0 = GC.CollectionCount(0);
    private readonly int _gen1 = GC.CollectionCount(1);
    private readonly int _gen2 = GC.CollectionCount(2);
    private readonly TimeSpan _pause = GC.GetTotalPauseDuration();
    private readonly long _allocated = GC.GetTotalAllocatedBytes(precise: false);
    private readonly TimeSpan _cpu = ReadCpu();
    private readonly long _started = System.Diagnostics.Stopwatch.GetTimestamp();

    private static TimeSpan ReadCpu()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        return process.TotalProcessorTime;
    }

    public ProcessLoad Capture()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        return new(
            GC.CollectionCount(0) - _gen0,
            GC.CollectionCount(1) - _gen1,
            GC.CollectionCount(2) - _gen2,
            (GC.GetTotalPauseDuration() - _pause).TotalMilliseconds,
            GC.GetTotalAllocatedBytes(precise: false) - _allocated,
            (process.TotalProcessorTime - _cpu).TotalMilliseconds,
            System.Diagnostics.Stopwatch.GetElapsedTime(_started).TotalMilliseconds,
            process.Id,
            process.WorkingSet64,
            Environment.ProcessorCount,
            ThreadPool.ThreadCount,
            ThreadPool.PendingWorkItemCount);
    }
}

/// <summary>One profile line: the registry's match load, the process numbers and the open Ghost Lap hub connection count.</summary>
public sealed record GhostLapProfileLine(DateTimeOffset At, string Kind, int OpenConnections, ProcessLoad Process, MatchLoadReport Load);

/// <summary>
/// Task 10.5 target-host instrumentation output. When <see cref="MultiplayerOptions.ProfileOutputPath"/> is set, appends one JSON line of load
/// measurements (tick execution, start lateness, backlog, per-room mailbox, outbound queues, snapshot size, drops, bytes, GC, CPU, admission
/// counters) every <see cref="MultiplayerOptions.ProfileIntervalSeconds"/> and a <c>final</c> line at host stop. Nothing is served over the
/// network and nothing here can change a tick, a limit or a schedule; a failed write is skipped, never fatal. When the path is empty the service
/// does nothing.
/// </summary>
public sealed class GhostLapLoadReporter(
    MatchRegistry matches,
    GhostLapConnections connections,
    IOptions<MultiplayerOptions> options,
    GhostLapLog log) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    private readonly ProcessProbe _probe = new();
    private int _finalWritten;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var path = options.Value.ProfileOutputPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.ProfileIntervalSeconds));
        try
        {
            await WriteAsync(path, "start", CancellationToken.None);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await WriteAsync(path, "sample", stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        var path = options.Value.ProfileOutputPath;
        if (!string.IsNullOrWhiteSpace(path) && Interlocked.Exchange(ref _finalWritten, 1) == 0)
        {
            await WriteAsync(path, "final", CancellationToken.None); // matches are still readable here; the registry stops after this service
        }
    }

    private async Task WriteAsync(string path, string kind, CancellationToken ct)
    {
        try
        {
            var line = new GhostLapProfileLine(
                DateTimeOffset.UtcNow, kind, connections.Count, _probe.Capture(), matches.CaptureLoad(connections.RefusedForCapacity));
            await File.AppendAllTextAsync(path, JsonSerializer.Serialize(line, Json) + "\n", ct);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            log.ServiceIterationFailed(e, nameof(GhostLapLoadReporter));
        }
    }
}
