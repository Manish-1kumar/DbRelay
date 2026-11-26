using System.Globalization;
using MySqlConnector;

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

    /// <summary>
    /// Creates what is missing, then checks the table's shape (MY-08): <c>CREATE TABLE IF NOT EXISTS</c> accepts an existing table of
    /// any shape, so a table left by something else fails here, with an explanation, instead of at the first claim.
    /// </summary>
    public Task EnsureCreatedAsync(CancellationToken cancellationToken = default) =>
        _connections.ExecuteAsync(
            async (connection, ct) =>
            {
                var command = _connections.CreateCommand(connection, _sql.CreateScript);
                await using (command.ConfigureAwait(false))
                {
                    await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                await VerifyShapeAsync(_connections, connection, _sql, ct).ConfigureAwait(false);
                return true;
            },
            cancellationToken);

    /// <summary>
    /// Throws unless the outbox table answers and has the three indexes and every column the provider's statements use. Used by
    /// <see cref="EnsureCreatedAsync"/> and the health check.
    /// </summary>
    internal static async Task VerifyShapeAsync(MySqlConnectionSource connections, MySqlConnection connection, MySqlOutboxSql sql, CancellationToken cancellationToken)
    {
        var command = connections.CreateCommand(connection, sql.HealthCheck);
        await using (command.ConfigureAwait(false))
        {
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                var indexes = Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture);
                var columns = Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture);
                if (indexes < 3 || columns < MySqlOutboxSql.RequiredColumns.Length)
                {
                    throw new InvalidOperationException(
                        $"The outbox table {sql.Table} does not have the shape DbRelay needs: {indexes} of its 3 indexes ({sql.ClaimIndexName}, {sql.CleanupIndexName}, " +
                        $"{sql.DeadIndexName}) and {columns} of its {MySqlOutboxSql.RequiredColumns.Length} columns were found. It may have been created by something " +
                        "else or altered. Rename it aside and run EnsureCreatedAsync, or compare it with GetCreateScript().");
                }
            }
        }
    }
}
