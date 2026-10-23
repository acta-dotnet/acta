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
    public void PgBaseline_ClaimIndex_CarriesNoNullsClause()
    {
        // The claim orders by next_run_at_utc ASC with PostgreSQL's default NULLS LAST, which the
        // index must match key for key or every claim sorts its candidates instead of walking them.
        // The filter leaves NULL instants out, so a parked wait never sits in a claim's path.
        Assert.Contains(
            "CREATE INDEX IF NOT EXISTS ix_jobs_state_claim_ready ON acta.jobs_state (namespace_id, job_priority_code DESC, next_run_at_utc, job_id, job_status_code) WHERE job_status_code IN (10, 20) AND next_run_at_utc IS NOT NULL;",
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
    public void EventsKey_LeadsWithWorker_AndSqliteKeepsTheRowid()
    {
        // The servers key events by (worker_id, event_id) with event_id still the identity; SQLite's AUTOINCREMENT needs
        // the rowid alias as the whole key, so it keeps event_id alone and worker_id is a plain column.
        var sqlServer = EventsTable(SqlServerBaseline);
        Assert.Contains("event_id bigint IDENTITY(1,1) NOT NULL", sqlServer, StringComparison.Ordinal);
        Assert.Contains("worker_id int DEFAULT 0 NOT NULL", sqlServer, StringComparison.Ordinal);
        Assert.Contains(
            "CONSTRAINT pk_events PRIMARY KEY (worker_id, event_id) WITH (OPTIMIZE_FOR_SEQUENTIAL_KEY = ON)",
            sqlServer,
            StringComparison.Ordinal
        );

        var pg = EventsTable(PgBaseline);
        Assert.Contains("event_id bigint GENERATED BY DEFAULT AS IDENTITY NOT NULL", pg, StringComparison.Ordinal);
        Assert.Contains("worker_id integer DEFAULT 0 NOT NULL", pg, StringComparison.Ordinal);
        Assert.Contains("CONSTRAINT pk_events PRIMARY KEY (worker_id, event_id)", pg, StringComparison.Ordinal);

        var sqlite = EventsTable(SqliteBaseline);
        Assert.Contains("event_id integer PRIMARY KEY AUTOINCREMENT", sqlite, StringComparison.Ordinal);
        Assert.Contains("worker_id integer DEFAULT 0 NOT NULL", sqlite, StringComparison.Ordinal);
        Assert.DoesNotContain("pk_events", sqlite, StringComparison.Ordinal);
    }

    private static string EventsTable(string baseline) =>
        baseline.Split("CREATE TABLE", StringSplitOptions.None).First(t => t.Contains(".events (", StringComparison.Ordinal)).Split(");")[
            0
        ];

    [Fact]
    public void PgAndSqliteBaseline_NeverEmitOptimizeForSequentialKey()
    {
        Assert.DoesNotContain("OPTIMIZE_FOR_SEQUENTIAL_KEY", PgBaseline, StringComparison.Ordinal);
        Assert.DoesNotContain("OPTIMIZE_FOR_SEQUENTIAL_KEY", SqliteBaseline, StringComparison.Ordinal);
    }
}
