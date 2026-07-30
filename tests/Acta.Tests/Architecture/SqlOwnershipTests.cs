using System.Text.RegularExpressions;
using Acta.Tests.Conformance.Sql;
using Xunit;

namespace Acta.Tests.Architecture;

/// <summary>
/// Table-ownership gate over provider SQL: every INSERT/UPDATE/DELETE target must be owned by the
/// writing capability (the <c>Sql/{Capability}/</c> folder), with two deliberate carve-outs. The
/// <c>events</c> ledger is append-anywhere by design (hot-path writes inline the JobEvent in the
/// same transaction), and the execution kernel's atomic routines that must mutate another owner's
/// state are declared one by one in <see cref="CrossOwnerRoutines"/>. A new cross-owner write fails
/// here until it is either moved behind the owning capability or explicitly declared.
/// </summary>
public sealed class SqlOwnershipTests
{
    /// <summary>Physical table -> owning capabilities. Every schema-snapshot table appears once.</summary>
    private static readonly Dictionary<string, string[]> OwnedTables = new(StringComparer.Ordinal)
    {
        ["alerts"] = ["Alerts"],
        // The slot substrate (signals, timers, variables, latches, progress share one table).
        // Execution covers its Checkpoints/Timers subdomain folders; Signals is a top-level capability.
        ["checkpoints"] = ["Execution", "Signals"],
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

    /// <summary>
    /// "{Capability}/{OperationStem}" routines allowed to write outside their owned tables: the
    /// atomic kernel invariants (enqueue tagging, completion advancing schedules and latches,
    /// schedule registration materializing job slots, state reset, retention purge).
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
        "Workers/ExtendWorkerLeases",
        "Workers/StartWorker",
        "Retention/PurgeExpiredData",
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
            // Sql/{Capability}/[{Subdomain}/]{Stem}[.routine].sql
            var segments = path.Split('/');
            var capability = segments[1];
            var stem = segments[^1].Replace(".routine", "").Replace(".sql", "");
            var routine = $"{capability}/{stem}";

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
            }
        }

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }
}
