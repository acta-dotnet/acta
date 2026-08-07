using System.Text.RegularExpressions;
using Xunit;

namespace Acta.Tests.Conformance.Sql;

/// <summary>
/// Guards the objective, dialect-neutral SQL style rules a reviewer should never have to police by
/// hand: no tabs, no trailing whitespace, and uppercase statement keywords. Layout beyond that
/// (the block style: clause rail at statement indent, one item per line in wide insert bodies) is
/// documented in CONTRIBUTING.md and maintained by eye; these rules are the machine-checkable
/// floor. Runs per dialect against <see cref="ProviderSqlResources.EnumerateIncludingViews"/> like
/// <see cref="DialectLeakageAssert"/>.
/// </summary>
public static partial class SqlStyleAssert
{
    public static void AssertStyle(string dialectToken)
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
    /// Per-content scan, exposed internally (InternalsVisibleTo -> Acta.Tests) so unit tests can
    /// feed synthetic SQL directly and prove the scanner actually fails.
    /// </summary>
    internal static IEnumerable<string> ScanContent(string logicalPath, string rawSql)
    {
        // Keywords are scanned on masked text so a lowercase keyword inside a string literal or
        // comment (error messages, prose) never trips the rule; masking preserves offsets.
        var masked = SqlCommentPolicyAssert.MaskStringsAndComments(rawSql);
        var line = 1;
        foreach (var (raw, scan) in rawSql.Split('\n').Zip(masked.Split('\n')))
        {
            var content = raw.TrimEnd('\r');
            if (content.Contains('\t', StringComparison.Ordinal))
            {
                yield return $"{logicalPath}:{line}: tab character (indent with spaces)";
            }
            if (content.Length > 0 && char.IsWhiteSpace(content[^1]))
            {
                yield return $"{logicalPath}:{line}: trailing whitespace";
            }
            var keyword = LowercaseClauseKeyword().Match(scan);
            if (keyword.Success)
            {
                yield return $"{logicalPath}:{line}: lowercase keyword '{keyword.Value.Trim()}' (keywords are UPPERCASE)";
            }
            line++;
        }
    }

    // Statement/clause keywords at the start of a line (optionally behind opening parens),
    // lowercase. Deliberately line-anchored: an identifier can legally be named "values" or
    // "update" mid-expression, but a clause keyword opening a line is unambiguous.
    [GeneratedRegex(
        @"^[\s(]*(select|from|where|update|insert|delete|set|values|returning|order by|group by|having|inner join|left join|join|union|create|drop|with|and|or|case|when|then|else|end|on|declare|begin|exec|limit)\b",
        RegexOptions.Compiled
    )]
    private static partial Regex LowercaseClauseKeyword();
}
