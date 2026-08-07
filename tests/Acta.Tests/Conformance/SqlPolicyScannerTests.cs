using Acta.Tests.Conformance.Sql;
using Xunit;

namespace Acta.Tests.Conformance;

/// <summary>
/// Synthetic positive- and negative-case coverage for the SQL-policy scanners. Feeds SQL directly
/// into each scanner's internal per-content method so a future regex change cannot silently turn a
/// scanner into a no-op and pass merely because the repository has no detected violations.
/// </summary>
public sealed class DialectLeakageAssertTests
{
    [Fact]
    public void Foreign_token_in_pg_dialect_fails() =>
        Assert.NotEmpty(DialectLeakageAssert.ScanContent("pg", "Fake/Fake.sql", "SELECT NVARCHAR(64) FROM x;"));

    [Fact]
    public void Foreign_token_inside_a_string_literal_passes() =>
        Assert.Empty(DialectLeakageAssert.ScanContent("pg", "Fake/Fake.sql", "SELECT 'NVARCHAR(64)' AS x;"));

    [Fact]
    public void Foreign_token_inside_a_template_placeholder_passes() =>
        Assert.Empty(DialectLeakageAssert.ScanContent("pg", "Fake/Fake.sql", "SELECT {{decode:NVARCHAR:x}} AS y;"));

    [Fact]
    public void Clean_snippet_passes() => Assert.Empty(DialectLeakageAssert.ScanContent("pg", "Fake/Fake.sql", "SELECT 1;"));
}

public sealed class SqlStyleAssertTests
{
    [Fact]
    public void Tab_character_fails() => Assert.NotEmpty(SqlStyleAssert.ScanContent("Fake/Fake.sql", "SELECT\t1;"));

    [Fact]
    public void Trailing_whitespace_fails() => Assert.NotEmpty(SqlStyleAssert.ScanContent("Fake/Fake.sql", "SELECT 1; \nFROM x;"));

    [Fact]
    public void Lowercase_clause_keyword_fails() => Assert.NotEmpty(SqlStyleAssert.ScanContent("Fake/Fake.sql", "select 1;"));

    [Fact]
    public void Lowercase_keyword_inside_a_string_passes() =>
        Assert.Empty(SqlStyleAssert.ScanContent("Fake/Fake.sql", "SELECT 'select one from x';"));

    [Fact]
    public void Lowercase_keyword_inside_a_comment_passes() =>
        Assert.Empty(SqlStyleAssert.ScanContent("Fake/Fake.sql", "-- select the winner\nSELECT 1;"));

    [Fact]
    public void Clean_uppercase_snippet_passes() =>
        Assert.Empty(SqlStyleAssert.ScanContent("Fake/Fake.sql", "SELECT 1\nFROM x\nWHERE a = 1;"));
}

public sealed class UnsafeDmlAssertTests
{
    [Fact]
    public void Update_without_where_or_join_fails() =>
        Assert.NotEmpty(UnsafeDmlAssert.ScanContent("Fake/Fake.sql", "UPDATE x SET a = 1;"));

    [Fact]
    public void Update_with_where_passes() =>
        Assert.Empty(UnsafeDmlAssert.ScanContent("Fake/Fake.sql", "UPDATE x SET a = 1 WHERE id = 2;"));

    [Fact]
    public void Delete_without_where_fails() => Assert.NotEmpty(UnsafeDmlAssert.ScanContent("Fake/Fake.sql", "DELETE FROM x;"));

    [Fact]
    public void Comma_join_fails() =>
        Assert.NotEmpty(UnsafeDmlAssert.ScanContent("Fake/Fake.sql", "UPDATE x SET a = 1 FROM a, b WHERE x.id = a.id;"));

    [Fact]
    public void Explicit_join_passes() =>
        Assert.Empty(UnsafeDmlAssert.ScanContent("Fake/Fake.sql", "UPDATE t SET a = 1 FROM t JOIN u ON u.id = t.id;"));
}

public sealed class RoutineBodyParameterAssertTests
{
    private const string MssqlWithDeadParam = """
        CREATE PROCEDURE Foo
            @p_used INT,
            @p_dead INT
        AS
        BEGIN
            SELECT @p_used;
        END
        """;

    private const string MssqlAllUsed = """
        CREATE PROCEDURE Foo
            @p_used INT,
            @p_other INT
        AS
        BEGIN
            SELECT @p_used, @p_other;
        END
        """;

    private const string PgWithDeadParam = """
        CREATE FUNCTION foo(p_used int, p_dead int) RETURNS void AS $$
        BEGIN
            PERFORM p_used;
        END;
        $$ LANGUAGE plpgsql;
        """;

    private const string PgAllUsed = """
        CREATE FUNCTION foo(p_used int, p_other int) RETURNS void AS $$
        BEGIN
            PERFORM p_used + p_other;
        END;
        $$ LANGUAGE plpgsql;
        """;

    [Fact]
    public void Mssql_dead_param_fails() =>
        Assert.NotEmpty(RoutineBodyParameterAssert.ScanContent("mssql", "Fake/Fake.routine.sql", MssqlWithDeadParam));

    [Fact]
    public void Mssql_all_used_passes() =>
        Assert.Empty(RoutineBodyParameterAssert.ScanContent("mssql", "Fake/Fake.routine.sql", MssqlAllUsed));

