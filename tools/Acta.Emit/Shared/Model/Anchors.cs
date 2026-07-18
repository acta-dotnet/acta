namespace Acta.Emit.Shared.Model;

/// <summary>
/// Stable cross-renderer anchor IDs and cross-doc links for the generated docs.
/// </summary>
internal static class Anchors
{
    public static string Entity(string tableName) => $"entity-acta-{Slug(tableName)}";

    // Table and column are joined with a double hyphen. A single '-' is ambiguous: Slug maps '_' to
    // '-', so job.tenant_id and tenants.id would both collapse to column-acta-job-tenant-id and the
    // two anchors would collide. '--' never occurs inside a slug (Slug collapses runs and trims ends),
    // so the boundary — and thus the (table, column) pair — is unique.
    public static string Column(string table, string column) => $"column-acta-{Slug(table)}--{Slug(column)}";

    public static string Index(string indexName) => $"index-{Slug(indexName)}";

    public static string Constraint(string ckName) => $"constraint-{Slug(ckName)}";

    public static string ForeignKey(string fkName) => $"fk-{Slug(fkName)}";

    public static string CodeFamily(string familyName) => $"code-family-{Slug(familyName)}";

    // Same double-hyphen boundary rule as Column: family and code each slug independently and may
    // contain '-', so '--' keeps the (family, code) pair unambiguous.
    public static string CodeValue(string familyName, string codeName) => $"code-{Slug(familyName)}--{Slug(codeName)}";

    public static string Tag(string id) => $"<a id=\"{id}\"></a>";

    public static string LinkEntityLocal(string tableName, string label) => $"[`{label}`](#{Entity(tableName)})";

    public static string LinkColumnToDataModel(string table, string column) =>
        $"[`{table}.{column}`](./data-model.md#{Column(table, column)})";

    public static string LinkCodeFamilyLocal(string familyName) => $"[`{familyName}`](#{CodeFamily(familyName)})";

    public static string LinkCodeFamilyToCodeFamilies(string familyName) =>
        $"[`{familyName}`](./code-families.md#{CodeFamily(familyName)})";

    private static string Slug(string s)
    {
        var lower = s.ToLowerInvariant();
        var chars = new List<char>(lower.Length);
        foreach (var c in lower)
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                chars.Add(c);
            }
            else if (c is '_' or '-' or ' ' or '.')
            {
                chars.Add('-');
            }
        }
        var sb = new System.Text.StringBuilder(chars.Count);
        char? last = null;
        foreach (var c in chars)
        {
            if (c == '-' && last == '-')
            {
                continue;
            }
            sb.Append(c);
            last = c;
        }
        return sb.ToString().Trim('-');
    }
}
