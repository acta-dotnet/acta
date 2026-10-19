using System.Data;
using System.Text.RegularExpressions;
using Acta.SqlServer;
using Microsoft.Data.SqlClient.Server;
using Xunit;

namespace Acta.Tests.Conformance;

/// <summary>
/// Guards the SQL Server TVP contract: the <c>CREATE TYPE</c> bodies in the migrations (emitted from
/// tools/Acta.Emit's <c>SqlServerDdlDialect</c>) and the runtime <see cref="SqlMetaData"/> record
/// shapes in <see cref="SqlServerDialect"/> bind positionally, so a column added on one side only
/// would otherwise surface as an "Invalid column name" against a live database. This test compares
/// the two column-for-column (name, type, width, order) so the drift fails here instead.
/// </summary>
public sealed partial class TvpParityTests
{
    /// <summary>
    /// Every <c>CREATE TYPE</c> in the script, in apply order. A type a later migration re-creates matches again, so the
    /// readers below assign by name and the last declaration wins.
    /// </summary>
    private static readonly Regex TvpBlock = MyRegex();

    private static readonly Regex ColumnLine = new(@"^(\w+)\s+([A-Z0-9]+(?:\((?:\d+|MAX)\))?)", RegexOptions.Compiled);

    /// <summary>
    /// The key of every TVP, pinned. A TVP is bound positionally and its key decides which rows the
    /// receiving routine treats as distinct: keying <c>enqueue_jobs_batch</c> on anything a caller can
    /// repeat within one batch makes the whole batch fail on a duplicate. That is the defect this
    /// pinning exists to catch, and it is why the current baseline is keyed on <c>ordinal</c> - a
    /// position the caller cannot collide - rather than on any caller-supplied value.
    /// </summary>
    private static readonly Dictionary<string, string> ExpectedKeys = new(StringComparer.Ordinal)
    {
        ["enqueue_jobs_batch"] = "ordinal",
        ["enqueue_tags_batch"] = "ordinal, name",
        ["complete_executions_batch"] = "job_id, execution_number",
        ["register_definitions_batch"] = "name",
        ["register_slots_batch"] = "definition_id",
        ["register_schedules_batch"] = "definition_id, name",
    };

    [Fact]
    public void Baseline_tvp_types_declare_the_pinned_primary_keys()
    {
        var script = Emit.BaselineCodeChecksTests.ProvisionScript("mssql");
        var actual = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (Match block in TvpBlock.Matches(script))
        {
            var key = block
                .Groups[2]
                .Value.Split('\n')
                .Select(line => line.Trim())
                .Select(line =>
                    line.StartsWith("PRIMARY KEY", StringComparison.Ordinal) ? line["PRIMARY KEY".Length..].Trim().Trim('(', ')', ',')
                    : line.Contains("PRIMARY KEY", StringComparison.Ordinal) ? line.Split(' ')[0]
                    : null
                )
                .FirstOrDefault(k => k is not null);

            actual[block.Groups[1].Value] = key ?? "<none>";
        }

        Assert.Equal(ExpectedKeys.OrderBy(p => p.Key, StringComparer.Ordinal), actual.OrderBy(p => p.Key, StringComparer.Ordinal));
    }

    /// <summary>The runtime record shape the dialect binds for each TVP the baseline declares.</summary>
    private static readonly Dictionary<string, SqlMetaData[]> TvpShapes = new(StringComparer.Ordinal)
    {
        ["enqueue_jobs_batch"] = SqlServerTableParameters.BatchColumns,
        ["enqueue_tags_batch"] = SqlServerTableParameters.TagColumns,
        ["complete_executions_batch"] = SqlServerTableParameters.CompleteExecutionsColumns,
        ["register_definitions_batch"] = SqlServerTableParameters.DefinitionColumns,
        ["register_slots_batch"] = SqlServerTableParameters.SlotColumns,
        ["register_schedules_batch"] = SqlServerTableParameters.ScheduleUpsertColumns,
    };

