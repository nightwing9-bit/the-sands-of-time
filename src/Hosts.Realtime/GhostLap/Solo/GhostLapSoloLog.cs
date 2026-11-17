using Microsoft.Extensions.Logging;

namespace GhostLapHost;

/// <summary>Ghost Lap solo log events 4111-4115. Run ids, tick numbers and reason codes only: never principals, cookies or inputs.</summary>
public sealed partial class GhostLapSoloLog(ILogger<GhostLapSoloLog> logger)
{
    [LoggerMessage(EventId = 4111, Level = LogLevel.Information, Message = "Ghost Lap solo run started: run {RunId}")]
    private static partial void LogRunStarted(ILogger logger, string runId);

    [LoggerMessage(EventId = 4112, Level = LogLevel.Warning, Message = "Ghost Lap solo run interrupted: run {RunId}, tick {Tick}, reason {Reason}. It is not an original.")]
    private static partial void LogRunInterrupted(ILogger logger, string runId, long tick, string reason);

    [LoggerMessage(EventId = 4113, Level = LogLevel.Information, Message = "Ghost Lap solo save: run {RunId}, status {Status}, code {Code}")]
    private static partial void LogSaveOutcome(ILogger logger, string runId, string status, string code);

    [LoggerMessage(EventId = 4114, Level = LogLevel.Error, Message = "Ghost Lap solo run loop failed: run {RunId}")]
    private static partial void LogRunFailed(ILogger logger, Exception exception, string runId);

    [LoggerMessage(EventId = 4115, Level = LogLevel.Warning, Message = "Ghost Lap solo input stream closed for flooding: connection {ConnectionId}")]
    private static partial void LogInputFlood(ILogger logger, string connectionId);

    public void RunStarted(string runId) => LogRunStarted(logger, runId);

    public void RunInterrupted(string runId, long tick, string reason) => LogRunInterrupted(logger, runId, tick, reason);

    public void SaveOutcome(string runId, string status, string? code) => LogSaveOutcome(logger, runId, status, code ?? "none");

    public void RunFailed(Exception exception, string runId) => LogRunFailed(logger, exception, runId);

    public void InputFlood(string connectionId) => LogInputFlood(logger, connectionId);
}
