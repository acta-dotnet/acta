using System.Reflection;
using Acta.Tests.Conformance.Sql;
using Xunit;

namespace Acta.Tests.Conformance;

/// <summary>
/// Provider-resource gate for the architecture migration: every capability SQL resource a provider
/// embeds under <c>Sql/</c> normalizes to a logical resource id (independent of dialect and
/// of routine/view/inline physical form), and the normalized inventories must stay paired across
/// PostgreSQL, SQL Server, and SQLite. Vacuously green until feature-local provider SQL starts landing;
/// tightens automatically as each feature slice moves its SQL into the providers.
/// </summary>
public sealed class ProviderResourceParityTests
{
    // Reviewed provider-specific supplemental resources, exempt from cross-provider parity. Every entry
    // needs a reason; an entry that becomes shared (or disappears) is stale and fails the gate.
    // SQLite has no batched-completion routine: the Bulk profile degrades to Direct there, so
    // complete_executions_batch exists only on the routine providers (Postgres, SQL Server).
    private static readonly HashSet<string> ProviderSpecific = new(StringComparer.Ordinal) { "Execution/CompleteExecutionsBatch" };

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

    // Normalizes "{Assembly}.Sql.Jobs.Enqueue.routine.sql" to "Jobs/Enqueue": the physical execution
    // form (.routine/.view infix) and the Sql/ root marker drop out, matching the logical ids used by
    // tools/sql-compare.ps1 and the changed-sibling report. Schema commands keep their Schema/ segment.
    internal static List<string> LogicalResources(string dialect)
    {
        var assembly = Assembly.Load(ProviderSqlResources.ProviderAssemblyName(dialect));
        var prefix = assembly.GetName().Name + ".";

        var ids = new List<string>();
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(prefix, StringComparison.Ordinal) || !resource.EndsWith(".sql", StringComparison.Ordinal))
            {
                continue;
            }

            var tail = resource[prefix.Length..];
            // Schema commands are deliberately not provider-paired (SQLite has no DROP SCHEMA), so
            // they stay out of the parity inventory; ordered migrations are not Sql/ resources at all.
            if (!tail.StartsWith("Sql.", StringComparison.Ordinal) || tail.StartsWith("Sql.Schema.", StringComparison.Ordinal))
            {
                continue;
            }

            var path = SqlLogicalPath.FromResourceTail(tail)[..^".sql".Length];
            foreach (var infix in (string[])[".routine", ".view"])
            {
                if (path.EndsWith(infix, StringComparison.Ordinal))
                {
                    path = path[..^infix.Length];
                }
            }

            ids.Add(path["Sql/".Length..]);
        }

        return ids;
    }
}

public sealed class TagRetentionLockPolicyTests
{
    [Fact]
    public void SqlServer_schedule_tag_cleanup_waits_for_locked_schedule_targets()
    {
        var sql = ProviderSqlResources
            .Enumerate("mssql")
            .Single(resource => resource.LogicalPath == "Sql/Maintenance/PurgeExpiredData.routine.sql")
            .Sql;
        var captureStart = sql.IndexOf("INSERT INTO @schedule_del", StringComparison.Ordinal);
        var cleanupStart = sql.IndexOf("DELETE FROM {{schema}}.tags", captureStart, StringComparison.Ordinal);

        Assert.True(captureStart >= 0 && cleanupStart > captureStart, "Schedule capture and tag cleanup statements must remain ordered.");
        var scheduleCapture = sql[captureStart..cleanupStart];
        Assert.Contains("WITH (UPDLOCK)", scheduleCapture, StringComparison.Ordinal);
        Assert.DoesNotContain("READPAST", scheduleCapture, StringComparison.Ordinal);
    }
}