    [Fact]
    public void Baseline_tvp_types_match_the_runtime_record_shapes()
    {
        var declared = DeclaredColumns();

        Assert.Equal(declared.Keys.Order(StringComparer.Ordinal), TvpShapes.Keys.Order(StringComparer.Ordinal));

        var failures = new List<string>();
        foreach (var (typeName, columns) in declared)
        {
            var shape = TvpShapes[typeName];
            var expected = columns.Select(c => $"{c.Name} {c.Type}").ToList();
            var actual = shape.Select(m => $"{m.Name} {Render(m)}").ToList();
            if (!expected.SequenceEqual(actual, StringComparer.Ordinal))
            {
                failures.Add($"{typeName}:\n  baseline: {string.Join(", ", expected)}\n  runtime:  {string.Join(", ", actual)}");
            }
        }

        Assert.True(
            failures.Count == 0,
            "TVP drift between the baseline (tools/Acta.Emit SqlServerDdlDialect) and SqlServerDialect record shapes:\n"
                + string.Join("\n", failures)
        );
    }

    /// <summary>
    /// A TVP column holding a case-exact key carries the key column's binary collation, and no other TVP column
    /// carries one: a routine joining the two under the database default fails on a collation conflict.
    /// </summary>
    [Fact]
    public void Baseline_tvp_key_columns_carry_the_binary_collation_of_their_case_exact_column()
    {
        var caseExact = ActaSchema
            .Entities.SelectMany(e => e.Columns)
            .Where(c => c.CaseExact)
            .Select(c => c.Name)
            .ToHashSet(StringComparer.Ordinal);

        var wrong = DeclaredColumns()
            .SelectMany(t => t.Value.Where(c => c.Collated != caseExact.Contains(c.Name)).Select(c => $"{t.Key}.{c.Name}"))
            .ToList();

        Assert.True(wrong.Count == 0, $"TVP columns whose binary collation disagrees with the schema model: {string.Join(", ", wrong)}");
    }

    private static Dictionary<string, List<(string Name, string Type, bool Collated)>> DeclaredColumns()
    {
        var script = Emit.BaselineCodeChecksTests.ProvisionScript("mssql");
        var declared = new Dictionary<string, List<(string Name, string Type, bool Collated)>>(StringComparer.Ordinal);

        foreach (Match block in TvpBlock.Matches(script))
        {
            var columns = new List<(string, string, bool)>();
            foreach (var raw in block.Groups[2].Value.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("PRIMARY KEY", StringComparison.Ordinal))
                {
                    continue;
                }

                var column = ColumnLine.Match(line);
                if (column.Success)
                {
                    var collated = line.Contains(" COLLATE Latin1_General_100_BIN2_UTF8 ", StringComparison.Ordinal);
                    columns.Add((column.Groups[1].Value, Normalize(column.Groups[2].Value), collated));
                }
            }

            declared[block.Groups[1].Value] = columns;
        }

        return declared;
    }

    // Precision-only suffixes (DATETIME2(3)) are dropped: SqlMetaData carries no datetime precision,
    // and positional TVP binding is insensitive to it.
    private static string Normalize(string ddlType) => ddlType.StartsWith("DATETIME2", StringComparison.Ordinal) ? "DATETIME2" : ddlType;

    private static string Render(SqlMetaData column) =>
        column.SqlDbType switch
        {
            SqlDbType.Int => "INT",
            SqlDbType.BigInt => "BIGINT",
            SqlDbType.SmallInt => "SMALLINT",
            SqlDbType.TinyInt => "TINYINT",
            SqlDbType.Bit => "BIT",
            SqlDbType.UniqueIdentifier => "UNIQUEIDENTIFIER",
            SqlDbType.DateTime2 => "DATETIME2",
            SqlDbType.VarChar => $"VARCHAR({column.MaxLength})",
            SqlDbType.NVarChar => $"NVARCHAR({column.MaxLength})",
            SqlDbType.VarBinary => column.MaxLength == -1 ? "VARBINARY(MAX)" : $"VARBINARY({column.MaxLength})",
            _ => column.SqlDbType.ToString().ToUpperInvariant(),
        };

    [GeneratedRegex(@"CREATE TYPE \w+\.(\w+) AS TABLE \((.*?)\);'", RegexOptions.Compiled | RegexOptions.Singleline)]
    private static partial Regex MyRegex();
}
