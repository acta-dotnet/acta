using System.Data.Common;
using Acta.Configuration;
using Acta.Features.Outbox;
using Acta.Relational.Commands;
using Acta.Relational.Connections;
using Acta.Relational.Resources;
using Acta.Relational.Stores;
using Acta.SqlServer.Configuration;
using Acta.SqlServer.Services;

namespace Acta;

/// <summary>
/// Builds the SQL Server external-outbox source store: a per-operation source connection, the inline
/// outbox SQL embedded in this package, and the shared <see cref="RelationalOutboxRelayStore"/>. The
/// source session is independent of the ledger's <c>IDbSession</c>/<c>ISqlDialect</c>.
/// </summary>
internal static class SqlServerOutboxSource
{
    public static IOutboxRelayStore CreateStore(string connectionString, string? schema, string table)
    {
        var dialect = new SqlServerOutboxDialect();
        // options.Schema drives only routine dispatch (never reached on the inline-only outbox path); the
        // catalog schema drives the actual table qualification and stays null when no override is supplied,
        // so the claim/introspection SQL resolves the table through the login's default schema.
        var options = new SqlServerProviderOptions { ConnectionString = connectionString, Schema = schema ?? "dbo" };
        var catalog = new SqlResourceCatalog(typeof(SqlServerOutboxDialect).Assembly, schema, table);
        return new RelationalOutboxRelayStore(new DbSession(options, dialect, catalog), dialect, schema, table);
    }
}

/// <summary>
/// Builds the SQL Server relay source store from the captured connection string and the source builder's
/// schema/table overrides. With no schema override the table reference is left unqualified so the login's
/// default schema resolves it (matching EF's null-schema mapping); the default table is the canonical
/// <c>acta_outbox</c>. Registered by <c>source.UseSqlServer(...)</c>; resolved lazily, so construction
/// never opens a connection.
/// </summary>
internal sealed class SqlServerOutboxSourceStoreFactory(string connectionString) : IOutboxSourceStoreFactory
{
    public IOutboxRelayStore Create(string? schema, string? table) =>
        SqlServerOutboxSource.CreateStore(connectionString, schema, table ?? "acta_outbox");
}
