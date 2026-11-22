using Npgsql;

namespace DbRelay.PostgreSql.Tests;

internal static class PostgreSqlTestHelpers
{
    public static string NewSchemaName() => DbRelay.Testing.TestSchemaNames.New("p");

    public static async Task<DateTimeOffset> GetDatabaseNowAsync(NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand("SELECT now()");
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return reader.GetFieldValue<DateTimeOffset>(0);
    }

    public static async Task DropSchemaAsync(NpgsqlDataSource dataSource, string schema)
    {
        await using var command = dataSource.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
        await command.ExecuteNonQueryAsync();
    }
}
