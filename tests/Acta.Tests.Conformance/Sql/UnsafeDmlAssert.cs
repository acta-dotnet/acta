using System.Text.RegularExpressions;
using Xunit;

namespace Acta.Tests.Conformance.Sql;

/// <summary>
/// Guards two accidental-blast-radius shapes in embedded <c>UPDATE</c>/<c>DELETE</c> statements: a
/// statement with neither a <c>WHERE</c> nor a correlating <c>JOIN</c> (so it touches every row of a
/// real table), and a comma-style <c>FROM</c>/<c>USING</c> join (implicit cross join) instead of an
/// explicit <c>JOIN ... ON</c>. Views are included (same reasoning as <see cref="DialectLeakageAssert"/>
/// — a view is exactly where an unreviewed fragment survives). A statement whose target is an mssql
/// table variable (<c>DELETE @del</c>) is exempt: it clears local scratch state, never persisted data. A
/// <c>FOR UPDATE</c> row-locking clause and an <c>ON CONFLICT (...) DO UPDATE SET</c> upsert are not
/// standalone DML statements and are skipped.
/// </summary>
public static class UnsafeDmlAssert
{
    private static readonly Regex DmlStart = new(@"\b(UPDATE|DELETE)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Target = new(
        @"^\s*(FROM\s+)?(?<target>[@A-Za-z_][A-Za-z0-9_.]*)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    // The tokenizer only checks for the presence of a JOIN keyword — it cannot evaluate ON predicates,
    // so a constant-true join (e.g. "ON 1=1") still counts as a guard here. Accepted limitation.
    private static readonly Regex WhereOrJoin = new(@"\b(WHERE|JOIN)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex FromOrUsing = new(@"\b(FROM|USING)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ClauseBoundary = new(
        @"\b(WHERE|ORDER|GROUP|HAVING|RETURNING|OUTPUT)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    /// <summary>(logical path, rule, one-line reason). Rule is "no-guard" or "comma-join". Starts empty.</summary>
    private static readonly (string Path, string Rule, string Reason)[] Allowlist = [];

    public static void AssertGuardedDml(string dialectToken)
    {
        var failures = new List<string>();
        foreach (var (logicalPath, rawSql) in ProviderSqlResources.EnumerateIncludingViews(dialectToken))
        {
            failures.AddRange(ScanContent(logicalPath, rawSql));
        }

        if (failures.Count > 0)
        {
            Assert.Fail(string.Join("\n", failures));
        }
    }

    /// <summary>
    /// Per-content scan, factored out of <see cref="AssertGuardedDml"/> and exposed internally
    /// (InternalsVisibleTo → Acta.Tests) so unit tests can feed synthetic SQL directly and prove the
    /// scanner actually fails, without going through embedded resources.
    /// </summary>
    internal static IEnumerable<string> ScanContent(string logicalPath, string rawSql)
    {
        var masked = SqlCommentPolicyAssert.MaskStringsAndComments(rawSql);

        foreach (Match start in DmlStart.Matches(masked))
        {
            if (IsPrecededByFor(masked, start.Index))
            {
                continue; // "FOR UPDATE [OF ...]" row-locking clause, not a DML statement
            }

            var stmt = ExtractStatement(masked, start.Index);
            var targetMatch = Target.Match(stmt[start.Length..]);
            if (targetMatch.Success && targetMatch.Groups["target"].Value.StartsWith('@'))
            {
                continue; // mssql table variable — local scratch state, not persisted data
            }

            var line = LineOf(masked, start.Index);

            if (!WhereOrJoin.IsMatch(TopLevel(stmt)) && !IsAllowlisted(logicalPath, "no-guard"))
            {
                yield return $"{logicalPath}:{line}: {start.Value} statement has neither WHERE nor a correlating JOIN — touches every row";
            }

            if (HasCommaJoin(stmt) && !IsAllowlisted(logicalPath, "comma-join"))
            {
                yield return $"{logicalPath}:{line}: {start.Value} statement's FROM/USING clause comma-joins instead of explicit JOIN";
            }
        }
    }

    private static bool IsAllowlisted(string path, string rule) => Array.Exists(Allowlist, a => a.Path == path && a.Rule == rule);

    // "FOR UPDATE [OF r] [SKIP LOCKED]" is a SELECT row-locking clause; "ON CONFLICT (...) DO UPDATE SET"
    // is an upsert whose scope is exactly the one conflicting row. Neither is a standalone DML statement.
    private static readonly string[] NonDmlPrecedingWords = ["FOR", "DO"];

    private static bool IsPrecededByFor(string text, int idx)
    {
        var end = idx - 1;
        while (end >= 0 && char.IsWhiteSpace(text[end]))
        {
            end--;
        }

        var start = end;
        while (start >= 0 && (char.IsLetterOrDigit(text[start]) || text[start] == '_'))
        {
            start--;
        }

        if (start == end)
        {
            return false; // no word found before the keyword
        }

        var word = text[(start + 1)..(end + 1)];
        return Array.Exists(NonDmlPrecedingWords, w => string.Equals(w, word, StringComparison.OrdinalIgnoreCase));
    }

    // From the DML keyword, collects text until the statement's own terminating top-level ';' or the
    // point just before an enclosing ')' (a CTE-embedded "updated AS ( UPDATE ... )" ends there instead).
    private static string ExtractStatement(string text, int startIndex)
    {
        int i = startIndex,
            n = text.Length,
            depth = 0;
        while (i < n)
        {
            var c = text[i];
            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                if (depth == 0)
                {
                    break;
                }

                depth--;
            }
            else if (c == ';' && depth == 0)
            {
                break;
            }

            i++;
        }

        return text[startIndex..i];
    }

    // The statement text restricted to paren-depth-0 characters (subquery/function-arg content blanked),
    // so a WHERE/JOIN/comma nested inside a scalar subquery never satisfies or trips the top-level checks.
    private static string TopLevel(string stmt)
    {
        var buf = new char[stmt.Length];
        var depth = 0;
        for (var i = 0; i < stmt.Length; i++)
        {
            var c = stmt[i];
            if (c == '(')
            {
                depth++;
                buf[i] = ' ';
                continue;
            }

            if (c == ')')
            {
                depth = Math.Max(0, depth - 1);
                buf[i] = ' ';
                continue;
            }

            buf[i] = depth == 0 ? c : (c == '\n' ? '\n' : ' ');
        }

        return new string(buf);
    }

    // A comma at depth 0 within the FROM/USING clause (before the next top-level clause keyword) is an
    // old-style comma join; commas inside function args, array literals, or hint-list parens are already
    // masked out of scope by TopLevel/depth tracking.
    private static bool HasCommaJoin(string stmt)
    {
        var top = TopLevel(stmt);
        var fromMatch = FromOrUsing.Match(top);
        if (!fromMatch.Success)
        {
            return false;
        }

        var afterFrom = top[(fromMatch.Index + fromMatch.Length)..];
        var boundary = ClauseBoundary.Match(afterFrom);
        var span = boundary.Success ? afterFrom[..boundary.Index] : afterFrom;
        return span.Contains(',');
    }

    private static int LineOf(string text, int index)
    {
        var line = 1;
        for (var i = 0; i < index; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }
}
