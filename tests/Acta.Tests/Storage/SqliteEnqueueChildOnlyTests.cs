using Acta.Relational.Resources;
using Acta.Sqlite.Services;
using Xunit;

namespace Acta.Tests.Storage;

/// <summary>
/// The SQLite enqueue script drops its ancestor-lane check for a job with no parent. The markers
/// live in the SQL and the cut in the dialect, so a renamed or moved marker would quietly bring
/// the parse cost back; this pins both halves together.
/// </summary>
public sealed class SqliteEnqueueChildOnlyTests
{
    [Fact]
    public void An_unparented_enqueue_loses_exactly_the_ancestor_check()
    {
        var sql = new SqlResourceCatalog(typeof(SqliteDialect).Assembly, "main").Load("Sql/Execution/Jobs/EnqueueOne.sql");
        Assert.Equal(2, sql.Split(SqliteDialect.ChildOnlyBegin).Length);
        Assert.Contains("ACTA:ENQ_ANCESTOR_LANE", sql, StringComparison.Ordinal);

        var cut = SqliteDialect.WithoutChildOnly(sql);

        Assert.DoesNotContain("ACTA:ENQ_ANCESTOR_LANE", cut, StringComparison.Ordinal);
        Assert.DoesNotContain("child-only", cut, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO main.lanes", cut, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO main.jobs", cut, StringComparison.Ordinal);
    }
}
