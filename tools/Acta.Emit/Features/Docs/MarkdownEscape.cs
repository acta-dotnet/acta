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
