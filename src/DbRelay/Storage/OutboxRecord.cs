namespace DbRelay.Storage;

/// <summary>
/// A validated message ready to be inserted by a storage provider.
/// </summary>
public sealed class OutboxRecord
{
    /// <summary>The message ID.</summary>
    public required Guid Id { get; init; }

    /// <summary>The logical message type name (at most <see cref="OutboxLimits.MaxMessageTypeLength"/> characters).</summary>
    public required string MessageType { get; init; }

    /// <summary>The routing hint, if any.</summary>
    public string? Destination { get; init; }

    /// <summary>The payload bytes.</summary>
    public required ReadOnlyMemory<byte> Payload { get; init; }

    /// <summary>The payload's media type.</summary>
    public required string ContentType { get; init; }

    /// <summary>The headers serialized as a JSON object, or <see langword="null"/> when there are none.</summary>
    public string? HeadersJson { get; init; }

    /// <summary>The correlation ID, if any.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>The causation ID, if any.</summary>
    public string? CausationId { get; init; }

    /// <summary>
    /// An absolute earliest delivery time. When both this and <see cref="Delay"/> are <see langword="null"/>, the message is due immediately.
    /// The provider stores <c>scheduled_at = available_at = DeliverAt</c>.
    /// </summary>
    public DateTimeOffset? DeliverAt { get; init; }

    /// <summary>
    /// A delay relative to the database's current time. The provider stores <c>scheduled_at = available_at = now + Delay</c>.
    /// Never set together with <see cref="DeliverAt"/>.
    /// </summary>
    public TimeSpan? Delay { get; init; }
}

/// <summary>Parameters for <see cref="IOutboxStore.ClaimAsync"/>.</summary>
/// <param name="BatchSize">The maximum number of messages to claim (1 to <see cref="OutboxLimits.MaxClaimBatchSize"/>).</param>
/// <param name="LeaseDuration">How long the claim is valid, measured from the database's current time.</param>
/// <param name="LockToken">A new, unique token identifying this claim.</param>
/// <param name="WorkerId">A diagnostic worker identifier (at most <see cref="OutboxLimits.MaxWorkerIdLength"/> characters).</param>
public sealed record ClaimRequest(int BatchSize, TimeSpan LeaseDuration, Guid LockToken, string WorkerId)
{
    /// <summary>
    /// The payload budget of the claim, in bytes (at least 1). Defaults to <see cref="OutboxLimits.BatchPayloadBudgetBytes"/>.
    /// Messages are taken in claim order until their payloads reach this total: a message is claimed only if the payloads of
    /// the messages before it in the batch total less than this value, so the first message is always claimed however large it
    /// is. This bounds the memory one claim can load (review finding CR-18, ADR 0004).
    /// </summary>
    public long MaxPayloadBytes { get; init; } = OutboxLimits.BatchPayloadBudgetBytes;
}

/// <summary>Size limits shared by validation and storage providers (column sizes).</summary>
public static class OutboxLimits
{
    /// <summary>Maximum message type name length.</summary>
    public const int MaxMessageTypeLength = 256;

    /// <summary>Maximum destination length.</summary>
    public const int MaxDestinationLength = 512;

    /// <summary>Maximum content type length.</summary>
    public const int MaxContentTypeLength = 128;

    /// <summary>Maximum correlation and causation ID length.</summary>
    public const int MaxCorrelationIdLength = 128;

    /// <summary>Maximum number of headers per message.</summary>
    public const int MaxHeaderCount = 64;

    /// <summary>Maximum header key length.</summary>
    public const int MaxHeaderKeyLength = 128;

    /// <summary>Maximum header value length.</summary>
    public const int MaxHeaderValueLength = 4096;

    /// <summary>Maximum stored error text length.</summary>
    public const int MaxErrorLength = 2000;

    /// <summary>Maximum worker ID length.</summary>
    public const int MaxWorkerIdLength = 256;

    /// <summary>Maximum number of messages in one enqueue call.</summary>
    public const int MaxEnqueueBatchSize = 1000;

    /// <summary>Maximum number of messages in one claim.</summary>
    public const int MaxClaimBatchSize = 1000;

    /// <summary>
    /// The payload bytes allowed in one enqueue call or one claim (64 MiB), unless a single message is larger (CR-18, ADR 0004).
    /// An enqueue call may carry up to this many bytes or <c>OutboxOptions.MaxPayloadBytes</c>, whichever is larger. A claim
    /// stops adding messages once their payloads reach this total (see <see cref="ClaimRequest.MaxPayloadBytes"/>).
    /// </summary>
    public const int BatchPayloadBudgetBytes = 64 * 1024 * 1024;
}
