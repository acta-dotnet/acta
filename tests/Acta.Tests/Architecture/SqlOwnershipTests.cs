using System.Text.RegularExpressions;
using Acta.Tests.Conformance.Sql;
using Xunit;

namespace Acta.Tests.Architecture;

/// <summary>
/// Two-tier table-ownership gate over provider SQL, whose layout is <c>Sql/{Module}/{Capability}/</c>
/// (single-capability modules keep their files at the module root). The capability tier is the fine
/// gate: every INSERT/UPDATE/DELETE target must be owned by the writing capability, with the atomic
/// kernel routines declared one by one. The module tier is the architectural gate: a write crossing
/// module lines needs its own declaration, so within-Execution coupling (enqueue touching runtimes,
/// completion advancing schedules) stays visible at the fine tier without weakening the module rule.
/// The <c>events</c> ledger is append-anywhere by design.
/// </summary>
public sealed class SqlOwnershipTests
{
    /// <summary>Physical table -> owning capabilities. Every schema-snapshot table appears once.</summary>
    private static readonly Dictionary<string, string[]> OwnedTables = new(StringComparer.Ordinal)
    {
        ["alerts"] = ["Alerting"],
        // The slot substrate: signals, timers, variables, latches, and progress share one table.
        ["checkpoints"] = ["Checkpoints", "Signals", "Timers", "Execution"],
        ["definitions"] = ["Definitions"],
        ["events"] = ["Events"],
        ["jobs"] = ["Jobs"],
        ["leases"] = ["Locks"],
        ["namespaces"] = ["Namespaces"],
        ["results"] = ["Execution"],
        // The unit-of-work pair: a jobs row and its 1:1 runtimes row change together.
        ["runtimes"] = ["Execution", "Jobs"],
        ["schedules"] = ["Schedules"],
        ["settings"] = ["Settings"],
        ["steps"] = ["Execution"],
        ["tags"] = ["Tags"],
        ["tenants"] = ["Tenants"],
        ["workers"] = ["Workers"],
    };

    /// <summary>Capability -> owning module, for the module tier (mirrors the Sql/ folder layout).</summary>
    private static readonly Dictionary<string, string> CapabilityModule = new(StringComparer.Ordinal)
    {
        ["Execution"] = "Execution",
        ["Checkpoints"] = "Execution",
        ["ChildLatches"] = "Execution",
        ["Schema"] = "Schema",
        ["Timers"] = "Execution",
        ["Jobs"] = "Execution",
        ["Signals"] = "Execution",
        ["Workers"] = "Execution",
        ["Schedules"] = "Execution",
        ["Definitions"] = "Execution",
        ["Namespaces"] = "Execution",
        ["Tenants"] = "Execution",
        ["Alerting"] = "Alerting",
        ["Outbox"] = "Outbox",
        ["Overview"] = "Operations",
        ["Events"] = "Operations",
        ["Tags"] = "Operations",
        ["Settings"] = "Execution",
        ["Maintenance"] = "Maintenance",
        ["Locks"] = "Services",
        ["Time"] = "Services",
    };

    /// <summary>
    /// "{Capability}/{OperationStem}" routines allowed to write outside their owned tables within
    /// their module: the atomic kernel invariants (enqueue tagging, completion advancing schedules
    /// and latches, schedule registration materializing job slots, state reset, retention purge).
    /// </summary>
    private static readonly HashSet<string> CrossOwnerRoutines = new(StringComparer.Ordinal)
    {
        "Definitions/RegisterJobDefinitions",
        "Execution/CompleteExecution",
        "Jobs/EnqueueOne",
        "Jobs/EnqueueBatch",
        "Jobs/PurgeJob",
        "Jobs/ResetJobState",
        "Schedules/PauseSchedule",
        "Schedules/RegisterScheduledJobs",
        "Schedules/ResumeSchedule",
        "Schedules/SetScheduleOverrides",
        "Schedules/TriggerScheduleNow",
        "Signals/RaiseSignal",
        "Timers/ArmOrConsumeSleepTimer",
        "Workers/ExtendWorkerLeases",
        "Workers/StartWorker",
        "Maintenance/PurgeExpiredData",
    };

    /// <summary>
    /// The strictly smaller set of routines whose writes cross MODULE lines: enqueue and purge
    /// touching the tags/alerts substrates, and the retention sweep. Everything else in
    /// <see cref="CrossOwnerRoutines"/> is within-Execution coupling.
    /// </summary>
    private static readonly HashSet<string> CrossModuleRoutines = new(StringComparer.Ordinal)
    {
        "Jobs/EnqueueOne",
        "Jobs/EnqueueBatch",
        "Jobs/PurgeJob",
        "Maintenance/PurgeExpiredData",
    };

    private static readonly Regex WriteTarget = new(
        @"\b(?:INSERT\s+INTO|UPDATE|DELETE\s+FROM)\s+\{\{schema\}\}\.(?<table>[a-z_]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    [Theory]
    [InlineData("pg")]
    [InlineData("mssql")]
    [InlineData("sqlite")]
    public void Provider_sql_writes_only_owned_tables(string dialect)
    {
        var violations = new List<string>();
        foreach (var (path, sql) in ProviderSqlResources.Enumerate(dialect))
        {
            // Sql/{Module}/{Capability}/{Stem}[.routine].sql, or Sql/{Module}/{Stem}[.routine].sql
            // when the module has a single capability (capability == module folder name).
            var segments = path.Split('/');
            var module = segments[1];
            var capability = segments.Length > 3 ? segments[2] : segments[1];
            var stem = segments[^1].Replace(".routine", "").Replace(".sql", "");
            var routine = $"{capability}/{stem}";

            if (!CapabilityModule.TryGetValue(capability, out var declaredModule))
            {
                violations.Add($"{dialect}:{path}: unmapped capability folder '{capability}'");
                continue;
            }

            if (declaredModule != module)
            {
                violations.Add($"{dialect}:{path}: capability '{capability}' belongs under Sql/{declaredModule}/, not Sql/{module}/");
            }

            foreach (Match match in WriteTarget.Matches(sql))
            {
                var table = match.Groups["table"].Value.ToLowerInvariant();
                var isInsert = match.Value.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase);
                if (table == "events" && isInsert)
                {
                    continue;
                }

                if (!OwnedTables.TryGetValue(table, out var owners))
                {
                    violations.Add($"{dialect}:{path}: writes unmapped table '{table}'");
                    continue;
                }

                if (!owners.Contains(capability) && !CrossOwnerRoutines.Contains(routine))
                {
                    violations.Add($"{dialect}:{path}: writes {owners[0]}-owned table '{table}' (declare '{routine}' or move the write)");
                }

                var ownerModule = CapabilityModule[owners[0]];
                if (ownerModule != module && !CrossModuleRoutines.Contains(routine))
                {
                    violations.Add(
                        $"{dialect}:{path}: {module}-module routine writes {ownerModule}-owned table '{table}' (declare '{routine}' in CrossModuleRoutines or move the write)"
                    );
                }
            }
        }

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }
}
