using DbRelay.PostgreSql;
using DbRelay.PostgreSql.Internal;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DbRelay;

/// <summary>Registers the PostgreSQL storage provider with DbRelay.</summary>
public static class PostgreSqlDbRelayBuilderExtensions
{
    /// <summary>
    /// Stores outbox messages in PostgreSQL, using an <see cref="NpgsqlDataSource"/> that DbRelay builds from
    /// <paramref name="connectionString"/>. DbRelay owns that data source and disposes it with the service provider.
    /// </summary>
    /// <param name="relay">The DbRelay builder.</param>
    /// <param name="connectionString">The Npgsql connection string used by the dispatcher, standalone enqueues and administration.</param>
    /// <param name="configure">Optionally configures the schema, table name and command timeout.</param>
    /// <returns>The builder.</returns>
    /// <exception cref="ArgumentException">The connection string is empty or malformed, or an option is invalid.</exception>
    /// <example>
    /// <code>
    /// services.AddDbRelay(relay =>
    /// {
    ///     relay.UsePostgreSql(connectionString, pg => pg.Schema = "messaging");
    ///     relay.UseDeliveryHandler&lt;MyBrokerPublisher&gt;();
    /// });
    /// </code>
    /// </example>
    public static DbRelayBuilder UsePostgreSql(this DbRelayBuilder relay, string connectionString, Action<PostgreSqlOutboxOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(relay);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        try
        {
            _ = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException ex)
        {
            // Never echo the connection string: it may contain a password.
            throw new ArgumentException("The PostgreSQL connection string is not valid: " + ex.Message, nameof(connectionString));
        }

        var settings = PostgreSqlSettings.Create(configure);
        return Register(relay, settings, _ => new PostgreSqlDataSourceHolder(NpgsqlDataSource.Create(connectionString), ownsDataSource: true));
    }

    /// <summary>
    /// Stores outbox messages in PostgreSQL using a caller-owned <see cref="NpgsqlDataSource"/>.
    /// DbRelay never disposes <paramref name="dataSource"/>.
    /// </summary>
    /// <param name="relay">The DbRelay builder.</param>
    /// <param name="dataSource">The data source used by the dispatcher, standalone enqueues and administration.</param>
    /// <param name="configure">Optionally configures the schema, table name and command timeout.</param>
    /// <returns>The builder.</returns>
    /// <exception cref="ArgumentException">An option is invalid.</exception>
    public static DbRelayBuilder UsePostgreSql(this DbRelayBuilder relay, NpgsqlDataSource dataSource, Action<PostgreSqlOutboxOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(relay);
        ArgumentNullException.ThrowIfNull(dataSource);
        var settings = PostgreSqlSettings.Create(configure);
        return Register(relay, settings, _ => new PostgreSqlDataSourceHolder(dataSource, ownsDataSource: false));
    }

    /// <summary>
    /// Stores outbox messages in PostgreSQL using an <see cref="NpgsqlDataSource"/> resolved from the service provider, for example
    /// one registered with <c>AddNpgsqlDataSource</c>. The factory is called once. DbRelay never disposes the returned data source.
    /// </summary>
    /// <param name="relay">The DbRelay builder.</param>
    /// <param name="dataSourceFactory">Returns the data source.</param>
    /// <param name="configure">Optionally configures the schema, table name and command timeout.</param>
    /// <returns>The builder.</returns>
    /// <exception cref="ArgumentException">An option is invalid.</exception>
    /// <example>
    /// <code>
    /// services.AddNpgsqlDataSource(connectionString);
    /// services.AddDbRelay(relay =>
    /// {
    ///     relay.UsePostgreSql(sp => sp.GetRequiredService&lt;NpgsqlDataSource&gt;());
    ///     relay.UseDeliveryHandler&lt;MyBrokerPublisher&gt;();
    /// });
    /// </code>
    /// </example>
    public static DbRelayBuilder UsePostgreSql(
        this DbRelayBuilder relay,
        Func<IServiceProvider, NpgsqlDataSource> dataSourceFactory,
        Action<PostgreSqlOutboxOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(relay);
        ArgumentNullException.ThrowIfNull(dataSourceFactory);
        var settings = PostgreSqlSettings.Create(configure);
        return Register(relay, settings, sp =>
        {
            var dataSource = dataSourceFactory(sp)
                ?? throw new InvalidOperationException("The NpgsqlDataSource factory passed to UsePostgreSql returned null.");
            return new PostgreSqlDataSourceHolder(dataSource, ownsDataSource: false);
        });
    }

    private static DbRelayBuilder Register(DbRelayBuilder relay, PostgreSqlSettings settings, Func<IServiceProvider, PostgreSqlDataSourceHolder> holderFactory)
    {
        relay.UseStorage(sp => new PostgreSqlOutboxStore(sp.GetRequiredService<PostgreSqlDataSourceHolder>().DataSource, settings));
        relay.Services.AddSingleton(holderFactory);
        relay.Services.AddSingleton<IOutboxSchemaManager>(sp =>
            new PostgreSqlSchemaManager(sp.GetRequiredService<PostgreSqlDataSourceHolder>().DataSource, settings));
        return relay;
    }
}
