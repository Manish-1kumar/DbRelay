namespace DbRelay.SqlServer;

/// <summary>
/// Options for the SQL Server outbox storage provider.
/// </summary>
/// <remarks>
/// <see cref="Schema"/> and <see cref="TableName"/> are validated when <c>UseSqlServer</c> is called: they must be 1 to 63
/// characters long, start with an ASCII letter or underscore and contain only ASCII letters, digits and underscores. They are
/// always quoted with square brackets in generated SQL.
/// </remarks>
public sealed class SqlServerOutboxOptions
{
    /// <summary>The database schema that holds the outbox table. Defaults to <c>dbrelay</c>.</summary>
    public string Schema { get; set; } = "dbrelay";

    /// <summary>
    /// The outbox table name. Defaults to <c>outbox_messages</c>. <c>schema_version</c> is reserved: DbRelay records the
    /// schema version of each outbox table in <c>[Schema].[schema_version]</c>.
    /// </summary>
    public string TableName { get; set; } = "outbox_messages";

    /// <summary>
    /// The command timeout applied to every statement DbRelay executes, including inserts on the caller's connection.
    /// Must be between 1 second and 1 hour. Defaults to 30 seconds.
    /// </summary>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
