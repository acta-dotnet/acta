using System.Text.RegularExpressions;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Emit;

/// <summary>Static DDL-content checks on the emitted M001 baselines.</summary>
public class M001CodeChecksTests
{
    private static string SqlServerM001 =>
        File.ReadAllText(Path.Combine(IntegrationConfig.FindRepoRoot(), "src", "Acta.SqlServer", "Schema", "Migrations", "M001_init.sql"));

    private static string PgM001 =>
        File.ReadAllText(Path.Combine(IntegrationConfig.FindRepoRoot(), "src", "Acta.Postgres", "Schema", "Migrations", "M001_init.sql"));

    private static string SqliteM001 =>
        File.ReadAllText(Path.Combine(IntegrationConfig.FindRepoRoot(), "src", "Acta.Sqlite", "Schema", "Migrations", "M001_init.sql"));

    [Fact]
    public void No_code_column_is_pinned_to_a_value_list()
    {
        // A new code value ships without a migration, so no CHECK lists a column's code ids. Rule
        // CHECKs that compare a code with another column, like ck_runtimes_status_lease, stay.
        var valueList = new Regex(@"CHECK \((\w+) (IS NULL OR \1 )?IN \([0-9, ]+\)\)", RegexOptions.CultureInvariant);
        foreach (var m001 in new[] { SqlServerM001, PgM001, SqliteM001 })
        {
            Assert.DoesNotMatch(valueList, m001);
        }
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

    [Fact]
    public void SqlServerM001_OptimizeForSequentialKey_OnMeasuredTailInsertObjects()
    {
        // pk_results takes rows in ascending key order; a per-index page-latch attribution measured tail-page
        // contention on it (see ResultEntity.cs).
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
