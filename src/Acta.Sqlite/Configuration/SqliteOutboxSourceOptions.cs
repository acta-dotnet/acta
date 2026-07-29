namespace Acta.Sqlite.Configuration;

/// <summary>
/// Connection options for a SQLite external-outbox source, captured on
/// <c>source.UseSqlite(...)</c>. Independent of the ledger's <c>SqliteProviderOptions</c>; schema and
/// table overrides live on the source builder, and nothing here connects at startup.
/// </summary>
public sealed class SqliteOutboxSourceOptions
{
    /// <summary>Connection string for the producer's SQLite outbox database. Required.</summary>
    public string ConnectionString { get; set; } = "";
}
