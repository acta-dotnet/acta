using System.Data;
using System.Text.RegularExpressions;
using Acta.SqlServer.Services;
using Microsoft.Data.SqlClient.Server;
using Xunit;

namespace Acta.Tests.Conformance;

/// <summary>
/// Guards the SQL Server TVP contract: the <c>CREATE TYPE</c> bodies in M001 (emitted from
/// tools/Acta.Emit's <c>SqlServerDdlDialect</c>) and the runtime <see cref="SqlMetaData"/> record
/// shapes in <see cref="SqlServerDialect"/> bind positionally, so a column added on one side only
/// would otherwise surface as an "Invalid column name" against a live database. This test compares
/// the two column-for-column (name, type, width, order) so the drift fails here instead.
/// </summary>
public sealed partial class TvpParityTests
{
    private static readonly Regex TvpBlock = MyRegex();

    private static readonly Regex ColumnLine = new(@"^(\w+)\s+([A-Z0-9]+(?:\((?:\d+|MAX)\))?)", RegexOptions.Compiled);

    /// <summary>
    /// The key of every TVP, pinned. A TVP is bound positionally and its key decides which rows the
    /// receiving routine treats as distinct: keying <c>job_enqueue_batch</c> on anything a caller can
    /// repeat within one batch makes the whole batch fail on a duplicate. That is the defect this
    /// pinning exists to catch, and it is why the current baseline is keyed on <c>ordinal</c> - a
    /// position the caller cannot collide - rather than on any caller-supplied value.
    /// </summary>
    private static readonly Dictionary<string, string> ExpectedKeys = new(StringComparer.Ordinal)
    {
        ["job_enqueue_batch"] = "ordinal",
        ["job_enqueue_tag_batch"] = "ordinal, name",
        ["job_definition_batch"] = "name",
        ["job_schedule_slot_batch"] = "definition_id",
        ["job_schedule_upsert_batch"] = "definition_id, name",
        ["job_schedule_advance_batch"] = "schedule_id",
        ["complete_executions_batch"] = "ordinal",
    };

    [Fact]
    public void M001_tvp_types_declare_the_pinned_primary_keys()
    {
        var m001 = File.ReadAllText(Path.Combine(ResolveRepoRoot(), "src", "Acta.SqlServer", "Schema", "Migrations", "M001_init.sql"));
        var actual = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (Match block in TvpBlock.Matches(m001))
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

    [Fact]
    public void M001_tvp_types_match_the_runtime_record_shapes()
    {
        var m001 = File.ReadAllText(Path.Combine(ResolveRepoRoot(), "src", "Acta.SqlServer", "Schema", "Migrations", "M001_init.sql"));
        var declared = new Dictionary<string, List<(string Name, string Type)>>(StringComparer.Ordinal);

        foreach (Match block in TvpBlock.Matches(m001))
        {
            var columns = new List<(string, string)>();
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
                    columns.Add((column.Groups[1].Value, Normalize(column.Groups[2].Value)));
                }
            }

            declared[block.Groups[1].Value] = columns;
        }

        Assert.Equal(declared.Keys.Order(StringComparer.Ordinal), SqlServerDialect.TvpShapes.Keys.Order(StringComparer.Ordinal));

        var failures = new List<string>();
        foreach (var (typeName, columns) in declared)
        {
            var shape = SqlServerDialect.TvpShapes[typeName];
            var expected = columns.Select(c => $"{c.Name} {c.Type}").ToList();
            var actual = shape.Select(m => $"{m.Name} {Render(m)}").ToList();
            if (!expected.SequenceEqual(actual, StringComparer.Ordinal))
            {
                failures.Add($"{typeName}:\n  M001:    {string.Join(", ", expected)}\n  runtime: {string.Join(", ", actual)}");
            }
        }

        Assert.True(
            failures.Count == 0,
            "TVP drift between M001 (tools/Acta.Emit SqlServerDdlDialect) and SqlServerDialect record shapes:\n"
                + string.Join("\n", failures)
        );
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

    private static string ResolveRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Acta.slnx")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException("TvpParityTests could not locate Acta.slnx from " + AppContext.BaseDirectory);
    }

    [GeneratedRegex(@"CREATE TYPE \{\{schema\}\}\.(\w+) AS TABLE \((.*?)\);'", RegexOptions.Compiled | RegexOptions.Singleline)]
    private static partial Regex MyRegex();
}
