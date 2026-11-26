using System.Data;
using Oracle.ManagedDataAccess.Client;

namespace DbRelay.Oracle.Internal;

/// <summary>Creates, opens and disposes the connections DbRelay owns, and applies the configured command settings.</summary>
internal sealed class OracleConnectionSource
{
    /// <summary>ORA-00060: deadlock detected while waiting for resource.</summary>
    internal const int DeadlockErrorNumber = 60;

    private const int MaxDeadlockAttempts = 3;

    private readonly Func<OracleConnection> _factory;
    private long _deadlockRetries;

    public OracleConnectionSource(Func<OracleConnection> factory, int commandTimeoutSeconds)
    {
        _factory = factory;
        CommandTimeoutSeconds = commandTimeoutSeconds;
    }

    public int CommandTimeoutSeconds { get; }

    /// <summary>How many times an operation hit ORA-00060 and was retried (diagnostics and tests).</summary>
    internal long DeadlockRetries => Interlocked.Read(ref _deadlockRetries);

    public async Task<OracleConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = _factory()
            ?? throw new InvalidOperationException("The DbRelay Oracle connection factory returned null. It must return a new, unopened OracleConnection.");

        if (connection.State != ConnectionState.Closed)
        {
            // Not disposed: an already-open connection is not ours to dispose.
            throw new InvalidOperationException(
                "The DbRelay Oracle connection factory returned a connection that is not closed. It must return a new, unopened OracleConnection; DbRelay opens and disposes it.");
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

    /// <summary>
    /// Creates a command with name binding (every statement uses <c>:name</c> placeholders, some more than once), the configured
    /// timeout and LOBs fetched inline with the row instead of one round trip per locator.
    /// </summary>
    public OracleCommand CreateCommand(OracleConnection connection, string commandText)
    {
#pragma warning disable CA2100 // Command text is built only from validated, quoted identifiers; all values are parameters.
        return new OracleCommand(commandText, connection)
        {
            BindByName = true,
            CommandType = CommandType.Text,
            CommandTimeout = CommandTimeoutSeconds,
            InitialLOBFetchSize = -1,
        };
#pragma warning restore CA2100
    }

    /// <summary>
    /// Disposes a command's parameters (OR-03). ODP.NET creates temporary LOBs for BLOB and CLOB values and does not free them when
    /// the command is disposed; on a pooled session they would otherwise stay allocated.
    /// </summary>
    public static void DisposeParameters(OracleCommand command)
    {
        foreach (OracleParameter parameter in command.Parameters)
        {
            parameter.Dispose();
        }
    }

    /// <summary>
    /// Runs <paramref name="operation"/> on a new provider-owned connection. ORA-00060 is retried a few times. Every operation passed
    /// here is either one autocommitted statement or PL/SQL block, which Oracle has rolled back as a whole when the error reaches the
    /// client, or a multi-statement insert in a transaction DbRelay began on this connection, which is rolled back when the error
    /// leaves it uncommitted. Running either again is safe.
    /// </summary>
    public async Task<T> ExecuteAsync<T>(Func<OracleConnection, CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
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
                catch (OracleException ex) when (ex.Number == DeadlockErrorNumber && attempt < MaxDeadlockAttempts)
                {
                    Interlocked.Increment(ref _deadlockRetries);
                }
                catch (OracleException ex) when (cancellationToken.IsCancellationRequested)
                {
                    // OR-09: ODP.NET reports a cancelled call as ORA-01013 ("user requested cancel"). Callers such as the dispatcher
                    // treat only OperationCanceledException as a normal shutdown.
                    throw new OperationCanceledException("The Oracle operation was cancelled.", ex, cancellationToken);
                }
            }

#pragma warning disable CA5394 // Jitter only; not security sensitive.
            await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(5, 50) * attempt), cancellationToken).ConfigureAwait(false);
#pragma warning restore CA5394
        }
    }
}
