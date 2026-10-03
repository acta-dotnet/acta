using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Emit;

/// <summary>
/// Static DDL-content checks: every coded (enum-backed) column of a <em>closed</em> family must
/// produce an <c>IN</c>-list <c>CHECK</c> so invalid enum ids are rejected at the DB layer, not only
/// by the C# layer, and nullable columns must allow <c>NULL</c> explicitly. Columns of an
/// <c>Extensible</c> family must produce no such CHECK: that is what lets a new member ship without
/// a migration.
/// </summary>
public class M001CodeChecksTests
{
    private static string SqlServerM001 =>
        File.ReadAllText(Path.Combine(IntegrationConfig.FindRepoRoot(), "src", "Acta.SqlServer", "Schema", "Migrations", "M001_init.sql"));

    private static string PgM001 =>
        File.ReadAllText(Path.Combine(IntegrationConfig.FindRepoRoot(), "src", "Acta.Postgres", "Schema", "Migrations", "M001_init.sql"));

    private static string SqliteM001 =>
        File.ReadAllText(Path.Combine(IntegrationConfig.FindRepoRoot(), "src", "Acta.Sqlite", "Schema", "Migrations", "M001_init.sql"));

    // JobStatusCode = { Ready=10, Suspended=20, Paused=30, Dispatched=40, Executing=50,
    //               Succeeded=100, Failed=200, Cancelled=220 }. Anchor enum for the test -
    // small, stable, and non-nullable on `runtimes.status_code`.
    private const string JobStatusInList = "(10, 15, 20, 30, 40, 50, 100, 200, 220)";

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
        // events.from_status_code is a nullable coded column of a closed family. The CHECK must
        // include `IS NULL OR ...` so a NULL row is admitted.
        Assert.Contains("CONSTRAINT ck_events_from_status_code CHECK (from_status_code IS NULL OR from_status_code IN (", SqlServerM001);
    }

    [Fact]
    public void PgM001_NullableCodeColumns_AllowNullExplicitly()
    {
        Assert.Contains("CONSTRAINT ck_events_from_status_code CHECK (from_status_code IS NULL OR from_status_code IN (", PgM001);
    }

    [Fact]
    public void Locks_carry_a_hold_token_and_no_kind_discriminator()
    {
        // 0.9.0 dropped the one-value kind column: the key's middle segment (.lock. / .sem. / .rate.)
        // discriminates the spaces, and the reap sweeps the whole table by expiry. A future
        // primitive re-adds a discriminator additively or gets its own table; this pins that the
        // baseline does not resurrect the dead column, and that every hold carries its CAS token.
        foreach (var m001 in new[] { SqlServerM001, PgM001, SqliteM001 })
        {
            var locksTable = m001.Split("CREATE TABLE", StringSplitOptions.None)
                .First(t => t.Contains(".locks (", StringComparison.Ordinal));
            Assert.DoesNotContain("kind_code", locksTable, StringComparison.Ordinal);
            Assert.Contains("hold_token", locksTable, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("ck_events_reason_code")]
    [InlineData("ck_events_event_code")]
    [InlineData("ck_steps_reason_code")]
    [InlineData("ck_alerts_kind_code")]
    public void ExtensibleFamilies_EmitNoInListCheck(string constraintName)
    {
        // The point of [CodeKind(Extensible = true)] is that a new member ships without a migration,
        // which only holds while the DB is not pinning the value list.
        Assert.DoesNotContain(constraintName, SqlServerM001, StringComparison.Ordinal);
        Assert.DoesNotContain(constraintName, PgM001, StringComparison.Ordinal);
        Assert.DoesNotContain(constraintName, SqliteM001, StringComparison.Ordinal);
    }

    [Fact]
    public void SqlServerM001_OptimizeForSequentialKey_OnMeasuredTailInsertObjects()
    {
        // ix_runtimes_retention and pk_results both take rows in ascending key order; a per-index
        // page-latch attribution measured tail-page contention on both (see JobRuntime.cs, JobResult.cs).
        Assert.Contains(
            "CREATE INDEX ix_runtimes_retention ON {{schema}}.runtimes (namespace_id, retention_until_utc, job_id) WHERE retention_until_utc IS NOT NULL AND status_code IN (100, 200, 220) WITH (OPTIMIZE_FOR_SEQUENTIAL_KEY = ON);",
            SqlServerM001
        );
        Assert.Contains(
            "CONSTRAINT pk_results PRIMARY KEY (job_id, execution_number) WITH (OPTIMIZE_FOR_SEQUENTIAL_KEY = ON)",
            SqlServerM001
        );
    }

    [Fact]
    public void PgM001_ClaimIndex_CarriesNoNullsClause()
    {
        // The claim orders by next_run_at_utc ASC with PostgreSQL's default NULLS LAST, which the
        // index must match key for key or every claim sorts its candidates instead of walking them.
        // The filter leaves NULL instants out, so a parked wait never sits in a claim's path.
        Assert.Contains(
            "CREATE INDEX IF NOT EXISTS ix_runtimes_claim_ready ON {{schema}}.runtimes (namespace_id, priority_code DESC, next_run_at_utc, job_id, status_code) WHERE status_code IN (10, 20) AND next_run_at_utc IS NOT NULL;",
            PgM001,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void NoM001_EmitsNullsFirst()
    {
        Assert.DoesNotContain("NULLS FIRST", PgM001, StringComparison.Ordinal);
        Assert.DoesNotContain("NULLS FIRST", SqlServerM001, StringComparison.Ordinal);
        Assert.DoesNotContain("NULLS FIRST", SqliteM001, StringComparison.Ordinal);
    }

    [Fact]
    public void PgAndSqliteM001_NeverEmitOptimizeForSequentialKey()
    {
        Assert.DoesNotContain("OPTIMIZE_FOR_SEQUENTIAL_KEY", PgM001, StringComparison.Ordinal);
        Assert.DoesNotContain("OPTIMIZE_FOR_SEQUENTIAL_KEY", SqliteM001, StringComparison.Ordinal);
    }
}
