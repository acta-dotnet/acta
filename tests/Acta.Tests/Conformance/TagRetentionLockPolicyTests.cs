using Acta.Tests.Conformance.Sql;
using Xunit;

namespace Acta.Tests.Conformance;

public sealed class TagRetentionLockPolicyTests
{
    [Fact]
    public void SqlServer_schedule_tag_cleanup_waits_for_locked_schedule_targets()
    {
        var sql = ProviderSqlResources.Read("mssql", "PurgeExpiredData");
        var captureStart = sql.IndexOf("INSERT INTO @schedule_del", StringComparison.Ordinal);
        var cleanupStart = sql.IndexOf("DELETE FROM {{schema}}.tags", captureStart, StringComparison.Ordinal);

        Assert.True(captureStart >= 0 && cleanupStart > captureStart, "Schedule capture and tag cleanup statements must remain ordered.");
        var scheduleCapture = sql[captureStart..cleanupStart];
        Assert.Contains("WITH (UPDLOCK, FORCESEEK)", scheduleCapture, StringComparison.Ordinal);
        Assert.DoesNotContain("READPAST", scheduleCapture, StringComparison.Ordinal);
    }
}
