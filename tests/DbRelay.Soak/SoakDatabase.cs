using System.Data.Common;
using Dapper;
using DbRelay;
using Microsoft.Data.SqlClient;
using Npgsql;
using Oracle.ManagedDataAccess.Client;

namespace DbRelay.Soak;

public enum Provider
{
    PostgreSql,
    SqlServer,
    Oracle,
}

[OutboxMessageType("soak.order")]
public sealed record SoakMessage(int Sequence, bool Poison, string Filler);

/// <summary>Provider-specific SQL for the soak harness (the library itself is exercised only through its public API).</summary>
/// <remarks>
/// Oracle: the run's schema is a schema-only user. Workers connect as a second, throwaway user with only the table rights they need,
/// so the chaos loop can find and kill exactly their sessions (ODP.NET has no application name setting).
/// </remarks>
public sealed class SoakDatabase(Provider provider, string schema)
{
    private readonly string _workerPassword = "Soak" + Guid.NewGuid().ToString("N")[..20];

    public Provider Provider { get; } = provider;

    public string Schema { get; } = schema;

    private string OracleSchema => Schema.ToUpperInvariant();

    private string OracleWorkerUser => OracleSchema + "_W";

    public string BaseConnectionString => Provider switch
    {
        Provider.PostgreSql => Environment.GetEnvironmentVariable("DBRELAY_TEST_POSTGRES") ?? throw new InvalidOperationException("Set DBRELAY_TEST_POSTGRES."),
        Provider.SqlServer => Environment.GetEnvironmentVariable("DBRELAY_TEST_SQLSERVER") ?? throw new InvalidOperationException("Set DBRELAY_TEST_SQLSERVER."),
        _ => Environment.GetEnvironmentVariable("DBRELAY_TEST_ORACLE") ?? throw new InvalidOperationException("Set DBRELAY_TEST_ORACLE."),
    };

    public string ConnectionString(string applicationName) => Provider switch
    {
        Provider.PostgreSql => new NpgsqlConnectionStringBuilder(BaseConnectionString) { ApplicationName = applicationName }.ConnectionString,
        Provider.SqlServer => new SqlConnectionStringBuilder(BaseConnectionString) { ApplicationName = applicationName }.ConnectionString,
        _ => applicationName.StartsWith("soak-worker", StringComparison.Ordinal)
            ? new OracleConnectionStringBuilder(BaseConnectionString) { UserID = OracleWorkerUser, Password = WorkerPassword }.ConnectionString
            : BaseConnectionString,
    };

    /// <summary>The Oracle worker user's password, passed to worker processes by the coordinator (a per-run throwaway value).</summary>
    public string WorkerPassword
    {
        get => Environment.GetEnvironmentVariable("DBRELAY_SOAK_WORKER_PASSWORD") ?? _workerPassword;
    }

    public string T(string table) => Provider switch
    {
        Provider.PostgreSql => $"\"{Schema}\".{table}",
        Provider.SqlServer => $"[{Schema}].[{table}]",
        _ => $"\"{OracleSchema}\".\"{table.ToUpperInvariant()}\"",
    };

    /// <summary>A parameter placeholder: <c>@name</c>, or <c>:name</c> on Oracle.</summary>
    public string P(string name) => Provider == Provider.Oracle ? ":" + name : "@" + name;

    /// <summary>A boolean parameter value (Oracle stores booleans as NUMBER(1)).</summary>
    public object Bool(bool value) => Provider == Provider.Oracle ? (value ? 1 : 0) : value;

    /// <summary>A GUID parameter value (Oracle stores GUIDs as RAW(16)).</summary>
    public object Id(Guid value) => Provider == Provider.Oracle ? value.ToByteArray() : value;

    public async Task<HashSet<Guid>> QueryIdsAsync(DbConnection connection, string sql)
    {
        if (Provider != Provider.Oracle)
        {
            return (await connection.QueryAsync<Guid>(sql, commandTimeout: 600)).ToHashSet();
        }

        return (await connection.QueryAsync<byte[]>(sql, commandTimeout: 600)).Select(bytes => new Guid(bytes)).ToHashSet();
    }

