using System.Data.Common;
using Acta.Relational.Commands;
using Acta.Relational.Connections;

namespace Acta.SqlServer.Services;

/// <summary>
/// SQL Server external-outbox source dialect. Inline claim/finalize SQL only; connection creation,
/// parameter binding, and transient/cancellation classification reuse the ledger
/// <see cref="SqlServerDialect"/> because the canonical outbox column types (datetime2,
/// uniqueidentifier, varbinary, tinyint) coincide with the ledger's.
/// </summary>
internal sealed class SqlServerOutboxDialect : OutboxSourceDialect
{
    private readonly SqlServerDialect _inner = new();

    public override DbProvider Provider => DbProvider.SqlServer;

    public override string DialectToken => "mssql";

    public override DbConnection CreateConnection(string connectionString) => _inner.CreateConnection(connectionString);

    public override bool OwnsConnection(DbConnection connection) => _inner.OwnsConnection(connection);

    public override bool IsTransientConflict(Exception exception) => _inner.IsTransientConflict(exception);

    public override bool IsCancellation(Exception exception) => _inner.IsCancellation(exception);

    public override DbParameter CreateParameter(DbParameterSpec spec) => _inner.CreateParameter(spec);
}
