using System.Data;
using Microsoft.Data.SqlClient;

namespace DbRelay.SqlServer.Internal;

/// <summary>Creates, opens and disposes the connections DbRelay owns, and applies the configured command timeout.</summary>
internal sealed class SqlServerConnectionSource
{
    /// <summary>SQL Server error number for "transaction was deadlocked ... and has been chosen as the deadlock victim".</summary>
    internal const int DeadlockVictimErrorNumber = 1205;

    private const int MaxDeadlockAttempts = 3;

    private readonly Func<SqlConnection> _factory;
    private long _deadlockRetries;

    public SqlServerConnectionSource(Func<SqlConnection> factory, int commandTimeoutSeconds)
    {
        _factory = factory;
        CommandTimeoutSeconds = commandTimeoutSeconds;
    }

    public int CommandTimeoutSeconds { get; }

    /// <summary>How many times an operation was chosen as a deadlock victim and retried (diagnostics and tests).</summary>
    internal long DeadlockRetries => Interlocked.Read(ref _deadlockRetries);

    public async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = _factory()
            ?? throw new InvalidOperationException("The DbRelay SQL Server connection factory returned null. It must return a new, unopened SqlConnection.");

        if (connection.State != ConnectionState.Closed)
        {
            // Not disposed: an already-open connection is not ours to dispose.
            throw new InvalidOperationException(
                "The DbRelay SQL Server connection factory returned a connection that is not closed. It must return a new, unopened SqlConnection; DbRelay opens and disposes it.");
        }

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return connection;
    }

    public SqlCommand CreateCommand(SqlConnection connection, string commandText, SqlTransaction? transaction = null)
    {
#pragma warning disable CA2100 // Command text is built only from validated, quoted identifiers; all values are parameters.
        var command = new SqlCommand(commandText, connection, transaction)
        {
            CommandType = CommandType.Text,
            CommandTimeout = CommandTimeoutSeconds,
        };
#pragma warning restore CA2100
        return command;
    }

    /// <summary>
    /// Runs <paramref name="operation"/> on a new provider-owned connection. A deadlock victim (error 1205) is retried a few times:
    /// every operation passed here is a single autocommit statement or runs in its own transaction, so SQL Server has already rolled
    /// it back completely and running it again is safe.
    /// </summary>
    public async Task<T> ExecuteAsync<T>(Func<SqlConnection, CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (connection.ConfigureAwait(false))
            {
                try
                {
                    return await operation(connection, cancellationToken).ConfigureAwait(false);
                }
                catch (SqlException ex) when (ex.Number == DeadlockVictimErrorNumber && attempt < MaxDeadlockAttempts)
                {
                    // Fall through to retry after a short, randomized pause.
                    Interlocked.Increment(ref _deadlockRetries);
                }
            }

#pragma warning disable CA5394 // Jitter only; not security sensitive.
            await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(5, 50) * attempt), cancellationToken).ConfigureAwait(false);
#pragma warning restore CA5394
        }
    }
}
