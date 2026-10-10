namespace Acta.Sqlite;

/// <summary>
/// Builds the SQLite external-outbox source's connection: a per-operation source session, the inline outbox
/// SQL embedded in this package, and the outbox dialect, which <see cref="IOutboxOperations.OpenSource"/>
/// opens as the source. The source session is independent of the ledger's <c>IDbSession</c>/<c>ISqlDialect</c>.
/// </summary>
internal static class SqliteOutboxSource
{
    public static OutboxSourceConnection CreateConnection(string connectionString, string schema, string table)
    {
        var dialect = new SqliteOutboxDialect();
        var options = new SqliteProviderOptions { ConnectionString = connectionString, Schema = schema };
        var catalog = new SqlResourceCatalog(dialect.DialectToken, schema, table);
        return new(new DbSession(options, dialect, catalog), dialect);
    }
}

/// <summary>
/// Builds the SQLite relay source's connection from the captured connection string and the source builder's
/// schema/table overrides. The SQLite producer default is the attached database's <c>main</c> schema and
/// the canonical <c>acta_outbox</c> table. Registered by <c>source.UseSqlite(...)</c>; resolved lazily,
/// so construction never opens a connection.
/// </summary>
internal sealed class SqliteOutboxSourceFactory(string connectionString) : IOutboxSourceFactory
{
    public OutboxSourceConnection Create(string? schema, string? table) =>
        SqliteOutboxSource.CreateConnection(connectionString, schema ?? "main", table ?? "acta_outbox");
}
