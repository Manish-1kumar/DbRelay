using DbRelay.Oracle;
using DbRelay.Oracle.Internal;
using DbRelay.Storage;
using Microsoft.Extensions.DependencyInjection;
using Oracle.ManagedDataAccess.Client;

namespace DbRelay;

/// <summary>
/// Registers the Oracle storage provider with DbRelay.
/// </summary>
public static class OracleDbRelayBuilderExtensions
{
    private static readonly TimeSpan MaxCommandTimeout = TimeSpan.FromHours(1);

    /// <summary>
    /// Stores outbox messages in Oracle, using <paramref name="connectionString"/> for the connections DbRelay owns (dispatching,
    /// standalone enqueue, administration and schema creation). Transactional enqueue always uses the caller's
    /// <see cref="OracleConnection"/> and <see cref="OracleTransaction"/>.
    /// </summary>
    /// <param name="relay">The DbRelay builder.</param>
    /// <param name="connectionString">The ODP.NET connection string.</param>
    /// <param name="configure">Optionally configures the schema, table name and command timeout.</param>
    /// <returns>The builder.</returns>
    /// <exception cref="ArgumentException">The connection string is empty, or the schema or table name is not a safe identifier.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The command timeout is out of range.</exception>
    public static DbRelayBuilder UseOracle(this DbRelayBuilder relay, string connectionString, Action<OracleOutboxOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(relay);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return Register(relay, _ => () => new OracleConnection(connectionString), configure);
    }

    /// <summary>
    /// Stores outbox messages in Oracle, using <paramref name="connectionFactory"/> to create the connections DbRelay owns
    /// (dispatching, standalone enqueue, administration and schema creation). Transactional enqueue always uses the caller's
    /// <see cref="OracleConnection"/> and <see cref="OracleTransaction"/>.
    /// </summary>
    /// <param name="relay">The DbRelay builder.</param>
    /// <param name="connectionFactory">
    /// Returns a new, unopened <see cref="OracleConnection"/> each time it is called. DbRelay opens and disposes it.
    /// </param>
    /// <param name="configure">Optionally configures the schema, table name and command timeout.</param>
    /// <returns>The builder.</returns>
    /// <exception cref="ArgumentException">The schema or table name is not a safe identifier.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The command timeout is out of range.</exception>
    public static DbRelayBuilder UseOracle(
        this DbRelayBuilder relay,
        Func<IServiceProvider, OracleConnection> connectionFactory,
        Action<OracleOutboxOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(relay);
        ArgumentNullException.ThrowIfNull(connectionFactory);
        return Register(relay, sp => () => connectionFactory(sp), configure);
    }

    private static DbRelayBuilder Register(
        DbRelayBuilder relay,
        Func<IServiceProvider, Func<OracleConnection>> factoryProvider,
        Action<OracleOutboxOptions>? configure)
    {
        var options = new OracleOutboxOptions();
        configure?.Invoke(options);

        var schema = options.Schema is null ? null : SqlIdentifier.Validate(options.Schema, "OracleOutboxOptions.Schema");
        var table = SqlIdentifier.Validate(options.TableName, "OracleOutboxOptions.TableName");
        if (string.Equals(table, OracleOutboxSql.VersionTableName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"OracleOutboxOptions.TableName '{OracleOutboxSql.VersionTableName}' is reserved for DbRelay's schema version table.", nameof(configure));
        }

        if (options.CommandTimeout < TimeSpan.FromSeconds(1) || options.CommandTimeout > MaxCommandTimeout)
        {
            throw new ArgumentOutOfRangeException(
                nameof(configure), options.CommandTimeout, "OracleOutboxOptions.CommandTimeout must be between 1 second and 1 hour.");
        }

        var sql = new OracleOutboxSql(schema, table);
        var commandTimeoutSeconds = (int)Math.Ceiling(options.CommandTimeout.TotalSeconds);

        relay.UseStorage(sp => new OracleOutboxStore(new OracleConnectionSource(factoryProvider(sp), commandTimeoutSeconds), sql));
        relay.Services.AddSingleton<IOutboxSchemaManager>(sp =>
            new OracleOutboxSchemaManager(new OracleConnectionSource(factoryProvider(sp), commandTimeoutSeconds), sql));
        return relay;
    }
}
