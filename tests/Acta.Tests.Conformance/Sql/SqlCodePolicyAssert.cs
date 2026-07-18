using System.Globalization;
using System.Text.RegularExpressions;
using Acta.Relational.Schema;
using Xunit;

namespace Acta.Tests.Conformance.Sql;

/// <summary>
/// Requires symbolic comments on numeric literals used as persisted code values or reviewed
/// transient C#↔SQL discriminators, then verifies every numeric constant comment against its
/// <c>Type.Member</c> value. Versioned migrations participate in value verification but not presence
/// scanning because their code checks are generated directly from the schema model.
/// </summary>
public static class SqlCodePolicyAssert
{
    private static readonly string[] CodeNames = BuildCodeNames();
    private static readonly string CodeToken = BuildCodeToken();

    private static readonly Regex PostgresReturnTable = new(
        @"\bRETURNS\s+TABLE\s*\((?<columns>[^;]*?)\)\s*(?:LANGUAGE|AS|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline
    );

    private static readonly Regex PostgresReturnQuery = new(
        @"\bRETURN\s+QUERY\s+SELECT\s+(?<items>.*?);",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline
    );

    private static readonly Regex ResultColumnName = new(
        @"^\s*(?<name>[a-z_][a-z0-9_]*)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase
    );

    private static readonly Regex DirectResultLiteral = new(
        @"^\s*(?:CAST\(\s*)?(?<value>-?\d+)(?<annotation>\s*/\*\s*[^*]+?\s*\*/)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase
    );

    private static readonly Regex InsertValues = new(
        @"\bINSERT\s+INTO\b[^;]*?\((?<columns>[^()]*)\)\s*(?:OUTPUT\b[^;]*?)?VALUES\s*(?<rows>.*?)(?=\bON\s+CONFLICT\b|\bRETURNING\b|;)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline
    );

    private static readonly Regex InsertSelect = new(
        @"\bINSERT\s+INTO\b[^;]*?\((?<columns>[^()]*)\)\s*SELECT\s+(?<items>.*?)(?=\bFROM\b|\bWHERE\b|\bON\s+CONFLICT\b|;)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline
    );

    private static readonly Regex Comparison = new(
        $@"(?<token>{CodeToken})\s*(?::=|=|<>|!=)\s*(?<value>-?\d+)(?<annotation>\s*/\*\s*[^*]+?\s*\*/)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase
    );

    private static readonly Regex ReverseComparison = new(
        $@"(?<value>-?\d+)(?<annotation>\s*/\*\s*[^*]+?\s*\*/)?\s*(?:=|<>|!=)\s*(?<token>{CodeToken})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase
    );

    private static readonly Regex InList = new(
        $@"(?<token>{CodeToken})\s+(?:NOT\s+)?IN\s*\((?<items>[^()]*)\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline
    );

    private static readonly Regex NumericItem = new(
        @"(?<![\w.])(?<value>-?\d+)(?<annotation>\s*/\*\s*[^*]+?\s*\*/)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant
    );

    private static readonly Regex CastResult = new(
        $@"CAST\(\s*(?<value>-?\d+)(?<annotation>\s*/\*\s*[^*]+?\s*\*/)?\s+AS\s+[^)]+\)\s+AS\s+(?<token>{CodeToken})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase
    );

    private static readonly Regex DirectAliasedResult = new(
        $@"(?<![\w.])(?<value>-?\d+)(?<annotation>\s*/\*\s*[^*]+?\s*\*/)?(?:\s*::\s*[a-z_][a-z0-9_]*)?\s+AS\s+(?<token>{CodeToken})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase
    );

    private static readonly Regex CaseResult = new(
        $@"CASE\b(?<body>(?:(?!\bCASE\b|\bEND\b).)*)\bEND\s+AS\s+(?<token>{CodeToken})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline
    );

    private static readonly Regex CaseResultLiteral = new(
        @"\b(?:THEN|ELSE)\s+(?<value>-?\d+)(?<annotation>\s*/\*\s*[^*]+?\s*\*/)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase
    );

    private static readonly Regex SimpleCodeCase = new(
        $@"CASE\s+(?<token>{CodeToken})\s+(?<body>.*?)\bEND(?:\s+CASE)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline
    );

    private static readonly Regex SimpleCaseLiteral = new(
        @"\bWHEN\s+(?<value>-?\d+)(?<annotation>\s*/\*\s*[^*]+?\s*\*/)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase
    );

    private static readonly Regex NumericComment = new(
        @"(?<![\w.])(?<value>-?\d+)\s*/\*\s*(?<comment>.*?)\s*\*/",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline
    );

    public static void AssertAllCodeConstantsAreValid(string dialectToken)
    {
        var failures = new List<string>();
        foreach (var resource in ProviderSqlResources.Enumerate(dialectToken, includeVersionedMigrations: true, includeViews: true))
        {
            failures.AddRange(ScanConstantAnnotations(resource.LogicalPath, resource.Sql));
            if (!resource.LogicalPath.StartsWith("Schema/Migrations/", StringComparison.Ordinal))
            {
                failures.AddRange(ScanRequiredAnnotations(resource.LogicalPath, resource.Sql));
            }
        }

        if (failures.Count > 0)
        {
            Assert.Fail(string.Join("\n", failures));
        }
    }

