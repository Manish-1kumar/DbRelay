using System.Data.Common;
using Dapper;
using DbRelay.Storage.Conformance;

namespace DbRelay.PostgreSql.Tests;

/// <summary>Runs the shared <see cref="OutboxStoreConformanceTests"/> against real PostgreSQL.</summary>
public sealed class PostgreSqlConformanceTests(PostgreSqlFixture fixture) : OutboxStoreConformanceTests
{
    protected override string? SkipReason => fixture.SkipReason;

    // The connection-string overload: DbRelay owns (and disposes) the data source of each test's service provider.
    protected override void UseStorage(DbRelayBuilder relay, string schema) =>
        relay.UsePostgreSql(fixture.ConnectionString, pg => pg.Schema = schema);

    protected override async Task<DbConnection> OpenConnectionAsync() => await fixture.DataSource.OpenConnectionAsync();

    protected override string CreateOrdersTableSql(string schema) =>
        $"CREATE TABLE \"{schema}\".orders (id uuid PRIMARY KEY, note text NOT NULL)";

    protected override async Task ExpireAsync(string schema, Guid messageId)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        var updated = await connection.ExecuteAsync(
            $"UPDATE \"{schema}\".outbox_messages SET available_at = now() - interval '1 second' WHERE id = @messageId",
            new { messageId });
        Assert.Equal(1, updated);
    }

    protected override async Task SetRawHeadersAsync(string schema, Guid messageId, string headersJson)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        var updated = await connection.ExecuteAsync(
            $"UPDATE \"{schema}\".outbox_messages SET headers = CAST(@headersJson AS jsonb) WHERE id = @messageId",
            new { messageId, headersJson });
        Assert.Equal(1, updated);
    }

    protected override Task<DateTimeOffset> GetDatabaseNowAsync() => PostgreSqlTestHelpers.GetDatabaseNowAsync(fixture.DataSource);

    protected override Task DropSchemaAsync(string schema) => PostgreSqlTestHelpers.DropSchemaAsync(fixture.DataSource, schema);
}
