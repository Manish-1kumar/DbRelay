namespace DbRelay.Configuration;

/// <summary>General enqueue options.</summary>
public sealed class OutboxOptions
{
    /// <summary>The maximum serialized payload size in bytes (1 byte to 256 MiB). Defaults to 1 MiB.</summary>
    public int MaxPayloadBytes { get; set; } = 1024 * 1024;
}

/// <summary>Background dispatcher options.</summary>
public sealed class DispatcherOptions
{
    /// <summary>The maximum number of messages claimed per database round trip (1 to 1000). Defaults to 100.</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>The maximum number of deliveries running at the same time in this process (1 to 256). Defaults to 8.</summary>
    public int MaxConcurrency { get; set; } = 8;

    /// <summary>How long to wait before polling again when the last claim returned fewer than <see cref="BatchSize"/> messages (10 ms to 1 hour). Defaults to 1 second. A ±10% jitter is applied.</summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long a claim stays valid (1 second to 1 hour). Defaults to 2 minutes. If a worker dies, its messages become claimable again
    /// after this time. Must be at least twice <see cref="DeliveryTimeout"/>.
    /// </summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>The maximum time a single delivery may take before it is cancelled and retried (more than 0, at most 30 minutes). Defaults to 30 seconds.</summary>
    public TimeSpan DeliveryTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The longest wait between polls while the database is failing (1 second to 10 minutes). Defaults to 30 seconds.</summary>
    public TimeSpan MaxErrorBackoff { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>A diagnostic identifier stored with claims. Defaults to <c>machine:process-id:random</c>. At most 256 characters.</summary>
    public string? WorkerId { get; set; }
}

/// <summary>Retry options.</summary>
public sealed class RetryOptions
{
    /// <summary>
    /// The maximum number of delivery attempts, including the first (1 to 1000). Defaults to 10. Attempts are counted when a message is
    /// claimed, so a message that crashes the worker every time still reaches the dead-letter state.
    /// </summary>
    public int MaxAttempts { get; set; } = 10;

    /// <summary>The base delay before the first retry (more than 0, at most 1 day). Defaults to 1 second.</summary>
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>The upper bound for any retry delay (at least <see cref="InitialDelay"/>, at most 7 days). Defaults to 5 minutes.</summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The exponential growth factor per attempt (1 to 10). Defaults to 2.</summary>
    public double BackoffMultiplier { get; set; } = 2.0;

    /// <summary>
    /// Decides whether an exception thrown by the delivery handler is permanent. Permanent failures are dead-lettered immediately.
    /// Defaults to <see langword="null"/>: every exception is treated as transient.
    /// </summary>
    public Func<Exception, bool>? IsPermanentFailure { get; set; }
}

/// <summary>Background maintenance options (retention cleanup and statistics for metrics).</summary>
public sealed class MaintenanceOptions
{
    /// <summary>Whether delivered messages are deleted after <see cref="DeliveredRetention"/>. Defaults to <see langword="true"/>.</summary>
    public bool CleanupEnabled { get; set; } = true;

    /// <summary>How long delivered messages are kept (0 to 3650 days). Defaults to 1 day.</summary>
    public TimeSpan DeliveredRetention { get; set; } = TimeSpan.FromDays(1);

    /// <summary>How often the cleanup runs (1 second to 1 day). Defaults to 5 minutes.</summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The maximum number of rows deleted per statement (1 to 10000). Defaults to 1000.</summary>
    public int CleanupBatchSize { get; set; } = 1000;

    /// <summary>
    /// How often outbox statistics are queried to feed the <c>dbrelay.messages.*</c> gauges (1 second to 1 day), or <see langword="null"/>
    /// to disable the gauges. Defaults to 1 minute.
    /// </summary>
    public TimeSpan? StatisticsInterval { get; set; } = TimeSpan.FromMinutes(1);
}
