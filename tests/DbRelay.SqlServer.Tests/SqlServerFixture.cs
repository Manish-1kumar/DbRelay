using Dapper;
using DbRelay.SqlServer.Tests;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

[assembly: AssemblyFixture(typeof(SqlServerFixture))]

namespace DbRelay.SqlServer.Tests;

/// <summary>
/// Provides two SQL Server test databases: one with READ_COMMITTED_SNAPSHOT OFF (the SQL Server default) and one with it ON
/// (the Azure SQL default). Uses <c>DBRELAY_TEST_SQLSERVER</c> when set, otherwise a Testcontainers SQL Server 2022 container.
/// When neither is available, every database test is reported as skipped.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    public const string EnvironmentVariable = "DBRELAY_TEST_SQLSERVER";
    private const string DefaultDatabase = "dbrelay_tests";

    private MsSqlContainer? _container;

    /// <summary>Connection string of the database with READ_COMMITTED_SNAPSHOT OFF.</summary>
    public string? ConnectionString { get; private set; }

    /// <summary>Connection string of the database with READ_COMMITTED_SNAPSHOT ON.</summary>
    public string? RcsiConnectionString { get; private set; }

    /// <summary>Non-null when the RCSI-off database is unavailable.</summary>
    public string? SkipReason { get; private set; }

    /// <summary>Non-null when the RCSI-on database is unavailable.</summary>
    public string? RcsiSkipReason { get; private set; }

    public string Source { get; private set; } = "none";

    public string? GetConnectionString(bool rcsi) => rcsi ? RcsiConnectionString : ConnectionString;

    public string? GetSkipReason(bool rcsi) => rcsi ? RcsiSkipReason : SkipReason;

    public async ValueTask InitializeAsync()
    {
        string baseConnectionString;
        var fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            baseConnectionString = fromEnvironment;
            Source = EnvironmentVariable;
        }
        else
        {
            try
            {
                _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
                await _container.StartAsync();
                baseConnectionString = _container.GetConnectionString();
                Source = "Testcontainers";
            }
            catch (Exception ex)
            {
                SkipReason = RcsiSkipReason =
                    $"SQL Server is not available: {EnvironmentVariable} is not set and Testcontainers could not start a container ({ex.GetType().Name}: {ex.Message}).";
                return;
            }
        }

        var builder = new SqlConnectionStringBuilder(baseConnectionString);
        var named = !string.IsNullOrEmpty(builder.InitialCatalog) && !builder.InitialCatalog.Equals("master", StringComparison.OrdinalIgnoreCase);
        var database = named ? builder.InitialCatalog : DefaultDatabase;
        var rcsiDatabase = database + "_rcsi";

        bool azure;
        try
        {
            azure = await IsAzureSqlDatabaseAsync(builder);
            if (azure)
            {
                // Never create or alter databases on Azure SQL Database: a database created with CREATE DATABASE is not
                // covered by the free offer and is billed. The tests use only databases the developer created (CONTRIBUTING).
                Source += " (Azure SQL Database)";
                if (!named)
                {
                    SkipReason = RcsiSkipReason =
                        $"Azure SQL Database: name the RCSI-off test database in {EnvironmentVariable} (Database=...). " +
                        "The tests never create databases on Azure SQL.";
                    return;
                }
            }
            else
            {
                await PrepareDatabasesAsync(builder, database, rcsiDatabase);
            }
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            SkipReason = RcsiSkipReason = $"SQL Server ({Source}) is not usable: {ex.GetType().Name}: {ex.Message}";
            return;
        }

        builder.InitialCatalog = database;
        ConnectionString = builder.ConnectionString;
        builder.InitialCatalog = rcsiDatabase;
        RcsiConnectionString = builder.ConnectionString;

        SkipReason = await CheckDatabaseAsync(ConnectionString, database, wantRcsi: false, azure);
        RcsiSkipReason = await CheckDatabaseAsync(RcsiConnectionString, rcsiDatabase, wantRcsi: true, azure);

        // K13: remove schemas left behind by killed test processes.
        foreach (var (connectionString, skip) in new[] { (ConnectionString, SkipReason), (RcsiConnectionString, RcsiSkipReason) })
        {
            if (skip is not null)
            {
                continue;
            }

            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await DbRelay.Testing.TestSchemaNames.SweepSqlServerAsync(connection);
        }
    }

    /// <summary>
    /// Returns null when <paramref name="databaseName"/> is reachable and has the wanted READ_COMMITTED_SNAPSHOT setting,
    /// otherwise the reason its tests are skipped.
    /// </summary>
    private static async Task<string?> CheckDatabaseAsync(string connectionString, string databaseName, bool wantRcsi, bool azure)
    {
        bool rcsi;
        try
        {
            rcsi = await IsRcsiOnAsync(connectionString);
        }
        catch (SqlException ex) when (azure)
        {
            return $"Azure SQL Database '{databaseName}' is not reachable ({ex.Number}: {ex.Message}). Create it with the free " +
                "offer (see CONTRIBUTING); the tests never create databases on Azure SQL.";
        }

        if (rcsi == wantRcsi)
        {
            return null;
        }

        var wanted = wantRcsi ? "ON" : "OFF";
        return azure
            ? $"Azure SQL Database '{databaseName}' needs READ_COMMITTED_SNAPSHOT {wanted}. Run once, connected to master, with " +
              $"no other connections to it: ALTER DATABASE [{databaseName}] SET READ_COMMITTED_SNAPSHOT {wanted};"
            : $"Database '{databaseName}' has READ_COMMITTED_SNAPSHOT {(rcsi ? "ON" : "OFF")}; these tests need it {wanted}.";
    }

    /// <summary>
    /// True for Azure SQL Database (engine edition 5). Connects to the named database, or to master when it is not reachable
    /// (on a local server the test database may not exist yet).
    /// </summary>
    private static async Task<bool> IsAzureSqlDatabaseAsync(SqlConnectionStringBuilder builder)
    {
        const string Sql = "SELECT CAST(SERVERPROPERTY('EngineEdition') AS int);";
        try
        {
            await using var connection = new SqlConnection(builder.ConnectionString);
            await connection.OpenAsync();
            return await connection.ExecuteScalarAsync<int>(Sql) == 5;
        }
        catch (SqlException)
        {
            var master = new SqlConnectionStringBuilder(builder.ConnectionString) { InitialCatalog = "master" };
            await using var connection = new SqlConnection(master.ConnectionString);
            await connection.OpenAsync();
            return await connection.ExecuteScalarAsync<int>(Sql) == 5;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    private static async Task PrepareDatabasesAsync(SqlConnectionStringBuilder builder, string database, string rcsiDatabase)
    {
        var master = new SqlConnectionStringBuilder(builder.ConnectionString) { InitialCatalog = "master" };
        await using var connection = new SqlConnection(master.ConnectionString);
        await connection.OpenAsync();

        // Test runs for several target frameworks start in parallel; serialize database setup across processes.
        await connection.ExecuteAsync(
            "EXEC sp_getapplock @Resource = N'dbrelay-sqlserver-tests-setup', @LockMode = N'Exclusive', @LockOwner = N'Session', @LockTimeout = 120000;");
        try
        {
            await connection.ExecuteAsync(
                """
                IF DB_ID(@database) IS NULL
                BEGIN
                    DECLARE @create nvarchar(max) = N'CREATE DATABASE ' + QUOTENAME(@database);
                    EXEC (@create);
                END;
                IF DB_ID(@rcsi) IS NULL
                BEGIN
                    DECLARE @createRcsi nvarchar(max) = N'CREATE DATABASE ' + QUOTENAME(@rcsi);
                    EXEC (@createRcsi);
                END;
                -- SQL Server Express creates databases with AUTO_CLOSE ON, which flushes cached plans whenever the last connection closes.
                IF (SELECT is_auto_close_on FROM sys.databases WHERE name = @database) = 1
                BEGIN
                    DECLARE @noCloseDb nvarchar(max) = N'ALTER DATABASE ' + QUOTENAME(@database) + N' SET AUTO_CLOSE OFF';
                    EXEC (@noCloseDb);
                END;
                IF (SELECT is_auto_close_on FROM sys.databases WHERE name = @rcsi) = 1
                BEGIN
                    DECLARE @noCloseRcsi nvarchar(max) = N'ALTER DATABASE ' + QUOTENAME(@rcsi) + N' SET AUTO_CLOSE OFF';
                    EXEC (@noCloseRcsi);
                END;
                IF (SELECT is_read_committed_snapshot_on FROM sys.databases WHERE name = @rcsi) = 0
                BEGIN
                    DECLARE @alter nvarchar(max) = N'ALTER DATABASE ' + QUOTENAME(@rcsi) + N' SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE';
                    EXEC (@alter);
                END;
                """,
                new { database, rcsi = rcsiDatabase },
                commandTimeout: 120);
        }
        finally
        {
            await connection.ExecuteAsync("EXEC sp_releaseapplock @Resource = N'dbrelay-sqlserver-tests-setup', @LockOwner = N'Session';");
        }
    }

    private static async Task<bool> IsRcsiOnAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        return await connection.ExecuteScalarAsync<bool>("SELECT is_read_committed_snapshot_on FROM sys.databases WHERE database_id = DB_ID();");
    }
}
