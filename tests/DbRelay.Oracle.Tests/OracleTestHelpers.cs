using Dapper;

namespace DbRelay.Oracle.Tests;

internal static class OracleTestHelpers
{
    public static string NewSchemaName() => DbRelay.Testing.TestSchemaNames.New("p");

    /// <summary>The upper-case, quoted form the provider uses for a configured name.</summary>
    public static string Quote(string name) => "\"" + name.ToUpperInvariant() + "\"";

    /// <summary>
    /// Creates a schema-only user (no password, so nobody can log in as it) that may store data in the USERS tablespace. DbRelay
    /// itself never creates users.
    /// </summary>
    public static async Task CreateSchemaAsync(OracleFixture fixture, string schema)
    {
        await using var connection = await fixture.OpenAsync();
        await connection.ExecuteAsync($"CREATE USER {Quote(schema)} NO AUTHENTICATION DEFAULT TABLESPACE USERS QUOTA UNLIMITED ON USERS");
    }

    public static async Task DropSchemaAsync(OracleFixture fixture, string schema)
    {
        await using var connection = await fixture.OpenAsync();
        await connection.ExecuteAsync(
            $"""
            BEGIN
                EXECUTE IMMEDIATE 'DROP USER {Quote(schema)} CASCADE';
            EXCEPTION
                WHEN OTHERS THEN
                    IF SQLCODE != -1918 THEN RAISE; END IF; -- ORA-01918: user does not exist
            END;
            """);
    }

    public static async Task<DateTimeOffset> GetDatabaseNowAsync(OracleFixture fixture)
    {
        await using var connection = await fixture.OpenAsync();
        var now = await connection.ExecuteScalarAsync<DateTime>("SELECT SYS_EXTRACT_UTC(SYSTIMESTAMP) FROM DUAL");
        return new DateTimeOffset(DateTime.SpecifyKind(now, DateTimeKind.Utc), TimeSpan.Zero);
    }
}
