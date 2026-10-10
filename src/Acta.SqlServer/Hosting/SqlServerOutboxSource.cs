namespace Acta.SqlServer;

/// <summary>
/// Builds the SQL Server external-outbox source's connection: a per-operation source session, the inline
/// outbox SQL embedded in this package, and the outbox dialect, which <see cref="IOutboxOperations.OpenSource"/>
/// opens as the source. The source session is independent of the ledger's <c>IDbSession</c>/<c>ISqlDialect</c>.
/// </summary>
internal static class SqlServerOutboxSource
{
    public static OutboxSourceConnection CreateConnection(string connectionString, string? schema, string table)
    {
        var dialect = new SqlServerOutboxDialect();
        // options.Schema drives only routine dispatch (never reached on the inline-only outbox path); the
        // catalog schema drives the actual table qualification and stays null when no override is supplied,
        // so the claim/introspection SQL resolves the table through the login's default schema.
        var options = new SqlServerProviderOptions { ConnectionString = connectionString, Schema = schema ?? "dbo" };
        var catalog = new SqlResourceCatalog(dialect.DialectToken, schema, table);
        return new(new DbSession(options, dialect, catalog), dialect);
    }
}

/// <summary>
/// Builds the SQL Server relay source's connection from the captured connection string and the source builder's
/// schema/table overrides. With no schema override the table reference is left unqualified so the login's
/// default schema resolves it (matching EF's null-schema mapping); the default table is the canonical
/// <c>acta_outbox</c>. Registered by <c>source.UseSqlServer(...)</c>; resolved lazily, so construction
/// never opens a connection.
/// </summary>
internal sealed class SqlServerOutboxSourceFactory(string connectionString) : IOutboxSourceFactory
{
    public OutboxSourceConnection Create(string? schema, string? table) =>
        SqlServerOutboxSource.CreateConnection(connectionString, schema, table ?? "acta_outbox");
}
