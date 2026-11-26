namespace DbRelay.MySql.Internal;

/// <summary>
/// Creates the MySQL / MariaDB outbox objects by running the idempotent script (<c>CREATE TABLE IF NOT EXISTS</c> with the indexes
/// inline, and <c>INSERT IGNORE</c>) on a DbRelay-owned connection. DDL commits implicitly, so this never runs on a caller's
/// connection. Instances that start together are safe: the server serializes DDL on a table with its metadata lock.
/// </summary>
internal sealed class MySqlOutboxSchemaManager : IOutboxSchemaManager
{
    private readonly MySqlConnectionSource _connections;
    private readonly MySqlOutboxSql _sql;

    public MySqlOutboxSchemaManager(MySqlConnectionSource connections, MySqlOutboxSql sql)
    {
        _connections = connections;
        _sql = sql;
    }

    public string GetCreateScript() => _sql.CreateScript;

    public Task EnsureCreatedAsync(CancellationToken cancellationToken = default) =>
        _connections.ExecuteAsync(
            async (connection, ct) =>
            {
                var command = _connections.CreateCommand(connection, _sql.CreateScript);
                await using (command.ConfigureAwait(false))
                {
                    await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                return true;
            },
            cancellationToken);
}
