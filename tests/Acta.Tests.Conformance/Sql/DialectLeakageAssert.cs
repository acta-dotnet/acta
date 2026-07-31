using System.Text.RegularExpressions;
using Xunit;

namespace Acta.Tests.Conformance.Sql;

/// <summary>
/// Guards that a dialect's embedded SQL never leaks another provider's syntax (a pg file with T-SQL's
/// <c>TOP</c>/bracket-quoting, an mssql file with pg's <c>::</c> cast or <c>RETURNING</c>, etc.). Runs
/// per dialect against <see cref="ProviderSqlResources.EnumerateIncludingViews"/>: views included,
/// unlike most other SQL-policy checks, because a view is exactly where a copy-pasted foreign fragment
/// tends to survive unnoticed. Because <see cref="SqlResolver"/> resolution already picks the most
/// specific physical file for the dialect being scanned, a dialect-neutral (bare, unsuffixed) file is
/// automatically re-scanned under every provider's own banned-token table across the three provider test
/// projects: together equivalent to banning the union of every provider-specific token in shared files,
/// with no extra bookkeeping needed here.
/// </summary>
public static partial class DialectLeakageAssert
{
    // {{schema}}/{{now}}/{{decode:kind:expr}} are template placeholders substituted at render time
    // (see SqlResolver.Render); ProviderSqlResources reads the raw, unsubstituted text, so they must be
    // blanked before token scanning or a decode-kind name could theoretically collide with a token.
    private static readonly Regex TemplateToken = MyRegex();

    private static readonly (string Token, Regex Pattern)[] PgBanned =
    [
        ("NVARCHAR", Word("NVARCHAR")),
        ("DATEADD", Word("DATEADD")),
        ("GETUTCDATE", Word("GETUTCDATE")),
        ("TOP", Top()),
        ("ISNULL(", new(@"\bISNULL\s*\(", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("[bracket-quoted identifier]", BracketIdentifier()),
        ("OUTPUT INSERTED", new(@"\bOUTPUT\s+INSERTED\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("HOLDLOCK", Word("HOLDLOCK")),
        ("READPAST", Word("READPAST")),
    ];

    private static readonly (string Token, Regex Pattern)[] MssqlBanned =
    [
        ("LIMIT n", new(@"\bLIMIT\s+\d+", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("ILIKE", Word("ILIKE")),
        ("make_interval", Word("make_interval")),
        ("unnest", Word("unnest")),
        ("ON CONFLICT", new(@"\bON\s+CONFLICT\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("RETURNING", Word("RETURNING")),
        ("::", new(@"::", RegexOptions.Compiled)),
        ("now()", new(@"\bnow\s*\(", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
    ];

    private static readonly (string Token, Regex Pattern)[] SqliteBanned =
    [
        ("NVARCHAR", Word("NVARCHAR")),
        ("DATEADD", Word("DATEADD")),
        ("make_interval", Word("make_interval")),
        ("unnest", Word("unnest")),
        ("TOP", Top()),
        ("OUTPUT INSERTED", new(@"\bOUTPUT\s+INSERTED\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("HOLDLOCK", Word("HOLDLOCK")),
    ];

    /// <summary>Verified-deliberate exceptions: (logical path, token, one-line reason). Starts empty.</summary>
    private static readonly (string Path, string Token, string Reason)[] Allowlist = [];

    public static void AssertNoForeignDialectTokens(string dialectToken)
    {
        var failures = new List<string>();
        foreach (var (logicalPath, rawSql) in ProviderSqlResources.EnumerateIncludingViews(dialectToken))
        {
            failures.AddRange(ScanContent(dialectToken, logicalPath, rawSql));
        }

        if (failures.Count > 0)
        {
            Assert.Fail(string.Join("\n", failures));
        }
    }

    /// <summary>
    /// Per-content scan, factored out of <see cref="AssertNoForeignDialectTokens"/> and exposed
    /// internally (InternalsVisibleTo → Acta.Tests) so unit tests can feed synthetic SQL directly and
    /// prove the scanner actually fails, without going through embedded resources.
    /// </summary>
    internal static IEnumerable<string> ScanContent(string dialectToken, string logicalPath, string rawSql)
    {
        var banned = dialectToken switch
        {
            "pg" => PgBanned,
            "mssql" => MssqlBanned,
            "sqlite" => SqliteBanned,
            _ => throw new InvalidOperationException($"No banned-token table for dialect '{dialectToken}'."),
        };

        var masked = SqlCommentPolicyAssert.MaskStringsAndComments(rawSql);
        var scanText = TemplateToken.Replace(masked, static m => new string(' ', m.Length));

        foreach (var (token, pattern) in banned)
        {
            var match = pattern.Match(scanText);
            if (!match.Success)
            {
                continue;
            }

            if (Array.Exists(Allowlist, a => a.Path == logicalPath && a.Token == token))
            {
                continue;
            }

            var line = 1;
            for (var i = 0; i < match.Index; i++)
            {
                if (scanText[i] == '\n')
                {
                    line++;
                }
            }

            yield return $"{logicalPath}:{line}: foreign-dialect token '{token}' (matched '{match.Value}') is not valid {dialectToken} SQL";
        }
    }

    private static Regex Word(string keyword) => new($@"\b{Regex.Escape(keyword)}\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // TOP\s+\d | TOP\s*\( per the plan's forbidden-token table.
    private static Regex Top() => new(@"\bTOP\s*(\(|\d)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // A bracketed single identifier with nothing else inside ([order], [Group]) is T-SQL bracket-quoting.
    // pg's own array syntax (VARCHAR[], ARRAY[]::INT[]) always leaves the brackets empty or numeric, so
    // it never matches this shape.
    private static Regex BracketIdentifier() => new(@"\[[A-Za-z_][A-Za-z0-9_]*\]", RegexOptions.Compiled);

    [GeneratedRegex(@"\{\{[^}]*\}\}", RegexOptions.Compiled)]
    private static partial Regex MyRegex();
}
