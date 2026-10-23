using Acta.Sqlite;
using Xunit;

namespace Acta.Tests.Storage;

/// <summary>
/// The SQLite enqueue script drops its parent-fence and ancestor-lane checks for a job with no parent. The
/// markers live in the SQL and the cut in the dialect, so a renamed or moved marker would quietly bring
/// the parse cost back; this pins both halves together.
/// </summary>
public sealed class SqliteEnqueueChildOnlyTests
{
    [Fact]
    public void An_unparented_enqueue_loses_exactly_the_parent_checks()
    {
        var sql = new SqlResourceCatalog("sqlite", "main").Load("EnqueueJob");
        Assert.Equal(3, sql.Split("-- begin child-only").Length);
        Assert.Contains("ACTA:EXECUTION_FENCED", sql, StringComparison.Ordinal);
        Assert.Contains("ACTA:ENQ_ANCESTOR_LANE", sql, StringComparison.Ordinal);

        var cut = SqliteDialect.WithoutSection(sql, "child-only");

        Assert.DoesNotContain("ACTA:EXECUTION_FENCED", cut, StringComparison.Ordinal);
        Assert.DoesNotContain("ACTA:ENQ_ANCESTOR_LANE", cut, StringComparison.Ordinal);
        Assert.DoesNotContain("child-only", cut, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO main.lanes", cut, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO main.jobs", cut, StringComparison.Ordinal);
    }
}
