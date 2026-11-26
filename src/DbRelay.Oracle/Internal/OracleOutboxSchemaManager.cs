namespace DbRelay.Oracle.Internal;

/// <summary>
/// Creates the Oracle outbox objects by running the idempotent script as one PL/SQL block on a DbRelay-owned connection. Oracle
/// commits DDL implicitly, so this never runs on a caller's connection. Instances that start together are safe: each object is
/// created by its own statement, "already exists" errors are ignored, and DDL waits for a concurrent instance's lock.
/// </summary>
internal sealed class OracleOutboxSchemaManager : IOutboxSchemaManager
{
    private readonly OracleConnectionSource _connections;
    private readonly OracleOutboxSql _sql;

    public OracleOutboxSchemaManager(OracleConnectionSource connections, OracleOutboxSql sql)
    {
        _connections = connections;
        _sql = sql;
    }

    public string GetCreateScript() => _sql.CreateScript;

    public Task EnsureCreatedAsync(CancellationToken cancellationToken = default) =>
        _connections.ExecuteAsync(
            async (connection, ct) =>
            {
                var command = _connections.CreateCommand(connection, _sql.CreateBlock);
                await using (command.ConfigureAwait(false))
                {
                    await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                return true;
            },
            cancellationToken);
}
