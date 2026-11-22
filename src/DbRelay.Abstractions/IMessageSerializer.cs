namespace DbRelay;

/// <summary>
/// Converts messages to and from payload bytes.
/// </summary>
/// <remarks>
/// The default implementation uses System.Text.Json. Deserialization always uses a type the caller names explicitly;
/// DbRelay never creates a type from a name stored in the database.
/// </remarks>
public interface IMessageSerializer
{
    /// <summary>The media type of the produced payloads, for example <c>application/json</c>.</summary>
    string ContentType { get; }

    /// <summary>Serializes <paramref name="message"/> using its runtime type.</summary>
    /// <param name="message">The message. Must not be <see langword="null"/>.</param>
    /// <returns>The payload bytes.</returns>
    byte[] Serialize<TMessage>(TMessage message);

    /// <summary>Deserializes <paramref name="payload"/> into <typeparamref name="TMessage"/>.</summary>
    /// <param name="payload">The payload bytes.</param>
    /// <returns>The deserialized message.</returns>
    /// <exception cref="InvalidOperationException">The payload is empty or represents a null value.</exception>
    TMessage Deserialize<TMessage>(ReadOnlySpan<byte> payload);
}
