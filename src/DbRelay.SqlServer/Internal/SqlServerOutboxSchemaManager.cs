using System.Data;
using Microsoft.Data.SqlClient;

namespace DbRelay.SqlServer.Internal;

/// <summary>
/// Creates the SQL Server outbox objects. <see cref="EnsureCreatedAsync"/> runs the idempotent script inside a transaction that
/// holds an exclusive application lock for the schema (<c>sp_getapplock</c>), so concurrent instances starting together
/// serialize instead of racing on <c>IF ... IS NULL CREATE ...</c>.
/// </summary>
internal sealed class SqlServerOutboxSchemaManager : IOutboxSchemaManager
{
    private const string AcquireLockSql =
        "DECLARE @result int;\n" +
        "EXEC @result = sp_getapplock @Resource = @resource, @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = @timeout_ms;\n" +
        "SELECT @result;";

    private readonly SqlServerConnectionSource _connections;
    private readonly SqlServerOutboxSql _sql;

    public SqlServerOutboxSchemaManager(SqlServerConnectionSource connections, SqlServerOutboxSql sql)
    {
        _connections = connections;
        _sql = sql;
    }

    public string GetCreateScript() => _sql.CreateScript;

    public Task EnsureCreatedAsync(CancellationToken cancellationToken = default) =>
        _connections.ExecuteAsync(
            async (connection, ct) =>
            {
                var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
                await using (transaction.ConfigureAwait(false))
                {
                    await AcquireLockAsync(connection, transaction, ct).ConfigureAwait(false);
                    foreach (var batch in _sql.CreateScriptBatches)
                    {
                        var command = _connections.CreateCommand(connection, batch, transaction);
                        await using (command.ConfigureAwait(false))
                        {
                            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                        }
                    }

                    // Committing releases the application lock.
                    await transaction.CommitAsync(ct).ConfigureAwait(false);
                }

                return true;
            },
            cancellationToken);

    private async Task AcquireLockAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken cancellationToken)
    {
        var command = _connections.CreateCommand(connection, AcquireLockSql, transaction);
        await using (command.ConfigureAwait(false))
        {
            command.Parameters.Add(new SqlParameter("@resource", SqlDbType.NVarChar, 255) { Value = _sql.SchemaLockResource });

            // Leave a margin so sp_getapplock reports its own timeout before the command times out.
            var timeoutMs = Math.Max(1000, (_connections.CommandTimeoutSeconds * 1000) - 500);
            command.Parameters.Add(new SqlParameter("@timeout_ms", SqlDbType.Int) { Value = timeoutMs });

            var result = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
            if (result < 0)
            {
                throw new TimeoutException(
                    $"DbRelay could not acquire the schema creation lock for schema '{_sql.Schema}' (sp_getapplock returned {result}). Another instance may be creating the outbox schema; retry later.");
            }
        }
    }
}
