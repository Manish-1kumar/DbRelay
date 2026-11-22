using System.Text;

namespace DbRelay;

/// <summary>
/// An outbox message being delivered. This is an immutable data object.
/// </summary>
/// <remarks>
/// To deserialize the payload into a type, inject <see cref="IMessageSerializer"/> into your handler and call
/// <c>serializer.Deserialize&lt;T&gt;(message.Payload.Span)</c>. DbRelay never creates a CLR type from
/// <see cref="MessageType"/>; you always name the target type explicitly.
/// </remarks>
public sealed class OutboxMessage
{
    private static readonly IReadOnlyDictionary<string, string> EmptyHeaders = new Dictionary<string, string>(0);

    /// <summary>
    /// The message ID. It is the same on every delivery attempt, so consumers should use it for idempotency
    /// (delivery is at-least-once).
    /// </summary>
    public required Guid Id { get; init; }

    /// <summary>The logical message type name.</summary>
    public required string MessageType { get; init; }

    /// <summary>The routing hint supplied at enqueue time, if any.</summary>
    public string? Destination { get; init; }

    /// <summary>The serialized payload, exactly as stored.</summary>
    public required ReadOnlyMemory<byte> Payload { get; init; }

    /// <summary>The payload's media type, for example <c>application/json</c>.</summary>
    public required string ContentType { get; init; }

    /// <summary>Headers supplied at enqueue time plus trace context captured by DbRelay.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = EmptyHeaders;

    /// <summary>The correlation ID supplied at enqueue time, if any.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>The causation ID supplied at enqueue time, if any.</summary>
    public string? CausationId { get; init; }

    /// <summary>When the message was inserted (database clock, UTC).</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>The earliest time the message was allowed to be delivered (database clock, UTC).</summary>
    public required DateTimeOffset ScheduledAt { get; init; }

    /// <summary>The 1-based number of this delivery attempt.</summary>
    public required int Attempt { get; init; }

    /// <summary>Decodes the payload as UTF-8 text. Use this only for textual content types.</summary>
    /// <returns>The payload as a string.</returns>
    public string GetPayloadAsString() => Encoding.UTF8.GetString(Payload.Span);
}
