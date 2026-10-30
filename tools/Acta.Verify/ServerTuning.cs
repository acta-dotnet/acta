using System.Globalization;

namespace Acta.Verify;

/// <summary>
/// The settings an owned PostgreSQL or SQL Server container runs with, resolved from the protocol's
/// <c>provisioning</c> terms and the Docker VM, and the check of what the server reads back against them.
/// </summary>
public static class ServerTuning
{
    /// <summary>
    /// The container memory cap in MB: the protocol's fraction of the Docker VM memory, never above its maximum.
    /// Throws when the cap falls below the protocol's minimum.
    /// </summary>
    public static int MemoryCapMb(RunPlan plan, long dockerVmMemoryBytes)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var vmMb = dockerVmMemoryBytes / 1048576;
        var fraction = Owned(plan, "memory_cap_fraction_of_docker_vm");
        var minimum = (int)Owned(plan, "memory_cap_min_mb");
        var cap = (int)Math.Min(Owned(plan, "memory_cap_max_mb"), Math.Floor(vmMb * fraction));
        if (cap < minimum)
        {
            throw new ServerSetupException(
                $"The Docker VM has {vmMb} MB of memory; an owned database container takes {fraction:0.####} of it and needs at least {minimum} MB. Give Docker more memory."
            );
        }
        return cap;
    }

    /// <summary>The tempdb data files for the logical CPUs SQL Server sees, within the protocol's bounds.</summary>
    public static int TempdbDataFiles(RunPlan plan, int cpus) =>
        Math.Clamp(cpus, (int)Term(plan, "sqlserver", "tempdb_data_files_min"), (int)Term(plan, "sqlserver", "tempdb_data_files_max"));

    /// <summary>The PostgreSQL container's shared memory in MB: the protocol's fraction of the cap, never above its maximum.</summary>
    public static int ShmSizeMb(RunPlan plan, int capMb) =>
        (int)Math.Min(Term(plan, "postgres", "shm_size_max_mb"), Math.Floor(capMb * Term(plan, "postgres", "shm_size_fraction_of_cap")));

    /// <summary>The <c>-c</c> settings an owned PostgreSQL container starts with, in MB where a setting is a size.</summary>
    public static Dictionary<string, string> PostgresFlags(RunPlan plan, int capMb) =>
        new(StringComparer.Ordinal)
        {
            ["shared_buffers"] = Mb(Math.Floor(capMb * Term(plan, "postgres", "shared_buffers_fraction_of_cap"))),
            ["effective_cache_size"] = Mb(Math.Floor(capMb * Term(plan, "postgres", "effective_cache_size_fraction_of_cap"))),
            ["maintenance_work_mem"] = Mb(Term(plan, "postgres", "maintenance_work_mem_mb")),
            ["max_wal_size"] = Mb(Term(plan, "postgres", "max_wal_size_mb")),
            ["max_connections"] = Number(MaxConnections(plan)),
        };

    /// <summary>
    /// The connections an owned PostgreSQL server allows: the most worker processes the plan runs at once, each on the
    /// drivers default pool of 100, the controllers enqueue pool, and a margin for the observer, the ledger checks and
    /// the readback. A normal trial opens a fraction of it; the chaos fleet is what needs the headroom.
    /// </summary>
    public static int MaxConnections(RunPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var cellWorkers = plan
            .DiagnosticCells.Where(c => plan.IsExtended || plan.RequestedDiagnosticCells.Contains(c.Name))
            .Select(c => c.Workers)
            .DefaultIfEmpty(1)
            .Max();
        return Math.Max(plan.ChaosWorkers("pg"), cellWorkers) * 100 + OwnedProvider.EnqueuePoolSize(plan.ProducerCalls["pg"]) + 64;
    }

    /// <summary>The server settings an owned SQL Server container is configured with, as its readback names them.</summary>
    public static Dictionary<string, string> SqlServerSettings(RunPlan plan, int capMb, int cpus) =>
        new(StringComparer.Ordinal)
        {
            ["max_server_memory_mb"] = Number(Math.Floor(capMb * Term(plan, "sqlserver", "max_server_memory_fraction_of_cap"))),
            ["max_degree_of_parallelism"] = Number(Term(plan, "sqlserver", "max_degree_of_parallelism")),
            ["cost_threshold_for_parallelism"] = Number(Term(plan, "sqlserver", "cost_threshold_for_parallelism")),
            ["optimize_for_ad_hoc_workloads"] = Number(Term(plan, "sqlserver", "optimize_for_ad_hoc_workloads")),
            ["memory_optimized_tempdb_metadata"] = Number(Term(plan, "sqlserver", "memory_optimized_tempdb_metadata")),
            [$"trace_flag_{Number(Term(plan, "sqlserver", "trace_flag"))}"] = "on",
            ["tempdb_data_files"] = Files(
                Enumerable.Repeat(
                    ((long)Term(plan, "sqlserver", "tempdb_data_file_mb"), (long)Term(plan, "sqlserver", "tempdb_data_growth_mb"), false),
                    TempdbDataFiles(plan, cpus)
                )
            ),
            ["tempdb_log_files"] = Files([
                ((long)Term(plan, "sqlserver", "tempdb_log_mb"), (long)Term(plan, "sqlserver", "tempdb_log_growth_mb"), false),
            ]),
        };

    /// <summary>The files a SQL Server run database is created with, as its readback names them.</summary>
    public static Dictionary<string, string> SqlServerDatabaseFiles(RunPlan plan) =>
        new(StringComparer.Ordinal)
        {
            ["database_data_files"] = Files([((long)Database(plan, "data_mb"), (long)Database(plan, "data_growth_mb"), false)]),
            ["database_log_files"] = Files([((long)Database(plan, "log_mb"), (long)Database(plan, "log_growth_mb"), false)]),
        };

    /// <summary>
    /// Describes database files as a count, size and growth per distinct shape, so uniformly sized files read as one
    /// term. Growth is in MB, or a percentage where <c>PercentGrowth</c> is set.
    /// </summary>
    public static string Files(IEnumerable<(long SizeMb, long Growth, bool PercentGrowth)> files) =>
        string.Join(
            "; ",
            files
                .GroupBy(f => f)
                .OrderBy(g => g.Key.SizeMb)
                .ThenBy(g => g.Key.Growth)
                .Select(g =>
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{g.Count()} x {g.Key.SizeMb} MB, growth {g.Key.Growth}{(g.Key.PercentGrowth ? "%" : " MB")}"
                    )
                )
        );

    /// <summary>Each requested setting whose readback differs, as <c>name: requested X, read Y</c>.</summary>
    public static string[] Mismatches(IReadOnlyDictionary<string, string> requested, IReadOnlyDictionary<string, string> readback)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(readback);
        return
        [
            .. requested
                .Where(r => !readback.TryGetValue(r.Key, out var read) || read != r.Value)
                .Select(r => $"{r.Key}: requested {r.Value}, read {readback.GetValueOrDefault(r.Key, "nothing")}"),
        ];
    }

    /// <summary>Reads a number from the protocol's <c>provisioning.owned_server.&lt;server&gt;</c> terms.</summary>
    public static double Term(RunPlan plan, string server, string name)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.Policy("provisioning", "owned_server", server, name);
    }

    /// <summary>Reads a number from the protocol's <c>provisioning.verify_database.sqlserver</c> terms.</summary>
    public static double Database(RunPlan plan, string name)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.Policy("provisioning", "verify_database", "sqlserver", name);
    }

    private static double Owned(RunPlan plan, string name) => plan.Policy("provisioning", "owned_server", name);

    private static string Mb(double value) => Number(value) + "MB";

    private static string Number(double value) => ((long)value).ToString(CultureInfo.InvariantCulture);
}
