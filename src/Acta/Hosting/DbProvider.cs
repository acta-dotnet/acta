namespace Acta;

/// <summary>
/// Identifies which durable provider package is backing the runtime. Surfaced by <c>IDbSession</c>
/// and the test read helper for the rare case where code must branch on provider behavior.
/// </summary>
public enum DbProvider : byte
{
    /// <summary>SQL Server (<c>Acta.SqlServer</c>).</summary>
    SqlServer = 1,

    /// <summary>PostgreSQL (<c>Acta.Postgres</c>).</summary>
    Postgres = 2,

    /// <summary>SQLite (<c>Acta.Sqlite</c>); embedded, single-node, inline SQL.</summary>
    Sqlite = 3,
}
