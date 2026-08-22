using System.Globalization;
using Acta;

namespace Anvil.Burst;

/// <summary>
/// The shipped bounds this certification is measured against, copied here with the symbol that owns each
/// one named. They are internal to <c>Acta.Runtime</c> (<c>AlertsJob.DefaultDrain</c>, its
/// <c>DeliverBatchSize</c>), so a harness outside that assembly cannot read them - and a certification
/// that silently re-derived them from whatever the engine currently does would assert nothing. If a bound
/// moves, this file is the one edit, and the run that follows says so on its verdict line.
/// </summary>
internal static class BurstBounds
{
    /// <summary>Events one generate batch reads (<c>AlertsJob.DefaultDrain.BatchSize</c>).</summary>
    public const int GenerateBatchSize = 256;

    /// <summary>Batches one generate drain may complete (<c>AlertsJob.DefaultDrain.MaxBatches</c>).</summary>
    public const int GenerateMaxBatches = 40;

    /// <summary>Events one invocation can project: the drain's batch size times its batch cap.</summary>
    public const int OneInvocationCeiling = GenerateBatchSize * GenerateMaxBatches;

    /// <summary>External attempts one invocation may make (<c>AlertsJob.DeliverBatchSize</c>).</summary>
    public const int DeliverBatchSize = 256;

    /// <summary>The plan's wall-clock budget for draining a 10K backlog on the certification host.</summary>
    public static readonly TimeSpan DrainBudget = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The name of the projector's cursor variable (<c>AlertsJob.CursorVariableName</c>). Read through
    /// <c>IJobs.GetCheckpointsAsync</c> on the <c>sys.alerts</c> slot, which is how the harness turns "how
    /// far did that invocation get" into a number without reaching into the engine.
    /// </summary>
    public const string CursorVariableName = "alerts-cursor";

    /// <summary>The recurring slot the projector runs on; its deduplication key is its job name.</summary>
    public const string AlertsJobName = "sys.alerts";

    /// <summary>The recurring slot the retention sweep runs on.</summary>
    public const string RetentionJobName = "sys.retention";

    /// <summary>Every framework recurring job declares its shipped schedule under this name.</summary>
    public const string DefaultScheduleName = "default";

    /// <summary>
    /// How far back the harness ages seeded events so the projection read's safe horizon admits them. The
    /// horizon is two <c>SqlProviderOptions.CommandTimeout</c>s wide (30s by default, so 60s); 30 minutes
    /// leaves room for that plus any skew between this host's clock and the database's, which is the clock
    /// the horizon predicate actually compares against.
    /// </summary>
    public static readonly TimeSpan HorizonBackdate = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The alert retention window the harness runs under: the validator's floor, so the stuck subset only
    /// has to be aged by days rather than by months for the sweep to become eligible to purge it.
    /// </summary>
    public static readonly TimeSpan AlertRetention = TimeSpan.FromDays(1);

    /// <summary>How far past <see cref="AlertRetention"/> the stuck subset is aged.</summary>
    public static readonly TimeSpan RetentionBackdate = TimeSpan.FromDays(3);

    /// <summary>
    /// The re-notification spacing the harness runs under, one second rather than the shipped day, so an
    /// open delivered incident is due again on the next invocation. See <c>BurstHost</c> for why the
    /// resolved-alerts check needs that.
    /// </summary>
    public static readonly TimeSpan ReminderInterval = TimeSpan.FromSeconds(1);
}

/// <summary>Parsed command line for one burst certification.</summary>
internal sealed record BurstOptions
{
    public required string Provider { get; init; }

    public required string Schema { get; init; }

    public required string Namespace { get; init; }

    public required string RunId { get; init; }

    /// <summary>Alertable events to seed. One seeded job fails once, so this is also the job count.</summary>
    public int Events { get; init; } = 10_000;

    public int Executors { get; init; } = 16;

    public int ClaimBatch { get; init; } = 64;

    /// <summary>Jobs the self-healed sweep amends to succeed and restarts.</summary>
    public int Healed { get; init; } = 200;

