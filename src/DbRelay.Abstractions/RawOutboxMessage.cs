namespace DbRelay;

/// <summary>
/// A message whose payload is already serialized. The bytes are stored and delivered unchanged.
/// </summary>
public sealed class RawOutboxMessage
{
    /// <summary>Creates a raw message.</summary>
    /// <param name="messageType">Logical message type name (1 to 256 characters), for example <c>orders.created</c>.</param>
    /// <param name="payload">The serialized payload.</param>
    /// <param name="contentType">The payload's media type (1 to 128 characters), for example <c>application/x-protobuf</c>.</param>
    public RawOutboxMessage(string messageType, ReadOnlyMemory<byte> payload, string contentType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageType);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        MessageType = messageType;
        Payload = payload;
        ContentType = contentType;
    }

    /// <summary>Logical message type name.</summary>
    public string MessageType { get; }

    /// <summary>The serialized payload.</summary>
    public ReadOnlyMemory<byte> Payload { get; }

    /// <summary>The payload's media type.</summary>
    public string ContentType { get; }
}