    public void UseStorage(DbRelayBuilder relay, string applicationName)
    {
        switch (Provider)
        {
            case Provider.PostgreSql:
                relay.UsePostgreSql(ConnectionString(applicationName), o => o.Schema = Schema);
                break;
            case Provider.SqlServer:
                relay.UseSqlServer(ConnectionString(applicationName), o => o.Schema = Schema);
                break;
            default:
                relay.UseOracle(ConnectionString(applicationName), o => o.Schema = Schema);
                break;
        }
    }

    public async Task<DbConnection> OpenAsync(string applicationName, CancellationToken ct = default)
    {
        DbConnection connection = Provider switch
        {
            Provider.PostgreSql => new NpgsqlConnection(ConnectionString(applicationName)),
            Provider.SqlServer => new SqlConnection(ConnectionString(applicationName)),
            _ => new OracleConnection(ConnectionString(applicationName)),
        };
        await connection.OpenAsync(ct);
        return connection;
    }

    /// <summary>Runs before the outbox schema is created. Oracle: creates the schema user and the worker user.</summary>
    public async Task PrepareAsync()
    {
        if (Provider != Provider.Oracle)
        {
            return;
        }

        await using var connection = await OpenAsync("soak-setup");
        await connection.ExecuteAsync($"CREATE USER \"{OracleSchema}\" NO AUTHENTICATION DEFAULT TABLESPACE USERS QUOTA UNLIMITED ON USERS");
        await connection.ExecuteAsync($"CREATE USER \"{OracleWorkerUser}\" IDENTIFIED BY \"{WorkerPassword}\"");
        await connection.ExecuteAsync($"GRANT CREATE SESSION TO \"{OracleWorkerUser}\"");
    }

    /// <summary>Runs after the tables exist. Oracle: grants the worker user what a dispatcher and the sink need.</summary>
    public async Task GrantWorkersAsync()
    {
        if (Provider != Provider.Oracle)
        {
            return;
        }

        await using var connection = await OpenAsync("soak-setup");
        await connection.ExecuteAsync($"GRANT SELECT, INSERT, UPDATE, DELETE ON {T("outbox_messages")} TO \"{OracleWorkerUser}\"");
        await connection.ExecuteAsync($"GRANT INSERT ON {T("sink")} TO \"{OracleWorkerUser}\"");
    }

    public string CreateTablesSql => Provider switch
    {
        Provider.PostgreSql =>
            $"""
            CREATE TABLE {T("orders")} (message_id uuid PRIMARY KEY, poison boolean NOT NULL, created_at timestamptz NOT NULL DEFAULT now());
            CREATE TABLE {T("sink")} (message_id uuid NOT NULL, attempt int NOT NULL, worker text NOT NULL, received_at timestamptz NOT NULL DEFAULT now());
            CREATE INDEX ON {T("sink")} (message_id);
            """,
        Provider.SqlServer =>
            $"""
            CREATE TABLE {T("orders")} ([message_id] uniqueidentifier NOT NULL PRIMARY KEY NONCLUSTERED, [poison] bit NOT NULL, [created_at] datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME());
            CREATE TABLE {T("sink")} ([message_id] uniqueidentifier NOT NULL, [attempt] int NOT NULL, [worker] nvarchar(64) NOT NULL, [received_at] datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME());
            CREATE INDEX ix_sink_message ON {T("sink")} ([message_id]);
            """,
        _ =>
            $"""
            BEGIN
                EXECUTE IMMEDIATE 'CREATE TABLE {T("orders")} (MESSAGE_ID RAW(16) PRIMARY KEY, POISON NUMBER(1) NOT NULL, CREATED_AT TIMESTAMP(6) DEFAULT SYS_EXTRACT_UTC(SYSTIMESTAMP) NOT NULL)';
                EXECUTE IMMEDIATE 'CREATE TABLE {T("sink")} (MESSAGE_ID RAW(16) NOT NULL, ATTEMPT NUMBER(10) NOT NULL, WORKER VARCHAR2(64) NOT NULL, RECEIVED_AT TIMESTAMP(6) DEFAULT SYS_EXTRACT_UTC(SYSTIMESTAMP) NOT NULL)';
                EXECUTE IMMEDIATE 'CREATE INDEX "{OracleSchema}"."IX_SINK_MESSAGE" ON {T("sink")} (MESSAGE_ID)';
            END;
            """,
    };

