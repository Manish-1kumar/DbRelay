using Oracle.ManagedDataAccess.Client;

namespace DbRelay.Oracle.Internal;

/// <summary>
/// Creates the Oracle outbox objects by running the idempotent script as one PL/SQL block on a DbRelay-owned connection. Oracle
/// commits DDL implicitly, so this never runs on a caller's connection. Instances that start together are safe: each object is
/// created by its own statement, "already exists" errors are ignored, and DDL waits for a concurrent instance's lock. Concurrent DDL
/// on the same objects can still end in a library cache deadlock or lock timeout; the whole script is then run again, which is safe
/// because it only creates what is missing.
/// </summary>
internal sealed class OracleOutboxSchemaManager : IOutboxSchemaManager
{
    /// <summary>ORA-04020 (library cache deadlock), ORA-04021 (timeout waiting for a library cache lock), ORA-00054 (resource busy).</summary>
    private static readonly int[] ConcurrentDdlErrors = [4020, 4021, 54];

    private const int MaxAttempts = 5;

    private readonly OracleConnectionSource _connections;
    private readonly OracleOutboxSql _sql;

    public OracleOutboxSchemaManager(OracleConnectionSource connections, OracleOutboxSql sql)
    {
        _connections = connections;
        _sql = sql;
    }

    public string GetCreateScript() => _sql.CreateScript;

    public async Task EnsureCreatedAsync(CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await RunScriptAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OracleException ex) when (attempt < MaxAttempts && IsConcurrentDdlError(ex))
            {
                // Another instance is creating the same objects; retry once it is likely done.
            }

#pragma warning disable CA5394 // Jitter only; not security sensitive.
            await Task.Delay(TimeSpan.FromMilliseconds((200 * attempt) + Random.Shared.Next(0, 200)), cancellationToken).ConfigureAwait(false);
#pragma warning restore CA5394
        }
    }

    /// <summary>The error raised inside the script arrives with ORA-06512 frames; any of its errors may be the concurrency error.</summary>
    private static bool IsConcurrentDdlError(OracleException exception)
    {
        if (Array.IndexOf(ConcurrentDdlErrors, exception.Number) >= 0)
        {
            return true;
        }

        foreach (OracleError error in exception.Errors)
        {
            if (Array.IndexOf(ConcurrentDdlErrors, error.Number) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private Task<bool> RunScriptAsync(CancellationToken cancellationToken) =>
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
