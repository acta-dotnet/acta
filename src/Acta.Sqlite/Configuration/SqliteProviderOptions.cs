using Acta.Relational.Connections;

namespace Acta.Sqlite.Configuration;

/// <summary>
/// Provider-specific options for the SQLite durable backend. The schema defaults to <c>main</c>;
/// SQLite has no CREATE SCHEMA, so all tables live in the connection's main database.
/// </summary>
public sealed class SqliteProviderOptions : SqlProviderOptions
{
    public SqliteProviderOptions()
    {
        Schema = "main";
    }
}
