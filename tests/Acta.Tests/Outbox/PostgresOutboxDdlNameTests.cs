using System.Text.RegularExpressions;
using Acta.Postgres.Hosting;
using Xunit;

namespace Acta.Tests.Outbox;

/// <summary>
/// PostgreSQL truncates an identifier past 63 bytes, so a table name long enough to push a derived
/// constraint or index name past it would give two objects one name and fail the script. The DDL API takes
/// every table name that keeps every derived name whole and refuses the rest up front.
/// </summary>
public sealed class PostgresOutboxDdlNameTests
{
    [Fact]
    public void The_longest_accepted_table_keeps_every_derived_name_within_63_bytes()
    {
        var script = PostgresOutboxDdl.CreateScript(new string('t', 41));

        var names = Regex.Matches(script, @"(?:CONSTRAINT|INDEX) (\w+)").Select(m => m.Groups[1].Value).ToList();
        Assert.NotEmpty(names);
        Assert.All(names, name => Assert.True(name.Length <= 63, $"{name} is {name.Length} bytes"));
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Fact]
    public void A_longer_table_name_is_refused()
    {
        var ex = Assert.Throws<ArgumentException>(() => PostgresOutboxDdl.CreateScript(new string('t', 42)));
        Assert.Equal("table", ex.ParamName);
    }
}
