using Acta.SqlServer.Configuration;
using Acta.Tests.Conformance.Sql;
using Xunit;

namespace Acta.Tests.Conformance.SqlServer;

public sealed class SqlCommentPolicyTests
{
    [Fact]
    public void Operation_sql_has_no_long_comment_blocks() =>
        SqlCommentPolicyAssert.AssertOperationSqlHasNoLongCommentBlocks("Acta.SqlServer.");
}

public sealed class SqlCodePolicyTests
{
    [Fact]
    public void Code_literals_are_symbolic_and_match_code_values() => SqlCodePolicyAssert.AssertAllCodeConstantsAreValid("mssql");
}

/// <summary>
/// SQL Server invoker for <see cref="SqlParameterCoverage.AssertParameterPrefix"/>: asserts every
/// <c>@</c>-token in the embedded MSSQL SQL carries the <c>@p_</c> bound-parameter prefix or is a
/// TSQL <c>DECLARE</c>'d local. Provider-store and conformance gates cover command binding.
/// </summary>
public sealed class SqlParameterCoverageTests
{
    [Fact]
    public void Every_at_token_carries_the_p_prefix() =>
        SqlParameterCoverage.AssertParameterPrefix(typeof(SqlServerProviderOptions).Assembly, "Acta.SqlServer.Sql.");
}

public sealed class SqlDialectLeakageTests
{
    [Fact]
    public void No_foreign_dialect_tokens_in_mssql_sql() => DialectLeakageAssert.AssertNoForeignDialectTokens("mssql");
}

public sealed class SqlUnsafeDmlTests
{
    [Fact]
    public void Update_and_delete_statements_are_guarded() => UnsafeDmlAssert.AssertGuardedDml("mssql");
}

public sealed class SqlRoutineBodyParameterTests
{
    [Fact]
    public void Declared_routine_params_are_used_in_the_body() => RoutineBodyParameterAssert.AssertDeclaredParamsUsed("mssql");
}

public sealed class SqlStyleTests
{
    [Fact]
    public void Sql_files_meet_the_style_floor() => SqlStyleAssert.AssertStyle("mssql");
}
