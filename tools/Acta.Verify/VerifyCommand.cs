using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Acta.Verify;

public static class VerifyCommand
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
            await Console.Error.WriteLineAsync($"Acta Verify could not complete the command ({cause.GetType().Name}: {cause.Message}).");
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
        if (args is ["--effects"])
        {
            await EffectService.ServeAsync(
                Environment.GetEnvironmentVariable("ACTA_VERIFY_EFFECTS")!,
                Environment.GetEnvironmentVariable("ACTA_VERIFY_EFFECTS_TOKEN")!,
                Environment.GetEnvironmentVariable("ACTA_VERIFY_EFFECTS_STORE")!,
                ct
            );
            return 0;
        }
        if (args is ["--worker"])
        {
            if (Environment.GetEnvironmentVariable("ACTA_VERIFY_OWNER") is not { Length: 32 })
            {
                return 3;
            }
            using var host = await RuntimeHost.StartAsync(
                Environment.GetEnvironmentVariable("ACTA_VERIFY_PROVIDER")!,
                Environment.GetEnvironmentVariable("ACTA_VERIFY_CONNECTION")!,
                Environment.GetEnvironmentVariable("ACTA_VERIFY_SCHEMA")!,
                true,
                false,
                ct,
                Environment.GetEnvironmentVariable("ACTA_VERIFY_INJECT_OPERATION") is { } operation
                    ? new StoreInjection(
                        operation,
                        Environment.GetEnvironmentVariable("ACTA_VERIFY_INJECT_BOUNDARY")!,
                        Environment.GetEnvironmentVariable("ACTA_VERIFY_INJECT_LOSE_REPLY") == "1",
                        long.TryParse(
                            Environment.GetEnvironmentVariable("ACTA_VERIFY_INJECT_JOB_ID"),
                            CultureInfo.InvariantCulture,
                            out var jobId
                        )
                            ? jobId
                            : null
                    )
                    : null,
                executors: Environment.GetEnvironmentVariable("ACTA_VERIFY_EXECUTORS") is { } executors
                    ? int.Parse(executors, CultureInfo.InvariantCulture)
                    : null,
                heartbeatSeconds: Environment.GetEnvironmentVariable("ACTA_VERIFY_HEARTBEAT_SECONDS") is { } heartbeat
                    ? int.Parse(heartbeat, CultureInfo.InvariantCulture)
                    : null,
                workerNamespace: Environment.GetEnvironmentVariable("ACTA_VERIFY_NAMESPACE")
            );
            var options = host.Services.GetRequiredService<IOptions<JobsOptions>>().Value;
            Console.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"ACTA_VERIFY_SETTINGS {options.MaxConcurrentExecutors} {options.HeartbeatInterval.TotalSeconds} {options.LeaseTtlSeconds} {options.SafetyPollInterval.TotalMilliseconds} {options.WorkerDeadAfter.TotalSeconds}"
                )
            );
            Console.WriteLine("ACTA_VERIFY_READY");
            while (await Console.In.ReadLineAsync(ct) is { } command)
            {
                if (command == "ACTA_VERIFY_STOP")
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
                """
                Acta Verify runs the written scenarios in Scenarios/ and judges them by the rules in README.md.
                  acta-verify quick|check|release [--db pg|mssql|sqlite|all] [--scenario NAME]... [--seed N] [--output DIR] [--use-compose-servers] [--dry-run]
                    quick: a few minutes while changing code; check: every scenario, short; release: the certification record
                  acta-verify run NAME... [--suite quick|check|release] [same options]: the named scenarios, with the release suite's timings unless --suite says otherwise
                  acta-verify list: every scenario's title and question
                  acta-verify replay PLAN [--output DIR]: run a bundle's resolved-plan.json again
                  acta-verify report RUN_DIRECTORY
                  acta-verify compare BEFORE_DIRECTORY AFTER_DIRECTORY
                  acta-verify publish RUN_DIRECTORY --to FILE
                  acta-verify cleanup [--yes]: list Verify's leftover containers, processes and databases; --yes removes them
                """
            );
            return 0;
        }
        if (args is ["list"])
        {
            foreach (var group in ScenarioFiles.All(null).GroupBy(s => s.Group))
            {
                Console.WriteLine($"{group.Key}:");
                foreach (var scenario in group)
                {
                    Console.WriteLine($"  {scenario.Id}: {scenario.Title}\n    {scenario.Question}");
                }
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
        string? suite = null;
        var db = "all";
        var output = Path.Combine("artifacts", "verify");
        var seed = 1729;
        var useComposeServers = false;
        var dryRun = false;
        List<string> scenarios = [];
        RunPlan? replay = null;
        var start = 1;
        switch (args)
        {
            case ["replay", var planFile, ..]:
                replay =
                    JsonSerializer.Deserialize(File.ReadAllText(planFile), VerifyJson.Default.RunPlan)
                    ?? throw new ArgumentException("Invalid plan.");
                if (replay.ProtocolSha256 != Protocol.StandardHash())
                {
                    throw new ArgumentException("The plan was written by a build with other scenario files; replay it with that build.");
                }
                start = 2;
                break;
            case [var name, ..] when ScenarioFiles.SuiteNames().Contains(name):
                suite = args[0];
                break;
            case ["run", ..]:
                suite = "release";
                for (; start < args.Length && !args[start].StartsWith("--", StringComparison.Ordinal); start++)
                {
                    scenarios.Add(args[start]);
                }
                if (scenarios.Count == 0)
                {
                    throw new ArgumentException("Name the scenarios to run; acta-verify list names every one.");
                }
                break;
            default:
                throw new ArgumentException("Unknown command: " + args[0] + ". acta-verify --help lists the commands.");
        }
        for (var i = start; i < args.Length; i++)
        {
            if (replay is not null && args[i] != "--output")
            {
                throw new ArgumentException("Replay runs the frozen plan; only --output may be overridden.");
            }
            switch (args[i])
            {
                case "--db":
                    db = Value(args, ref i);
                    break;
                case "--seed":
                    seed = int.Parse(Value(args, ref i), CultureInfo.InvariantCulture);
                    break;
                case "--output":
                    output = Value(args, ref i);
                    break;
                case "--scenario":
                    scenarios.Add(Value(args, ref i));
                    break;
                case "--suite" when args[0] == "run":
                    suite = Value(args, ref i);
                    break;
                case "--use-compose-servers":
                    useComposeServers = true;
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                default:
                    throw new ArgumentException("Unknown option: " + args[i]);
            }
        }
        var plan = replay is null
            ? Protocol.Plan(suite!, db == "all" ? Protocol.AllProviders : [db], seed, [.. scenarios]) with
            {
                UseComposeServers = useComposeServers,
            }
            : replay with
            {
                RunId = $"{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}",
            };
        // A replay runs a plan another process wrote; this build must plan exactly the same.
        if (replay is not null)
        {
            Protocol.Validate(plan);
        }
        if (dryRun)
        {
            Console.WriteLine(DryRun.Describe(plan, output));
            return 0;
        }
        if (!await CleanStart.CheckAsync(plan.MeasurementGatesVerdict, ct))
        {
            return 3;
        }
        // The same command against the same output continues the run a crash or a stop interrupted there.
        return replay is null && RunResume.Find(output, plan) is { } interrupted
            ? await SuiteRunner.RunAsync(plan with { RunId = interrupted }, output, ct, resume: true)
            : await SuiteRunner.RunAsync(plan, output, ct);
    }

    private static string Value(string[] args, ref int index) =>
        ++index < args.Length && !args[index].StartsWith("--", StringComparison.Ordinal)
            ? args[index]
            : throw new ArgumentException("Missing option value.");

    private static RunResult Load(string directory) => BundleVerifier.Load(directory);
}
