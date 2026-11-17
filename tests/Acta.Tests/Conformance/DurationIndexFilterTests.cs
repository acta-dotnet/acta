using System.Reflection;
using System.Text.RegularExpressions;
using Acta.Tests.Conformance.Sql;
using Xunit;

namespace Acta.Tests.Conformance;

/// <summary>
/// A definition's performance is read from <c>ix_events_duration</c>, a filtered index, and SQLite takes a filtered
/// index only for a query that repeats the filter term for term, a list in the same order. Each provider's read
/// restates the filter as the index declares it.
/// </summary>
public sealed partial class DurationIndexFilterTests
{
    [Theory]
    [InlineData("pg")]
    [InlineData("mssql")]
    [InlineData("sqlite")]
    public void The_performance_read_restates_the_duration_index_filter(string dialect)
    {
        var filter = typeof(EventEntity).GetCustomAttributes<DbIndexAttribute>().Single(index => index.Name == "ix_events_duration").Filter;
        var sql = ProviderSqlResources.Read(dialect, "GetDefinitionPerformance");

        var read = Whitespace().Replace(Alias().Replace(Comment().Replace(sql, ""), ""), " ");

        Assert.Contains(filter!, read, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"\s*/\*.*?\*/|--[^\n]*")]
    private static partial Regex Comment();

    [GeneratedRegex(@"\be\.")]
    private static partial Regex Alias();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
