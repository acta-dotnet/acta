namespace Acta.Emit.Features.Docs;

/// <summary>
/// Text normalization helpers shared by the documentation emitters.
/// </summary>
internal static class MarkdownText
{
    // Returns -1 when no sentence-ending period exists. Skips periods inside backtick code spans.
    public static int FirstSentenceEnd(string s)
    {
        var inCode = false;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '`')
            {
                inCode = !inCode;
                continue;
            }
            if (inCode)
            {
                continue;
            }
            if (c == '.' && (i == s.Length - 1 || char.IsWhiteSpace(s[i + 1])))
            {
                return i;
            }
        }
        return -1;
    }

    // Normalize whitespace, truncate at first sentence end, cap at 280 chars. Null on empty input.
    public static string? FirstSentenceOrNull(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return null;
        }
        var s = summary.Replace("\r", " ").Replace("\n", " ").Trim();
        var idx = FirstSentenceEnd(s);
        if (idx > 0)
        {
            s = s[..(idx + 1)];
        }
        if (s.Length > 280)
        {
            s = s[..277] + "...";
        }
        return s;
    }
}
