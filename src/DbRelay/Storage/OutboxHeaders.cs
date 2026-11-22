using System.Text.Json;
using System.Text.Json.Serialization;

namespace DbRelay.Storage;

/// <summary>
/// Serializes headers to and from the JSON object format stored in the outbox <c>headers</c> column.
/// </summary>
public static class OutboxHeaders
{
    private static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>(0);

    /// <summary>Serializes headers to a JSON object string, or returns <see langword="null"/> when there are none.</summary>
    /// <param name="headers">The headers.</param>
    /// <returns>The JSON text, or <see langword="null"/>.</returns>
    public static string? Serialize(IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null || headers.Count == 0)
        {
            return null;
        }

        var copy = headers as Dictionary<string, string> ?? new Dictionary<string, string>(headers, StringComparer.Ordinal);
        return JsonSerializer.Serialize(copy, HeadersJsonContext.Default.DictionaryStringString);
    }

    /// <summary>The reserved header added when stored headers could not be read as a JSON object.</summary>
    public const string ErrorHeader = "dbrelay-headers-error";

    /// <summary>
    /// Parses a stored headers JSON object. <b>Never throws</b>: providers call this while mapping claimed rows, and one unreadable
    /// row must not fail a whole claim (CR-01). Returns an empty dictionary for <see langword="null"/> or empty input.
    /// String values are returned as-is. Non-string values (numbers, booleans, objects, arrays, null) are returned as their raw JSON text.
    /// If the text is not a JSON object, the result contains only <see cref="ErrorHeader"/>.
    /// </summary>
    /// <param name="json">The stored JSON text.</param>
    /// <returns>The headers.</returns>
    public static IReadOnlyDictionary<string, string> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Unreadable("the stored headers are not a JSON object");
            }

            var headers = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                headers[property.Name] = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString()!
                    : property.Value.GetRawText();
            }

            return headers;
        }
        catch (Exception)
        {
            // Not only JsonException. Invalid UTF-16 makes Parse throw ArgumentException, and an escaped lone surrogate such as
            // "\ud800" parses but makes GetString()/Name throw InvalidOperationException (follow-up audit FU-01). This method must
            // be total.
            return Unreadable("the stored headers could not be read as JSON");
        }
    }

    private static Dictionary<string, string> Unreadable(string reason) =>
        new(1, StringComparer.Ordinal) { [ErrorHeader] = reason + "; they were ignored." };
}

[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class HeadersJsonContext : JsonSerializerContext;
