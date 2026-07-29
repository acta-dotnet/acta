namespace Acta.Postgres.Configuration;

/// <summary>
/// Connection options for a PostgreSQL external-outbox source, captured on
/// <c>source.UsePostgres(...)</c>. Independent of the ledger's <c>PostgresProviderOptions</c>: the
/// relay may target a different database or provider than the worker's own ledger. Schema and table
/// overrides live on the source builder; nothing here connects at startup.
/// </summary>
public sealed class PostgresOutboxSourceOptions
{
    /// <summary>Connection string for the producer's PostgreSQL outbox database. Required.</summary>
    public string ConnectionString { get; set; } = "";
}
