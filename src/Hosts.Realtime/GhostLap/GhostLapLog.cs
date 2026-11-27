using Microsoft.Extensions.Logging;

namespace GhostLapHost;

/// <summary>Ghost Lap Realtime log events 4101-4110. Connection ids, kinds and reason codes only: never cookies, tokens, principals or payloads.</summary>
public sealed partial class GhostLapLog(ILogger<GhostLapLog> logger)
{
    [LoggerMessage(EventId = 4101, Level = LogLevel.Information, Message = "Ghost Lap connection refused: connection {ConnectionId}, reason {Reason}")]
    private static partial void LogConnectionRefused(ILogger logger, string connectionId, string reason);

    [LoggerMessage(EventId = 4102, Level = LogLevel.Information, Message = "Ghost Lap connection closed: access no longer verified, connection {ConnectionId}, reason {Reason}")]
    private static partial void LogConnectionClosedForAccess(ILogger logger, string connectionId, string reason);

    [LoggerMessage(EventId = 4103, Level = LogLevel.Warning, Message = "Ghost Lap input stream closed for flooding: connection {ConnectionId}")]
    private static partial void LogInputFloodClosed(ILogger logger, string connectionId);

    [LoggerMessage(EventId = 4104, Level = LogLevel.Warning, Message = "Ghost Lap request refused: cookie-authenticated request without an allowed Origin")]
    private static partial void LogOriginRejected(ILogger logger);

    [LoggerMessage(EventId = 4105, Level = LogLevel.Warning, Message = "Ghost Lap room publication rejected: result {Result}")]
    private static partial void LogPublishRejected(ILogger logger, string result);

    [LoggerMessage(EventId = 4106, Level = LogLevel.Error, Message = "Ghost Lap background service failed an iteration: {Service}")]
    private static partial void LogServiceIterationFailed(ILogger logger, Exception exception, string service);

    [LoggerMessage(EventId = 4107, Level = LogLevel.Information, Message = "Ghost Lap multiplayer lobby composition verified: authority generation {Generation}")]
    private static partial void LogCompositionVerified(ILogger logger, string generation);

    [LoggerMessage(EventId = 4108, Level = LogLevel.Information, Message = "Ghost Lap process generation {Generation} registered: retired {Retired} predecessor generation(s), aborted {Aborted} unfinished start receipt(s)")]
    private static partial void LogGenerationRegistered(ILogger logger, string generation, int retired, int aborted);

    [LoggerMessage(EventId = 4109, Level = LogLevel.Critical, Message = "Ghost Lap process generation {Generation} was retired by a successor: match authority withdrawn, host stopping")]
    private static partial void LogGenerationRetired(ILogger logger, string generation);

    public void GenerationRegistered(string generation, int retired, int aborted) => LogGenerationRegistered(logger, generation, retired, aborted);

    public void GenerationRetired(string generation) => LogGenerationRetired(logger, generation);

    public void ConnectionRefused(string connectionId, string reason) => LogConnectionRefused(logger, connectionId, reason);

    public void ConnectionClosedForAccess(string connectionId, string reason) => LogConnectionClosedForAccess(logger, connectionId, reason);

    public void InputFloodClosed(string connectionId) => LogInputFloodClosed(logger, connectionId);

    public void OriginRejected() => LogOriginRejected(logger);

    public void PublishRejected(string result) => LogPublishRejected(logger, result);

    public void ServiceIterationFailed(Exception exception, string service) => LogServiceIterationFailed(logger, exception, service);

    public void CompositionVerified(string generation) => LogCompositionVerified(logger, generation);
}
