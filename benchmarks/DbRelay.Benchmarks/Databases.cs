using System.Data.Common;
using Dapper;
using DbRelay;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DbRelay.Benchmarks;

public enum Provider
{
    PostgreSql,
    SqlServer,
}

public sealed record BenchMessage(Guid OrderId, string Customer, decimal Total);

/// <summary>Connection strings come from DBRELAY_TEST_POSTGRES / DBRELAY_TEST_SQLSERVER (same as the test suites).</summary>
public static class Databases
{
    public static string ConnectionString(Provider provider) => provider switch
    {
        Provider.PostgreSql => Environment.GetEnvironmentVariable("DBRELAY_TEST_POSTGRES")
            ?? throw new InvalidOperationException("Set DBRELAY_TEST_POSTGRES."),
        _ => Environment.GetEnvironmentVariable("DBRELAY_TEST_SQLSERVER")
            ?? throw new InvalidOperationException("Set DBRELAY_TEST_SQLSERVER."),
    };

    public static void UseProvider(this DbRelayBuilder relay, Provider provider, string schema)
    {
        if (provider == Provider.PostgreSql)
        {
            relay.UsePostgreSql(ConnectionString(provider), o => o.Schema = schema);
        }
        else
        {
            relay.UseSqlServer(ConnectionString(provider), o => o.Schema = schema);
        }
    }

    public static async Task<DbConnection> OpenAsync(Provider provider)
    {
        DbConnection connection = provider == Provider.PostgreSql
            ? new NpgsqlConnection(ConnectionString(provider))
            : new SqlConnection(ConnectionString(provider));
        await connection.OpenAsync();
        return connection;
    }

    public static string OrdersDdl(Provider provider, string schema) => provider == Provider.PostgreSql
        ? $"CREATE TABLE {schema}.orders (id uuid PRIMARY KEY, customer text NOT NULL, total numeric(12,2) NOT NULL)"
        : $"CREATE TABLE [{schema}].[orders] (id uniqueidentifier PRIMARY KEY NONCLUSTERED, customer nvarchar(100) NOT NULL, total decimal(12,2) NOT NULL)";

    public static string InsertOrderSql(string schema) => $"INSERT INTO {schema}.orders (id, customer, total) VALUES (@OrderId, @Customer, @Total)";

    public static async Task<ServiceProvider> CreateSchemaAsync(Provider provider, string schema, Action<DbRelayBuilder>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbRelay(relay =>
        {
            relay.UseProvider(provider, schema);
            relay.DisableDispatcher();
            configure?.Invoke(relay);
        });
        var sp = services.BuildServiceProvider();
        await sp.GetRequiredService<IOutboxSchemaManager>().EnsureCreatedAsync();
        await using var connection = await OpenAsync(provider);
        await connection.ExecuteAsync(OrdersDdl(provider, schema));
        return sp;
    }

    public static async Task DropSchemaAsync(Provider provider, string schema)
    {
        await using var connection = await OpenAsync(provider);
        if (provider == Provider.PostgreSql)
        {
            await connection.ExecuteAsync($"DROP SCHEMA IF EXISTS {schema} CASCADE");
        }
        else
        {
            await connection.ExecuteAsync($"""
                DECLARE @sql nvarchar(max) = N'';
                SELECT @sql += N'DROP TABLE ' + QUOTENAME(s.name) + N'.' + QUOTENAME(t.name) + N';'
                FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = @schema;
                EXEC (@sql);
                DECLARE @drop nvarchar(300) = N'DROP SCHEMA ' + QUOTENAME(@schema);
                IF SCHEMA_ID(@schema) IS NOT NULL EXEC (@drop);
                """, new { schema });
        }
    }

    public static string NewSchema(string prefix) => $"{prefix}_{Guid.NewGuid():N}"[..Math.Min(40, prefix.Length + 17)];
}
