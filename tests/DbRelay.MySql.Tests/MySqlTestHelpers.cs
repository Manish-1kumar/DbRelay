using Dapper;

namespace DbRelay.MySql.Tests;

internal static class MySqlTestHelpers
{
    public static string NewSchemaName() => DbRelay.Testing.TestSchemaNames.New("p");

    public static string Quote(string name) => "`" + name + "`";

    /// <summary>The value DbRelay stores for a GUID: <c>UNHEX</c> of its 32 hex digits.</summary>
    public static string Hex(Guid id) => id.ToString("N");

    /// <summary>Creates the test database. DbRelay itself never creates databases.</summary>
    public static async Task CreateSchemaAsync(MySqlFixture fixture, string schema)
    {
        await using var connection = await fixture.OpenAsync();
        await connection.ExecuteAsync($"CREATE DATABASE {Quote(schema)} CHARACTER SET utf8mb4 COLLATE utf8mb4_bin");
    }

    public static async Task DropSchemaAsync(MySqlFixture fixture, string schema)
    {
        await using var connection = await fixture.OpenAsync();
        await connection.ExecuteAsync($"DROP DATABASE IF EXISTS {Quote(schema)}");
    }

    public static async Task<DateTimeOffset> GetDatabaseNowAsync(MySqlFixture fixture)
    {
        await using var connection = await fixture.OpenAsync();
        var now = await connection.ExecuteScalarAsync<DateTime>("SELECT UTC_TIMESTAMP(6)");
        return new DateTimeOffset(DateTime.SpecifyKind(now, DateTimeKind.Utc), TimeSpan.Zero);
    }
}
