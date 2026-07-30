using System.Data.Common;
using Acta.Configuration;
using Acta.Modules.Outbox;
using Acta.Postgres.Configuration;
using Acta.Postgres.Services;
using Acta.Relational.Commands;
using Acta.Relational.Connections;
using Acta.Relational.Resources;
using Acta.Relational.Stores;

namespace Acta;

/// <summary>
/// Builds the PostgreSQL external-outbox source store: a per-operation source connection, the inline
/// outbox SQL embedded in this package, and the shared <see cref="RelationalOutboxRelayStore"/>. The
/// source session is independent of the ledger's <c>IDbSession</c>/<c>ISqlDialect</c>; a PostgreSQL
/// worker may relay a source on any supported provider.
/// </summary>
internal static class PostgresOutboxSource
{
    public static IOutboxRelayStore CreateStore(string connectionString, string? schema, string table)
    {
        var dialect = new PostgresOutboxDialect();
        // options.Schema drives only routine dispatch (never reached on the inline-only outbox path); the
        // catalog schema drives the actual table qualification and stays null when no override is supplied,
        // so the claim/introspection SQL resolves the table through the connection's search_path.
        var options = new PostgresProviderOptions { ConnectionString = connectionString, Schema = schema ?? "public" };
        var catalog = new SqlResourceCatalog(typeof(PostgresOutboxDialect).Assembly, schema, table);
        return new RelationalOutboxRelayStore(new DbSession(options, dialect, catalog), dialect, schema, table);
    }
}

/// <summary>
/// Builds the PostgreSQL relay source store from the captured connection string and the source builder's
/// schema/table overrides. With no schema override the table reference is left unqualified so the
/// connection's search_path resolves it (matching EF's null-schema mapping); the default table is the
/// canonical <c>acta_outbox</c>. Registered by <c>source.UsePostgres(...)</c>; resolved lazily, so
/// construction never opens a connection.
/// </summary>
internal sealed class PostgresOutboxSourceStoreFactory(string connectionString) : IOutboxSourceStoreFactory
{
    public IOutboxRelayStore Create(string? schema, string? table) =>
        PostgresOutboxSource.CreateStore(connectionString, schema, table ?? "acta_outbox");
}
