using System.Data;
using System.Data.Common;
using Xunit;

namespace Acta.Tests.Storage;

public sealed class ProjectionByNameTests
{
    private static readonly string[] Columns = ["job_id", "job_name"];

    private static readonly Func<DbDataReader, int[], (long JobId, string JobName)> Bind = (r, o) =>
        (Convert.ToInt64(r.GetValue(o[0])), r.GetString(o[1]));

    [Fact]
    public void A_row_binds_by_column_name_whatever_the_order_or_case()
    {
        var read = DbProjectionResolver.ByName("Row", Columns, Bind);
        using var reader = Reader(("JOB_NAME", typeof(string)), ("job_id", typeof(long)), ("extra", typeof(int)))
            .Row("nightly", 7L, 1)
            .Row("hourly", 8L, 2)
            .CreateDataReader();

        reader.Read();
        Assert.Equal((7L, "nightly"), read(reader));
        reader.Read();
        Assert.Equal((8L, "hourly"), read(reader));
    }

    [Fact]
    public void A_new_reader_looks_its_columns_up_again()
    {
        var read = DbProjectionResolver.ByName("Row", Columns, Bind);
        using var first = Reader(("job_id", typeof(long)), ("job_name", typeof(string))).Row(1L, "a").CreateDataReader();
        using var second = Reader(("job_name", typeof(string)), ("job_id", typeof(long))).Row("b", 2L).CreateDataReader();

        first.Read();
        second.Read();
        Assert.Equal((1L, "a"), read(first));
        Assert.Equal((2L, "b"), read(second));
    }

    [Fact]
    public void Missing_columns_are_named_with_the_result_they_were_looked_for_in()
    {
        var read = DbProjectionResolver.ByName("Row", Columns, Bind);
        using var reader = Reader(("id", typeof(long)), ("name", typeof(string))).Row(1L, "a").CreateDataReader();
        reader.Read();

        var ex = Assert.Throws<InvalidOperationException>(() => read(reader));
        Assert.Equal("Row reads job_id, job_name, which the result (id, name) does not have.", ex.Message);
    }

    [Fact]
    public void A_column_the_result_holds_twice_is_refused()
    {
        var read = DbProjectionResolver.ByName("Row", Columns, Bind);
        var table = new DataTable { CaseSensitive = true };
        table.Columns.Add("job_id", typeof(long));
        table.Columns.Add("job_name", typeof(string));
        table.Columns.Add("JOB_ID", typeof(long));
        table.Rows.Add(1L, "a", 2L);
        using var reader = table.CreateDataReader();
        reader.Read();

        var ex = Assert.Throws<InvalidOperationException>(() => read(reader));
        Assert.Equal("Row reads column 'job_id', which the result holds twice.", ex.Message);
    }

    private static DataTable Reader(params (string Name, Type Type)[] columns)
    {
        var table = new DataTable { CaseSensitive = true };
        foreach (var (name, type) in columns)
        {
            table.Columns.Add(name, type);
        }
        return table;
    }
}

file static class DataTableRows
{
    public static DataTable Row(this DataTable table, params object[] values)
    {
        table.Rows.Add(values);
        return table;
    }
}
