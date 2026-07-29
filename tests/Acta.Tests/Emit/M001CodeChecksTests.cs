using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Emit;

/// <summary>
/// Static DDL-content checks: every coded (enum-backed) column in the schema must produce an
/// <c>IN</c>-list <c>CHECK</c> constraint so invalid enum ids are rejected at the DB layer, not
/// only by the C# layer. Nullable columns must allow <c>NULL</c> explicitly.
/// </summary>
public class M001CodeChecksTests
{
    private static string SqlServerM001 =>
        File.ReadAllText(Path.Combine(IntegrationConfig.FindRepoRoot(), "src", "Acta.SqlServer", "Schema", "Migrations", "M001_init.sql"));

    private static string PgM001 =>
        File.ReadAllText(Path.Combine(IntegrationConfig.FindRepoRoot(), "src", "Acta.Postgres", "Schema", "Migrations", "M001_init.sql"));

    // Routine bodies live in provider-owned feature resources (installed by SqlObjectInstaller),
    // not in M001, so the transaction-shape check reads the resource file directly.
    private static string CompleteExecutionsBatchMssql =>
        File.ReadAllText(
            Path.Combine(
                IntegrationConfig.FindRepoRoot(),
                "src",
                "Acta.SqlServer",
                "Sql",
                "Execution",
                "CompleteExecutionsBatch.routine.sql"
            )
        );

    // JobStatusCode = { Ready=10, Suspended=20, Paused=30, Dispatched=40, Executing=50,
    //               Done=100, Failed=200, Cancelled=220 }. Anchor enum for the test -
    // small, stable, and non-nullable on `runtimes.status_code`.
    private const string JobStatusInList = "(10, 20, 30, 40, 50, 100, 200, 220)";

    [Fact]
    public void SqlServerM001_EmitsJobStatusCheckConstraint()
    {
        Assert.Contains($"CONSTRAINT ck_runtimes_status_code CHECK (status_code IN {JobStatusInList})", SqlServerM001);
    }

    [Fact]
    public void PgM001_EmitsJobStatusCheckConstraint()
    {
        Assert.Contains($"CONSTRAINT ck_runtimes_status_code CHECK (status_code IN {JobStatusInList})", PgM001);
    }

    [Fact]
    public void SqlServerM001_NullableCodeColumns_AllowNullExplicitly()
    {
        // events.reason_code is a nullable coded column (smallint NULL). The CHECK must include
        // `IS NULL OR ...` so a NULL row is admitted.
        Assert.Contains("CONSTRAINT ck_events_reason_code CHECK (reason_code IS NULL OR reason_code IN (", SqlServerM001);
    }

    [Fact]
    public void PgM001_NullableCodeColumns_AllowNullExplicitly()
    {
        Assert.Contains("CONSTRAINT ck_events_reason_code CHECK (reason_code IS NULL OR reason_code IN (", PgM001);
    }

    [Fact]
    public void SqlServerM001_CompleteExecutionsBatch_DmlIsInsideOneTransaction()
    {
        var routine = ExtractRoutine(CompleteExecutionsBatchMssql, "complete_executions_batch");
        var lines = routine.Split(["\r\n", "\n"], StringSplitOptions.None);
        var beginTransaction = Array.FindIndex(lines, static line => line.Trim() == "BEGIN TRANSACTION;");
        var commitTransaction = Array.FindIndex(lines, static line => line.Trim() == "COMMIT TRANSACTION;");
        var rollbackTransaction = Array.FindIndex(lines, static line => line.Trim() == "ROLLBACK TRANSACTION;");

        Assert.True(beginTransaction >= 0, "complete_executions_batch must open an explicit transaction.");
        Assert.True(commitTransaction > beginTransaction, "complete_executions_batch must commit after its DML.");
        Assert.True(rollbackTransaction > commitTransaction, "complete_executions_batch must roll back in CATCH.");

        var dmlLines = lines
            .Select((line, index) => new { Line = line.TrimStart(), Index = index })
            .Where(x =>
                x.Line.StartsWith("UPDATE ", StringComparison.Ordinal) || x.Line.StartsWith("INSERT INTO ", StringComparison.Ordinal)
            )
            .ToArray();

        Assert.NotEmpty(dmlLines);
        Assert.All(
            dmlLines,
            dml =>
                Assert.True(
                    dml.Index > beginTransaction && dml.Index < commitTransaction,
                    $"DML line must be inside the transaction: {dml.Line}"
                )
        );
    }

    private static string ExtractRoutine(string sql, string routineName)
    {
        sql = sql.ReplaceLineEndings("\n");
        var marker = $"CREATE OR ALTER PROCEDURE {{{{schema}}}}.{routineName}";
        var start = sql.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing routine {routineName}.");

        var end = sql.IndexOf("\nGO", start, StringComparison.Ordinal);
        return end > start ? sql[start..end] : sql[start..];
    }
}
