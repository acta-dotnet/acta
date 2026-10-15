namespace Acta;

/// <summary>
/// Builds a relay source's connection from the captured connection string, the provider's outbox dialect, and the
/// provider's default schema, which each provider's <c>source.Use{Provider}</c> sets, so core names no provider. The
/// relay may target a different database or provider than the worker's own ledger.
/// Building opens nothing; the first source contact happens inside the relay tick.
/// </summary>
internal sealed class OutboxSourceFactory(string connectionString, ISqlDialect dialect, string defaultSchema)
{
    public OutboxSourceConnection Create(string? schema, string? table)
    {
        // options.Schema drives only routine dispatch, never reached on the inline-only outbox path; the catalog
        // schema qualifies the table and stays null when no override is given, so the SQL resolves the table through
        // the connection's default (search_path, the login's default schema, main).
        var options = new SqlProviderOptions { ConnectionString = connectionString, Schema = schema ?? defaultSchema };
        return new(new DbSession(options, dialect, new SqlResourceCatalog(dialect.DialectToken, schema, table ?? "acta_outbox")), dialect);
    }
}
