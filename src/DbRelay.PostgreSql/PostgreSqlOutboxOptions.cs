namespace DbRelay.PostgreSql;

/// <summary>
/// Options for the DbRelay PostgreSQL storage provider. Configured through
/// <see cref="PostgreSqlDbRelayBuilderExtensions.UsePostgreSql(DbRelayBuilder, string, Action{PostgreSqlOutboxOptions}?)"/>.
/// </summary>
public sealed class PostgreSqlOutboxOptions
{
    /// <summary>
    /// The schema that holds the outbox table. Defaults to <c>dbrelay</c>.
    /// Must be 1 to 63 characters: an ASCII letter or underscore followed by ASCII letters, digits or underscores.
    /// The name is quoted in SQL, so it is case-sensitive.
    /// </summary>
    public string Schema { get; set; } = "dbrelay";

    /// <summary>
    /// The outbox table name. Defaults to <c>outbox_messages</c>. Same rules as <see cref="Schema"/>; <c>schema_version</c> is reserved.
    /// Several outbox tables can share one schema.
    /// </summary>
    public string TableName { get; set; } = "outbox_messages";

    /// <summary>The timeout applied to every command DbRelay executes. Defaults to 30 seconds. Must be positive.</summary>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
