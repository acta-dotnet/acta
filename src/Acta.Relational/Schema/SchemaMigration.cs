namespace Acta.Relational.Schema;

/// <summary>
/// One discovered migration's parsed version, display name, and <c>{{schema}}</c>-templated body.
/// </summary>
internal sealed record SchemaMigration(int Version, string Name, string Template)
{
    public string SubstituteSchema(string schemaName) => Template.Replace("{{schema}}", schemaName, StringComparison.Ordinal);
}
