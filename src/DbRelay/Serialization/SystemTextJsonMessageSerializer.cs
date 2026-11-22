using System.Text.Json;

namespace DbRelay.Serialization;

/// <summary>
/// The default <see cref="IMessageSerializer"/>, based on System.Text.Json. It produces <c>application/json</c> payloads.
/// </summary>
/// <remarks>
/// Messages are serialized using their runtime type. Polymorphic type metadata (<c>$type</c>) is only honoured if you configure it
/// explicitly on the <see cref="JsonSerializerOptions"/>. DbRelay never resolves CLR types from stored names.
/// </remarks>
public sealed class SystemTextJsonMessageSerializer : IMessageSerializer
{
    private readonly JsonSerializerOptions _options;

    /// <summary>Creates a serializer.</summary>
    /// <param name="options">Serializer options. Defaults to <see cref="JsonSerializerDefaults.Web"/> (camelCase property names).</param>
    public SystemTextJsonMessageSerializer(JsonSerializerOptions? options = null)
    {
        // Copy before freezing so the caller's instance stays mutable (CR-20).
        _options = options is null ? new JsonSerializerOptions(JsonSerializerDefaults.Web) : new JsonSerializerOptions(options);
        _options.MakeReadOnly(populateMissingResolver: true);
    }

    /// <inheritdoc />
    public string ContentType => "application/json";

    /// <inheritdoc />
    public byte[] Serialize<TMessage>(TMessage message)
    {
        if (message is null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        return JsonSerializer.SerializeToUtf8Bytes(message, message.GetType(), _options);
    }

    /// <inheritdoc />
    public TMessage Deserialize<TMessage>(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
        {
            throw new InvalidOperationException("The payload is empty.");
        }

        return JsonSerializer.Deserialize<TMessage>(payload, _options)
            ?? throw new InvalidOperationException($"The payload deserialized to null for type '{typeof(TMessage).FullName}'.");
    }
}
