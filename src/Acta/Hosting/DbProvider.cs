namespace Acta;

/// <summary>The database the engine runs on.</summary>
/// <remarks>Surfaced by <c>IDbSession</c> and the test read helper, for the rare code that must branch on provider behavior.</remarks>
public enum DbProvider : byte
{
    /// <summary>SQL Server (<c>Acta.SqlServer</c>).</summary>
    SqlServer = 1,

    /// <summary>PostgreSQL (<c>Acta.Postgres</c>).</summary>
    Postgres = 2,

    /// <summary>SQLite (<c>Acta.Sqlite</c>); embedded, single-node, inline SQL.</summary>
    Sqlite = 3,
}
