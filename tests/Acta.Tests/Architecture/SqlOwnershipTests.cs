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
/// The <c>events</c> ledger is Execution-owned: Execution capabilities append freely, any other
/// module's append is declared one by one, and nothing outside the declared purge routines may
/// update or delete ledger rows.
/// </summary>
public sealed partial class SqlOwnershipTests
{
    /// <summary>Physical table -> owning capabilities. Every schema-snapshot table appears once.</summary>
    private static readonly Dictionary<string, string[]> OwnedTables = new(StringComparer.Ordinal)
    {
        ["alerts"] = ["Alerting"],
        // The slot substrate: signals, timers, variables, latches, and progress share one table.
        ["checkpoints"] = ["Checkpoints", "Signals", "Timers", "Execution"],
        ["definitions"] = ["Definitions"],
        // The ledger: write ownership is Execution's; the Operations Events capability is read-only.
        ["events"] = ["Execution"],
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
    /// The declared process-manager routines (the proposal's cross-owner atomic exception), each
    /// with the exact foreign tables it may write: enqueue stamping tags, single-job purge removing
    /// the job's tag/alert satellites, and the retention sweep. A new foreign-table write inside one
    /// of these routines fails until its table is declared here.
    /// </summary>
    private static readonly Dictionary<string, string[]> ProcessManagerRoutines = new(StringComparer.Ordinal)
    {
        ["Jobs/EnqueueOne"] = ["tags"],
        ["Jobs/EnqueueBatch"] = ["tags"],
        ["Jobs/PurgeJob"] = ["tags", "alerts"],
        ["Maintenance/PurgeExpiredData"] =
        [
            "jobs",
            "runtimes",
            "checkpoints",
            "steps",
            "results",
            "events",
            "alerts",
            "workers",
            "leases",
            "tags",
        ],
    };

    /// <summary>
    /// Non-Execution routines allowed to APPEND to the Execution-owned <c>events</c> ledger:
    /// Alerting's operator verbs record their audit event with the status flip in one transaction.
    /// </summary>
    private static readonly HashSet<string> ForeignEventAppendRoutines = new(StringComparer.Ordinal)
    {
        "Alerting/AcknowledgeJobAlert",
        "Alerting/ResolveJobAlertManual",
    };

    // Write targets across dialects: plain INSERT/UPDATE/DELETE plus the T-SQL alias forms
    // ("DELETE a FROM {{schema}}.alerts a", "UPDATE r ... FROM {{schema}}.runtimes r"), which the
    // plain patterns would silently skip.
    private static readonly Regex WriteTarget = MyRegex();

    private static readonly Regex AliasedUpdateTarget = new(
        @"\bUPDATE\s+(?<alias>[a-z]\w*)\s+SET\b[\s\S]*?\bFROM\s+\{\{schema\}\}\.(?<table>[a-z_]+)\s+\k<alias>\b",
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

            var writes = WriteTarget
                .Matches(sql)
                .Select(m =>
                    (
                        Table: m.Groups["table"].Value.ToLowerInvariant(),
                        IsInsert: m.Value.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase)
                    )
                )
                .Concat(AliasedUpdateTarget.Matches(sql).Select(m => (Table: m.Groups["table"].Value.ToLowerInvariant(), IsInsert: false)));
            foreach (var (table, isInsert) in writes)
            {
                if (table == "events" && isInsert && (module == "Execution" || ForeignEventAppendRoutines.Contains(routine)))
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
                if (
                    ownerModule != module
                    && !(ProcessManagerRoutines.TryGetValue(routine, out var foreignTables) && foreignTables.Contains(table))
                )
                {
                    violations.Add(
                        $"{dialect}:{path}: {module}-module routine writes {ownerModule}-owned table '{table}' (declare the table under '{routine}' in ProcessManagerRoutines or move the write)"
                    );
                }
            }
        }

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [GeneratedRegex(
        @"\b(?:INSERT\s+INTO|UPDATE|DELETE\s+(?:\w+\s+)?FROM)\s+\{\{schema\}\}\.(?<table>[a-z_]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        "sl-SI"
    )]
    private static partial Regex MyRegex();
}
