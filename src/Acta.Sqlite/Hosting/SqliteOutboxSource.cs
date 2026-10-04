using Acta.Runtime;

namespace Acta.Sqlite;

/// <summary>
/// Builds the SQLite external-outbox source: a per-operation source connection, the inline outbox
/// SQL embedded in this package, and the shared <see cref="OutboxSource"/>. The source
/// session is independent of the ledger's <c>IDbSession</c>/<c>ISqlDialect</c>.
/// </summary>
internal static class SqliteOutboxSource
{
    public static OutboxSource CreateSource(string connectionString, string schema, string table)
    {
        var dialect = new SqliteOutboxDialect();
        var options = new SqliteProviderOptions { ConnectionString = connectionString, Schema = schema };
        var catalog = new SqlResourceCatalog(dialect.DialectToken, schema, table);
        return new OutboxSource(new DbSession(options, dialect, catalog), dialect);
    }
}

/// <summary>
/// Builds the SQLite relay source from the captured connection string and the source builder's
/// schema/table overrides. The SQLite producer default is the attached database's <c>main</c> schema and
/// the canonical <c>acta_outbox</c> table. Registered by <c>source.UseSqlite(...)</c>; resolved lazily,
/// so construction never opens a connection.
/// </summary>
internal sealed class SqliteOutboxSourceFactory(string connectionString) : IOutboxSourceFactory
{
    public OutboxSource Create(string? schema, string? table) =>
        SqliteOutboxSource.CreateSource(connectionString, schema ?? "main", table ?? "acta_outbox");
}
