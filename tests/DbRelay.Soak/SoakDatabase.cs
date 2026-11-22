using System.Data.Common;
using Dapper;
using DbRelay;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace DbRelay.Soak;

public enum Provider
{
    PostgreSql,
    SqlServer,
}

[OutboxMessageType("soak.order")]
public sealed record SoakMessage(int Sequence, bool Poison, string Filler);

/// <summary>Provider-specific SQL for the soak harness (the library itself is exercised only through its public API).</summary>
public sealed class SoakDatabase(Provider provider, string schema)
{
    public Provider Provider { get; } = provider;

    public string Schema { get; } = schema;

    public string BaseConnectionString => Provider == Provider.PostgreSql
        ? Environment.GetEnvironmentVariable("DBRELAY_TEST_POSTGRES") ?? throw new InvalidOperationException("Set DBRELAY_TEST_POSTGRES.")
        : Environment.GetEnvironmentVariable("DBRELAY_TEST_SQLSERVER") ?? throw new InvalidOperationException("Set DBRELAY_TEST_SQLSERVER.");

    public string ConnectionString(string applicationName) => Provider == Provider.PostgreSql
        ? new NpgsqlConnectionStringBuilder(BaseConnectionString) { ApplicationName = applicationName }.ConnectionString
        : new SqlConnectionStringBuilder(BaseConnectionString) { ApplicationName = applicationName }.ConnectionString;

    public string T(string table) => Provider == Provider.PostgreSql ? $"\"{Schema}\".{table}" : $"[{Schema}].[{table}]";

    public void UseStorage(DbRelayBuilder relay, string applicationName)
    {
        if (Provider == Provider.PostgreSql)
        {
            relay.UsePostgreSql(ConnectionString(applicationName), o => o.Schema = Schema);
        }
        else
        {
            relay.UseSqlServer(ConnectionString(applicationName), o => o.Schema = Schema);
        }
    }

    public async Task<DbConnection> OpenAsync(string applicationName, CancellationToken ct = default)
    {
        DbConnection connection = Provider == Provider.PostgreSql
            ? new NpgsqlConnection(ConnectionString(applicationName))
            : new SqlConnection(ConnectionString(applicationName));
        await connection.OpenAsync(ct);
        return connection;
    }

    public string CreateTablesSql => Provider == Provider.PostgreSql
        ? $"""
          CREATE TABLE {T("orders")} (message_id uuid PRIMARY KEY, poison boolean NOT NULL, created_at timestamptz NOT NULL DEFAULT now());
          CREATE TABLE {T("sink")} (message_id uuid NOT NULL, attempt int NOT NULL, worker text NOT NULL, received_at timestamptz NOT NULL DEFAULT now());
          CREATE INDEX ON {T("sink")} (message_id);
          """
        : $"""
          CREATE TABLE {T("orders")} ([message_id] uniqueidentifier NOT NULL PRIMARY KEY NONCLUSTERED, [poison] bit NOT NULL, [created_at] datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME());
          CREATE TABLE {T("sink")} ([message_id] uniqueidentifier NOT NULL, [attempt] int NOT NULL, [worker] nvarchar(64) NOT NULL, [received_at] datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME());
          CREATE INDEX ix_sink_message ON {T("sink")} ([message_id]);
          """;

    public string InsertOrderSql => $"INSERT INTO {T("orders")} (message_id, poison) VALUES (@Id, @Poison)";

    public string InsertSinkSql => $"INSERT INTO {T("sink")} (message_id, attempt, worker) VALUES (@Id, @Attempt, @Worker)";

    /// <summary>Kills every database session opened by soak workers (chaos). Returns the number killed.</summary>
    public async Task<int> KillWorkerSessionsAsync()
    {
        await using var connection = await OpenAsync("soak-chaos");
        if (Provider == Provider.PostgreSql)
        {
            return await connection.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM (SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE application_name LIKE 'soak-worker%') AS k");
        }

        return await connection.ExecuteScalarAsync<int>("""
            DECLARE @n int = 0, @sql nvarchar(max) = N'';
            SELECT @sql += N'KILL ' + CAST(session_id AS nvarchar(10)) + N';', @n += 1
            FROM sys.dm_exec_sessions WHERE program_name LIKE 'soak-worker%' AND session_id <> @@SPID;
            EXEC (@sql);
            SELECT @n;
            """);
    }

    public async Task DropAsync()
    {
        await using var connection = await OpenAsync("soak-cleanup");
        if (Provider == Provider.PostgreSql)
        {
            await connection.ExecuteAsync($"DROP SCHEMA IF EXISTS \"{Schema}\" CASCADE");
            return;
        }

        await connection.ExecuteAsync("""
            DECLARE @sql nvarchar(max) = N'';
            SELECT @sql += N'DROP TABLE ' + QUOTENAME(s.name) + N'.' + QUOTENAME(t.name) + N';'
            FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = @schema;
            EXEC (@sql);
            DECLARE @drop nvarchar(300) = N'DROP SCHEMA ' + QUOTENAME(@schema);
            IF SCHEMA_ID(@schema) IS NOT NULL EXEC (@drop);
            """, new { schema = Schema });
    }
}
