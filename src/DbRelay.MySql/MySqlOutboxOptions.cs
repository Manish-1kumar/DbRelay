namespace DbRelay.MySql;

/// <summary>Options for the MySQL and MariaDB storage provider.</summary>
public sealed class MySqlOutboxOptions
{
    /// <summary>
    /// The database (in MySQL, a schema is a database) that holds the outbox tables. Defaults to <see langword="null"/>: the
    /// connection's current database. DbRelay never creates databases, so a configured one must already exist.
    /// </summary>
    public string? Schema { get; set; }

    /// <summary>The outbox table name. Defaults to <c>outbox_messages</c>.</summary>
    public string TableName { get; set; } = "outbox_messages";

    /// <summary>The command timeout for statements DbRelay runs on its own connections. Defaults to 30 seconds.</summary>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