    internal static IReadOnlyList<string> ScanRequiredAnnotations(string logicalPath, string sql)
    {
        var failures = new List<string>();
        var reported = new HashSet<int>();

        foreach (Match match in Comparison.Matches(sql))
        {
            RequireAnnotation(logicalPath, sql, match.Groups["value"], match.Groups["annotation"], reported, failures);
        }

        foreach (Match match in ReverseComparison.Matches(sql))
        {
            RequireAnnotation(logicalPath, sql, match.Groups["value"], match.Groups["annotation"], reported, failures);
        }

        foreach (Match match in InList.Matches(sql))
        {
            foreach (Match item in NumericItem.Matches(match.Groups["items"].Value))
            {
                RequireAnnotation(
                    logicalPath,
                    sql,
                    item.Groups["value"],
                    item.Groups["annotation"],
                    reported,
                    failures,
                    match.Groups["items"].Index + item.Index
                );
            }
        }

        foreach (Match match in CastResult.Matches(sql))
        {
            RequireAnnotation(logicalPath, sql, match.Groups["value"], match.Groups["annotation"], reported, failures);
        }

        foreach (Match match in DirectAliasedResult.Matches(sql))
        {
            RequireAnnotation(logicalPath, sql, match.Groups["value"], match.Groups["annotation"], reported, failures);
        }

        foreach (Match resultCase in CaseResult.Matches(sql))
        {
            foreach (Match literal in CaseResultLiteral.Matches(resultCase.Groups["body"].Value))
            {
                RequireAnnotation(
                    logicalPath,
                    sql,
                    literal.Groups["value"],
                    literal.Groups["annotation"],
                    reported,
                    failures,
                    resultCase.Groups["body"].Index + literal.Groups["value"].Index
                );
            }
        }

        foreach (Match simpleCase in SimpleCodeCase.Matches(sql))
        {
            foreach (Match literal in SimpleCaseLiteral.Matches(simpleCase.Groups["body"].Value))
            {
                RequireAnnotation(
                    logicalPath,
                    sql,
                    literal.Groups["value"],
                    literal.Groups["annotation"],
                    reported,
                    failures,
                    simpleCase.Groups["body"].Index + literal.Groups["value"].Index
                );
            }
        }

        ScanPostgresPositionalResults(logicalPath, sql, reported, failures);
        ScanInsertValues(logicalPath, sql, reported, failures);

        return failures;
    }

    internal static IReadOnlyList<string> ScanConstantAnnotations(string logicalPath, string sql)
    {
        var failures = new List<string>();

        foreach (Match match in NumericComment.Matches(sql))
        {
            var actualText = match.Groups["value"].Value;
            var comment = match.Groups["comment"].Value.Trim();
            if (!ConstantCatalog.VerifiableConstantName.IsMatch(comment))
            {
                failures.Add($"{logicalPath}: {actualText} /* {comment} */ is not a verifiable Type.Member constant comment");
                continue;
            }

            if (!ConstantCatalog.CodeConstants.TryGetValue(comment, out var expected))
            {
                failures.Add($"{logicalPath}: unknown SQL constant symbol '{comment}'");
                continue;
            }

            var actual = int.Parse(actualText, CultureInfo.InvariantCulture);
            if (actual != expected)
            {
                failures.Add($"{logicalPath}: {actual} /* {comment} */ should be {expected}");
            }
        }

        return failures;
    }

    private static void ScanInsertValues(string logicalPath, string sql, ISet<int> reported, ICollection<string> failures)
    {
        foreach (Match insert in InsertValues.Matches(sql))
        {
            var columns = ReadInsertColumns(insert);
            var rows = insert.Groups["rows"];
            foreach (var row in SplitTopLevel(rows.Value))
            {
                var openParenthesis = row.Text.IndexOf('(');
                var closeParenthesis = row.Text.LastIndexOf(')');
                if (openParenthesis < 0 || closeParenthesis <= openParenthesis)
                {
                    continue;
                }

                var itemsStart = row.Start + openParenthesis + 1;
                var itemsText = row.Text[(openParenthesis + 1)..closeParenthesis];
                ScanInsertExpressions(logicalPath, sql, columns, rows.Index + itemsStart, itemsText, reported, failures);
            }
        }

        foreach (Match insert in InsertSelect.Matches(sql))
        {
            var columns = ReadInsertColumns(insert);
            var items = insert.Groups["items"];
            ScanInsertExpressions(logicalPath, sql, columns, items.Index, items.Value, reported, failures);
        }
    }

    private static string[] ReadInsertColumns(Match insert) =>
        SplitTopLevel(insert.Groups["columns"].Value)
            .Select(static part => ResultColumnName.Match(part.Text))
            .Select(static match => match.Success ? match.Groups["name"].Value : string.Empty)
            .ToArray();

