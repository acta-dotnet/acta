using System.Text;
using System.Text.RegularExpressions;

namespace Acta.Emit.Features.Learn;

/// <summary>
/// C# as the site colours it: comments, strings, keywords, and attribute lines, each in a span of its own class. A
/// string or a comment that runs over several lines is coloured line by line, so any line can be banded by itself.
/// </summary>
internal static partial class CSharpColour
{
    private static readonly HashSet<string> Keywords = new(
        (
            "abstract as async await base bool break byte case catch char class const continue decimal default do double else "
            + "enum false finally for foreach get if in init int interface internal is lock long namespace new not null object out override params "
            + "private protected public readonly record ref required return sealed set static string struct switch this throw true try typeof using "
            + "var virtual void when where while with yield"
        ).Split(' '),
        StringComparer.Ordinal
    );

    public static string Esc(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);

    public static string Colour(string src)
    {
        var html = new StringBuilder();
        var i = 0;
        var lineStart = true;
        while (i < src.Length)
        {
            var c = src[i];
            if (c == '\n')
            {
                html.Append('\n');
                lineStart = true;
                i++;
                continue;
            }
            if (c is ' ' or '\t')
            {
                html.Append(c);
                i++;
                continue;
            }
            if (At(src, i, "//"))
            {
                var end = src.IndexOf('\n', i);
                var stop = end < 0 ? src.Length : end;
                Push(html, "c", src[i..stop]);
                i = stop;
                continue;
            }
            if (At(src, i, "/*"))
            {
                var end = src.IndexOf("*/", i + 2, StringComparison.Ordinal);
                var stop = end < 0 ? src.Length : end + 2;
                Push(html, "c", src[i..stop]);
                i = stop;
                lineStart = false;
                continue;
            }
            if (lineStart && c == '[' && AttributeStart().IsMatch(src.AsSpan(i, Math.Min(40, src.Length - i))))
            {
                // an attribute: coloured whole, with its strings inside
                var j = i;
                var depth = 0;
                while (j < src.Length)
                {
                    if (StartsString(src, j, anyPrefix: true))
                    {
                        j = StringEnd(src, j);
                        continue;
                    }
                    if (src[j] == '[')
                    {
                        depth++;
                    }
                    if (src[j] == ']' && --depth == 0)
                    {
                        j++;
                        break;
                    }
                    j++;
                }
                var inner = "[" + Colour(src[(i + 1)..(j - 1)]) + "]";
                html.Append(string.Join('\n', inner.Split('\n').Select(line => $"<span class=\"a\">{line}</span>")));
                i = j;
                lineStart = false;
                continue;
            }
            lineStart = false;
            if (StartsString(src, i, anyPrefix: false))
            {
                var end = StringEnd(src, i);
                Push(html, "s", src[i..end]);
                i = end;
                continue;
            }
            if (c == '\'')
            {
                var literal = CharLiteral().Match(src, i);
                if (literal.Success && literal.Index == i)
                {
                    Push(html, "s", literal.Value);
                    i += literal.Length;
                    continue;
                }
            }
            if (c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or '_')
            {
                var end = i + 1;
                while (end < src.Length && IsWordChar(src[end]))
                {
                    end++;
                }
                var word = src[i..end];
                Push(html, Keywords.Contains(word) ? "k" : null, word);
                i = end;
                continue;
            }
            html.Append(Esc(c.ToString()));
            i++;
        }
        return html.ToString();
    }

    private static void Push(StringBuilder html, string? cls, string text)
    {
        if (cls is null)
        {
            html.Append(Esc(text));
            return;
        }
        html.Append(string.Join('\n', text.Split('\n').Select(line => $"<span class=\"{cls}\">{Esc(line)}</span>")));
    }

    private static bool At(string src, int i, string text) => string.CompareOrdinal(src, i, text, 0, text.Length) == 0;

    private static bool IsWordChar(char c) => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_';

    /// <summary>
    /// Whether a string literal opens at <paramref name="i"/>: a quote, or a quote behind one or two of <c>$</c> and
    /// <c>@</c>. Inside an attribute the scan is looser and takes a quote that is the third character too.
    /// </summary>
    private static bool StartsString(string src, int i, bool anyPrefix)
    {
        if (src[i] == '"')
        {
            return true;
        }
        if (src[i] is not ('$' or '@'))
        {
            return false;
        }
        var j = i;
        while (j < src.Length && j < i + 3 && src[j] is '$' or '@')
        {
            j++;
        }
        var prefix = j - i;
        return j < src.Length && j < i + 3 && src[j] == '"' && (anyPrefix || prefix is 1 or 2);
    }

    /// <summary>The index just past the string literal that opens at <paramref name="i"/>, prefixes included.</summary>
    private static int StringEnd(string src, int i)
    {
        var j = i;
        var interpolated = false;
        var verbatim = false;
        while (j < src.Length && src[j] is '$' or '@')
        {
            if (src[j] == '$')
            {
                interpolated = true;
            }
            else
            {
                verbatim = true;
            }
            j++;
        }
        if (At(src, j, "\"\"\""))
        {
            var end = src.IndexOf("\"\"\"", j + 3, StringComparison.Ordinal);
            return end < 0 ? src.Length : end + 3;
        }
        j++;
        var depth = 0;
        while (j < src.Length)
        {
            var c = src[j];
            if (interpolated && c == '{')
            {
                if (j + 1 < src.Length && src[j + 1] == '{' && depth == 0)
                {
                    j += 2;
                    continue;
                }
                depth++;
                j++;
                continue;
            }
            if (interpolated && c == '}' && depth > 0)
            {
                depth--;
                j++;
                continue;
            }
            if (depth > 0 && c == '"')
            {
                j = StringEnd(src, j);
                continue;
            }
            if (c == '\\' && !verbatim && depth == 0)
            {
                j += 2;
                continue;
            }
            if (c == '"' && depth == 0)
            {
                if (verbatim && j + 1 < src.Length && src[j + 1] == '"')
                {
                    j += 2;
                    continue;
                }
                return j + 1;
            }
            if (c == '\n' && !verbatim && depth == 0)
            {
                return j;
            }
            j++;
        }
        return src.Length;
    }

    [GeneratedRegex(@"^\[[A-Z][A-Za-z0-9_]*[(\],]")]
    private static partial Regex AttributeStart();

    [GeneratedRegex(@"\G'(?:\\.|[^'\\\n])'")]
    private static partial Regex CharLiteral();
}
