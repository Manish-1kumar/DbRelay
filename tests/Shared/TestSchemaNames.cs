using System.Data.Common;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DbRelay.Testing;

/// <summary>
/// Names of the per-test schemas, and the sweep that removes schemas left behind by killed test processes (K13).
/// </summary>
/// <remarks>
/// A name is <c>prefix_</c> followed by 16 hex digits: 8 for the creation time (Unix seconds, UTC) and 8 random. Embedding the
/// time lets a sweep drop only schemas that are clearly abandoned. The target frameworks run their test processes in parallel
/// against one database, so a sweep must never drop a schema that another live process is still using.
/// This file is compiled into DbRelay.Storage.Conformance and DbRelay.IntegrationTests (linked, not referenced).
/// </remarks>
public static partial class TestSchemaNames
{
    /// <summary>Schemas older than this are assumed abandoned. No test, or whole test run, takes anywhere near this long.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(6);

    /// <summary>Creates a schema name such as <c>t_6740b5c1a3f09e27</c>.</summary>
    public static string New(string prefix)
    {
        var seconds = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return prefix + "_" + seconds.ToString("x8", CultureInfo.InvariantCulture) + Guid.NewGuid().ToString("N")[..8];
    }

    /// <summary>
    /// True for a test schema name created at least <see cref="StaleAfter"/> before <paramref name="now"/>. Names in the older,
    /// fully random format are judged by their first 8 digits too, so some of those leftovers are swept and the rest stay
    /// (CONTRIBUTING.md has the manual cleanup).
    /// </summary>
    public static bool IsStale(string name, DateTimeOffset now)
    {
        var match = NamePattern().Match(name);
        if (!match.Success)
        {
            return false;
        }

        var created = DateTimeOffset.FromUnixTimeSeconds(uint.Parse(match.Groups["time"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        return created <= now - StaleAfter;
    }

    /// <summary>Drops stale PostgreSQL test schemas. Best effort: failures (for example a concurrent sweep) are ignored.</summary>
    public static async Task<int> SweepPostgreSqlAsync(DbConnection connection)
    {
        var stale = await ListStaleAsync(connection, "SELECT nspname FROM pg_namespace WHERE nspname ~ '^(t|it|p)_[0-9a-f]{16}$'");
        var dropped = 0;
        foreach (var schema in stale)
        {
            // The name matched the pattern above, so it contains only letters, digits and an underscore.
            if (await TryExecuteAsync(connection, $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE"))
            {
                dropped++;
            }
        }

        return dropped;
    }

    /// <summary>Drops stale SQL Server test schemas and their tables. Best effort: failures are ignored.</summary>
    public static async Task<int> SweepSqlServerAsync(DbConnection connection)
    {
        var stale = await ListStaleAsync(connection, "SELECT name FROM sys.schemas WHERE name LIKE '[tip]%[_]________________'");
        var dropped = 0;
        foreach (var schema in stale)
        {
            var sql =
                $"""
                DECLARE @sql nvarchar(max) = N'';
                SELECT @sql += N'DROP TABLE ' + QUOTENAME(s.name) + N'.' + QUOTENAME(t.name) + N';'
                FROM sys.tables AS t JOIN sys.schemas AS s ON s.schema_id = t.schema_id WHERE s.name = N'{schema}';
                SET @sql += N'IF SCHEMA_ID(N''{schema}'') IS NOT NULL DROP SCHEMA [{schema}];';
                EXEC (@sql);
                """;
            if (await TryExecuteAsync(connection, sql))
            {
                dropped++;
            }
        }

        return dropped;
    }

    /// <summary>
    /// Drops stale Oracle test schemas. In Oracle a schema is a user, and the provider stores names in upper case
    /// (<c>T_6740B5C1A3F09E27</c>). Best effort: failures (for example a user still connected) are ignored.
    /// </summary>
    public static async Task<int> SweepOracleAsync(DbConnection connection)
    {
        var stale = await ListStaleAsync(connection, "SELECT USERNAME FROM ALL_USERS WHERE REGEXP_LIKE(USERNAME, '^(T|IT|P)_[0-9A-F]{16}$')");
        var dropped = 0;
        foreach (var user in stale)
        {
            // The name matched the pattern above, so it contains only letters, digits and an underscore.
            if (await TryExecuteAsync(connection, $"DROP USER \"{user}\" CASCADE"))
            {
                dropped++;
            }
        }

        return dropped;
    }

    /// <summary>
    /// Drops stale MySQL / MariaDB test schemas. In MySQL a schema is a database. Best effort: failures are ignored.
    /// </summary>
    public static async Task<int> SweepMySqlAsync(DbConnection connection)
    {
        var stale = await ListStaleAsync(connection, "SELECT SCHEMA_NAME FROM information_schema.SCHEMATA WHERE SCHEMA_NAME REGEXP '^(t|it|p)_[0-9a-f]{16}$'");
        var dropped = 0;
        foreach (var schema in stale)
        {
            // The name matched the pattern above, so it contains only letters, digits and an underscore.
            if (await TryExecuteAsync(connection, $"DROP DATABASE IF EXISTS `{schema}`"))
            {
                dropped++;
            }
        }

        return dropped;
    }

    private static async Task<List<string>> ListStaleAsync(DbConnection connection, string sql)
    {
        var now = DateTimeOffset.UtcNow;
        var names = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var name = reader.GetString(0);
            if (IsStale(name.ToLowerInvariant(), now))
            {
                names.Add(name);
            }
        }

        return names;
    }

    private static async Task<bool> TryExecuteAsync(DbConnection connection, string sql)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = 60;
            await command.ExecuteNonQueryAsync();
            return true;
        }
        catch (DbException)
        {
            return false;
        }
    }

    [GeneratedRegex("^(?:t|it|p)_(?<time>[0-9a-f]{8})[0-9a-f]{8}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
