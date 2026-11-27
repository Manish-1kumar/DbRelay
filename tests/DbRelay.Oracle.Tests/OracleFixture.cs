using DbRelay.Oracle.Tests;
using Oracle.ManagedDataAccess.Client;

[assembly: AssemblyFixture(typeof(OracleFixture))]

namespace DbRelay.Oracle.Tests;

/// <summary>
/// Supplies the test database from <c>DBRELAY_TEST_ORACLE</c>: an ODP.NET connection string for a user that may create and drop
/// users and create objects in their schemas (in CI, SYSTEM on the container's pluggable database). Each test gets its own schema,
/// and in Oracle a schema is a user. If the variable is not set, every database test is reported as skipped. A configured database
/// that cannot be reached fails the tests instead.
/// </summary>
public sealed class OracleFixture : IAsyncLifetime
{
    public const string EnvironmentVariable = "DBRELAY_TEST_ORACLE";

    public string? SkipReason { get; private set; }

    public string ConnectionString { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            SkipReason = $"No Oracle database available: set {EnvironmentVariable} (see CONTRIBUTING.md).";
            return;
        }

        ConnectionString = configured;
        await using var connection = await OpenAsync();
        await DbRelay.Testing.TestSchemaNames.SweepOracleAsync(connection); // K13: users left by killed test processes
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public async Task<OracleConnection> OpenAsync()
    {
        var connection = new OracleConnection(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }
}
