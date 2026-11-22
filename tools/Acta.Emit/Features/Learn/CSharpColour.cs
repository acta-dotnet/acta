using System.Text;
using System.Text.RegularExpressions;

namespace Acta.Emit.Features.Learn;

/// <summary>
/// C# as the site colours it: comments, strings, keywords, and attribute lines, each in a span of its own class. A
/// string or a comment that runs over several lines is coloured line by line, so any line can be banded by itself. A
/// string a job is stored under is marked with its kind, for the page to say that it is a contract. A setting of a job
/// or a schedule, and an option of an enqueue, is marked with its name, for the page to say what it means.
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

    /// <summary>The named arguments of <c>[Job]</c> and <c>[Schedule]</c> that the page explains.</summary>
    private static readonly HashSet<string> Settings = new(
        (
            "MaxAttempts Backoff ExecutionTimeout Deadline DeadlineBehavior JobRetention EventRecording ConcurrencyLimit RateLimit Format "
            + "TimeZoneId MisfireStrategy MaxOutstanding FullBehavior Calendar Availability"
        ).Split(' '),
        StringComparer.Ordinal
    );

    /// <summary>The enqueue options the page explains.</summary>
    private static readonly HashSet<string> Options = new(
        "IdempotencyKey Lane ConcurrencyKey Priority Delayed NextRunAt Tag TenantName CorrelationKey".Split(' '),
        StringComparer.Ordinal
    );

    public static string Esc(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);

    public static string Colour(string src) => Colour(src, []);

    /// <summary>
    /// <paramref name="src"/> coloured, with a setting span from each start in <paramref name="settings"/> to its end
    /// and around each enqueue option call.
    /// </summary>
    private static string Colour(string src, Dictionary<int, (int End, string Key)> settings)
    {
        var html = new StringBuilder();
        var i = 0;
        var lineStart = true;
        var close = -1;
        while (i < src.Length)
        {
            if (close >= 0 && i >= close)
            {
                html.Append("</span>");
                close = -1;
            }
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
                    if (StartsString(src, j))
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
                var attribute = src[(i + 1)..(j - 1)];
                var inner = "[" + Colour(attribute, AttributeSettings(attribute)) + "]";
                html.Append(string.Join('\n', inner.Split('\n').Select(line => $"<span class=\"a\">{line}</span>")));
                i = j;
                lineStart = false;
                continue;
            }
            lineStart = false;
            if (close < 0 && (settings.TryGetValue(i, out var setting) ? setting : OptionCall(src, i)) is { } hint)
            {
                html.Append("<span class=\"st\" data-st=\"").Append(hint.Key).Append("\" tabindex=\"0\">");
                close = hint.End;
            }
            if (StartsString(src, i))
            {
                var end = StringEnd(src, i);
                if (ContractKind(src, i) is { } kind && src.IndexOf('\n', i, end - i) < 0)
                {
                    html.Append("<span class=\"s ct\" data-ct=\"").Append(kind).Append("\" tabindex=\"0\">");
                    html.Append(Esc(src[i..end])).Append("</span>");
                }
                else
                {
                    Push(html, "s", src[i..end]);
                }
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
        if (close >= 0)
        {
            html.Append("</span>");
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

    /// <summary>
    /// The kind of stored name the string at <paramref name="at"/> is: the first argument of an attribute or a call
    /// that stores a job, or a part of one, under it. Null for every other string.
    /// </summary>
    private static string? ContractKind(string src, int at)
    {
        var i = at - 1;
        while (i >= 0 && char.IsWhiteSpace(src[i]))
        {
            i--;
        }
        if (i < 0 || src[i] != '(')
        {
            return null;
        }
        i--;
        var generic = i >= 0 && src[i] == '>';
        if (generic)
        {
            var depth = 0;
            while (i >= 0)
            {
                if (src[i] == '>')
                {
                    depth++;
                }
                else if (src[i] == '<' && --depth == 0)
                {
                    i--;
                    break;
                }
                i--;
            }
        }
        var end = i + 1;
        while (i >= 0 && IsWordChar(src[i]))
        {
            i--;
        }
        return src[(i + 1)..end] switch
        {
            "Job" => "job",
            "Run" or "Reference" when generic => "namespace",
            "RunStepAsync" or "RunStepWithCheckAsync" => "step",
            "WaitSignalAsync" => "signal",
            "SleepAsync" or "SleepUntilAsync" => "timer",
            "SetVariableAsync" or "GetOrSetVariableAsync" or "GetVariableAsync" or "TryGetVariableAsync" or "GetVariableOrDefaultAsync" =>
                "variable",
            "EnqueueChildAsync" or "CallChildAsync" or "MapAsync" => "child",
            _ => null,
        };
    }

    /// <summary>
    /// The settings among the arguments of a <c>Job</c> or <c>Schedule</c> attribute, by where each starts: a named
    /// argument the page explains, and a schedule's expression. One that runs over several lines is left unmarked.
    /// </summary>
    private static Dictionary<int, (int End, string Key)> AttributeSettings(string attribute)
    {
        var settings = new Dictionary<int, (int End, string Key)>();
        var call = SettingAttribute().Match(attribute);
        if (!call.Success)
        {
            return settings;
        }
        var (arguments, _) = Arguments(attribute, call.Length - 1);
        for (var n = 0; n < arguments.Count; n++)
        {
            var (start, end) = arguments[n];
            var text = attribute[start..end];
            var named = NamedArgument().Match(text);
            var key =
                named.Success ? (Settings.Contains(named.Groups[1].Value) ? Kebab(named.Groups[1].Value) : null)
                : n == 1 && call.Groups[1].Value == "Schedule" ? "expression"
                : null;
            if (key is not null && !text.Contains('\n', StringComparison.Ordinal))
            {
                settings[start] = (end, key);
            }
        }
        return settings;
    }

    /// <summary>
    /// The enqueue option whose name starts at <paramref name="at"/>, after a dot, through its closing parenthesis.
    /// Null for anything else, and for a call that runs over several lines.
    /// </summary>
    private static (int End, string Key)? OptionCall(string src, int at)
    {
        if (at == 0 || src[at - 1] != '.')
        {
            return null;
        }
        var open = at;
        while (open < src.Length && IsWordChar(src[open]))
        {
            open++;
        }
        if (open == src.Length || src[open] != '(' || !Options.Contains(src[at..open]))
        {
            return null;
        }
        var (_, end) = Arguments(src, open);
        return end > 0 && src.IndexOf('\n', at, end - at) < 0 ? (end, Kebab(src[at..open])) : null;
    }

    /// <summary>
    /// The arguments of the call whose parenthesis opens at <paramref name="open"/>, each trimmed, and the index just
    /// past its closing parenthesis, or -1 when it never closes. Strings and nested brackets are stepped over.
    /// </summary>
    private static (List<(int Start, int End)> Arguments, int End) Arguments(string src, int open)
    {
        var arguments = new List<(int Start, int End)>();
        var start = open + 1;
        var depth = 0;
        var j = start;
        while (j < src.Length)
        {
            if (StartsString(src, j))
            {
                j = StringEnd(src, j);
                continue;
            }
            var c = src[j];
            if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}' && depth > 0)
            {
                depth--;
            }
            else if (c is ',' or ')' && depth == 0)
            {
                var from = start;
                var to = j;
                while (from < to && char.IsWhiteSpace(src[from]))
                {
                    from++;
                }
                while (to > from && char.IsWhiteSpace(src[to - 1]))
                {
                    to--;
                }
                arguments.Add((from, to));
                if (c == ')')
                {
                    return (arguments, j + 1);
                }
                start = j + 1;
            }
            j++;
        }
        return (arguments, -1);
    }

    /// <summary>A setting's name as the page keys its card: <c>ExecutionTimeout</c> is <c>execution-timeout</c>.</summary>
    private static string Kebab(string name) => WordStart().Replace(name, "-$0").ToLowerInvariant();

    private static bool At(string src, int i, string text) => string.CompareOrdinal(src, i, text, 0, text.Length) == 0;

    private static bool IsWordChar(char c) => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_';

    /// <summary>
    /// Whether a string literal opens at <paramref name="i"/>: a quote, or a quote behind one or two of <c>$</c> and
    /// <c>@</c>.
    /// </summary>
    private static bool StartsString(string src, int i)
    {
        var j = i;
        while (j < src.Length && j < i + 2 && src[j] is '$' or '@')
        {
            j++;
        }
        return j < src.Length && src[j] == '"';
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

    [GeneratedRegex(@"^(Job|Schedule)\(")]
    private static partial Regex SettingAttribute();

    [GeneratedRegex(@"^([A-Z][A-Za-z0-9_]*)\s*=(?!=)")]
    private static partial Regex NamedArgument();

    [GeneratedRegex("(?<!^)[A-Z]")]
    private static partial Regex WordStart();
}
