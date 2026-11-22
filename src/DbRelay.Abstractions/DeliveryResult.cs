namespace DbRelay;

/// <summary>The outcome category of a delivery attempt.</summary>
public enum DeliveryOutcome
{
    /// <summary>The message was delivered. It will not be delivered again unless a crash prevents DbRelay from recording success.</summary>
    Delivered = 1,

    /// <summary>A transient failure. The message is retried with backoff until the maximum number of attempts is reached.</summary>
    Retry = 2,

    /// <summary>A permanent failure. The message is moved to the dead-letter state without further attempts.</summary>
    DeadLetter = 3,
}

/// <summary>
/// The result a delivery handler returns.
/// </summary>
public sealed class DeliveryResult
{
    private static readonly DeliveryResult DeliveredInstance = new(DeliveryOutcome.Delivered, null, null);

    private DeliveryResult(DeliveryOutcome outcome, string? reason, TimeSpan? retryAfter)
    {
        Outcome = outcome;
        Reason = reason;
        RetryAfter = retryAfter;
    }

    /// <summary>The outcome category.</summary>
    public DeliveryOutcome Outcome { get; }

    /// <summary>Why the delivery failed. <see langword="null"/> for <see cref="DeliveryOutcome.Delivered"/>.</summary>
    public string? Reason { get; }

    /// <summary>For <see cref="DeliveryOutcome.Retry"/>, an explicit delay that overrides the computed backoff (capped at the configured maximum delay).</summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>The message was delivered.</summary>
    /// <returns>A cached result instance.</returns>
    public static DeliveryResult Delivered() => DeliveredInstance;

    /// <summary>A transient failure; retry later.</summary>
    /// <param name="reason">A short description of the failure. Stored in the outbox row (truncated to 2000 characters), so do not include secrets.</param>
    /// <param name="retryAfter">An optional explicit delay, for example from a <c>Retry-After</c> header. Must not be negative.</param>
    /// <returns>A retry result.</returns>
    public static DeliveryResult Retry(string reason, TimeSpan? retryAfter = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (retryAfter is { } delay && delay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retryAfter), delay, "The retry delay must not be negative.");
        }

        return new DeliveryResult(DeliveryOutcome.Retry, reason, retryAfter);
    }

    /// <summary>A permanent failure; move the message to the dead-letter state.</summary>
    /// <param name="reason">A short description of the failure. Stored in the outbox row (truncated to 2000 characters), so do not include secrets.</param>
    /// <returns>A dead-letter result.</returns>
    public static DeliveryResult DeadLetter(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new DeliveryResult(DeliveryOutcome.DeadLetter, reason, null);
    }

    /// <inheritdoc />
    public override string ToString() => Reason is null ? Outcome.ToString() : $"{Outcome}: {Reason}";
}