    [Fact]
    public void Pg_dead_param_fails() =>
        Assert.NotEmpty(RoutineBodyParameterAssert.ScanContent("pg", "Fake/Fake.routine.sql", PgWithDeadParam));

    [Fact]
    public void Pg_all_used_passes() => Assert.Empty(RoutineBodyParameterAssert.ScanContent("pg", "Fake/Fake.routine.sql", PgAllUsed));
}

public sealed class SqlCodePolicyAssertTests
{
    [Fact]
    public void Unannotated_persisted_code_comparison_fails() =>
        Assert.NotEmpty(SqlCodePolicyAssert.ScanRequiredAnnotations("Fake/Fake.sql", "SELECT 1 WHERE status_code = 30;"));

    [Fact]
    public void Unannotated_reversed_code_comparison_fails() =>
        Assert.NotEmpty(SqlCodePolicyAssert.ScanRequiredAnnotations("Fake/Fake.sql", "SELECT 1 WHERE 30 = status_code;"));

    [Fact]
    public void Unannotated_internal_discriminator_fails() =>
        Assert.NotEmpty(SqlCodePolicyAssert.ScanRequiredAnnotations("Fake/Fake.sql", "SELECT 1 WHERE @p_action IN (10, 20);"));

    [Fact]
    public void Symbolically_annotated_code_literals_pass() =>
        Assert.Empty(
            SqlCodePolicyAssert.ScanRequiredAnnotations(
                "Fake/Fake.sql",
                "SELECT 1 WHERE status_code IN (10 /* JobStatusCode.Ready */, 100 /* JobStatusCode.Succeeded */);"
            )
        );

    [Fact]
    public void Unannotated_transient_outcome_code_fails() =>
        Assert.NotEmpty(
            SqlCodePolicyAssert.ScanRequiredAnnotations("Fake/Fake.sql", "SELECT CASE WHEN ready THEN 1 ELSE 2 END AS outcome_code;")
        );

    [Fact]
    public void Unannotated_direct_code_result_fails() =>
        Assert.NotEmpty(SqlCodePolicyAssert.ScanRequiredAnnotations("Fake/Fake.sql", "SELECT 1 AS outcome_code;"));

    [Fact]
    public void Unannotated_postgres_positional_code_result_fails() =>
        Assert.NotEmpty(
            SqlCodePolicyAssert.ScanRequiredAnnotations(
                "Fake/Fake.routine.sql",
                "CREATE FUNCTION fake() RETURNS TABLE(out_action SMALLINT, out_status_code SMALLINT) LANGUAGE plpgsql AS $$ BEGIN RETURN QUERY SELECT 1::SMALLINT, 30::SMALLINT; END; $$;"
            )
        );

    [Fact]
    public void Unannotated_inserted_code_value_fails() =>
        Assert.NotEmpty(
            SqlCodePolicyAssert.ScanRequiredAnnotations(
                "Fake/Fake.sql",
                "INSERT INTO checkpoints (job_id, value_format_id, value) VALUES (1, 0, NULL);"
            )
        );

    [Fact]
    public void Unannotated_code_value_in_every_inserted_row_fails()
    {
        var failures = SqlCodePolicyAssert.ScanRequiredAnnotations(
            "Fake/Fake.sql",
            "INSERT INTO jobs (status_code) VALUES (10 /* JobStatusCode.Ready */), (100);"
        );

        Assert.Single(failures);
    }

    [Fact]
    public void Unannotated_code_value_after_sql_server_output_fails() =>
        Assert.NotEmpty(
            SqlCodePolicyAssert.ScanRequiredAnnotations(
                "Fake/Fake.sql",
                "INSERT INTO jobs (status_code) OUTPUT inserted.id INTO @ids VALUES (30);"
            )
        );

    [Fact]
    public void Case_failure_reports_the_numeric_literal_line()
    {
        var failure = Assert.Single(
            SqlCodePolicyAssert.ScanRequiredAnnotations(
                "Fake/Fake.sql",
                "SELECT CASE WHEN ready THEN\n    1\nELSE 2 /* SignalWaitOutcomeCode.ContinueSet */ END AS outcome_code;"
            )
        );

        Assert.StartsWith("Fake/Fake.sql:2:", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void Ordinary_numeric_limits_do_not_require_code_annotations() =>
        Assert.Empty(SqlCodePolicyAssert.ScanRequiredAnnotations("Fake/Fake.sql", "SELECT 1 WHERE batch_size = 1000;"));

    [Fact]
    public void Matching_type_member_annotation_passes() =>
        Assert.Empty(SqlCodePolicyAssert.ScanConstantAnnotations("Fake/Fake.sql", "SELECT 10 /* JobStatusCode.Ready */;"));

    [Fact]
    public void Legacy_code_kind_annotation_fails() =>
        Assert.NotEmpty(SqlCodePolicyAssert.ScanConstantAnnotations("Fake/Fake.sql", "SELECT 10 /* job-status:ready */;"));

    [Fact]
    public void Unknown_type_member_annotation_fails() =>
        Assert.NotEmpty(SqlCodePolicyAssert.ScanConstantAnnotations("Fake/Fake.sql", "SELECT 10 /* JobStatusCode.NoSuchThing */;"));

    [Fact]
    public void Incorrect_numeric_value_fails() =>
        Assert.NotEmpty(SqlCodePolicyAssert.ScanConstantAnnotations("Fake/Fake.sql", "SELECT 40 /* JobStatusCode.Ready */;"));
}
