using System.Text.RegularExpressions;
using Acta.Tests.Conformance.Sql;
using Xunit;

namespace Acta.Tests.Architecture;

/// <summary>
/// Table-ownership gate over provider SQL, whose layout is <c>Sql/{Area}/{Capability}/</c> (a
/// single-capability area keeps its files at the area root): every INSERT/UPDATE/DELETE target must be
/// owned by the writing capability, with the atomic kernel routines declared one by one. Any capability
/// may append to the <c>events</c> ledger; only its owners and the declared routines update or delete it.
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
        // Notes is the one application-authored writer, and the only event code an app can emit.
        ["events"] = ["Execution", "Notes"],
        ["jobs"] = ["Jobs"],
        ["locks"] = ["Locks"],
        // Enqueue registers a job's lane and retention deletes an unreferenced one.
        ["lanes"] = ["Jobs"],
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
    /// "{Capability}/{OperationStem}" routines allowed to write outside their owned tables: the atomic
    /// kernel invariants (enqueue tagging, completion advancing schedules and latches, schedule
    /// registration materializing job slots, state reset, restart re-opening exhausted steps, retention
    /// purge, and the operator retire cancelling the definition's parked runtimes).
    /// </summary>
    private static readonly HashSet<string> CrossOwnerRoutines = new(StringComparer.Ordinal)
    {
        "Definitions/RegisterJobDefinitions",
        "Definitions/RetireJobDefinition",
        "Execution/CompleteExecution",
        "Jobs/EnqueueOne",
        "Jobs/EnqueueBatch",
        "Jobs/PurgeJob",
        "Jobs/ResetJobState",
        "Jobs/RestartJob",
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
            // Sql/{Area}/{Capability}/{Stem}[.routine].sql, or Sql/{Area}/{Stem}[.routine].sql when the area
            // has a single capability (capability == area folder name).
            var segments = path.Split('/');
            var capability = segments.Length > 3 ? segments[2] : segments[1];
            var stem = segments[^1].Replace(".routine", "").Replace(".sql", "");
            var routine = $"{capability}/{stem}";

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

    [GeneratedRegex(
        @"\b(?:INSERT\s+INTO|UPDATE|DELETE\s+(?:\w+\s+)?FROM)\s+\{\{schema\}\}\.(?<table>[a-z_]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        "sl-SI"
    )]
    private static partial Regex MyRegex();
}
