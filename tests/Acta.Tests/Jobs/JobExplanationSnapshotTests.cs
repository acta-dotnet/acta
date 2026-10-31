using System.Text.RegularExpressions;
using Xunit;

namespace Acta.Tests.Jobs;

/// <summary>
/// The explain read is one statement on every provider, so the header, the steps, and the checkpoints come from one
/// snapshot: under read committed, and SQL Server's read-committed snapshot, each statement takes its own, so a
/// signal landing between two statements could pair a Suspended header with a checkpoint already Set.
/// </summary>
public sealed partial class JobExplanationSnapshotTests
{
    [Theory]
    [InlineData("sqlite")]
    [InlineData("pg")]
    [InlineData("mssql")]
    public void The_explain_read_is_one_statement(string dialect)
    {
        var sql = new SqlResourceCatalog(dialect, "acta").Load("GetJobExplanation");

        var code = LineComment().Replace(BlockComment().Replace(sql, ""), "");

        Assert.Equal(1, code.Count(c => c == ';'));
        Assert.EndsWith(";", code.TrimEnd(), StringComparison.Ordinal);
    }

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex BlockComment();

    [GeneratedRegex(@"--[^\n]*")]
    private static partial Regex LineComment();
}
