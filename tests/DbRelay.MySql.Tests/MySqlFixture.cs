using DbRelay.MySql.Tests;
using MySqlConnector;

[assembly: AssemblyFixture(typeof(MySqlFixture))]

namespace DbRelay.MySql.Tests;

/// <summary>
/// Supplies the test server from <c>DBRELAY_TEST_MYSQL</c>: a MySqlConnector connection string for a user that may create and drop
/// databases (in CI, root on the container). Each test gets its own schema, which in MySQL is a database, so the connection string
/// names no database. If the variable is not set, every database test is reported as skipped. A configured server that cannot be
/// reached fails the tests instead.
/// </summary>
public sealed class MySqlFixture : IAsyncLifetime
{
    public const string EnvironmentVariable = "DBRELAY_TEST_MYSQL";

    public string? SkipReason { get; private set; }

    public string ConnectionString { get; private set; } = string.Empty;

    /// <summary>"MySQL 8.4.3" or "MariaDB 11.4.4", for test output.</summary>
    public string ServerVersion { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            SkipReason = $"No MySQL or MariaDB server available: set {EnvironmentVariable} (see CONTRIBUTING.md).";
            return;
        }

        ConnectionString = configured;
        await using var connection = await OpenAsync();
        ServerVersion = connection.ServerVersion;
        await DbRelay.Testing.TestSchemaNames.SweepMySqlAsync(connection); // K13: databases left by killed test processes
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public async Task<MySqlConnection> OpenAsync()
    {
        var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }
}
