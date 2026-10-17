using System.Text.RegularExpressions;
using Acta.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Acta.Tests.Storage;

/// <summary>
/// SQLite plans a statement without its parameter values, so the dialect folds each <c>(@p IS NULL OR x)</c>
/// catch-all against the bound value before the command runs: <c>(x)</c> when <c>@p</c> holds a value, <c>1</c> when not.
/// </summary>
public sealed class SqliteNullCatchAllTests
{
    [Theory]
    [InlineData("@p_a")]
    [InlineData("p_a")]
    public void A_set_parameter_keeps_its_filter(string boundAs) =>
        Assert.Equal("WHERE (a = @p_a) AND b = 2", Fold("WHERE (@p_a IS NULL OR a = @p_a) AND b = 2", (boundAs, 5)));

    [Fact]
    public void A_null_or_dbnull_parameter_folds_to_true()
    {
        const string sql = "WHERE (@p_a IS NULL OR a = @p_a) AND b = 2";

        Assert.Equal("WHERE 1 AND b = 2", Fold(sql, ("@p_a", null)));
        Assert.Equal("WHERE 1 AND b = 2", Fold(sql, ("@p_a", DBNull.Value)));
    }

    [Fact]
    public void An_unbound_parameter_leaves_its_catch_all_for_the_driver_to_refuse()
    {
        const string sql = "WHERE (@p_a IS NULL OR a = @p_a) AND (@p_b IS NULL OR b = @p_b)";

        Assert.Equal("WHERE (@p_a IS NULL OR a = @p_a) AND (b = @p_b)", Fold(sql, ("@p_b", 5)));
    }

    [Fact]
    public void A_chain_keeps_its_filter_only_when_every_parameter_is_set()
    {
        const string sql = "WHERE (@p_a IS NULL OR @p_b IS NULL OR x = @p_a + @p_b)";

        Assert.Equal("WHERE (x = @p_a + @p_b)", Fold(sql, ("@p_a", 1), ("@p_b", 2)));
        Assert.Equal("WHERE 1", Fold(sql, ("@p_a", 1), ("@p_b", DBNull.Value)));
        Assert.Equal("WHERE 1", Fold(sql, ("@p_a", DBNull.Value), ("@p_b", 2)));
    }

    [Fact]
    public void A_keyset_cursor_folds_as_one_filter_despite_its_own_parentheses_and_or()
    {
        const string sql = """
            WHERE
                e.namespace_id = 2
                AND (
                    @p_cursor_at IS NULL
                    OR e.created_at_utc < @p_cursor_at
                    OR (e.created_at_utc = @p_cursor_at AND e.id < @p_cursor_id)
                )
            ORDER BY e.created_at_utc DESC
            """;

        Assert.Equal(
            """
            WHERE
                e.namespace_id = 2
                AND (e.created_at_utc < @p_cursor_at
                    OR (e.created_at_utc = @p_cursor_at AND e.id < @p_cursor_id)
                )
            ORDER BY e.created_at_utc DESC
            """,
            Fold(sql, ("@p_cursor_at", 10L), ("@p_cursor_id", 7L))
        );
        Assert.Equal(
            """
            WHERE
                e.namespace_id = 2
                AND 1
            ORDER BY e.created_at_utc DESC
            """,
            Fold(sql, ("@p_cursor_at", DBNull.Value), ("@p_cursor_id", DBNull.Value))
        );
    }

    [Fact]
    public void Parentheses_inside_literals_and_comments_do_not_close_the_filter()
    {
        const string sql = "WHERE (@p_a IS NULL OR a IN (')', 'it''s )') /* ) */ OR b = @p_a -- )\n) AND c = 1";

        Assert.Equal("WHERE (a IN (')', 'it''s )') /* ) */ OR b = @p_a -- )\n) AND c = 1", Fold(sql, ("@p_a", 1)));
        Assert.Equal("WHERE 1 AND c = 1", Fold(sql, ("@p_a", null)));
    }

    [Fact]
    public void Is_not_null_tests_are_left_alone()
    {
        const string sql = "SELECT CASE WHEN @p_total IS NOT NULL THEN 1 END WHERE (@p_a IS NOT NULL OR a = 1)";

        Assert.Equal(sql, Fold(sql, ("@p_total", 1), ("@p_a", 1)));
        Assert.Equal(sql, Fold(sql));
    }

    [Fact]
    public void Every_statement_in_the_text_is_folded()
    {
        const string sql =
            "SELECT 1 WHERE (@p_a IS NULL OR a = @p_a) AND (@p_b IS NULL OR b = @p_b);\n"
            + "SELECT 2 WHERE (@p_a IS NULL OR a = @p_a) AND (@p_b IS NULL OR b = @p_b);";

        Assert.Equal("SELECT 1 WHERE (a = @p_a) AND 1;\nSELECT 2 WHERE (a = @p_a) AND 1;", Fold(sql, ("@p_a", 1), ("@p_b", DBNull.Value)));
    }

    [Fact]
    public void The_events_page_keeps_exactly_the_namespace_filter_it_was_given()
    {
        const string namespaceFilter = "(e.namespace_id = (SELECT n.id FROM main.namespaces n WHERE n.name = @p_namespace_name))";
        var sql = new SqlResourceCatalog("sqlite", "main").Load("ListEvents");

        // The handler binds every filter, null when unset; only the namespace carries a value here.
        var unset = Regex.Matches(sql, @"@p_\w+").Select(m => m.Value).Distinct().Where(n => n != "@p_namespace_name");
        var folded = Fold(sql, [("@p_namespace_name", "busy"), .. unset.Select(n => (n, (object?)null))]);

        Assert.Equal(2, folded.Split(namespaceFilter).Length - 1);
        Assert.DoesNotContain("IS NULL OR", folded, StringComparison.Ordinal);
        Assert.DoesNotContain("@p_job_id", folded, StringComparison.Ordinal);
        Assert.DoesNotContain("@p_cursor_id", folded, StringComparison.Ordinal);
    }

    private static string Fold(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = new SqliteCommand();
        foreach (var (name, value) in parameters)
        {
            command.Parameters.Add(new SqliteParameter(name, value));
        }
        return SqliteDialect.WithoutNullCatchAlls(sql, command.Parameters);
    }
}
