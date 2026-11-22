namespace DbRelay;

/// <summary>
/// Optional metadata for an enqueued message.
/// </summary>
public sealed class EnqueueOptions
{
    /// <summary>
    /// The message ID to use. When <see langword="null"/>, DbRelay generates a time-ordered UUID (version 7).
    /// Supplying your own ID lets you derive it from business data, for idempotent producers. Inserting a duplicate ID fails
    /// with the provider's unique-constraint error.
    /// </summary>
    public Guid? MessageId { get; init; }

    /// <summary>
    /// A routing hint for the delivery handler, such as a topic, queue, exchange or URL. At most 512 characters.
    /// </summary>
    public string? Destination { get; init; }

    /// <summary>A correlation ID propagated to the handler. At most 128 characters.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>The ID of the message or command that caused this message. At most 128 characters.</summary>
    public string? CausationId { get; init; }

    /// <summary>
    /// Delivers the message no earlier than this absolute time. This is compared with the <b>database</b> clock.
    /// Cannot be combined with <see cref="Delay"/>.
    /// </summary>
    public DateTimeOffset? DeliverAt { get; init; }

    /// <summary>
    /// Delivers the message no earlier than this long after the insert, measured by the <b>database</b> clock, so it is
    /// unaffected by application server clock skew. Cannot be combined with <see cref="DeliverAt"/>.
    /// </summary>
    public TimeSpan? Delay { get; init; }

    /// <summary>
    /// Custom headers passed to the delivery handler. At most 64 entries; keys at most 128 characters, values at most 4096.
    /// Keys starting with <c>dbrelay-</c> are reserved. W3C trace context (<c>traceparent</c>, <c>tracestate</c>) is added
    /// automatically when an activity is current, unless you supply those headers yourself.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
}
