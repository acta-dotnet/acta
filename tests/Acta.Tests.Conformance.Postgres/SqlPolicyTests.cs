using Acta.Postgres.Configuration;
using Acta.Tests.Conformance.Sql;
using Xunit;

namespace Acta.Tests.Conformance.Postgres;

public sealed class SqlCommentPolicyTests
{
    [Fact]
    public void Operation_sql_has_no_long_comment_blocks() =>
        SqlCommentPolicyAssert.AssertOperationSqlHasNoLongCommentBlocks("Acta.Postgres.");
}

public sealed class SqlCodePolicyTests
{
    [Fact]
    public void Code_literals_are_symbolic_and_match_code_values() => SqlCodePolicyAssert.AssertAllCodeConstantsAreValid("pg");
}

/// <summary>
/// PostgreSQL invoker for <see cref="SqlParameterCoverage.AssertParameterPrefix"/>: asserts every
/// <c>@</c>-token in the embedded PG SQL carries the <c>@p_</c> bound-parameter prefix. The
/// source-to-SQL binding behavior is covered by the provider-store and conformance gates.
/// </summary>
public sealed class SqlParameterCoverageTests
{
    [Fact]
    public void Every_at_token_carries_the_p_prefix() =>
        SqlParameterCoverage.AssertParameterPrefix(typeof(PostgresProviderOptions).Assembly, "Acta.Postgres.Sql.");
}

public sealed class SqlDialectLeakageTests
{
    [Fact]
    public void No_foreign_dialect_tokens_in_pg_sql() => DialectLeakageAssert.AssertNoForeignDialectTokens("pg");
}

public sealed class SqlUnsafeDmlTests
{
    [Fact]
    public void Update_and_delete_statements_are_guarded() => UnsafeDmlAssert.AssertGuardedDml("pg");
}

public sealed class SqlRoutineBodyParameterTests
{
    [Fact]
    public void Declared_routine_params_are_used_in_the_body() => RoutineBodyParameterAssert.AssertDeclaredParamsUsed("pg");
}

public sealed class SqlStyleTests
{
    [Fact]
    public void Sql_files_meet_the_style_floor() => SqlStyleAssert.AssertStyle("pg");
}
