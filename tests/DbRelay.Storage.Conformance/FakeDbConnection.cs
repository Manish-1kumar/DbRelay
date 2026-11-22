using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace DbRelay.Storage.Conformance;

/// <summary>An "open" connection of a foreign provider type, used to verify provider type validation.</summary>
[SuppressMessage("Design", "CA1065", Justification = "Test double.")]
public sealed class FakeDbConnection : DbConnection
{
    [AllowNull]
    public override string ConnectionString { get; set; } = string.Empty;

    public override string Database => "fake";

    public override string DataSource => "fake";

    public override string ServerVersion => "0";

    public override ConnectionState State => ConnectionState.Open;

    public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

    public override void Close()
    {
    }

    public override void Open()
    {
    }

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();

    protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
}
