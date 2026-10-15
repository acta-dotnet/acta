using System.Text.RegularExpressions;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Emit;

/// <summary>
/// Static DDL-content checks over each provider's whole migration chain: a must-appear check passes on the baseline, and
/// a must-never-appear check covers every later migration too. The default checks guard <c>[DbColumn(Default = ...)]</c>
/// metadata the emitter could otherwise drop without anyone reading the SQL noticing.
/// </summary>
public class BaselineCodeChecksTests
{
    private static string SqlServerBaseline => ProvisionScript("mssql");

    private static string PgBaseline => ProvisionScript("pg");

    private static string SqliteBaseline => ProvisionScript("sqlite");

    /// <summary>
    /// A provider's drift-checked provisioning script: every migration in apply order, rendered for the default schema.
    /// </summary>
    internal static string ProvisionScript(string token) =>
        File.ReadAllText(Path.Combine(IntegrationConfig.FindRepoRoot(), "docs", "reference", $"schema-{token}.sql"));

    [Fact]
    public void SqlServerBaseline_RendersUtcNowDefaultForAuditTimestamps()
    {
        var sql = SqlServerBaseline;
        Assert.Contains("created_at_utc datetime2(3) DEFAULT SYSUTCDATETIME() NOT NULL", sql);
        Assert.Contains("modified_at_utc datetime2(3) DEFAULT SYSUTCDATETIME() NOT NULL", sql);
    }

    [Fact]
    public void SqlServerBaseline_RendersZeroDefaultForConcurrencyToken()
    {
        Assert.Contains("version int DEFAULT 0 NOT NULL", SqlServerBaseline);
    }

    [Fact]
    public void PgBaseline_RendersUtcNowDefaultForAuditTimestamps()
    {
        var sql = PgBaseline;
        Assert.Contains("created_at_utc timestamptz DEFAULT now() NOT NULL", sql);
        Assert.Contains("modified_at_utc timestamptz DEFAULT now() NOT NULL", sql);
    }

    [Fact]
    public void PgBaseline_RendersZeroDefaultForConcurrencyToken()
    {
        Assert.Contains("version integer DEFAULT 0 NOT NULL", PgBaseline);
    }

    [Fact]
    public void EveryBaseline_LeavesScheduleTimeZoneWithoutAServerDefault()
    {
        Assert.Contains("time_zone_id varchar(128) NOT NULL", SqlServerBaseline);
        Assert.Contains("time_zone_id varchar(128) NOT NULL", PgBaseline);
        Assert.Contains("time_zone_id text NOT NULL", SqliteBaseline);
        Assert.DoesNotContain("time_zone_id varchar(128) DEFAULT", SqlServerBaseline);
        Assert.DoesNotContain("time_zone_id varchar(128) DEFAULT", PgBaseline);
        Assert.DoesNotContain("time_zone_id text DEFAULT", SqliteBaseline);
    }

    [Fact]
    public void No_code_column_is_pinned_to_a_value_list()
    {
        // A new code value ships without a migration, so no CHECK lists a column's code ids. Rule
        // CHECKs that compare a code with another column, like ck_runtimes_status_lease, stay.
        var valueList = new Regex(@"CHECK \((\w+) (IS NULL OR \1 )?IN \([0-9, ]+\)\)", RegexOptions.CultureInvariant);
        foreach (var baseline in new[] { SqlServerBaseline, PgBaseline, SqliteBaseline })
        {
            Assert.DoesNotMatch(valueList, baseline);
        }
    }

    [Fact]
    public void Locks_carry_a_hold_token_and_no_kind_discriminator()
    {
        // 0.9.0 dropped the one-value kind column: the key's middle segment (.lock. / .sem. / .rate.)
        // discriminates the spaces, and the reap sweeps the whole table by expiry. A future
        // primitive re-adds a discriminator additively or gets its own table; this pins that the
        // baseline does not resurrect the dead column, and that every hold carries its CAS token.
        foreach (var baseline in new[] { SqlServerBaseline, PgBaseline, SqliteBaseline })
        {
            var locksTable = baseline
                .Split("CREATE TABLE", StringSplitOptions.None)
                .First(t => t.Contains(".locks (", StringComparison.Ordinal));
            Assert.DoesNotContain("kind_code", locksTable, StringComparison.Ordinal);
            Assert.Contains("hold_token", locksTable, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SqlServerBaseline_OptimizeForSequentialKey_OnMeasuredTailInsertObjects()
    {
        // pk_results takes rows in ascending key order; a per-index page-latch attribution measured tail-page
        // contention on it (see ResultEntity.cs).
        Assert.Contains(
            "CONSTRAINT pk_results PRIMARY KEY (job_id, execution_number) WITH (OPTIMIZE_FOR_SEQUENTIAL_KEY = ON)",
            SqlServerBaseline
        );
    }

    [Fact]
    public void PgBaseline_ClaimIndex_CarriesNoNullsClause()
    {
        // The claim orders by next_run_at_utc ASC with PostgreSQL's default NULLS LAST, which the
        // index must match key for key or every claim sorts its candidates instead of walking them.
        // The filter leaves NULL instants out, so a parked wait never sits in a claim's path.
        Assert.Contains(
            "CREATE INDEX IF NOT EXISTS ix_runtimes_claim_ready ON acta.runtimes (namespace_id, priority_code DESC, next_run_at_utc, job_id, status_code) WHERE status_code IN (10, 20) AND next_run_at_utc IS NOT NULL;",
            PgBaseline,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void NoBaseline_EmitsNullsFirst()
    {
        Assert.DoesNotContain("NULLS FIRST", PgBaseline, StringComparison.Ordinal);
        Assert.DoesNotContain("NULLS FIRST", SqlServerBaseline, StringComparison.Ordinal);
        Assert.DoesNotContain("NULLS FIRST", SqliteBaseline, StringComparison.Ordinal);
    }

    [Fact]
    public void PgAndSqliteBaseline_NeverEmitOptimizeForSequentialKey()
    {
        Assert.DoesNotContain("OPTIMIZE_FOR_SEQUENTIAL_KEY", PgBaseline, StringComparison.Ordinal);
        Assert.DoesNotContain("OPTIMIZE_FOR_SEQUENTIAL_KEY", SqliteBaseline, StringComparison.Ordinal);
    }
}
