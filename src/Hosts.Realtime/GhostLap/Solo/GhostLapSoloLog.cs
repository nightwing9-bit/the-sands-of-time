using Microsoft.Extensions.Logging;

namespace GhostLapHost;

/// <summary>Ghost Lap solo log events 4111-4118. Run ids, tick numbers and reason codes only: never principals, cookies or inputs.</summary>
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

    [LoggerMessage(EventId = 4116, Level = LogLevel.Information, Message = "Ghost Lap challenge preparation failed: code {Code}. No challenge started.")]
    private static partial void LogChallengePrepareFailed(ILogger logger, string code);

    [LoggerMessage(EventId = 4117, Level = LogLevel.Information, Message = "Ghost Lap challenge started: child {RunId}")]
    private static partial void LogChallengeStarted(ILogger logger, string runId);

    [LoggerMessage(EventId = 4118, Level = LogLevel.Information, Message = "Ghost Lap challenge finish: child {RunId}, outcome {Outcome}")]
    private static partial void LogChallengeEnded(ILogger logger, string runId, string outcome);

    public void ChallengePrepareFailed(string code) => LogChallengePrepareFailed(logger, code);

    public void ChallengeStarted(string runId) => LogChallengeStarted(logger, runId);

    public void ChallengeEnded(string runId, string outcome) => LogChallengeEnded(logger, runId, outcome);

    public void RunStarted(string runId) => LogRunStarted(logger, runId);

    public void RunInterrupted(string runId, long tick, string reason) => LogRunInterrupted(logger, runId, tick, reason);

    public void SaveOutcome(string runId, string status, string? code) => LogSaveOutcome(logger, runId, status, code ?? "none");

    public void RunFailed(Exception exception, string runId) => LogRunFailed(logger, exception, runId);

    public void InputFlood(string connectionId) => LogInputFlood(logger, connectionId);
}