    /// <summary>Undelivered, unresolved alerts the retention check ages past the window.</summary>
    public int Stuck { get; init; } = 200;

    /// <summary>How deep the pagination probe walks the alert list, in pages of 100.</summary>
    public int PageDepth { get; init; } = 50;

    public TimeSpan SeedTimeout { get; init; } = TimeSpan.FromMinutes(45);

    public TimeSpan DrainTimeout { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>True when the backlog fits inside one bounded invocation, which is the 10K pass condition.</summary>
    public bool OneInvocationExpected => Events <= BurstBounds.OneInvocationCeiling;

    public static BurstOptions Parse(string[] args)
    {
        // Empty counts as unset at every rung (a shell's VAR='' must not bypass the SQLite default), the
        // same rule LocalDatabase applies to the provider it resolves.
        var provider = Arg(args, "--provider") ?? Environment.GetEnvironmentVariable("ACTA_LOCAL_PROVIDER");
        provider = string.IsNullOrWhiteSpace(provider) ? "sqlite" : provider;

        var events = Int(args, "--events", 10_000);
        var runId = Arg(args, "--run") ?? $"b{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";

        return new BurstOptions
        {
            Provider = provider,
            // A fresh schema per run, like the bench: an empty schema needs no reset step, and two runs on
            // one database cannot see each other's backlog through a shared alert list.
            Schema = Arg(args, "--schema") ?? $"anvil_burst_{runId.Replace('-', '_')}",
            Namespace = Arg(args, "--namespace") ?? "acta-burst",
            RunId = runId,
            Events = events,
            Executors = Int(args, "--executors", 16),
            ClaimBatch = Int(args, "--claim-batch", 64),
            // Subsets, not the whole backlog: both sweeps prove a property that holds per row, and running
            // them over 100,000 rows would spend the run's wall clock re-proving one fact.
            Healed = Math.Min(Int(args, "--healed", 200), events),
            Stuck = Math.Min(Int(args, "--stuck", 200), events),
            PageDepth = Int(args, "--page-depth", 50),
            SeedTimeout = TimeSpan.FromMinutes(Int(args, "--seed-timeout-min", 45)),
            DrainTimeout = TimeSpan.FromMinutes(Int(args, "--drain-timeout-min", 30)),
        };
    }

    public static void PrintUsage()
    {
        Console.WriteLine();
        Console.WriteLine("  Anvil.Burst - the sys.alerts burst certification (C6).");
        Console.WriteLine();
        Console.WriteLine("  dotnet run --project anvil/Anvil.Burst -- [options]");
        Console.WriteLine();
        Console.WriteLine("    --provider <sqlite|pg|mssql>  durable provider (default: ACTA_LOCAL_PROVIDER, else sqlite)");
        Console.WriteLine("    --schema <name>               schema to run in (default: a fresh anvil_burst_* schema)");
        Console.WriteLine("    --namespace <name>            job namespace (default: acta-burst)");
        Console.WriteLine("    --events <n>                  alertable events to seed (default: 10000)");
        Console.WriteLine("    --executors <n>               executor slots on the harness worker (default: 16)");
        Console.WriteLine("    --claim-batch <n>             claim batch size (default: 64)");
        Console.WriteLine("    --healed <n>                  jobs the self-healed sweep succeeds (default: 200)");
        Console.WriteLine("    --stuck <n>                   alerts the retention check ages out (default: 200)");
        Console.WriteLine("    --page-depth <n>              pages of 100 the pagination probe walks (default: 50)");
        Console.WriteLine("    --seed-timeout-min <n>        seeding drain timeout (default: 45)");
        Console.WriteLine("    --drain-timeout-min <n>       projection drain timeout (default: 30)");
        Console.WriteLine();
        Console.WriteLine("  Exit codes: 0 pass, 1 fail, 2 the run could not be set up.");
        Console.WriteLine();
    }

    private static string? Arg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        var value = i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static int Int(string[] args, string name, int fallback) =>
        Arg(args, name) is { } raw && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : fallback;
}
