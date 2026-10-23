using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Acta.Bench;

public static class BenchCommand
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        using var interrupted = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ConsoleCancelEventHandler handler = (_, e) =>
        {
            e.Cancel = true;
            interrupted.Cancel();
        };
        Console.CancelKeyPress += handler;
        try
        {
            return await DispatchAsync(args, interrupted.Token);
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
        catch (ArgumentException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message);
            return 3;
        }
        catch (Exception ex)
        {
            var cause = ex.GetBaseException();
            await Console.Error.WriteLineAsync($"Acta Bench could not complete the command ({cause.GetType().Name}: {cause.Message}).");
            return 3;
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }

    private static async Task<int> DispatchAsync(string[] args, CancellationToken ct)
    {
        using var parentLifetime = ParentLifetime.ForInternalRole(args);
        if (args is ["--oracle"])
        {
            await EffectService.ServeAsync(
                Environment.GetEnvironmentVariable("ACTA_BENCH_ORACLE")!,
                Environment.GetEnvironmentVariable("ACTA_BENCH_ORACLE_TOKEN")!,
                Environment.GetEnvironmentVariable("ACTA_BENCH_ORACLE_STORE")!,
                ct
            );
            return 0;
        }
        if (args is ["--worker"])
        {
            if (Environment.GetEnvironmentVariable("ACTA_BENCH_OWNER") is not { Length: 32 })
            {
                return 3;
            }
            using var host = await RuntimeHost.StartAsync(
                Environment.GetEnvironmentVariable("ACTA_BENCH_PROVIDER")!,
                Environment.GetEnvironmentVariable("ACTA_BENCH_CONNECTION")!,
                Environment.GetEnvironmentVariable("ACTA_BENCH_SCHEMA")!,
                true,
                false,
                ct,
                Environment.GetEnvironmentVariable("ACTA_BENCH_INJECT_OPERATION") is { } operation
                    ? new StoreInjection(
                        operation,
                        Environment.GetEnvironmentVariable("ACTA_BENCH_INJECT_BOUNDARY")!,
                        Environment.GetEnvironmentVariable("ACTA_BENCH_INJECT_LOSE_REPLY") == "1",
                        long.TryParse(
                            Environment.GetEnvironmentVariable("ACTA_BENCH_INJECT_JOB_ID"),
                            CultureInfo.InvariantCulture,
                            out var jobId
                        )
                            ? jobId
                            : null
                    )
                    : null,
                executors: Environment.GetEnvironmentVariable("ACTA_BENCH_EXECUTORS") is { } executors
                    ? int.Parse(executors, CultureInfo.InvariantCulture)
                    : null,
                heartbeatSeconds: Environment.GetEnvironmentVariable("ACTA_BENCH_HEARTBEAT_SECONDS") is { } heartbeat
                    ? int.Parse(heartbeat, CultureInfo.InvariantCulture)
                    : null,
                workerNamespace: Environment.GetEnvironmentVariable("ACTA_BENCH_NAMESPACE")
            );
            var options = host.Services.GetRequiredService<IOptions<JobsOptions>>().Value;
            Console.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"ACTA_BENCH_SETTINGS {options.MaxConcurrentExecutors} {options.HeartbeatInterval.TotalSeconds} {options.LeaseTtlSeconds} {options.SafetyPollInterval.TotalMilliseconds} {options.WorkerDeadAfter.TotalSeconds}"
                )
            );
            Console.WriteLine("ACTA_BENCH_READY");
            while (await Console.In.ReadLineAsync(ct) is { } command)
            {
                if (command == "ACTA_BENCH_STOP")
                {
                    using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    shutdown.CancelAfter(TimeSpan.FromSeconds(10));
                    await host.StopAsync(shutdown.Token);
                    return 0;
                }
            }
            return 0;
        }
        if (args is [] or ["--help"] or ["-h"])
        {
            Console.WriteLine(
                "Acta Bench\n  acta-bench --standard|--full|--quick [--db sqlite|pg|mssql|all] [--seed N] [--output DIR] [--case ID] [--cell NAME] [--chaos-jobs N] [--repetitions N] [--preload N] [--use-compose-servers]\n  --quick: small smoke suite; --standard: all cases, short trials; --full: extended repeated campaign\n  --cell: run only the named diagnostic cells of their cases; --repetitions: independent trials per cell; --preload: L04 preloaded roots per provider\n  --use-compose-servers: run PostgreSQL and SQL Server on the acta-dev Compose services instead of owned containers\n  acta-bench replay PLAN [--output DIR]\n  acta-bench report RUN_DIRECTORY\n  acta-bench compare BASELINE_DIRECTORY CANDIDATE_DIRECTORY\n  acta-bench publish RUN_DIRECTORY --to FILE\n  acta-bench list\n  acta-bench cleanup [--yes]: list owned containers and marked b_ databases with sizes; --yes removes them unless another run is active"
            );
            return 0;
        }
        if (args is ["list"])
        {
            foreach (var c in Protocol.Catalog().Cases)
            {
                Console.WriteLine($"{c.Id}  {c.Family, -14} {c.Name}");
            }
            return 0;
        }
        if (args is ["report", var directory])
        {
            var result = File.Exists(Path.Combine(directory, "result.json"))
                ? Load(directory)
                : EvidenceRecovery.FinalizeInterrupted(directory);
            Console.WriteLine($"{result.Status}: {Path.GetFullPath(Path.Combine(directory, "report.html"))}");
            return Verdicts.ExitCode(result.Status, result.HarnessError);
        }
        if (args is ["cleanup"] or ["cleanup", "--yes"])
        {
            return await OwnedProvider.CleanupAsync(args is [_, "--yes"], ct);
        }
        if (args is ["compare", var baseline, var candidate])
        {
            return BundleComparison.Print(baseline, candidate);
        }
        if (args is ["publish", var source, "--to", var target])
        {
            _ = Load(source);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
            File.Copy(Path.Combine(source, "report.md"), target, overwrite: true);
            return 0;
        }
        string? profile = null;
        string db = "all";
        string output = Path.Combine("artifacts", "bench");
        int seed = 1729;
        int? chaosJobs = null;
        int? repetitions = null;
        int? preload = null;
        var useComposeServers = false;
        List<string> cases = [];
        List<string> requestedCells = [];
        RunPlan? replay = null;
        var start = 0;
        if (args is ["replay", var planFile, ..])
        {
            replay =
                JsonSerializer.Deserialize(File.ReadAllText(planFile), BenchJson.Default.RunPlan)
                ?? throw new ArgumentException("Invalid plan.");
            if (replay.ProtocolSha256 != Protocol.Plan("full", ["sqlite"], seed).ProtocolSha256)
            {
                throw new ArgumentException("Replay protocol hash does not match this executable.");
            }
            start = 2;
        }
        for (var i = start; i < args.Length; i++)
        {
            if (replay is not null && args[i] != "--output")
            {
                throw new ArgumentException("Replay consumes the frozen plan; only --output may be overridden.");
            }
            switch (args[i])
            {
                case "--full":
                    profile = "full";
                    break;
                case "--quick":
                    profile = "quick";
                    break;
                case "--standard":
                    profile = "standard";
                    break;
                case "--db":
                    db = Value(args, ref i);
                    break;
                case "--seed":
                    seed = int.Parse(Value(args, ref i), CultureInfo.InvariantCulture);
                    break;
                case "--chaos-jobs":
                    chaosJobs = int.Parse(Value(args, ref i), CultureInfo.InvariantCulture);
                    break;
                case "--repetitions":
                    repetitions = int.Parse(Value(args, ref i), CultureInfo.InvariantCulture);
                    break;
                case "--preload":
                    preload = int.Parse(Value(args, ref i), CultureInfo.InvariantCulture);
                    break;
                case "--output":
                    output = Value(args, ref i);
                    break;
                case "--cell":
                    requestedCells.Add(Value(args, ref i));
                    break;
                case "--case":
                    cases.Add(Value(args, ref i));
                    break;
                case "--use-compose-servers":
                    useComposeServers = true;
                    break;
                default:
                    throw new ArgumentException("Unknown option: " + args[i]);
            }
        }
        if (requestedCells.Count > 0)
        {
            if (replay is not null)
            {
                throw new ArgumentException("Replay does not allow cell scope overrides.");
            }
            var available = Protocol.Plan(profile ?? "quick", ["sqlite"], seed).DiagnosticCells;
            if (requestedCells.Any(name => !available.Any(c => c.Name == name)))
            {
                throw new ArgumentException("Unknown diagnostic cell. See the frozen diagnostic_cells catalogue in resolved-plan.json.");
            }
            cases.AddRange(available.Where(c => requestedCells.Contains(c.Name, StringComparer.Ordinal)).Select(c => c.CaseId));
        }
        var plan = replay is null
            ? Protocol.Plan(
                profile ?? throw new ArgumentException("Choose --standard, --full, or --quick explicitly."),
                db == "all" ? ["sqlite", "pg", "mssql"] : [db],
                seed,
                [.. cases]
            )
            : replay with
            {
                RunId = $"{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}",
            };
        if (chaosJobs is < 1 || repetitions is < 1 || preload is < 1)
        {
            throw new ArgumentException("--chaos-jobs, --repetitions and --preload must be positive counts.");
        }
        plan = plan with
        {
            RequestedDiagnosticCells = replay?.RequestedDiagnosticCells ?? requestedCells,
            ChaosJobs = chaosJobs ?? plan.ChaosJobs,
            UseComposeServers = replay?.UseComposeServers ?? useComposeServers,
            Repetitions = repetitions ?? plan.Repetitions,
            PreloadedBacklogRoots = preload is { } roots
                ? plan.PreloadedBacklogRoots.ToDictionary(p => p.Key, _ => roots, StringComparer.Ordinal)
                : plan.PreloadedBacklogRoots,
        };
        Protocol.Validate(plan);
        return await SuiteRunner.RunAsync(plan, output, ct);
    }

    private static string Value(string[] args, ref int index) =>
        ++index < args.Length && !args[index].StartsWith("--", StringComparison.Ordinal)
            ? args[index]
            : throw new ArgumentException("Missing option value.");

    private static RunResult Load(string directory) => BundleVerifier.Load(directory);
}
