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
            bundle.RecordEnvironment(
                new()
                {
                    ["resumes"] = (int.Parse(resumes, System.Globalization.CultureInfo.InvariantCulture) + 1).ToString(
                        System.Globalization.CultureInfo.InvariantCulture
                    ),
                    ["units_kept_on_resume"] = kept.Finished.Count == 0 ? "none" : string.Join(",", kept.Finished),
                }
            );
            Console.WriteLine($"Resuming {plan.RunId}: kept {(kept.Finished.Count == 0 ? "nothing" : string.Join(", ", kept.Finished))}.");
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
        // The databases of the correctness phase run at once and share these: the results, the environment record,
        // the run's flags and the disk record.
        var gate = new object();
        var history = DryRun.History(output);
        using var disk = DiskCounters.Start();
        disk.Enter(resume ? "at resume" : "before");
        Console.WriteLine($"Acta Verify {plan.SuiteName} | {string.Join(", ", plan.Providers)} | {bundle.DirectoryPath}");
        Console.WriteLine(plan.Suite.Purpose);
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
            if (
                plan.SuiteName != "quick"
                && typeof(VerifyCommand).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration != "Release"
            )
            {
                harnessError = true;
                settingsVerified = false;
                foreach (var provider in plan.Providers)
                {
                    foreach (var scenario in plan.Scenarios)
                    {
                        Record(
                            CaseResult.Incomplete(
                                scenario.Id,
                                provider,
                                "verify",
                                $"The {plan.SuiteName} suite requires a Release executable. Rebuild or publish with -c Release; no engine workload was started."
                            )
                        );
                    }
                }
            }
            else
            {
                // Half a minute of the drive at rest before any database writes to it.
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                Scenario[] performance = [.. plan.Scenarios.Where(s => ScenarioRunner.PhaseOf(s) == "performance")];
                Scenario[] correctness = [.. plan.Scenarios.Where(s => ScenarioRunner.PhaseOf(s) == "correctness")];
                foreach (var provider in performance.Length > 0 ? plan.Providers : [])
                {
                    ct.ThrowIfCancellationRequested();
                    disk.Enter(provider);
                    await RunUnitAsync("performance", provider, performance, 1);
                }
                if (correctness.Length > 0)
                {
                    disk.Enter("correctness");
                    string[] pending = [.. plan.Providers.Where(p => kept?.Finished.Contains(RunResume.Unit("correctness", p)) != true)];
                    // The server containers running side by side split the memory cap one would take alone.
                    var servers = pending.Count(p => p != "sqlite");
                    await Task.WhenAll(pending.Select(p => RunUnitAsync("correctness", p, correctness, servers > 1 ? 1.0 / servers : 1)));
                    disk.Save(bundle);
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
        // And half a minute after the last database, while the drive settles from the run's writes.
        disk.Enter("after");
        if (!aborted)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), CancellationToken.None);
        }
        disk.Stop(bundle);
        foreach (var provider in plan.Providers)
        {
            foreach (var c in plan.Scenarios.Where(c => !results.Any(r => r.Provider == provider && r.Id == c.Id)))
            {
                Record(CaseResult.Incomplete(c.Id, provider, "verify", "Run ended before this scenario ran."));
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
        foreach (var name in HarnessControls.Names.Where(name => !results.Any(r => r.Id == name && r.Provider == "harness")))
        {
            Record(CaseResult.Incomplete(name, "harness", "self_test", "The self-check did not finish."));
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
            "acta-verify.result/1",
            plan.RunId,
            "real_execution",
            status,
            provenance,
            new(
                plan.Providers,
                [
                    .. plan.Providers.Where(p =>
                        ScenarioRunner
                            .Phases.Where(phase => plan.Scenarios.Any(s => ScenarioRunner.PhaseOf(s) == phase))
                            .All(phase => complete.Contains(RunResume.Unit(phase, p)))
                    ),
                ],
                ["physical_power_loss", "vm_crash", "multi_region_failover"]
            ),
            coverage,
            new("single_node_durable", settingsVerified, false),
            [.. results],
            [],
            harnessError
        );
        bundle.RecordEnvironment(
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
            lock (gate)
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

        // One phase on one database: provision, measure on a quiet disk where the suite gates on it, run the phase's
        // scenarios, owe their coverage, and mark the unit finished so a resume keeps it.
        async Task RunUnitAsync(string phase, string provider, Scenario[] scenarios, double capShare)
        {
            var unit = RunResume.Unit(phase, provider);
            if (kept?.Finished.Contains(unit) == true)
            {
                return;
            }
            var unitWall = Stopwatch.StartNew();
            var verified = true;
            var ended = false;
#pragma warning disable CA2000 // Disposed in the finally below, where a failed disposal is logged.
            var environment = new OwnedProvider(provider, bundle.Resolve($"repro/state/{unit}"));
#pragma warning restore CA2000
            try
            {
                Console.WriteLine($"Preflight/provision {unit}");
                try
                {
                    await environment.ProvisionAsync(plan, plan.UseComposeServers, ct, capShare);
                }
                finally
                {
                    // The performance servers' specifications are the ones a comparison reads; later phases are prefixed.
                    bundle.RecordEnvironment(
                        environment.ServerSpecs.ToDictionary(s => phase == "performance" ? s.Key : $"{phase}_{s.Key}", s => s.Value)
                    );
                }
                var settings = await environment.CaptureSettingsAsync(ct);
                verified = environment.SettingsVerified;
                bundle.Write($"histories/{unit}-settings.json", settings, VerifyJson.Default.DictionaryStringString);
                if (phase == "performance" && plan.MeasurementGatesVerdict)
                {
                    // Provisioning writes, and so can anything else on the box: the measurements start once the
                    // drives have been quiet a few seconds, or within a minute saying they were not.
                    var quiet = await disk.WaitForQuietAsync(
                        TimeSpan.FromSeconds(plan.Policy("measurement", "disk_quiet", "seconds")),
                        TimeSpan.FromSeconds(plan.Policy("measurement", "disk_quiet", "limit_seconds")),
                        plan.Policy("measurement", "disk_quiet", "min_idle_percent"),
                        plan.Policy("measurement", "disk_quiet", "max_writes_per_second"),
                        ct
                    );
                    Console.WriteLine($"{provider}: quiet disk before measuring: {quiet}");
                    bundle.RecordEnvironment(new() { [$"disk_quiet_before_{provider}"] = quiet });
                }
                await ScenarioRunner.RunAsync(
                    plan,
                    scenarios,
                    environment,
                    bundle,
                    Record,
                    s => DryRun.Estimate(plan, s, provider, history).Seconds,
                    ct
                );
                foreach (var gap in CoveragePolicy.Missing(plan with { Scenarios = scenarios }, provider, Snapshot()))
                {
                    Record(gap);
                }
                if (
                    Snapshot()
                        .Where(r => r.Provider == provider && scenarios.Any(s => s.Id == r.Id))
                        .All(c => c.Status is "PASS" or "NOT_APPLICABLE")
                )
                {
                    lock (gate)
                    {
                        complete.Add(unit);
                    }
                    environment.RetainState = false;
                }
                ended = true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not ServerSetupException)
            {
                verified = false;
                ended = true;
                Console.WriteLine($"{unit}: incomplete ({ex.GetType().Name}: {ex.Message}); retained evidence.");
                File.WriteAllText(bundle.Resolve($"logs/{unit}-error.txt"), ex.ToString());
                var recorded = Snapshot();
                foreach (var c in scenarios.Where(c => !recorded.Any(r => r.Provider == provider && r.Id == c.Id)))
                {
                    Record(
                        CaseResult.Incomplete(
                            c.Id,
                            provider,
                            "verify",
                            ex is ResourceBudgetException ? $"The resource budget stopped this database: {ex.Message}"
                                : provider == "mssql" && !System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported
                                    ? "SQL Server requires dynamic code support; publish the managed build with -p:PublishAot=false. Native and AOT-constrained managed runs do not cover SQL Server."
                                : $"Provider setup/execution unavailable ({ex.GetType().Name}: {ex.Message})."
                        )
                    );
                }
            }
            finally
            {
                // A unit's verdicts are recorded by now; leftover resources do not stop the next unit.
                try
                {
                    await environment.DisposeAsync();
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"{unit}: cleanup failed ({ex.GetType().Name}: {ex.Message}); acta-verify cleanup lists what remains."
                    );
                    File.WriteAllText(bundle.Resolve($"logs/{unit}-cleanup-error.txt"), ex.ToString());
                }
                lock (gate)
                {
                    settingsVerified &= verified;
                    // Only a unit that ran to its end is kept by a resume; an interrupted one runs again.
                    if (ended)
                    {
                        // The parallel phase saves its disk record once every database is done; saving ends a phase.
                        if (phase == "performance")
                        {
                            disk.Save(bundle);
                        }
                        RunResume.MarkFinished(bundle, unit, complete.Contains(unit), verified, unitWall.Elapsed.TotalSeconds);
                    }
                }
            }
        }

        CaseResult[] Snapshot()
        {
            lock (gate)
            {
                return [.. results];
            }
        }
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
            ["suite"] = plan.SuiteName,
            ["measurement_version"] = plan.MeasurementVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
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
