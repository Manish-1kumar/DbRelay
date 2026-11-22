using DbRelay.PostgreSql.Tests;
using Npgsql;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(PostgreSqlFixture))]

namespace DbRelay.PostgreSql.Tests;

/// <summary>
/// Supplies the test database: <c>DBRELAY_TEST_POSTGRES</c> if set (failures are then real failures), otherwise a
/// Testcontainers <c>postgres:17-alpine</c> container. If neither is available, <see cref="SkipReason"/> is set and every database
/// test is reported as skipped.
/// </summary>
public sealed class PostgreSqlFixture : IAsyncLifetime
{
    public const string EnvironmentVariable = "DBRELAY_TEST_POSTGRES";

    private PostgreSqlContainer? _container;
    private NpgsqlDataSource? _dataSource;

    public string? SkipReason { get; private set; }

    public string ConnectionString { get; private set; } = string.Empty;

    /// <summary>A shared, fixture-owned data source for test helpers.</summary>
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

        _dataSource = NpgsqlDataSource.Create(ConnectionString);
        await using var connection = await _dataSource.OpenConnectionAsync();
        await DbRelay.Testing.TestSchemaNames.SweepPostgreSqlAsync(connection); // K13: schemas left by killed test processes
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
}
