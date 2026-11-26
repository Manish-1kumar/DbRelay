namespace DbRelay.Oracle;

/// <summary>
/// Options for the Oracle storage provider. Names are validated like every DbRelay identifier and stored in upper case (Oracle's
/// convention for unquoted names), so <c>outbox_messages</c> becomes the table <c>OUTBOX_MESSAGES</c>.
/// </summary>
public sealed class OracleOutboxOptions
{
    /// <summary>
    /// The schema (in Oracle, the owning user) of the outbox tables. Defaults to <see langword="null"/>: the schema of the
    /// connection's user. DbRelay never creates users, so a configured schema must already exist.
    /// </summary>
    public string? Schema { get; set; }

    /// <summary>The outbox table name. Defaults to <c>outbox_messages</c> (stored as <c>OUTBOX_MESSAGES</c>).</summary>
    public string TableName { get; set; } = "outbox_messages";

    /// <summary>The command timeout for statements DbRelay runs on its own connections. Defaults to 30 seconds.</summary>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
