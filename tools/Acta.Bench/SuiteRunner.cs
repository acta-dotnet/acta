using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Acta.Bench;

internal static class SuiteRunner
{
    public static async Task<int> RunAsync(RunPlan plan, string output, CancellationToken ct)
    {
        var bundle = new EvidenceBundle(output, plan.RunId);
        using var runLease = new FileStream(bundle.Resolve("run.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
        bundle.Write("resolved-plan.json", plan, BenchJson.Default.RunPlan);
        Provenance provenance;
        Exception? captureError = null;
        try
        {
            provenance = await CaptureAsync(plan, bundle, ct);
        }
        catch (Exception ex)
        {
            captureError = ex;
            provenance = EvidenceRecovery.UnknownProvenance(plan, bundle);
        }
        bundle.Write("provenance.json", provenance, BenchJson.Default.Provenance);
        List<CaseResult> results = [];
        List<string> complete = [];
        var settingsVerified = true;
        var aborted = false;
        var harnessError = false;
        Console.WriteLine($"Acta Bench {plan.Profile} | {string.Join(", ", plan.Providers)} | {bundle.DirectoryPath}");
        if (plan.Profile == "standard")
        {
            Console.WriteLine(
                "Short all-case campaign: one conservative rate, one measured trial, reduced populations and fault repetitions. Capacity search, repeated-trial confidence and extended endurance are not established."
            );
        }
        try
        {
            ct.ThrowIfCancellationRequested();
            if (captureError is not null)
            {
                throw new InvalidOperationException($"Build/environment capture failed: {captureError.Message}", captureError);
            }
            var controls = HarnessControls.Run(plan, bundle);
            foreach (var result in controls)
            {
                Record(result);
            }
            if (controls.Any(c => c.Status != "PASS"))
            {
                harnessError = true;
                throw new InvalidOperationException("Harness negative controls failed.");
            }
            foreach (var provider in plan.Providers)
            {
                ct.ThrowIfCancellationRequested();
                if (
                    plan.Profile != "quick"
                    && typeof(BenchCommand).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration != "Release"
                )
                {
                    harnessError = true;
                    settingsVerified = false;
                    foreach (var scenario in plan.Cases.Where(c => c.Family != "harness"))
                    {
                        Record(
                            CaseResult.Incomplete(
                                scenario.Id,
                                provider,
                                "verify",
                                "The full protocol requires a Release executable. Rebuild or publish with -c Release; no engine workload was started."
                            )
                        );
                    }
                    continue;
                }
                var before = results.Count;
#pragma warning disable CA2000 // Disposed in the finally below, where a failed disposal is logged.
                var environment = new OwnedProvider(provider, bundle.Resolve($"repro/state/{provider}"));
#pragma warning restore CA2000
                try
                {
                    Console.WriteLine($"Preflight/provision {provider}");
                    try
                    {
                        await environment.ProvisionAsync(plan, plan.UseComposeServers, ct);
                    }
                    finally
                    {
                        RecordServer(bundle, environment.ServerSpecs);
                    }
                    var settings = await environment.CaptureSettingsAsync(ct);
                    settingsVerified &= environment.SettingsVerified;
                    bundle.Write($"histories/{provider}-settings.json", settings, BenchJson.Default.DictionaryStringString);
                    await ScenarioRunner.RunAsync(plan, environment, bundle, Record, ct);
                    foreach (var gap in CoveragePolicy.Missing(plan, provider, results))
                    {
                        Record(gap);
                    }
                    if (results.Skip(before).All(c => c.Status is "PASS" or "NOT_APPLICABLE"))
                    {
                        complete.Add(provider);
                        environment.RetainState = false;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is not ServerSetupException)
                {
                    settingsVerified = false;
                    Console.WriteLine($"{provider}: incomplete ({ex.GetType().Name}: {ex.Message}); retained evidence.");
                    File.WriteAllText(bundle.Resolve($"logs/{provider}-error.txt"), ex.ToString());
                    foreach (var c in plan.Cases.Where(c => c.Family != "harness" && !results.Skip(before).Any(r => r.Id == c.Id)))
                    {
                        Record(
                            CaseResult.Incomplete(
                                c.Id,
                                provider,
                                "verify",
                                ex is ResourceBudgetException ? $"The resource budget stopped this provider: {ex.Message}"
                                    : provider == "mssql" && !System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported
                                        ? "SQL Server requires dynamic code support; publish the managed build with -p:PublishAot=false. Native and AOT-constrained managed runs do not cover SQL Server."
                                    : $"Provider setup/execution unavailable ({ex.GetType().Name}: {ex.Message})."
                            )
                        );
                    }
                }
                finally
                {
                    // A provider's verdicts are recorded by now; leftover resources do not stop the next provider.
                    try
                    {
                        await environment.DisposeAsync();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(
                            $"{provider}: cleanup failed ({ex.GetType().Name}: {ex.Message}); acta-bench cleanup lists what remains."
                        );
                        File.WriteAllText(bundle.Resolve($"logs/{provider}-cleanup-error.txt"), ex.ToString());
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            aborted = true;
        }
        catch (Exception ex)
        {
            harnessError = true;
            Console.WriteLine($"Harness failure ({ex.GetType().Name}: {ex.Message}); no full verdict is established.");
            File.WriteAllText(bundle.Resolve("logs/harness-error.txt"), $"No full verdict is established.\n{ex}");
        }
        foreach (var provider in plan.Providers)
        {
            foreach (var c in plan.Cases.Where(c => c.Family != "harness" && !results.Any(r => r.Provider == provider && r.Id == c.Id)))
            {
                Record(CaseResult.Incomplete(c.Id, provider, "verify", "Run ended before this required case was executed."));
            }
        }
        foreach (var provider in plan.Providers)
        {
            foreach (
                var gap in CoveragePolicy
                    .Missing(plan, provider, results)
                    .Where(g => !results.Any(r => r.Provider == g.Provider && r.Id == g.Id && r.Reason == g.Reason))
            )
            {
                Record(gap);
            }
        }
        foreach (var control in plan.Cases.Where(c => c.Family == "harness" && !results.Any(r => r.Id == c.Id && r.Provider == "harness")))
        {
            Record(CaseResult.Incomplete(control.Id, "harness", "self_test", "Required harness check did not finish."));
        }
        var status = Verdicts.Overall(results, aborted, settingsVerified);
        if (harnessError && status == "PASS")
        {
            status = "INCONCLUSIVE";
        }
        var coverage = new RunCoverage(
            results.Count,
            results.Count(c => c.Status == "PASS"),
            results.Count(c => c.Status == "FAIL"),
            results.Count(c => c.Status == "INCONCLUSIVE")
        );
        var report = new RunResult(
            "acta-bench.result/1",
            plan.RunId,
            "real_execution",
            status,
            provenance,
            new(plan.Providers, [.. complete], ["physical_power_loss", "vm_crash", "multi_region_failover"]),
            coverage,
            new("single_node_durable", settingsVerified, false),
            [.. results],
            [],
            harnessError
        );
        bundle.Finish(report);
        Console.WriteLine($"{status}: {bundle.Resolve("report.html")}");
        return Verdicts.ExitCode(status, harnessError);

        void Record(CaseResult result)
        {
            results.Add(result);
            bundle.AppendCase(result);
            if (result.Phase == "fault")
            {
                bundle.Append(
                    "faults.jsonl",
                    new Dictionary<string, string>
                    {
                        ["event"] = "case_result",
                        ["provider"] = result.Provider,
                        ["case_id"] = result.Id,
                        ["repetition"] = result.Repetition.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["recorded_at_utc"] = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                        ["status"] = result.Status,
                        ["coverage"] = result.Coverage,
                        ["evidence"] = string.Join(";", result.Evidence),
                    },
                    BenchJson.Default.DictionaryStringString
                );
            }
            Console.WriteLine($"  {result.Id} {result.Provider} {result.Phase} #{result.Repetition}: {result.Status}");
        }
    }

    /// <summary>Adds a provisioned server's Docker VM, container and readback settings to the run's environment record.</summary>
    private static void RecordServer(EvidenceBundle bundle, Dictionary<string, string> specs)
    {
        if (specs.Count == 0)
        {
            return;
        }
        var environment = JsonSerializer.Deserialize(
            File.ReadAllText(bundle.Resolve("environment.json")),
            BenchJson.Default.DictionaryStringString
        )!;
        foreach (var (key, value) in specs)
        {
            environment[key] = value;
        }
        bundle.Write("environment.json", environment, BenchJson.Default.DictionaryStringString);
    }

    private static async Task<Provenance> CaptureAsync(RunPlan plan, EvidenceBundle bundle, CancellationToken ct)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["os"] = RuntimeInformation.OSDescription,
            ["machine_name"] = Environment.MachineName,
            ["architecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
            ["runtime"] = RuntimeInformation.FrameworkDescription,
            ["processor_count"] = Environment.ProcessorCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["cpu_model"] = HardwareDescription.CpuModel(),
            ["physical_cores"] =
                HardwareDescription.PhysicalCores()?.ToString(System.Globalization.CultureInfo.InvariantCulture)
                ?? "Unavailable on this platform.",
            ["memory_available_to_runtime_bytes"] = GC.GetGCMemoryInfo()
                .TotalAvailableMemoryBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["filesystem"] = new DriveInfo(Path.GetPathRoot(bundle.DirectoryPath)!).DriveFormat,
            ["artifact_disk_free_at_start_bytes"] = new DriveInfo(Path.GetPathRoot(bundle.DirectoryPath)!).AvailableFreeSpace.ToString(
                System.Globalization.CultureInfo.InvariantCulture
            ),
            ["database_backing_disk"] = "Unavailable for Docker volumes; artifact-drive free space is guarded across the complete run.",
            ["gc_server"] = System.Runtime.GCSettings.IsServerGC.ToString(),
            ["compilation_mode"] = BuildMode.Current(),
            ["worker_compilation_mode"] = BuildMode.Current(),
            ["observer"] = "Independent committed-state SQL reads; completion times are observed upper bounds.",
            ["monotonic_clock_frequency"] = Stopwatch.Frequency.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["build_configuration"] =
                typeof(BenchCommand).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "unknown",
            ["missing_metrics"] =
                "Physical disk, per-database CPU/waits, live/allocation storage and network metrics require collectors. Whole-container CPU/memory are sampled where cgroup v2 is available; unavailable values are not zero.",
        };
        Console.WriteLine(
            $"CPU: {environment["cpu_model"]} ({environment["physical_cores"]} cores, {Environment.ProcessorCount} available logical processors)"
        );
        bundle.Write("environment.json", environment, BenchJson.Default.DictionaryStringString);
        var binaryFiles = Directory
            .EnumerateFiles(AppContext.BaseDirectory, "*", SearchOption.AllDirectories)
            .Where(p =>
                p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                || p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                || p.EndsWith(".so", StringComparison.OrdinalIgnoreCase)
                || p.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase)
                || p.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase)
                || p.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase)
            )
            .Order(StringComparer.Ordinal)
            .ToDictionary(
                p => Path.GetRelativePath(AppContext.BaseDirectory, p).Replace('\\', '/'),
                EvidenceBundle.FileHash,
                StringComparer.Ordinal
            );
        if (Environment.ProcessPath is { } executable)
        {
            binaryFiles["runtime_host/" + Path.GetFileName(executable)] = EvidenceBundle.FileHash(executable);
        }
        bundle.Write("repro/build-manifest.json", binaryFiles, BenchJson.Default.DictionaryStringString);
        var informational = typeof(BenchCommand).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var compiledCommit = informational?.Split('+').Last().Split('.').Last();
        var commit =
            compiledCommit is { Length: 40 } && compiledCommit.All(Uri.IsHexDigit)
                ? compiledCommit
                : await GitAsync(["rev-parse", "HEAD"], ct);
        if (commit.Length != 40 || !commit.All(Uri.IsHexDigit))
        {
            commit = new string('0', 40);
            environment["source_provenance"] = "Unavailable in this installation; zero hash is an explicit unknown marker.";
            bundle.Write("environment.json", environment, BenchJson.Default.DictionaryStringString);
        }
        return new(
            plan.ProtocolId,
            plan.ProtocolSha256,
            EvidenceBundle.FileHash(bundle.Resolve("resolved-plan.json")),
            commit,
            typeof(BenchCommand)
                .Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "Acta.Bench.SourceDirty")
                ?.Value != "false",
            binaryFiles.Values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            "environment.json"
        );
    }

    private static async Task<string> GitAsync(string[] arguments, CancellationToken ct)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new("git")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };
            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync(ct);
            var error = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            await error;
            return process.ExitCode == 0 ? (await output).Trim() : "";
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return "";
        }
    }
}
