using DbRelay.MySql;
using DbRelay.MySql.Internal;
using DbRelay.Storage;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;

namespace DbRelay;

/// <summary>
/// Registers the MySQL and MariaDB storage provider with DbRelay.
/// </summary>
public static class MySqlDbRelayBuilderExtensions
{
    /// <summary>64 (MySQL's identifier limit) minus the 6 characters of the generated CHECK constraint name's <c>_chk_1</c>.</summary>
    private const int MaxTableNameLength = 58;

    private static readonly TimeSpan MaxCommandTimeout = TimeSpan.FromHours(1);

    /// <summary>
    /// Stores outbox messages in MySQL or MariaDB, using <paramref name="connectionString"/> for the connections DbRelay owns
    /// (dispatching, standalone enqueue, administration and schema creation). Transactional enqueue always uses the caller's
    /// <see cref="MySqlConnection"/> and <see cref="MySqlTransaction"/>.
    /// </summary>
    /// <param name="relay">The DbRelay builder.</param>
    /// <param name="connectionString">The MySqlConnector connection string.</param>
    /// <param name="configure">Optionally configures the schema (database), table name and command timeout.</param>
    /// <returns>The builder.</returns>
    /// <exception cref="ArgumentException">The connection string is empty, or the schema or table name is not a safe identifier.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The command timeout is out of range.</exception>
    public static DbRelayBuilder UseMySql(this DbRelayBuilder relay, string connectionString, Action<MySqlOutboxOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(relay);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return Register(relay, _ => () => new MySqlConnection(connectionString), configure);
    }

    /// <summary>
    /// Stores outbox messages in MySQL or MariaDB, using <paramref name="connectionFactory"/> to create the connections DbRelay owns
    /// (dispatching, standalone enqueue, administration and schema creation). Transactional enqueue always uses the caller's
    /// <see cref="MySqlConnection"/> and <see cref="MySqlTransaction"/>.
    /// </summary>
    /// <param name="relay">The DbRelay builder.</param>
    /// <param name="connectionFactory">
    /// Returns a new, unopened <see cref="MySqlConnection"/> each time it is called. DbRelay opens and disposes it.
    /// </param>
    /// <param name="configure">Optionally configures the schema (database), table name and command timeout.</param>
    /// <returns>The builder.</returns>
    /// <exception cref="ArgumentException">The schema or table name is not a safe identifier.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The command timeout is out of range.</exception>
    public static DbRelayBuilder UseMySql(
        this DbRelayBuilder relay,
        Func<IServiceProvider, MySqlConnection> connectionFactory,
        Action<MySqlOutboxOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(relay);
        ArgumentNullException.ThrowIfNull(connectionFactory);
        return Register(relay, sp => () => connectionFactory(sp), configure);
    }

    private static DbRelayBuilder Register(
        DbRelayBuilder relay,
        Func<IServiceProvider, Func<MySqlConnection>> factoryProvider,
        Action<MySqlOutboxOptions>? configure)
    {
        var options = new MySqlOutboxOptions();
        configure?.Invoke(options);

        var schema = options.Schema is null ? null : SqlIdentifier.Validate(options.Schema, "MySqlOutboxOptions.Schema");
        var table = SqlIdentifier.Validate(options.TableName, "MySqlOutboxOptions.TableName");
        if (string.Equals(table, MySqlOutboxSql.VersionTableName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"MySqlOutboxOptions.TableName '{MySqlOutboxSql.VersionTableName}' is reserved for DbRelay's schema version table.", nameof(configure));
        }

        if (table.Length > MaxTableNameLength)
        {
            // MySQL names the table's CHECK constraint <table>_chk_1, within its 64-character identifier limit.
            throw new ArgumentException(
                $"MySqlOutboxOptions.TableName can be at most {MaxTableNameLength} characters on MySQL.", nameof(configure));
        }

        if (options.CommandTimeout < TimeSpan.FromSeconds(1) || options.CommandTimeout > MaxCommandTimeout)
        {
            throw new ArgumentOutOfRangeException(
                nameof(configure), options.CommandTimeout, "MySqlOutboxOptions.CommandTimeout must be between 1 second and 1 hour.");
        }

        var sql = new MySqlOutboxSql(schema, table);
        var commandTimeoutSeconds = (int)Math.Ceiling(options.CommandTimeout.TotalSeconds);

        relay.UseStorage(sp => new MySqlOutboxStore(new MySqlConnectionSource(factoryProvider(sp), commandTimeoutSeconds), sql));
        relay.Services.AddSingleton<IOutboxSchemaManager>(sp =>
            new MySqlOutboxSchemaManager(new MySqlConnectionSource(factoryProvider(sp), commandTimeoutSeconds), sql));
        return relay;
    }
}
