using Microsoft.Extensions.Logging;

namespace DbRelay.Diagnostics;

/// <summary>
/// Source-generated log messages. Payloads and connection strings are never logged.
/// </summary>
internal static partial class Log
{
    [LoggerMessage(1000, LogLevel.Information, "DbRelay dispatcher {WorkerId} started (batch size {BatchSize}, max concurrency {MaxConcurrency}, lease {LeaseDuration}).")]
    public static partial void DispatcherStarted(ILogger logger, string workerId, int batchSize, int maxConcurrency, TimeSpan leaseDuration);

    [LoggerMessage(1001, LogLevel.Information, "DbRelay dispatcher {WorkerId} stopped.")]
    public static partial void DispatcherStopped(ILogger logger, string workerId);

    [LoggerMessage(1002, LogLevel.Error, "DbRelay failed to claim messages ({ConsecutiveFailures} consecutive failures). Retrying in {Delay}.")]
    public static partial void ClaimFailed(ILogger logger, Exception exception, int consecutiveFailures, TimeSpan delay);

    [LoggerMessage(1003, LogLevel.Debug, "DbRelay claimed {Count} message(s).")]
    public static partial void Claimed(ILogger logger, int count);

    [LoggerMessage(1004, LogLevel.Debug, "Delivered message {MessageId} ({MessageType}) on attempt {Attempt}.")]
    public static partial void Delivered(ILogger logger, Guid messageId, string messageType, int attempt);

    [LoggerMessage(1005, LogLevel.Warning, "Delivery of message {MessageId} ({MessageType}) failed on attempt {Attempt}/{MaxAttempts}; retrying in {Delay}. Reason: {Reason}")]
    public static partial void RetryScheduled(ILogger logger, Exception? exception, Guid messageId, string messageType, int attempt, int maxAttempts, string reason, TimeSpan delay);

    [LoggerMessage(1006, LogLevel.Error, "Message {MessageId} ({MessageType}) was dead-lettered after attempt {Attempt}: {Reason}")]
    public static partial void DeadLettered(ILogger logger, Exception? exception, Guid messageId, string messageType, int attempt, string reason);

    [LoggerMessage(1007, LogLevel.Warning, "Message {MessageId} ({MessageType}) was reclaimed by another worker before outcome '{Outcome}' was recorded (lease lost). It may be delivered again.")]
    public static partial void LeaseLost(ILogger logger, Guid messageId, string messageType, string outcome);

    [LoggerMessage(1008, LogLevel.Warning, "Recording outcome '{Outcome}' for message {MessageId} failed (attempt {Try}); retrying.")]
    public static partial void OutcomePersistRetry(ILogger logger, Exception exception, Guid messageId, string outcome, int @try);

    [LoggerMessage(1009, LogLevel.Error, "Recording outcome '{Outcome}' for message {MessageId} failed. The message will be claimed again after its lease expires and may be delivered again.")]
    public static partial void OutcomePersistFailed(ILogger logger, Exception exception, Guid messageId, string outcome);

    [LoggerMessage(1010, LogLevel.Debug, "Released {Count} claimed message(s) without delivering them ({Reason}).")]
    public static partial void Released(ILogger logger, int count, string reason);

    [LoggerMessage(1011, LogLevel.Error, "Unexpected error while processing message {MessageId}.")]
    public static partial void ProcessingFailed(ILogger logger, Exception exception, Guid messageId);

    [LoggerMessage(1012, LogLevel.Warning, "Releasing message {MessageId} failed; it will become claimable when its lease expires.")]
    public static partial void ReleaseFailed(ILogger logger, Exception exception, Guid messageId);

    [LoggerMessage(1013, LogLevel.Error, "The delivery handler for message {MessageId} ({MessageType}) did not return within {DeliveryTimeout} and ignored cancellation; it was abandoned and the message rescheduled. Make the handler honour its CancellationToken.")]
    public static partial void HandlerAbandoned(ILogger logger, Guid messageId, string messageType, TimeSpan deliveryTimeout);

    [LoggerMessage(1014, LogLevel.Warning, "An abandoned delivery handler for message {MessageId} eventually failed.")]
    public static partial void AbandonedHandlerFaulted(ILogger logger, Exception exception, Guid messageId);

    [LoggerMessage(1102, LogLevel.Warning, "DbRelay cleanup is behind: it deleted the per-run maximum of {Count} delivered message(s) and will run again in {Delay}.")]
    public static partial void CleanupBehind(ILogger logger, int count, TimeSpan delay);

    [LoggerMessage(1100, LogLevel.Information, "DbRelay cleanup deleted {Count} delivered message(s) older than {Retention}.")]
    public static partial void CleanupDeleted(ILogger logger, int count, TimeSpan retention);

    [LoggerMessage(1101, LogLevel.Warning, "DbRelay maintenance task '{Task}' failed.")]
    public static partial void MaintenanceFailed(ILogger logger, Exception exception, string task);
}
