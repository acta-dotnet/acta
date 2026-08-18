using System.Globalization;
using System.Text;

namespace Acta.Emit.Features.Docs;

/// <summary>
/// Markdown table-cell escape: pipes become <c>\|</c>, line breaks become spaces.
/// </summary>
internal static class MarkdownEscape
{
    public static string Cell(string? value) =>
        string.IsNullOrEmpty(value)
            ? ""
            : value
                .Replace("|", "\\|", StringComparison.Ordinal)
                .Replace("\r", " ", StringComparison.Ordinal)
                .Replace("\n", " ", StringComparison.Ordinal);
}

/// <summary>
/// Table-rendering primitives shared by the doc emitters.
/// </summary>
internal static class Tables
{
    public static void Header(StringBuilder sb, params string[] headers)
    {
        sb.Append("| ").Append(string.Join(" | ", headers)).AppendLine(" |");
        sb.Append('|');
        for (var i = 0; i < headers.Length; i++)
        {
            sb.Append("---|");
        }
        sb.AppendLine();
    }

    public static void Metadata(StringBuilder sb, string col1Header, string col2Header, params (string label, string value)[] rows)
    {
        Header(sb, col1Header, col2Header);
        foreach (var (label, value) in rows)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {label} | {value} |");
        }
        sb.AppendLine();
    }
}
