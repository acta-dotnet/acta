using Xunit;

namespace Acta.Tests.Conformance.Sql;

/// <summary>
/// Enforces the executable-SQL comment policy: a provider-owned SQL file may carry
/// short comments - <c>--</c> lines (standalone or trailing) and <c>/* … */</c> blocks, including the
/// inline numeric drift markers - but a comment block may span at most <see cref="MaxCommentLines"/>
/// lines. Longer stacked prose is banned: bloated narrative belongs in the operation's C# XML doc, and
/// a few short multi-line notes near tricky SQL are fine. Versioned DDL migrations are out of scope.
/// </summary>
public static class SqlCommentPolicyAssert
{
    /// <summary>The most lines a single comment block (a `/* … */` or a run of `--` lines) may span.</summary>
    private const int MaxCommentLines = 3;

    public static void AssertOperationSqlHasNoLongCommentBlocks(string providerResourcePrefix)
    {
        var dialect = ProviderSqlResources.DialectFromPrefix(providerResourcePrefix);
        var failures = new List<string>();

        foreach (var (logicalPath, sql) in ProviderSqlResources.Enumerate(dialect))
        {
            Scan(logicalPath, sql, failures);
        }

        if (failures.Count > 0)
        {
            Assert.Fail(string.Join("\n", failures));
        }
    }

    /// <summary>
    /// Returns a same-length copy of <paramref name="sql"/> with every single-quoted string literal and
    /// every comment (<c>--</c> line, <c>/* … */</c> block) blanked to spaces: newlines preserved, so
    /// line numbers still line up. Shared by the dialect-leakage and unsafe-DML scanners so a quoted or
    /// commented-out token/keyword never false-positives; one string/comment walker instead of a
    /// half-parser per scanner.
    /// </summary>
    public static string MaskStringsAndComments(string sql)
    {
        var buf = new char[sql.Length];
        int i = 0,
            n = sql.Length;

        while (i < n)
        {
            var c = sql[i];
            var next = i + 1 < n ? sql[i + 1] : '\0';

            if (c == '\'')
            {
                buf[i++] = '\'';
                while (i < n && sql[i] != '\'')
                {
                    buf[i] = sql[i] == '\n' ? '\n' : ' ';
                    i++;
                }
                if (i < n)
                {
                    buf[i++] = '\'';
                }
                continue;
            }

            if (c == '-' && next == '-')
            {
                while (i < n && sql[i] != '\n')
                {
                    buf[i++] = ' ';
                }
                continue;
            }

            if (c == '/' && next == '*')
            {
                buf[i] = buf[i + 1] = ' ';
                i += 2;
                while (i < n && !(sql[i] == '*' && i + 1 < n && sql[i + 1] == '/'))
                {
                    buf[i] = sql[i] == '\n' ? '\n' : ' ';
                    i++;
                }
                if (i < n)
                {
                    buf[i] = ' ';
                    if (i + 1 < n)
                    {
                        buf[i + 1] = ' ';
                    }
                    i += 2;
                }
                continue;
            }

            buf[i] = c;
            i++;
        }

        return new string(buf);
    }

    // Single pass tracking normal / string-literal / line-comment / block-comment regions (so `--` or `/*`
    // inside a quoted string is never seen as a comment), flagging only multi-line comment blocks: a
    // `/* … */` that spans more than one line, or two-plus consecutive lines that are nothing but a `--`
    // comment. Single-line comments - standalone, trailing, or one-line block - are allowed.
    private static void Scan(string path, string sql, List<string> failures)
    {
        int i = 0,
            n = sql.Length,
            line = 1;
        var lineHasCode = false; // non-comment, non-whitespace content seen on the current line
        var commentOnlyLines = new SortedSet<int>();

        while (i < n)
        {
            var c = sql[i];
            var next = i + 1 < n ? sql[i + 1] : '\0';

            if (c == '\n')
            {
                line++;
                lineHasCode = false;
                i++;
                continue;
            }

            if (c == '\'')
            {
                lineHasCode = true;
                i++;
                while (i < n && sql[i] != '\'')
                {
                    if (sql[i] == '\n')
                    {
                        line++;
                        lineHasCode = false;
                    }
                    i++;
                }
                i++;
                continue;
            }

            if (c == '-' && next == '-')
            {
                // A comment that is the only content on its line is prose; a trailing comment after code is fine.
                if (!lineHasCode)
                {
                    commentOnlyLines.Add(line);
                }
                while (i < n && sql[i] != '\n')
                {
                    i++;
                }
                continue;
            }

            if (c == '/' && next == '*')
            {
                var startLine = line;
                i += 2;
                while (i + 1 < n && !(sql[i] == '*' && sql[i + 1] == '/'))
                {
                    if (sql[i] == '\n')
                    {
                        line++;
                    }
                    i++;
                }
                i = Math.Min(n, i + 2);
                if (line - startLine + 1 > MaxCommentLines)
                {
                    failures.Add(
                        $"{path}:{startLine}: '/* … */' comment block spans {line - startLine + 1} lines (max {MaxCommentLines}); trim it or move the prose to the op's C# XML doc."
                    );
                }
                lineHasCode = true;
                continue;
            }

            if (!char.IsWhiteSpace(c))
            {
                lineHasCode = true;
            }
            i++;
        }

        // Flag runs of two-plus consecutive comment-only lines (a stacked prose paragraph).
        int runStart = 0,
            prev = -2;
        foreach (var ln in commentOnlyLines)
        {
            if (ln != prev + 1)
            {
                FlushRun(path, runStart, prev, failures);
                runStart = ln;
            }
            prev = ln;
        }
        FlushRun(path, runStart, prev, failures);
    }

    private static void FlushRun(string path, int start, int end, List<string> failures)
    {
        if (start >= 1 && end - start + 1 > MaxCommentLines)
        {
            failures.Add(
                $"{path}:{start}: '--' comment block spans {end - start + 1} lines (max {MaxCommentLines}); trim it or move the prose to the op's C# XML doc."
            );
        }
    }
}
