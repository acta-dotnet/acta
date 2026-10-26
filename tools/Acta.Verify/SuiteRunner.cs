using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Acta.Verify;

internal static class SuiteRunner
{
    /// <summary>
    /// Runs <paramref name="plan"/> into a new bundle, or with <paramref name="resume"/> continues the unfinished bundle of
    /// the same run id: the build must be the one that started it, and only the providers that did not finish run again.
    /// </summary>
    public static async Task<int> RunAsync(RunPlan plan, string output, CancellationToken ct, bool resume = false)
    {
        var wall = Stopwatch.StartNew();
        EvidenceBundle bundle;
        FileStream lease;
        if (resume)
        {
            // The lease comes first: nothing of an interrupted run is touched while another process still holds it.
            var directory = Path.Combine(output, plan.RunId);
            lease = Lease(Path.Combine(directory, "run.lock"), resume);
            RunResume.Reopen(directory);
            bundle = new EvidenceBundle(directory);
        }
        else
        {
            bundle = new EvidenceBundle(output, plan.RunId);
            lease = Lease(bundle.Resolve("run.lock"), resume);
        }
        using var runLease = lease;
        Provenance provenance;
        Exception? captureError = null;
        RunResume.Kept? kept = null;
        if (resume)
        {
            provenance = JsonSerializer.Deserialize(File.ReadAllText(bundle.Resolve("provenance.json")), VerifyJson.Default.Provenance)!;
            if (
                !BinaryManifest()
                    .Values.Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .SequenceEqual(provenance.BinarySha256)
            )
            {
                throw new InvalidOperationException(
                    $"Run {plan.RunId} was started by another build; a resumed run must keep its build. Start a fresh run in another output directory."
                );
            }
            kept = RunResume.Load(bundle, plan);
            var resumes = JsonSerializer
                .Deserialize(File.ReadAllText(bundle.Resolve("environment.json")), VerifyJson.Default.DictionaryStringString)!
                .GetValueOrDefault("resumes", "0");
            RecordEnvironment(
                bundle,
                new()
                {
                    ["resumes"] = (int.Parse(resumes, System.Globalization.CultureInfo.InvariantCulture) + 1).ToString(
                        System.Globalization.CultureInfo.InvariantCulture
                    ),
                    ["providers_kept_on_resume"] = kept.Finished.Count == 0 ? "none" : string.Join(",", kept.Finished),
                }
            );
            Console.WriteLine(
                $"Resuming {plan.RunId}: kept {(kept.Finished.Count == 0 ? "no provider" : string.Join(", ", kept.Finished))}."
            );
        }
        else
        {
            bundle.Write("resolved-plan.json", plan, VerifyJson.Default.RunPlan);
            try
            {
                provenance = await CaptureAsync(plan, bundle, ct);
            }
            catch (Exception ex)
            {
                captureError = ex;
                provenance = EvidenceRecovery.UnknownProvenance(plan, bundle);
            }
            bundle.Write("provenance.json", provenance, VerifyJson.Default.Provenance);
        }
        List<CaseResult> results = [.. kept?.Results ?? []];
        List<string> complete = [.. kept?.Complete ?? []];
        var settingsVerified = kept?.SettingsVerified ?? true;
        var aborted = false;
        var harnessError = false;
        using var disk = DiskCounters.Start();
        disk.Enter(resume ? "at resume" : "before");
        Console.WriteLine($"Acta Verify {plan.Profile} | {string.Join(", ", plan.Providers)} | {bundle.DirectoryPath}");
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
            CaseResult[] controls = [.. results.Where(r => r.Provider == "harness")];
            if (controls.Length == 0)
            {
                controls = HarnessControls.Run(plan, bundle);
                foreach (var result in controls)
                {
                    Record(result);
                }
            }
            if (controls.Any(c => c.Status != "PASS"))
            {
                harnessError = true;
                throw new InvalidOperationException("Harness negative controls failed.");
            }
            // Half a minute of the drive at rest before any provider writes to it.
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            foreach (var provider in plan.Providers.Where(p => kept?.Finished.Contains(p) != true))
            {
                ct.ThrowIfCancellationRequested();
                disk.Enter(provider);
                if (
                    plan.Profile != "quick"
                    && typeof(VerifyCommand).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration != "Release"
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
                var providerWall = Stopwatch.StartNew();
                var providerVerified = true;
                var providerEnded = false;
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
                        RecordEnvironment(bundle, environment.ServerSpecs);
                    }
                    var settings = await environment.CaptureSettingsAsync(ct);
                    providerVerified = environment.SettingsVerified;
                    bundle.Write($"histories/{provider}-settings.json", settings, VerifyJson.Default.DictionaryStringString);
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
                    providerEnded = true;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is not ServerSetupException)
                {
                    providerVerified = false;
                    providerEnded = true;
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
                            $"{provider}: cleanup failed ({ex.GetType().Name}: {ex.Message}); acta-verify cleanup lists what remains."
                        );
                        File.WriteAllText(bundle.Resolve($"logs/{provider}-cleanup-error.txt"), ex.ToString());
                    }
                    settingsVerified &= providerVerified;
                    // Only a provider that ran to its end is kept by a resume; an interrupted one runs again.
                    if (providerEnded)
                    {
                        disk.Save(bundle);
                        RunResume.MarkFinished(
                            bundle,
                            provider,
                            complete.Contains(provider),
                            providerVerified,
                            providerWall.Elapsed.TotalSeconds
                        );
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
        // And half a minute after the last provider, while the drive settles from the run's writes.
        disk.Enter("after");
        if (!aborted)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), CancellationToken.None);
        }
        disk.Stop(bundle);
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
        // A passing run whose evidence outgrew its budget wrote per-job data it should not have; heavy writes also
        // degrade the drives that later runs measure on.
        var evidenceBytes = EvidenceBundle.Bytes(bundle.DirectoryPath);
        var budgetBytes = (long)(plan.Policy("output", "passing_bundle_budget_mb") * 1048576);
        if (status == "PASS" && evidenceBytes > budgetBytes)
        {
            harnessError = true;
            var message =
                $"EVIDENCE BUDGET EXCEEDED: the passing bundle holds {evidenceBytes / 1048576.0:F1} MB, over its {budgetBytes / 1048576} MB budget.";
            Console.WriteLine(message);
            File.WriteAllText(bundle.Resolve("logs/evidence-budget.txt"), message);
        }
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
        RecordEnvironment(
            bundle,
            new()
            {
                ["run_wall_seconds"] = (wall.Elapsed.TotalSeconds + (kept?.WallSeconds ?? 0)).ToString(
                    "F0",
                    System.Globalization.CultureInfo.InvariantCulture
                ),
            }
        );
        report = bundle.Finish(report);
        Console.WriteLine(Report.Lead(bundle.DirectoryPath, report));
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
                    VerifyJson.Default.DictionaryStringString
                );
            }
            Console.WriteLine($"  {result.Id} {result.Provider} {result.Phase} #{result.Repetition}: {result.Status}");
        }
    }

    /// <summary>
    /// Adds entries to the run's environment record: a provisioned server's Docker VM, container and readback settings,
    /// or the run's wall time.
    /// </summary>
    private static void RecordEnvironment(EvidenceBundle bundle, Dictionary<string, string> specs)
    {
        if (specs.Count == 0)
        {
            return;
        }
        var environment = JsonSerializer.Deserialize(
            File.ReadAllText(bundle.Resolve("environment.json")),
            VerifyJson.Default.DictionaryStringString
        )!;
        foreach (var (key, value) in specs)
        {
            environment[key] = value;
        }
        bundle.Write("environment.json", environment, VerifyJson.Default.DictionaryStringString);
    }

    private static async Task<Provenance> CaptureAsync(RunPlan plan, EvidenceBundle bundle, CancellationToken ct)
    {
        var performance = HardwareDescription.PerformanceMask();
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["hybrid_cpu"] = (performance is not null).ToString(),
            ["performance_logical_processors"] = performance is { } mask
                ? string.Join(",", Enumerable.Range(0, 64).Where(i => (mask >> i & 1) == 1))
                : "none: every core shares one efficiency class, or the topology is unknown",
            ["workers_pinned_to_performance_cores"] = (OwnedWorker.Affinity is not null).ToString(),
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
            ["observer"] =
                "Aggregate SQL counts during a trial and one paged read after it; completion times are database completion stamps on the controller stopwatch.",
            ["repetitions"] = plan.Repetitions.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["preloaded_backlog_roots"] = string.Join(",", plan.PreloadedBacklogRoots.Select(p => $"{p.Key}={p.Value}")),
            ["monotonic_clock_frequency"] = Stopwatch.Frequency.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["build_configuration"] =
                typeof(VerifyCommand).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "unknown",
            ["missing_metrics"] =
                "Physical disk, per-database CPU/waits, live/allocation storage and network metrics require collectors. Whole-container CPU/memory are sampled where cgroup v2 is available; unavailable values are not zero.",
        };
        Console.WriteLine(
            $"CPU: {environment["cpu_model"]} ({environment["physical_cores"]} cores, {Environment.ProcessorCount} available logical processors)"
        );
        bundle.Write("environment.json", environment, VerifyJson.Default.DictionaryStringString);
        var binaryFiles = BinaryManifest();
        bundle.Write("repro/build-manifest.json", binaryFiles, VerifyJson.Default.DictionaryStringString);
        var informational = typeof(VerifyCommand)
            .Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        var compiledCommit = informational?.Split('+').Last().Split('.').Last();
        var commit =
            compiledCommit is { Length: 40 } && compiledCommit.All(Uri.IsHexDigit)
                ? compiledCommit
                : await GitAsync(["rev-parse", "HEAD"], ct);
        if (commit.Length != 40 || !commit.All(Uri.IsHexDigit))
        {
            commit = new string('0', 40);
            environment["source_provenance"] = "Unavailable in this installation; zero hash is an explicit unknown marker.";
            bundle.Write("environment.json", environment, VerifyJson.Default.DictionaryStringString);
        }
        return new(
            plan.ProtocolId,
            plan.ProtocolSha256,
            EvidenceBundle.FileHash(bundle.Resolve("resolved-plan.json")),
            commit,
            typeof(VerifyCommand)
                .Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "Acta.Verify.SourceDirty")
                ?.Value != "false",
            binaryFiles.Values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            "environment.json"
        );
    }

    /// <summary>The hash of every binary this executable runs, by path relative to it, the runtime host included.</summary>
    private static Dictionary<string, string> BinaryManifest()
    {
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
        return binaryFiles;
    }

    /// <summary>
    /// The run's lease, held while it runs. A resume takes the lease an interrupted run left exclusively, which fails
    /// while that run still holds it, on Unix too, where a shared lease would not conflict.
    /// </summary>
    private static FileStream Lease(string path, bool resume)
    {
        try
        {
            return resume
                ? new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)
                : new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
        }
        catch (IOException ex) when (resume)
        {
            throw new InvalidOperationException("The unfinished run is still active in another process; it cannot be resumed.", ex);
        }
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
