using Acta.Tests.Conformance.Sql;
using Xunit;

namespace Acta.Tests.Conformance;

/// <summary>
/// Every SQL stem exists in PostgreSQL, SQL Server, and SQLite alike, whether the file is a routine, a
/// view, or inline SQL, unless it is a reviewed provider-specific exception.
/// </summary>
public sealed class ProviderResourceParityTests
{
    // Reviewed provider-specific supplemental resources, exempt from cross-provider parity. Every entry
    // needs a reason; an entry that becomes shared (or disappears) is stale and fails the gate.
    private static readonly HashSet<string> ProviderSpecific = new(StringComparer.Ordinal)
    {
        // SQLite has no DROP SCHEMA; the SQLite bootstrap clears sqlite_master objects in code instead.
        "DropSchema",
    };

    internal static readonly string[] Dialects = ["pg", "mssql", "sqlite"];

    /// <summary>
    /// Asserts every feature-local logical resource is owned by all three providers (or is a reviewed
    /// provider-specific exception), and that the allowlist has no stale entries.
    /// </summary>
    [Fact]
    public void Feature_local_resource_inventories_match_across_providers()
    {
        var inventories = Dialects.ToDictionary(d => d, d => LogicalResources(d).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        var union = inventories.Values.SelectMany(s => s).ToHashSet(StringComparer.Ordinal);

        var gaps = union
            .Where(id => !ProviderSpecific.Contains(id))
            .Select(id => (Id: id, Missing: Dialects.Where(d => !inventories[d].Contains(id)).ToList()))
            .Where(x => x.Missing.Count > 0)
            .OrderBy(x => x.Id)
            .Select(x => $"{x.Id}: missing in {string.Join(", ", x.Missing)}")
            .ToList();

        var stale = ProviderSpecific
            .Where(id => !union.Contains(id) || Dialects.All(d => inventories[d].Contains(id)))
            .OrderBy(x => x)
            .ToList();

        Assert.True(gaps.Count == 0, "Logical resources not owned by every provider:\n" + string.Join("\n", gaps));
        Assert.True(stale.Count == 0, "Allowlist entries now shared or non-existent (remove them):\n" + string.Join("\n", stale));
    }

    /// <summary>
    /// Asserts no provider embeds two physical resources for the same logical id (e.g. both an inline
    /// body and a routine body for one operation).
    /// </summary>
    [Fact]
    public void Feature_local_resources_are_unique_within_each_provider()
    {
        var duplicated = Dialects
            .SelectMany(d =>
                LogicalResources(d).GroupBy(id => id, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => $"{d}: {g.Key}")
            )
            .OrderBy(x => x)
            .ToList();

        Assert.True(
            duplicated.Count == 0,
            "Logical resources with multiple physical bodies in one provider:\n" + string.Join("\n", duplicated)
        );
    }

    // The stems of one dialect's SQL files, views and schema commands included.
    internal static List<string> LogicalResources(string dialect) =>
        [.. ProviderSqlResources.EnumerateIncludingViews(dialect).Select(r => ProviderSqlResources.Stem(r.LogicalPath))];
}

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