    public string InsertOrderSql => $"INSERT INTO {T("orders")} (message_id, poison) VALUES ({P("Id")}, {P("Poison")})";

    public string InsertSinkSql => $"INSERT INTO {T("sink")} (message_id, attempt, worker) VALUES ({P("Id")}, {P("Attempt")}, {P("Worker")})";

    /// <summary>Milliseconds from creation to delivery, one row per delivered message.</summary>
    public string LatencySql(string outbox) => Provider switch
    {
        Provider.PostgreSql => $"SELECT EXTRACT(EPOCH FROM delivered_at - created_at) * 1000.0 FROM {outbox} WHERE status = 2",
        Provider.SqlServer => $"SELECT CAST(DATEDIFF_BIG(millisecond, created_at, delivered_at) AS float) FROM {outbox} WHERE status = 2",
        _ => $"""
              SELECT CAST((EXTRACT(DAY FROM delivered_at - created_at) * 86400 + EXTRACT(HOUR FROM delivered_at - created_at) * 3600
                     + EXTRACT(MINUTE FROM delivered_at - created_at) * 60 + EXTRACT(SECOND FROM delivered_at - created_at)) * 1000 AS BINARY_DOUBLE)
              FROM {outbox} WHERE status = 2
              """,
    };

    /// <summary>Kills every database session opened by soak workers (chaos). Returns the number killed.</summary>
    public async Task<int> KillWorkerSessionsAsync()
    {
        await using var connection = await OpenAsync("soak-chaos");
        switch (Provider)
        {
            case Provider.PostgreSql:
                return await connection.ExecuteScalarAsync<int>(
                    "SELECT count(*) FROM (SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE application_name LIKE 'soak-worker%') AS k");
            case Provider.SqlServer:
                return await connection.ExecuteScalarAsync<int>("""
                    DECLARE @n int = 0, @sql nvarchar(max) = N'';
                    SELECT @sql += N'KILL ' + CAST(session_id AS nvarchar(10)) + N';', @n += 1
                    FROM sys.dm_exec_sessions WHERE program_name LIKE 'soak-worker%' AND session_id <> @@SPID;
                    EXEC (@sql);
                    SELECT @n;
                    """);
            default:
                return await KillOracleWorkerSessionsAsync(connection);
        }
    }

    private async Task<int> KillOracleWorkerSessionsAsync(DbConnection connection)
    {
        var sessions = (await connection.QueryAsync<(decimal Sid, decimal Serial)>(
            "SELECT SID, SERIAL# FROM V$SESSION WHERE USERNAME = :WorkerUser", new { WorkerUser = OracleWorkerUser })).ToList();
        var killed = 0;
        foreach (var (sid, serial) in sessions)
        {
            try
            {
                await connection.ExecuteAsync($"ALTER SYSTEM KILL SESSION '{(long)sid},{(long)serial}' IMMEDIATE");
                killed++;
            }
            catch (OracleException)
            {
                // The session ended on its own in between (ORA-00030).
            }
        }

        return killed;
    }

    public async Task DropAsync()
    {
        await using var connection = await OpenAsync("soak-cleanup");
        switch (Provider)
        {
            case Provider.PostgreSql:
                await connection.ExecuteAsync($"DROP SCHEMA IF EXISTS \"{Schema}\" CASCADE");
                return;
            case Provider.SqlServer:
                await connection.ExecuteAsync("""
                    DECLARE @sql nvarchar(max) = N'';
                    SELECT @sql += N'DROP TABLE ' + QUOTENAME(s.name) + N'.' + QUOTENAME(t.name) + N';'
                    FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = @schema;
                    EXEC (@sql);
                    DECLARE @drop nvarchar(300) = N'DROP SCHEMA ' + QUOTENAME(@schema);
                    IF SCHEMA_ID(@schema) IS NOT NULL EXEC (@drop);
                    """, new { schema = Schema });
                return;
            default:
                await KillOracleWorkerSessionsAsync(connection);
                await connection.ExecuteAsync($"DROP USER \"{OracleWorkerUser}\" CASCADE");
                await connection.ExecuteAsync($"DROP USER \"{OracleSchema}\" CASCADE");
                return;
        }
    }
}
