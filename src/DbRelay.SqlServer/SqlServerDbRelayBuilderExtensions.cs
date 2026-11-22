using DbRelay.SqlServer;
using DbRelay.SqlServer.Internal;
using DbRelay.Storage;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace DbRelay;

/// <summary>
/// Registers the SQL Server storage provider with DbRelay.
/// </summary>
public static class SqlServerDbRelayBuilderExtensions
{
    private const string ReservedTableName = "schema_version";
    private static readonly TimeSpan MaxCommandTimeout = TimeSpan.FromHours(1);

    /// <summary>
    /// Stores outbox messages in SQL Server, using <paramref name="connectionString"/> for the connections DbRelay owns
    /// (dispatching, standalone enqueue, administration and schema creation). Transactional enqueue always uses the caller's
    /// <see cref="SqlConnection"/> and <see cref="SqlTransaction"/>.
    /// </summary>
    /// <param name="relay">The DbRelay builder.</param>
    /// <param name="connectionString">The SQL Server connection string.</param>
    /// <param name="configure">Optionally configures the schema, table name and command timeout.</param>
    /// <returns>The builder.</returns>
    /// <exception cref="ArgumentException">The connection string is empty, or the schema or table name is not a safe identifier.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The command timeout is out of range.</exception>
    public static DbRelayBuilder UseSqlServer(this DbRelayBuilder relay, string connectionString, Action<SqlServerOutboxOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(relay);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return Register(relay, _ => () => new SqlConnection(connectionString), configure);
    }

    /// <summary>
    /// Stores outbox messages in SQL Server, using <paramref name="connectionFactory"/> to create the connections DbRelay owns
    /// (dispatching, standalone enqueue, administration and schema creation). Transactional enqueue always uses the caller's
    /// <see cref="SqlConnection"/> and <see cref="SqlTransaction"/>.
    /// </summary>
    /// <param name="relay">The DbRelay builder.</param>
    /// <param name="connectionFactory">
    /// Returns a new, unopened <see cref="SqlConnection"/> each time it is called. DbRelay opens and disposes it.
    /// </param>
    /// <param name="configure">Optionally configures the schema, table name and command timeout.</param>
    /// <returns>The builder.</returns>
    /// <exception cref="ArgumentException">The schema or table name is not a safe identifier.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The command timeout is out of range.</exception>
    public static DbRelayBuilder UseSqlServer(
        this DbRelayBuilder relay,
        Func<IServiceProvider, SqlConnection> connectionFactory,
        Action<SqlServerOutboxOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(relay);
        ArgumentNullException.ThrowIfNull(connectionFactory);
        return Register(relay, sp => () => connectionFactory(sp), configure);
    }

    private static DbRelayBuilder Register(
        DbRelayBuilder relay,
        Func<IServiceProvider, Func<SqlConnection>> factoryProvider,
        Action<SqlServerOutboxOptions>? configure)
    {
        var options = new SqlServerOutboxOptions();
        configure?.Invoke(options);

        var schema = SqlIdentifier.Validate(options.Schema, "SqlServerOutboxOptions.Schema");
        var table = SqlIdentifier.Validate(options.TableName, "SqlServerOutboxOptions.TableName");
        if (string.Equals(table, ReservedTableName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"SqlServerOutboxOptions.TableName '{ReservedTableName}' is reserved for DbRelay's schema version table.", nameof(configure));
        }

        if (options.CommandTimeout < TimeSpan.FromSeconds(1) || options.CommandTimeout > MaxCommandTimeout)
        {
            throw new ArgumentOutOfRangeException(
                nameof(configure), options.CommandTimeout, "SqlServerOutboxOptions.CommandTimeout must be between 1 second and 1 hour.");
        }

        var sql = new SqlServerOutboxSql(schema, table);
        var commandTimeoutSeconds = (int)Math.Ceiling(options.CommandTimeout.TotalSeconds);

        relay.UseStorage(sp => new SqlServerOutboxStore(new SqlServerConnectionSource(factoryProvider(sp), commandTimeoutSeconds), sql));
        relay.Services.AddSingleton<IOutboxSchemaManager>(sp =>
            new SqlServerOutboxSchemaManager(new SqlServerConnectionSource(factoryProvider(sp), commandTimeoutSeconds), sql));
        return relay;
    }
}
