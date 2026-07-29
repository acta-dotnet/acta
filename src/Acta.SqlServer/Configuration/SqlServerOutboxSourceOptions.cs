namespace Acta.SqlServer.Configuration;

/// <summary>
/// Connection options for a SQL Server external-outbox source, captured on
/// <c>source.UseSqlServer(...)</c>. Independent of the ledger's <c>SqlServerProviderOptions</c>; schema
/// and table overrides live on the source builder, and nothing here connects at startup.
/// </summary>
public sealed class SqlServerOutboxSourceOptions
{
    /// <summary>Connection string for the producer's SQL Server outbox database. Required.</summary>
    public string ConnectionString { get; set; } = "";
}
