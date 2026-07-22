using System.Text.RegularExpressions;
using Xunit;

namespace Acta.Tests.Conformance.Sql;

/// <summary>
/// Guards that every declared routine parameter is actually referenced in the routine body: a dead
/// parameter is either leftover cruft or a sign the body silently stopped honoring an input it still
/// accepts. mssql: every <c>@p_*</c> in the header (before the first top-level <c>AS</c>) must appear at
/// least once after it. pg: every <c>p_*</c> in the <c>FUNCTION(...)</c> signature must appear in the
/// <c>$$</c>-delimited body. Inline (non-routine) files are exempt:
/// <see cref="SqlParameterCoverage"/> and the provider-store binding gate cover inline commands,
/// which have no separate header/body indirection to drift.
/// </summary>
public static class RoutineBodyParameterAssert
{
    private static readonly Regex MssqlHeaderBoundary = new(@"\bAS\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AtParam = new(@"@p_[A-Za-z0-9_]*", RegexOptions.Compiled);
    private static readonly Regex ParamName = new(@"^\s*([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);

    /// <summary>(logical path, parameter, one-line reason). Starts empty.</summary>
    private static readonly (string Path, string Parameter, string Reason)[] Allowlist = [];

    public static void AssertDeclaredParamsUsed(string dialectToken)
    {
        if (dialectToken != "mssql" && dialectToken != "pg")
        {
            return; // sqlite is inline-only: SqlResolver never resolves a ".routine.sql" body for it
        }

        var failures = new List<string>();
        foreach (var (logicalPath, rawSql) in ProviderSqlResources.Enumerate(dialectToken))
        {
            if (!logicalPath.EndsWith(".routine.sql", StringComparison.Ordinal))
            {
                continue;
            }

            failures.AddRange(ScanContent(dialectToken, logicalPath, rawSql));
        }

        if (failures.Count > 0)
        {
            Assert.Fail(string.Join("\n", failures));
        }
    }

    /// <summary>
    /// Per-content scan, factored out of <see cref="AssertDeclaredParamsUsed"/> and exposed internally
    /// (InternalsVisibleTo → Acta.Tests) so unit tests can feed synthetic SQL directly and prove the
    /// scanner actually fails, without going through embedded resources.
    /// </summary>
    internal static IEnumerable<string> ScanContent(string dialectToken, string logicalPath, string rawSql)
    {
        var masked = SqlCommentPolicyAssert.MaskStringsAndComments(rawSql);
        var (declared, body) = dialectToken == "mssql" ? SplitMssql(masked) : SplitPg(masked);

        foreach (var name in declared)
        {
            if (Array.Exists(Allowlist, a => a.Path == logicalPath && a.Parameter == name))
            {
                continue;
            }

            // "@" is not a word character, so a leading \b only makes sense for the bare pg form
            // (a leading \b before "@..." would wrongly require a word char immediately before it).
            var pattern = name.StartsWith('@') ? $@"{Regex.Escape(name)}\b" : $@"\b{Regex.Escape(name)}\b";
            var used = Regex.IsMatch(body, pattern, RegexOptions.IgnoreCase);
            if (!used)
            {
                yield return $"{logicalPath}: parameter '{name}' is declared but never referenced in the routine body";
            }
        }
    }

    // mssql: the parameter list carries no parens in T-SQL CREATE PROCEDURE syntax, so the first
    // whole-word "AS" is unambiguously the header/body boundary. Body is everything after it.
    // A parameter default value containing the literal token "AS" would truncate the header early;
    // no current routine's default does this.
    private static (IReadOnlyList<string> Declared, string Body) SplitMssql(string masked)
    {
        var boundary = MssqlHeaderBoundary.Match(masked);
        var header = boundary.Success ? masked[..boundary.Index] : masked;
        var body = boundary.Success ? masked[(boundary.Index + boundary.Length)..] : "";
        var declared = AtParam.Matches(header).Select(m => m.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return (declared, body);
    }

    // pg: parameters are the leading identifier of each top-level item in the "FUNCTION name(...)"
    // parameter list (the file's first '(': nothing before it in a CREATE FUNCTION header can carry
    // one); the body is the text between the first and second "$$" dollar-quote delimiters.
    private static (IReadOnlyList<string> Declared, string Body) SplitPg(string masked)
    {
        var open = masked.IndexOf('(');
        var close = MatchingParen(masked, open);
        var paramList = open >= 0 && close > open ? masked[(open + 1)..close] : "";

        var declared = new List<string>();
        foreach (var item in SplitTopLevel(paramList))
        {
            var m = ParamName.Match(item);
            if (m.Success)
            {
                declared.Add(m.Groups[1].Value);
            }
        }

        var firstDollar = masked.IndexOf("$$", StringComparison.Ordinal);
        var secondDollar = firstDollar >= 0 ? masked.IndexOf("$$", firstDollar + 2, StringComparison.Ordinal) : -1;
        var body = firstDollar >= 0 && secondDollar > firstDollar ? masked[(firstDollar + 2)..secondDollar] : "";

        return (declared, body);
    }

    private static int MatchingParen(string s, int open)
    {
        if (open < 0)
        {
            return -1;
        }

        var depth = 0;
        for (var i = open; i < s.Length; i++)
        {
            if (s[i] == '(')
            {
                depth++;
            }
            else if (s[i] == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static IEnumerable<string> SplitTopLevel(string s)
    {
        int start = 0,
            depth = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                yield return s[start..i];
                start = i + 1;
            }
        }

        yield return s[start..];
    }
}
