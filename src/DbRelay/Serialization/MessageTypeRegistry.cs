using System.Collections.Concurrent;
using System.Reflection;
using DbRelay.Storage;

namespace DbRelay.Serialization;

/// <summary>
/// Maps CLR types to logical message type names: explicit mappings first, then <see cref="OutboxMessageTypeAttribute"/>, then the
/// full CLR name. This is one-way. Names are never resolved back to types.
/// </summary>
internal sealed class MessageTypeRegistry
{
    private readonly IReadOnlyDictionary<Type, string> _explicit;
    private readonly ConcurrentDictionary<Type, string> _cache = new();

    public MessageTypeRegistry(IReadOnlyDictionary<Type, string> explicitMappings)
    {
        _explicit = explicitMappings;
    }

    public string GetName(Type type) => _cache.GetOrAdd(type, Resolve);

    internal static void ValidateName(string name, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name, paramName);
        if (name.Length > OutboxLimits.MaxMessageTypeLength)
        {
            throw new ArgumentException($"Message type names must be at most {OutboxLimits.MaxMessageTypeLength} characters.", paramName);
        }

        foreach (var c in name)
        {
            if (char.IsControl(c))
            {
                throw new ArgumentException("Message type names must not contain control characters.", paramName);
            }
        }
    }

    private string Resolve(Type type)
    {
        if (_explicit.TryGetValue(type, out var mapped))
        {
            return mapped;
        }

        var attribute = type.GetCustomAttribute<OutboxMessageTypeAttribute>(inherit: false);
        var name = attribute?.Name ?? FormatClrName(type);
        ValidateName(name, nameof(type));
        return name;
    }

    /// <summary>
    /// A stable, readable CLR name. For generic types this gives <c>Ns.Envelope&lt;Ns.OrderCreated&gt;</c> instead of
    /// <see cref="Type.FullName"/>, which embeds assembly-qualified argument names with versions that change on every upgrade (CR-14).
    /// </summary>
    internal static string FormatClrName(Type type)
    {
        if (type.IsArray)
        {
            // List<int>[] would otherwise fall back to FullName and embed assembly versions (FU-07).
            var rank = type.GetArrayRank();
            return $"{FormatClrName(type.GetElementType()!)}[{new string(',', rank - 1)}]";
        }

        if (!type.IsGenericType)
        {
            return type.FullName ?? type.Name;
        }

        // Format each nesting level with its own arguments. Outer`1+Inner2`1 with [int, string] becomes
        // Ns.Outer<System.Int32>+Inner2<System.String>, so Outer<int>.Inner never collides with Outer<int> (FU-07).
        var definition = type.GetGenericTypeDefinition().FullName ?? type.Name;
        var arguments = type.GetGenericArguments();
        var next = 0;
        var segments = definition.Split('+');
        var builder = new System.Text.StringBuilder();
        for (var i = 0; i < segments.Length; i++)
        {
            if (i > 0)
            {
                builder.Append('+');
            }

            var segment = segments[i];
            var tick = segment.IndexOf('`', StringComparison.Ordinal);
            if (tick < 0)
            {
                builder.Append(segment);
                continue;
            }

            builder.Append(segment, 0, tick);
            var arity = int.Parse(segment.AsSpan(tick + 1), System.Globalization.CultureInfo.InvariantCulture);
            builder.Append('<');
            for (var a = 0; a < arity; a++)
            {
                if (a > 0)
                {
                    builder.Append(',');
                }

                builder.Append(FormatClrName(arguments[next++]));
            }

            builder.Append('>');
        }

        return builder.ToString();
    }
}
