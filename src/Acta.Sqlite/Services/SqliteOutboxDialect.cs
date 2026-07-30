using System.Data.Common;
using System.Globalization;
using Acta.Configuration;
using Acta.Modules.Outbox;
using Acta.Relational.Commands;
using Acta.Relational.Connections;
using Acta.Relational.Resources;
using Acta.Relational.Schema;
using Acta.Relational.Stores;
using Acta.Sqlite.Configuration;
using Acta.Sqlite.Services;
using Microsoft.Data.Sqlite;

namespace Acta.Sqlite.Services;

/// <summary>
/// SQLite external-outbox source dialect. The canonical outbox SQLite shape stores instants as ISO text
/// (<c>strftime('%Y-%m-%d %H:%M:%f', ...)</c>), not the ledger's epoch-milliseconds INTEGER, so this
/// dialect binds <see cref="DbKind.UtcInstant"/> as an ISO-text string that sorts and compares against
/// the source clock. Connection creation (with the ledger's per-connection PRAGMAs) and busy/locked
/// classification reuse the ledger <see cref="SqliteDialect"/>. Claims run under BEGIN IMMEDIATE.
/// </summary>
internal sealed class SqliteOutboxDialect : OutboxSourceDialect
{
    // The canonical SQLite outbox instant encoding (millisecond ISO text), shared with the staging extension.
    public const string InstantFormat = "yyyy-MM-dd HH:mm:ss.fff";

    private readonly SqliteDialect _inner = new(ExecutionProfile.Direct);

    public override DbProvider Provider => DbProvider.Sqlite;

    public override string DialectToken => "sqlite";

    public override DbConnection CreateConnection(string connectionString) => _inner.CreateConnection(connectionString);

    public override bool OwnsConnection(DbConnection connection) => _inner.OwnsConnection(connection);

    public override bool IsTransientConflict(Exception exception) => _inner.IsTransientConflict(exception);

    public override DbTransaction BeginImmediateTransaction(DbConnection connection) =>
        ((SqliteConnection)connection).BeginTransaction(deferred: false);

    public override DbParameter CreateParameter(DbParameterSpec spec)
    {
        DbParams.Validate(spec);
        var coerced = DbParams.Coerce(spec);
        // Reuse the ledger dialect's DbKind-keyed coercion (Guid -> text, bool -> 0/1, ...) and override only
        // the instant arm: the outbox SQLite shape stores instants as sortable ISO text, not the ledger's
        // epoch-milliseconds INTEGER.
        var value =
            spec.Kind == DbKind.UtcInstant && coerced is DateTime dt
                ? dt.ToString(InstantFormat, CultureInfo.InvariantCulture)
                : SqliteDialect.ToSqliteValue(spec.Kind, coerced);
        return new SqliteParameter { ParameterName = "@" + spec.Name, Value = value };
    }
}
