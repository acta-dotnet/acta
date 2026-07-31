using Acta.Relational.Connections;
using Acta.Relational.Resources;
using Acta.Relational.Stores;
using Acta.Runtime.Modules.Outbox;
using Acta.Sqlite.Configuration;
using Acta.Sqlite.Services;

namespace Acta.Sqlite.Hosting;

/// <summary>
/// Builds the SQLite external-outbox source store: a per-operation source connection, the inline outbox
/// SQL embedded in this package, and the shared <see cref="RelationalOutboxRelayStore"/>. The source
/// session is independent of the ledger's <c>IDbSession</c>/<c>ISqlDialect</c>.
/// </summary>
internal static class SqliteOutboxSource
{
    public static IOutboxRelayStore CreateStore(string connectionString, string schema, string table)
    {
        var dialect = new SqliteOutboxDialect();
        var options = new SqliteProviderOptions { ConnectionString = connectionString, Schema = schema };
        var catalog = new SqlResourceCatalog(typeof(SqliteOutboxDialect).Assembly, schema, table);
        return new RelationalOutboxRelayStore(new DbSession(options, dialect, catalog), dialect, schema, table);
    }
}

/// <summary>
/// Builds the SQLite relay source store from the captured connection string and the source builder's
/// schema/table overrides. The SQLite producer default is the attached database's <c>main</c> schema and
/// the canonical <c>acta_outbox</c> table. Registered by <c>source.UseSqlite(...)</c>; resolved lazily,
/// so construction never opens a connection.
/// </summary>
internal sealed class SqliteOutboxSourceStoreFactory(string connectionString) : IOutboxSourceStoreFactory
{
    public IOutboxRelayStore Create(string? schema, string? table) =>
        SqliteOutboxSource.CreateStore(connectionString, schema ?? "main", table ?? "acta_outbox");
}
