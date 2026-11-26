using System.Data.Common;
using Dapper;
using DbRelay.Storage.Conformance;
using static DbRelay.MySql.Tests.MySqlTestHelpers;

namespace DbRelay.MySql.Tests;

/// <summary>Runs the shared <see cref="OutboxStoreConformanceTests"/> against a real MySQL or MariaDB server.</summary>
public sealed class MySqlConformanceTests(MySqlFixture fixture) : OutboxStoreConformanceTests
{
    protected override string? SkipReason => fixture.SkipReason;

    protected override void UseStorage(DbRelayBuilder relay, string schema) =>
        relay.UseMySql(fixture.ConnectionString, mysql => mysql.Schema = schema);

    protected override async Task<DbConnection> OpenConnectionAsync() => await fixture.OpenAsync();

    protected override Task PrepareSchemaAsync(string schema) => CreateSchemaAsync(fixture, schema);

    protected override string CreateOrdersTableSql(string schema) =>
        $"CREATE TABLE {Quote(schema)}.orders (id CHAR(36) NOT NULL PRIMARY KEY, note TEXT NOT NULL) ENGINE = InnoDB";

    protected override async Task ExpireAsync(string schema, Guid messageId)
    {
        await using var connection = await fixture.OpenAsync();
        var updated = await connection.ExecuteAsync(
            $"UPDATE {Quote(schema)}.outbox_messages SET available_at = UTC_TIMESTAMP(6) - INTERVAL 1 SECOND WHERE id = UNHEX(@Id)",
            new { Id = Hex(messageId) });
        Assert.Equal(1, updated);
    }

    protected override async Task SetRawHeadersAsync(string schema, Guid messageId, string headersJson)
    {
        await using var connection = await fixture.OpenAsync();
        var updated = await connection.ExecuteAsync(
            $"UPDATE {Quote(schema)}.outbox_messages SET headers = @Headers WHERE id = UNHEX(@Id)",
            new { Headers = headersJson, Id = Hex(messageId) });
        Assert.Equal(1, updated);
    }

    protected override Task<DateTimeOffset> GetDatabaseNowAsync() => MySqlTestHelpers.GetDatabaseNowAsync(fixture);

    protected override Task DropSchemaAsync(string schema) => MySqlTestHelpers.DropSchemaAsync(fixture, schema);
}
