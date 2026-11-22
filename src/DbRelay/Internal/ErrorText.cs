using System.Text;
using DbRelay.Storage;

namespace DbRelay.Internal;

internal static class ErrorText
{
    private const char Replacement = '�';
    private const string TruncationMarker = "…";

    /// <summary>
    /// Formats an exception for storage: the type name and message only (no stack trace, no inner exceptions), sanitized and truncated.
    /// Full details go to the logger.
    /// </summary>
    public static string FromException(Exception exception) =>
        Truncate($"{exception.GetType().Name}: {exception.Message}");

    /// <summary>
    /// Makes text safe to store in any provider and truncates it to <see cref="OutboxLimits.MaxErrorLength"/> characters.
    /// NUL characters (rejected by PostgreSQL <c>text</c>) and unpaired surrogates (not encodable as UTF-8) are replaced with U+FFFD,
    /// and truncation never splits a surrogate pair.
    /// </summary>
    public static string Truncate(string text)
    {
        var safe = Sanitize(text);
        if (safe.Length <= OutboxLimits.MaxErrorLength)
        {
            return safe;
        }

        var cut = OutboxLimits.MaxErrorLength - TruncationMarker.Length;
        if (char.IsHighSurrogate(safe[cut - 1]))
        {
            cut--; // keep the pair whole by dropping its first half
        }

        return string.Concat(safe.AsSpan(0, cut), TruncationMarker);
    }

    /// <summary>A fixed, always-storable reason used when the real one could not be stored.</summary>
    public static string Fallback(string outcome) =>
        $"DbRelay could not store the original {outcome} reason; see the application logs for details.";

    internal static string Sanitize(string text)
    {
        if (!NeedsSanitizing(text))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\0')
            {
                builder.Append(Replacement);
            }
            else if (char.IsHighSurrogate(c))
            {
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    builder.Append(c).Append(text[i + 1]);
                    i++;
                }
                else
                {
                    builder.Append(Replacement);
                }
            }
            else if (char.IsLowSurrogate(c))
            {
                builder.Append(Replacement); // a low surrogate not preceded by a high one
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private static bool NeedsSanitizing(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\0')
            {
                return true;
            }

            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1]))
                {
                    return true;
                }

                i++;
            }
            else if (char.IsLowSurrogate(c))
            {
                return true;
            }
        }

        return false;
    }
}
