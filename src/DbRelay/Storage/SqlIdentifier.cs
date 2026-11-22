using System.Text.RegularExpressions;

namespace DbRelay.Storage;

/// <summary>
/// Validates configurable SQL identifiers (schema and table names) before providers embed them in SQL.
/// </summary>
/// <remarks>
/// Identifiers cannot be passed as SQL parameters, so providers only accept names consisting solely of an ASCII letter or
/// underscore followed by up to 62 ASCII letters, digits or underscores (no whitespace or control characters anywhere,
/// including a trailing newline), and quote them as well. Anything else is rejected, which rules out SQL injection
/// through configuration.
/// </remarks>
public static partial class SqlIdentifier
{
    /// <summary>Throws <see cref="ArgumentException"/> unless <paramref name="identifier"/> is a safe identifier.</summary>
    /// <param name="identifier">The identifier.</param>
    /// <param name="paramName">The configuration property name, used in the exception.</param>
    /// <returns><paramref name="identifier"/>, unchanged.</returns>
    public static string Validate(string? identifier, string paramName)
    {
        if (identifier is null || !IsValid(identifier))
        {
            throw new ArgumentException(
                $"'{paramName}' must be 1 to 63 characters long, start with a letter or underscore, and contain only ASCII letters, digits and underscores.",
                paramName);
        }

        return identifier;
    }

    /// <summary>Returns <see langword="true"/> if <paramref name="identifier"/> is a safe identifier.</summary>
    /// <param name="identifier">The identifier.</param>
    /// <returns>Whether the identifier is valid.</returns>
    public static bool IsValid(string? identifier) => identifier is not null && Pattern().IsMatch(identifier);

    // \A and \z, not ^ and $: in .NET, $ also matches before a trailing '\n', which would let "name\n" through (CR-02).
    [GeneratedRegex(@"\A[A-Za-z_][A-Za-z0-9_]{0,62}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Pattern();
}