    private static void ScanInsertExpressions(
        string logicalPath,
        string sql,
        IReadOnlyList<string> columns,
        int itemsIndex,
        string items,
        ISet<int> reported,
        ICollection<string> failures
    )
    {
        var expressions = SplitTopLevel(items);
        for (var index = 0; index < Math.Min(columns.Count, expressions.Count); index++)
        {
            if (IsCodeName(columns[index]))
            {
                ScanCodedExpression(logicalPath, sql, itemsIndex, expressions[index], reported, failures);
            }
        }
    }

    private static void ScanPostgresPositionalResults(string logicalPath, string sql, ISet<int> reported, ICollection<string> failures)
    {
        var returnTable = PostgresReturnTable.Match(sql);
        if (!returnTable.Success)
        {
            return;
        }

        var columns = SplitTopLevel(returnTable.Groups["columns"].Value)
            .Select(static part => ResultColumnName.Match(part.Text))
            .Select(static match => match.Success ? match.Groups["name"].Value : string.Empty)
            .ToArray();

        foreach (Match query in PostgresReturnQuery.Matches(sql))
        {
            var items = query.Groups["items"];
            var expressions = SplitTopLevel(items.Value);
            for (var index = 0; index < Math.Min(columns.Length, expressions.Count); index++)
            {
                if (!IsCodeName(columns[index]))
                {
                    continue;
                }

                ScanCodedExpression(logicalPath, sql, items.Index, expressions[index], reported, failures);
            }
        }
    }

    private static void ScanCodedExpression(
        string logicalPath,
        string sql,
        int itemsIndex,
        (int Start, string Text) expression,
        ISet<int> reported,
        ICollection<string> failures
    )
    {
        var direct = DirectResultLiteral.Match(expression.Text);
        if (direct.Success)
        {
            RequireAnnotation(
                logicalPath,
                sql,
                direct.Groups["value"],
                direct.Groups["annotation"],
                reported,
                failures,
                itemsIndex + expression.Start + direct.Groups["value"].Index
            );
        }

        foreach (Match literal in CaseResultLiteral.Matches(expression.Text))
        {
            RequireAnnotation(
                logicalPath,
                sql,
                literal.Groups["value"],
                literal.Groups["annotation"],
                reported,
                failures,
                itemsIndex + expression.Start + literal.Groups["value"].Index
            );
        }
    }

    private static void RequireAnnotation(
        string logicalPath,
        string sql,
        Group value,
        Group annotation,
        ISet<int> reported,
        ICollection<string> failures,
        int? absoluteIndex = null
    )
    {
        var index = absoluteIndex ?? value.Index;
        if (annotation.Success || !reported.Add(index))
        {
            return;
        }

        var line = 1 + sql.AsSpan(0, index).Count('\n');
        failures.Add($"{logicalPath}:{line}: code literal {value.Value} requires a symbolic /* Type.Member */ comment");
    }

    private static bool IsCodeName(string name)
    {
        var normalized = name.StartsWith("out_", StringComparison.OrdinalIgnoreCase) ? name[4..] : name;
        return CodeNames.Contains(normalized, StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<(int Start, string Text)> SplitTopLevel(string text)
    {
        var parts = new List<(int Start, string Text)>();
        var start = 0;
        var depth = 0;
        var inString = false;

        for (var index = 0; index < text.Length; index++)
        {
            var current = text[index];
            if (current == '\'' && (index + 1 >= text.Length || text[index + 1] != '\''))
            {
                inString = !inString;
                continue;
            }

            if (inString)
            {
                if (current == '\'' && index + 1 < text.Length && text[index + 1] == '\'')
                {
                    index++;
                }

                continue;
            }

            if (current == '(')
            {
                depth++;
            }
            else if (current == ')')
            {
                depth--;
            }
            else if (current == ',' && depth == 0)
            {
                parts.Add((start, text[start..index]));
                start = index + 1;
            }
        }

        parts.Add((start, text[start..]));
        return parts;
    }

    private static string[] BuildCodeNames()
    {
        return ActaSchema
            .Entities.SelectMany(static entity => entity.Columns)
            .Where(static column =>
                column.IsCoded || column.CodeKind is not null || column.Name.EndsWith("_format_id", StringComparison.Ordinal)
            )
            .Select(static column => column.Name)
            .Concat([
                "action",
                "outcome",
                "status",
                "state",
                "fmt",
                "rfid",
                "from_status",
                "to_status",
                "cur_status",
                "sig_state",
                "outcome_code",
                "p_action",
                "p_mutation",
            ])
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(static name => name.Length)
            .ToArray();
    }

    private static string BuildCodeToken()
    {
        var escapedNames = CodeNames.Select(Regex.Escape);
        return $@"(?:@?(?:{string.Join("|", escapedNames)})|[a-z_][a-z0-9_]*\.(?:{string.Join("|", escapedNames)}))";
    }
}
