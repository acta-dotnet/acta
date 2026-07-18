using System.Text.RegularExpressions;

namespace Acta.Relational.Resources;

internal static class CodeDecodeSql
{
    private static readonly Regex DecodeToken = new(@"\{\{decode:([a-z][a-z0-9.-]*):([^}]+)\}\}", RegexOptions.CultureInvariant);

    public static string RenderDecodeTokens(string sql) =>
        DecodeToken.Replace(sql, static match => Case(match.Groups[1].Value, match.Groups[2].Value));

    internal static string Case(string codeKind, string expression)
    {
        var entries = global::Acta
            .CodeManifests.All.Where(e => string.Equals(e.CodeKind, codeKind, StringComparison.Ordinal))
            .OrderBy(e => e.Id)
            .ToArray();
        if (entries.Length == 0)
        {
            throw new InvalidOperationException($"No code manifest entries exist for code kind '{codeKind}'.");
        }

        var arms = entries.Select(e =>
            $"WHEN {e.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)} THEN '{SqlLiteral(e.Code)}'"
        );
        return $"CASE {expression} {string.Join(" ", arms)} END";
    }

    private static string SqlLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}
