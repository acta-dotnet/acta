using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Acta.Sqlite;

/// <summary>
/// SQLite external-outbox source dialect. The canonical outbox SQLite shape stores instants as ISO text
/// (<c>strftime('%Y-%m-%d %H:%M:%f', ...)</c>), not the ledger's epoch-milliseconds INTEGER, so this
/// dialect binds <see cref="DbKind.UtcInstant"/> as an ISO-text string that sorts and compares against
/// the source clock. Connection creation (with the ledger's per-connection PRAGMAs), busy/locked
/// classification, and the BEGIN IMMEDIATE claim transaction reuse the ledger <see cref="SqliteDialect"/>.
/// </summary>
internal sealed class SqliteOutboxDialect() : OutboxSourceDialect(new SqliteDialect())
{
    /// <summary>
    /// The canonical SQLite outbox instant encoding (millisecond ISO text), shared with the staging extension.
    /// </summary>
    public const string InstantFormat = "yyyy-MM-dd HH:mm:ss.fff";

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
