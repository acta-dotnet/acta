using System.Text.RegularExpressions;
using Acta.Tests.Conformance.Sql;
using Xunit;

namespace Acta.Tests.Architecture;

/// <summary>
/// The SQL files of one operation must return the same columns in every dialect: a PostgreSQL routine's
/// <c>RETURNS TABLE</c>, otherwise the last result-producing <c>SELECT</c>, <c>RETURNING</c>, or <c>OUTPUT</c>.
/// Differences that predate the rule are listed in the <c>dialect-parity</c> ratchet, one entry per SQL stem.
/// </summary>
public sealed partial class DialectParityTests
{
    private const string Scalar = "(scalar)";

    private static readonly HashSet<string> Boundaries = new(["BEGIN", "END", "THEN", "ELSE", "GO", ";"], StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> NotResults = new(["INSERT", "MERGE", "TABLE"], StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ListEnds = new(
        [
            "FROM",
            "INTO",
            "WHERE",
            "GROUP",
            "ORDER",
            "HAVING",
            "UNION",
            "EXCEPT",
            "INTERSECT",
            "LIMIT",
            "OFFSET",
            "FOR",
            "WINDOW",
            "VALUES",
            "DEFAULT",
            "RETURN",
            "END",
            "ELSE",
            "IF",
            "SELECT",
            "INSERT",
            "UPDATE",
            "DELETE",
            "SET",
            "DECLARE",
            "BEGIN",
            "COMMIT",
            "OPTION",
            "GO",
            ";",
        ],
        StringComparer.OrdinalIgnoreCase
    );

    private static readonly HashSet<string> NotAliases = new(["END", "NULL", "TRUE", "FALSE"], StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Every_dialect_of_an_operation_returns_the_same_columns()
    {
        var columns = new Dictionary<string, SortedDictionary<string, List<string>>>(StringComparer.Ordinal);
        foreach (var (file, stem, dialect) in SqlFiles())
        {
            if (!columns.TryGetValue(stem, out var byDialect))
            {
                columns[stem] = byDialect = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
            }
            Assert.True(byDialect.TryAdd(dialect, ResultColumns(File.ReadAllText(file))), $"Two {dialect} files share the stem {stem}.");
        }

        var differences = columns
            .Where(entry => entry.Value.Count > 1 && !AllAgree(entry.Value.Values))
            .Select(entry => (entry.Key, (string?)string.Join(" ", entry.Value.Select(d => $"{d.Key}({string.Join(",", d.Value)})"))));

        Ratchet.Check("dialect-parity", differences, "Return the same column names, in the same order, from every dialect.");
    }

    // X.<dialect>.sql beside each operation; the schema commands and views are not operations.
    private static IEnumerable<(string File, string Stem, string Dialect)> SqlFiles()
    {
        foreach (var (path, relative) in ShippedCode.SourceFiles("*.sql"))
        {
            var parts = relative.Split('/');
            if (parts[1] == "Acta.Runtime" && parts[2] == "Operations" && OperationFile().Match(parts[^1]) is { Success: true } match)
            {
                yield return (path, match.Groups[1].Value, match.Groups[2].Value);
            }
        }
    }

    private static bool AllAgree(IEnumerable<List<string>> results)
    {
        var lists = results.ToList();
        return lists.All(list => Agree(lists[0], list));
    }

    private static bool Agree(List<string> a, List<string> b) =>
        a.SequenceEqual(b, StringComparer.Ordinal) || (a is [Scalar] && b.Count == 1) || (b is [Scalar] && a.Count == 1);

    private static List<string> ResultColumns(string sql)
    {
        var tokens = Tokens(sql);
        if (sql.Contains("CREATE OR REPLACE FUNCTION", StringComparison.OrdinalIgnoreCase))
        {
            var returns = tokens.FindIndex(t => t.Text.Equals("RETURNS", StringComparison.OrdinalIgnoreCase));
            return tokens[returns + 1].Text.ToUpperInvariant() switch
            {
                // A RETURNS TABLE column is an OUT parameter, which plpgsql resolves against a same-named table
                // column; routines prefix it out_ to keep the two apart, and the comparison drops the prefix.
                "TABLE" => [.. Items(tokens, returns + 3, stopAtListEnd: false).Select(item => Name(item[0].Text)).Select(OutParameter)],
                "VOID" => [],
                _ => [Scalar],
            };
        }

        List<string> result = [];
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Depth != 0)
            {
                continue;
            }
            var keyword = tokens[i].Text.ToUpperInvariant();
            if (keyword == "SELECT" && !InsideNonResultStatement(tokens, i) && SelectList(tokens, i) is { } list)
            {
                result = list;
            }
            else if (keyword == "RETURNING")
            {
                result = [.. Items(tokens, i + 1, stopAtListEnd: true).Select(Alias)];
            }
            else if (keyword == "OUTPUT" && SelectList(tokens, i) is { } output)
            {
                result = output;
            }
        }
        return result;
    }

    private static bool InsideNonResultStatement(List<Token> tokens, int select)
    {
        for (var i = select - 1; i >= 0 && !(tokens[i].Depth == 0 && Boundaries.Contains(tokens[i].Text)); i--)
        {
            if (tokens[i].Depth == 0 && NotResults.Contains(tokens[i].Text))
            {
                return true;
            }
        }
        return false;
    }

    // The list after SELECT or OUTPUT; null for SELECT ... INTO, OUTPUT ... INTO, and a T-SQL variable assignment, none of which returns rows.
    private static List<string>? SelectList(List<Token> tokens, int keyword)
    {
        var start = keyword + 1;
        while (start < tokens.Count && tokens[start].Text.ToUpperInvariant() is "DISTINCT" or "ALL")
        {
            start++;
        }
        if (start < tokens.Count && tokens[start].Text.Equals("TOP", StringComparison.OrdinalIgnoreCase))
        {
            start += tokens[start + 1].Text == "(" ? tokens.FindIndex(start + 1, t => t.Text == ")" && t.Depth == 0) - start + 1 : 2;
        }
        var items = Items(tokens, start, stopAtListEnd: true);
        // The token right after the list: its items plus the commas between them.
        var next = start + items.Sum(item => item.Count) + items.Count - 1;
        if (next < tokens.Count && tokens[next].Text.Equals("INTO", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        if (items is [[{ Text: var first }, { Text: "=" }, ..], ..] && first.StartsWith('@'))
        {
            return null;
        }
        return [.. items.Select(Alias)];
    }

    // Comma-separated items at the list's own depth, up to a clause keyword, or to the closing parenthesis.
    private static List<List<Token>> Items(List<Token> tokens, int start, bool stopAtListEnd)
    {
        var items = new List<List<Token>> { new() };
        var depth = start < tokens.Count ? tokens[start].Depth : 0;
        var openCases = 0;
        for (var i = start; i < tokens.Count; i++)
        {
            var token = tokens[i];
            var atListLevel = token.Depth == depth && openCases == 0;
            if (token.Depth < depth || (atListLevel && stopAtListEnd && ListEnds.Contains(token.Text)))
            {
                break;
            }
            if (token.Depth == depth && token.Text.Equals("CASE", StringComparison.OrdinalIgnoreCase))
            {
                openCases++;
            }
            else if (token.Depth == depth && openCases > 0 && token.Text.Equals("END", StringComparison.OrdinalIgnoreCase))
            {
                openCases--;
            }
            if (atListLevel && token.Text == ",")
            {
                items.Add([]);
                continue;
            }
            items[^1].Add(token);
        }
        return [.. items.Where(item => item.Count > 0)];
    }

    // The column a select-list item produces: its alias, the column it names, or "?" for an unnamed expression.
    private static string Alias(List<Token> item)
    {
        var last = item[^1];
        if (last.Text == "*")
        {
            return "*";
        }
        if (!IsWord(last.Text) || NotAliases.Contains(last.Text))
        {
            return "?";
        }
        if (item.Count == 1 || item[^2].Text is "." or ")" || IsWord(item[^2].Text) || item[^2].Text.StartsWith('\''))
        {
            return Name(last.Text);
        }
        return "?";
    }

    private static string Name(string identifier) => identifier.Trim('"', '[', ']').ToLowerInvariant();

    private static string OutParameter(string name) => name.StartsWith("out_", StringComparison.Ordinal) ? name["out_".Length..] : name;

    private static bool IsWord(string text) => text.Length > 0 && (char.IsLetter(text[0]) || text[0] is '_' or '"' or '[');

    private static List<Token> Tokens(string sql)
    {
        var clean = SqlCommentPolicyAssert.MaskStringsAndComments(sql);
        var tokens = new List<Token>();
        var depth = 0;
        foreach (Match match in TokenPattern().Matches(clean))
        {
            var text = match.Value;
            if (text == ")")
            {
                depth--;
            }
            tokens.Add(new Token(text, depth));
            if (text == "(")
            {
                depth++;
            }
        }
        return tokens;
    }

    private readonly record struct Token(string Text, int Depth);

    [GeneratedRegex(@"""[^""]*""|\[[^\]]*\]|[@#]?[A-Za-z_][A-Za-z0-9_$]*|\d+(?:\.\d+)?|\S")]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"^(.+)\.(pg|mssql|sqlite)\.sql$")]
    private static partial Regex OperationFile();
}
