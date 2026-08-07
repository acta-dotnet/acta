using Acta.Tests.Conformance.Sql;
using Xunit;

namespace Acta.Tests.Conformance.Sqlite;

public sealed class SqlCommentPolicyTests
{
    [Fact]
    public void Operation_sql_has_no_long_comment_blocks() =>
        SqlCommentPolicyAssert.AssertOperationSqlHasNoLongCommentBlocks("Acta.Sqlite.");
}

public sealed class SqlCodePolicyTests
{
    [Fact]
    public void Code_literals_are_symbolic_and_match_code_values() => SqlCodePolicyAssert.AssertAllCodeConstantsAreValid("sqlite");
}

public sealed class SqlDialectLeakageTests
{
    [Fact]
    public void No_foreign_dialect_tokens_in_sqlite_sql() => DialectLeakageAssert.AssertNoForeignDialectTokens("sqlite");
}

public sealed class SqlUnsafeDmlTests
{
    [Fact]
    public void Update_and_delete_statements_are_guarded() => UnsafeDmlAssert.AssertGuardedDml("sqlite");
}

public sealed class SqlRoutineBodyParameterTests
{
    [Fact]
    public void Declared_routine_params_are_used_in_the_body() => RoutineBodyParameterAssert.AssertDeclaredParamsUsed("sqlite");
}

public sealed class SqlStyleTests
{
    [Fact]
    public void Sql_files_meet_the_style_floor() => SqlStyleAssert.AssertStyle("sqlite");
}
