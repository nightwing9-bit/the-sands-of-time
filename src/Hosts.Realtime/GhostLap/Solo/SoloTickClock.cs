using System.Diagnostics;
using GhostLap.Contracts.Solo;

namespace GhostLapHost;

/// <summary>
/// Monotonic 30 Hz deadlines. PeriodicTimer truncates a 33.333 ms period to whole milliseconds,
/// accumulating a roughly 0.9 s early finish over a round. Rounding each remaining wait up avoids
/// cumulative acceleration. A stalled commit skips elapsed clock slots, never logical game ticks.
/// </summary>
internal sealed class SoloTickClock
{
    private readonly long _started = Stopwatch.GetTimestamp();
    private long _slot;

    public async ValueTask<bool> WaitForNextTickAsync(CancellationToken ct)
    {
        var now = Stopwatch.GetTimestamp();
        _slot = Math.Max(_slot + 1, (long)((now - _started) * (double)GhostLapSoloWire.TickRateHz / Stopwatch.Frequency) + 1);
        var deadline = _started + (long)Math.Ceiling(_slot * (double)Stopwatch.Frequency / GhostLapSoloWire.TickRateHz);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var remaining = deadline - Stopwatch.GetTimestamp();
            if (remaining <= 0) return true;
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Ceiling(remaining * 1000d / Stopwatch.Frequency)), ct).ConfigureAwait(false);
        }
    }
}
