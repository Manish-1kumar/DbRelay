using DbRelay.IntegrationTests;
using Npgsql;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(PostgreSqlFixture))]

// Several tests are timing-sensitive (leases, retry gaps, process start-up). The four target frameworks already run in parallel
// against one database, so tests inside one assembly run one at a time to keep the measurements meaningful.
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]

namespace DbRelay.IntegrationTests;

/// <summary>
/// Supplies the test database: <c>DBRELAY_TEST_POSTGRES</c> if set (failures are then real failures), otherwise a
/// Testcontainers <c>postgres:17-alpine</c> container. If neither is available, <see cref="SkipReason"/> is set and every database
/// test is reported as skipped. Copied from DbRelay.PostgreSql.Tests (not referenced, by design).
/// </summary>
public sealed class PostgreSqlFixture : IAsyncLifetime
{
    public const string EnvironmentVariable = "DBRELAY_TEST_POSTGRES";

    private PostgreSqlContainer? _container;
    private NpgsqlDataSource? _dataSource;

    public string? SkipReason { get; private set; }

    public string ConnectionString { get; private set; } = string.Empty;

    /// <summary>A shared, fixture-owned data source for business writes and assertions (never targeted by connection kills).</summary>
    public NpgsqlDataSource DataSource => _dataSource ?? throw new InvalidOperationException(SkipReason ?? "The fixture is not initialized.");

    public async ValueTask InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            // An explicitly configured database must work: connection failures fail the tests instead of skipping them.
            ConnectionString = configured;
        }
        else
        {
            try
            {
                _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
                await _container.StartAsync();
                ConnectionString = _container.GetConnectionString();
            }
            catch (Exception ex)
            {
                SkipReason = $"No PostgreSQL available: set {EnvironmentVariable} or start Docker for Testcontainers ({ex.GetType().Name}: {ex.Message}).";
                if (_container is not null)
                {
                    await _container.DisposeAsync();
                    _container = null;
                }

                return;
            }
        }

        var builder = new NpgsqlConnectionStringBuilder(ConnectionString) { ApplicationName = "dbrelay-it-fixture" };
        _dataSource = NpgsqlDataSource.Create(builder.ConnectionString);
        await using var connection = await _dataSource.OpenConnectionAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
        }

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>A connection string with a distinct application name (to target <c>pg_terminate_backend</c>) and pool size.</summary>
    public string ConnectionStringFor(string applicationName, int maxPoolSize = 20)
    {
        var builder = new NpgsqlConnectionStringBuilder(ConnectionString)
        {
            ApplicationName = applicationName,
            MaxPoolSize = maxPoolSize,
        };
        return builder.ConnectionString;
    }
}
