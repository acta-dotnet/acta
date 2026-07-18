using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Emit;

/// <summary>
/// Static DDL-content checks: every <c>[DbColumn(... Default = DbDefault.X)]</c> annotation must
/// land in the emitted M001 migration. Guards against the metadata being silently dropped by the
/// emitter - a bug class the model is otherwise vulnerable to because the only way to spot it
/// today is to inspect the SQL by eye.
/// </summary>
public class M001DefaultsTests
{
    private static string SqlServerM001 =>
        File.ReadAllText(Path.Combine(IntegrationConfig.FindRepoRoot(), "src", "Acta.SqlServer", "Schema", "Migrations", "M001_init.sql"));

    private static string PgM001 =>
        File.ReadAllText(Path.Combine(IntegrationConfig.FindRepoRoot(), "src", "Acta.Postgres", "Schema", "Migrations", "M001_init.sql"));

    private static string SqliteM001 =>
        File.ReadAllText(Path.Combine(IntegrationConfig.FindRepoRoot(), "src", "Acta.Sqlite", "Schema", "Migrations", "M001_init.sql"));

    [Fact]
    public void SqlServerM001_RendersUtcNowDefaultForAuditTimestamps()
    {
        var sql = SqlServerM001;
        Assert.Contains("created_at_utc datetime2(3) DEFAULT SYSUTCDATETIME() NOT NULL", sql);
        Assert.Contains("modified_at_utc datetime2(3) DEFAULT SYSUTCDATETIME() NOT NULL", sql);
    }

    [Fact]
    public void SqlServerM001_RendersZeroDefaultForConcurrencyToken()
    {
        Assert.Contains("version int DEFAULT 0 NOT NULL", SqlServerM001);
    }

    [Fact]
    public void PgM001_RendersUtcNowDefaultForAuditTimestamps()
    {
        var sql = PgM001;
        Assert.Contains("created_at_utc timestamptz DEFAULT now() NOT NULL", sql);
        Assert.Contains("modified_at_utc timestamptz DEFAULT now() NOT NULL", sql);
    }

    [Fact]
    public void PgM001_RendersZeroDefaultForConcurrencyToken()
    {
        Assert.Contains("version integer DEFAULT 0 NOT NULL", PgM001);
    }

    [Fact]
    public void EveryM001_LeavesScheduleTimeZoneWithoutAServerDefault()
    {
        Assert.Contains("time_zone_id varchar(128) NOT NULL", SqlServerM001);
        Assert.Contains("time_zone_id varchar(128) NOT NULL", PgM001);
        Assert.Contains("time_zone_id text NOT NULL", SqliteM001);
        Assert.DoesNotContain("time_zone_id varchar(128) DEFAULT", SqlServerM001);
        Assert.DoesNotContain("time_zone_id varchar(128) DEFAULT", PgM001);
        Assert.DoesNotContain("time_zone_id text DEFAULT", SqliteM001);
    }
}
