using System.Data.Common;
using Dapper;
using DbRelay.Storage.Conformance;
using static DbRelay.Oracle.Tests.OracleTestHelpers;

namespace DbRelay.Oracle.Tests;

/// <summary>Runs the shared <see cref="OutboxStoreConformanceTests"/> against real Oracle.</summary>
public sealed class OracleConformanceTests(OracleFixture fixture) : OutboxStoreConformanceTests
{
    protected override string? SkipReason => fixture.SkipReason;

    protected override void UseStorage(DbRelayBuilder relay, string schema) =>
        relay.UseOracle(fixture.ConnectionString, oracle => oracle.Schema = schema);

    protected override async Task<DbConnection> OpenConnectionAsync() => await fixture.OpenAsync();

    protected override Task PrepareSchemaAsync(string schema) => CreateSchemaAsync(fixture, schema);

    protected override string SchemaNameInScript(string schema) => schema.ToUpperInvariant();

    protected override string CreateOrdersTableSql(string schema) =>
        $"CREATE TABLE {Quote(schema)}.ORDERS (ID RAW(16) PRIMARY KEY, NOTE VARCHAR2(200 CHAR) NOT NULL)";

    protected override Task InsertOrderAsync(DbConnection connection, DbTransaction? transaction, string note) =>
        connection.ExecuteAsync(
            $"INSERT INTO {Quote(Schema)}.ORDERS (ID, NOTE) VALUES (:Id, :Note)",
            new { Id = Guid.NewGuid().ToByteArray(), Note = note },
            transaction);

    protected override async Task ExpireAsync(string schema, Guid messageId)
    {
        await using var connection = await fixture.OpenAsync();
        var updated = await connection.ExecuteAsync(
            $"UPDATE {Quote(schema)}.\"OUTBOX_MESSAGES\" SET AVAILABLE_AT = SYS_EXTRACT_UTC(SYSTIMESTAMP) - INTERVAL '1' SECOND WHERE ID = :Id",
            new { Id = messageId.ToByteArray() });
        Assert.Equal(1, updated);
    }

    protected override async Task SetRawHeadersAsync(string schema, Guid messageId, string headersJson)
    {
        await using var connection = await fixture.OpenAsync();
        var updated = await connection.ExecuteAsync(
            $"UPDATE {Quote(schema)}.\"OUTBOX_MESSAGES\" SET HEADERS = :Headers WHERE ID = :Id",
            new { Headers = headersJson, Id = messageId.ToByteArray() });
        Assert.Equal(1, updated);
    }

    protected override Task<DateTimeOffset> GetDatabaseNowAsync() => OracleTestHelpers.GetDatabaseNowAsync(fixture);

    protected override Task DropSchemaAsync(string schema) => OracleTestHelpers.DropSchemaAsync(fixture, schema);
}
