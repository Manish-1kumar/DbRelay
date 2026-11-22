using System.Data.Common;
using Dapper;
using DbRelay.Storage;
using DbRelay.Storage.Conformance;
using Microsoft.Data.SqlClient;

namespace DbRelay.SqlServer.Tests;

/// <summary>Runs the shared storage conformance suite against SQL Server.</summary>
public abstract class SqlServerConformanceTestsBase : OutboxStoreConformanceTests
{
    private readonly SqlServerFixture _fixture;
    private readonly bool _rcsi;

    protected SqlServerConformanceTestsBase(SqlServerFixture fixture, bool rcsi)
    {
        _fixture = fixture;
        _rcsi = rcsi;
    }

    protected override string? SkipReason => _fixture.GetSkipReason(_rcsi);

    private string ConnectionString => _fixture.GetConnectionString(_rcsi)!;

    protected override void UseStorage(DbRelayBuilder relay, string schema) =>
        relay.UseSqlServer(ConnectionString, options => options.Schema = schema);

    protected override async Task<DbConnection> OpenConnectionAsync()
    {
        var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(Ct);
        return connection;
    }

    protected override string CreateOrdersTableSql(string schema) =>
        $"CREATE TABLE [{schema}].[orders] ([id] uniqueidentifier NOT NULL PRIMARY KEY, [note] nvarchar(400) NOT NULL);";

    /// <summary>
    /// With RCSI OFF a plain read-committed COUNT would wait for the caller's open transaction, so skip locked rows instead.
    /// With RCSI ON the default statement reads the last committed version without blocking.
    /// </summary>
    protected override string CountCommittedOrdersSql(string schema) =>
        _rcsi ? base.CountCommittedOrdersSql(schema) : $"SELECT COUNT(*) FROM [{schema}].[orders] WITH (READPAST);";

    protected override async Task ExpireAsync(string schema, Guid messageId)
    {
        await using var connection = await OpenConnectionAsync();
        var rows = await connection.ExecuteAsync(
            $"UPDATE [{schema}].[outbox_messages] SET [available_at] = DATEADD(second, -1, SYSUTCDATETIME()) WHERE [id] = @messageId;",
            new { messageId });
        Assert.Equal(1, rows);
    }

    protected override async Task SetRawHeadersAsync(string schema, Guid messageId, string headersJson)
    {
        await using var connection = await OpenConnectionAsync();
        var rows = await connection.ExecuteAsync(
            $"UPDATE [{schema}].[outbox_messages] SET [headers] = @headersJson WHERE [id] = @messageId;",
            new { messageId, headersJson });
        Assert.Equal(1, rows);
    }

    protected override async Task<DateTimeOffset> GetDatabaseNowAsync()
    {
        await using var connection = await OpenConnectionAsync();
        var now = await connection.ExecuteScalarAsync<DateTime>("SELECT SYSUTCDATETIME();");
        return new DateTimeOffset(DateTime.SpecifyKind(now, DateTimeKind.Utc));
    }

    protected override async Task DropSchemaAsync(string schema)
    {
        await using var connection = await OpenConnectionAsync();
        await TestDatabase.DropSchemaAsync(connection, schema);
    }

    /// <summary>
    /// FU-01 / FU-T2, SQL Server only. <c>nvarchar</c> accepts text that PostgreSQL <c>jsonb</c> rejects at insert: invalid JSON and
    /// lone UTF-16 surrogates, escaped or raw. Such rows must not fail the claim.
    /// </summary>
    [Fact]
    public async Task Claim_tolerates_headers_that_are_invalid_json_or_invalid_utf16()
    {
        Assert.SkipWhen(SkipReason is not null, SkipReason ?? string.Empty);
        string[] poison =
        [
            "{\"a\":\"\\ud800\"}",          // escaped lone surrogate in a value
            "{\"\\udc00\":\"v\"}",          // escaped lone surrogate in a key
            "{\"a\":\"" + '\uD800' + "\"}", // raw lone surrogate (built at runtime)
            "{\"a\":\"cut \\ud83d\"}",      // string cut mid-emoji by a JavaScript producer
            "not json at all",
        ];

        var ids = new List<Guid>();
        foreach (var headers in poison)
        {
            var id = await Outbox.EnqueueStandaloneAsync(new TestMessage("poison"), cancellationToken: Ct);
            await SetRawHeadersAsync(Schema, id, headers);
            ids.Add(id);
        }

        var healthy = await Outbox.EnqueueStandaloneAsync(new TestMessage("healthy"), cancellationToken: Ct);
        var token = Guid.NewGuid();

        var claimed = await Store.ClaimAsync(new ClaimRequest(100, TimeSpan.FromMinutes(5), token, "fu-t2"), Ct);

        // The essential property: no poison row fails the claim (FU-01 reproduced as a wedge before the fix).
        Assert.Equal(ids.Count + 1, claimed.Count);

        // Inputs that reach the parser as invalid JSON or invalid UTF-16 come back with the error header. The raw lone surrogate
        // (index 2) is normalised by the driver round trip into valid text before DbRelay parses it, so it is an ordinary header:
        // still claimed and readable, which is what matters. The parser's own handling of a raw lone surrogate is covered by
        // CoreTests.Header_parsing_never_throws_on_a_raw_lone_surrogate.
        var mustBeFlagged = ids.Where((_, index) => index != 2).ToHashSet();
        Assert.All(claimed.Where(m => mustBeFlagged.Contains(m.Id)), m => Assert.True(m.Headers.ContainsKey(OutboxHeaders.ErrorHeader)));
        Assert.False(claimed.Single(m => m.Id == healthy).Headers.ContainsKey(OutboxHeaders.ErrorHeader));
        foreach (var message in claimed)
        {
            Assert.True(await Store.MarkDeliveredAsync(message.Id, token, Ct));
        }

        Assert.All(ids, id => Assert.NotNull(Admin.GetAsync(id, Ct).GetAwaiter().GetResult())); // admin reads tolerate them too
    }
}

/// <summary>Conformance with READ_COMMITTED_SNAPSHOT OFF (SQL Server's default; locking read committed).</summary>
public sealed class SqlServerConformanceTests(SqlServerFixture fixture) : SqlServerConformanceTestsBase(fixture, rcsi: false);

/// <summary>Conformance with READ_COMMITTED_SNAPSHOT ON (Azure SQL's default; row-versioned read committed).</summary>
public sealed class SqlServerRcsiConformanceTests(SqlServerFixture fixture) : SqlServerConformanceTestsBase(fixture, rcsi: true);

internal static class TestDatabase
{
    public static Task DropSchemaAsync(System.Data.IDbConnection connection, string schema) =>
        connection.ExecuteAsync(
            """
            DECLARE @sql nvarchar(max) = N'';
            SELECT @sql += N'DROP TABLE ' + QUOTENAME(s.name) + N'.' + QUOTENAME(t.name) + N';'
            FROM sys.tables AS t JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            WHERE s.name = @schema;
            EXEC (@sql);
            IF SCHEMA_ID(@schema) IS NOT NULL
            BEGIN
                DECLARE @drop nvarchar(max) = N'DROP SCHEMA ' + QUOTENAME(@schema);
                EXEC (@drop);
            END;
            """,
            new { schema });
}
