namespace Acta;

/// <summary>
/// The one rendering of a name in Acta's schema. PostgreSQL and SQL Server qualify it with the schema, <c>acta.jobs</c>.
/// SQLite has no schemas, so there the schema is a prefix on every table, index and view Acta creates in the file, and
/// a statement names the object in <c>main</c>, <c>main.acta_jobs</c>, so a TEMP table of the same name on the
/// caller's connection cannot shadow it.
/// </summary>
internal static class SchemaQualifier
{
    /// <summary>The token SQL text writes for the schema.</summary>
    public const string Token = "{{schema}}";

    /// <summary>The name a statement gives the object <paramref name="name"/> of <paramref name="schema"/>.</summary>
    public static string Qualify(string dialectToken, string schema, string name) => Render($"{Token}.{name}", dialectToken, schema);

    /// <summary>The name a statement gives the object <paramref name="name"/> of <paramref name="schema"/>.</summary>
    public static string Qualify(DbProvider provider, string schema, string name) => Render($"{Token}.{name}", provider, schema);

    /// <summary>
    /// <paramref name="sql"/> with every token rendered: <c>{{schema}}.jobs</c> as <see cref="Qualify(string, string, string)"/>
    /// names it, and a bare <c>{{schema}}</c> as the schema's name, which SQLite text writes before an underscore where its
    /// grammar takes no qualifier: an index's table, a <c>REFERENCES</c> target, and a column's table.
    /// </summary>
    public static string Render(string sql, string dialectToken, string schema) =>
        Render(sql, string.Equals(dialectToken, "sqlite", StringComparison.Ordinal), schema);

    /// <inheritdoc cref="Render(string, string, string)"/>
    public static string Render(string sql, DbProvider provider, string schema) => Render(sql, provider == DbProvider.Sqlite, schema);

    private static string Render(string sql, bool prefixed, string schema) =>
        prefixed
            ? sql.Replace(Token + ".", "main." + schema + "_", StringComparison.Ordinal).Replace(Token, schema, StringComparison.Ordinal)
            : sql.Replace(Token, schema, StringComparison.Ordinal);
}
