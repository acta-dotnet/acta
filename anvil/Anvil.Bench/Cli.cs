using System.Globalization;

namespace Anvil.Bench;

public static class CellSummary
{
    /// <summary>The one-line headline summary for a finished cell (shared by the console and the Lab).</summary>
    public static string Summarize(string scenario, CellMetrics m) =>
        scenario switch
        {
            "throughput" => $"{m.EndToEndRatePerSec, 8:F0} jobs/s e2e  (enqueue {m.EnqueueRatePerSec:F0}/s, p99 {m.LatencyP99Ms:F1}ms)"
                + Deadlocks(m),
            "drain" => $"{m.DrainRatePerSec, 8:F0} jobs/s drain  ({Ex(m, "workers"):F0}w"
                + (m.Extra is not null && m.Extra.ContainsKey("fairnessSpread") ? $", fairness x{Ex(m, "fairnessSpread"):F1}" : "")
                + $", p99 {m.LatencyP99Ms:F1}ms)"
                + Deadlocks(m),
            "latency" => $"p50 {m.LatencyP50Ms:F2}ms  p95 {m.LatencyP95Ms:F2}ms  p99 {m.LatencyP99Ms:F2}ms",
            "enqueue" => $"{m.EnqueueRatePerSec, 8:F0} enq/s  ({Ex(m, "producers"):F0} producers, p95 {m.LatencyP95Ms:F2}ms/call)",
            "enqueue-batch" => $"{m.EnqueueRatePerSec, 8:F0} enq/s batch  ({Ex(m, "producers"):F0} producers, over {m.EnqueueSeconds:F2}s)",
            "recovery" => $"recovered in {Ex(m, "recoveryMs"):F0}ms  (lease {Ex(m, "leaseTtlSeconds"):F0}s)",
            "wakeup" => $"pickup p50 in-proc {Ex(m, "pickupInProcP50Ms"):F1}ms vs no-op {Ex(m, "pickupNoOpP50Ms"):F1}ms",
            "query" => $"list p95 {m.LatencyP95Ms:F2}ms over {Ex(m, "rows"):F0} rows",
            "purge" =>
                $"purged {Ex(m, "purgeRows"):F0} in {Ex(m, "purgeSeconds"):F1}s ({Ex(m, "purgeRowsPerSec"):F0}/s, probe p95 {Ex(m, "contendedEnqueueP95Ms"):F1}ms)",
            "loadprofile" => m.Extra switch
            {
                { } e when e.ContainsKey("maxSustainableRatePerSec") => $"max sustainable {Ex(m, "maxSustainableRatePerSec"):F0} jobs/s",
                { } e when e.ContainsKey("recoverSeconds") =>
                    $"spike peak {Ex(m, "peakQueueDepth"):F0}, recovered in {Ex(m, "recoverSeconds"):F1}s",
                { } e when e.ContainsKey("latencyDriftPct") =>
                    $"soak {Ex(m, "emitted"):F0} jobs, p95 {m.LatencyP95Ms:F1}ms, drift {Ex(m, "latencyDriftPct"):F0}%",
                _ => $"p95 {m.LatencyP95Ms:F1}ms",
            },
            _ => "",
        };

    private static double Ex(CellMetrics m, string key) => m.Extra is not null && m.Extra.TryGetValue(key, out var v) ? v : 0;

    // Silent when zero (the healthy case); a nonzero count is loud on the headline line.
    private static string Deadlocks(CellMetrics m) => Ex(m, "deadlocks") > 0 ? $"  DEADLOCKS {Ex(m, "deadlocks"):F0}" : "";
}

/// <summary>Simple benchmark CLI dispatch for the Anvil.Bench executable.</summary>
public static class BenchCli
{
    public static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        return await Dispatch(args, ct);
    }

    private static async Task<int> Dispatch(string[] args, CancellationToken ct)
    {
        try
        {
            return args switch
            {
                ["--help"] or ["-h"] => Usage(),
                [] => await InteractiveRunAsync(ct),
                [var presetName, .. var rest] => await ScriptedRunAsync(presetName, rest, ct),
            };
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException)
        {
            await Console.Error.WriteLineAsync(ex.Message);
            await Console.Error.WriteLineAsync("Run with --help for usage.");
            return 2;
        }
    }

    private static async Task<int> InteractiveRunAsync(CancellationToken ct)
    {
        var preset = PromptPreset();
        var providers = PromptProviders();
        return await RunPresetAsync(preset, providers, ct);
    }

    private static async Task<int> ScriptedRunAsync(string presetName, string[] args, CancellationToken ct)
    {
        var preset = BaselineSuite.Preset(presetName);
        var (providers, scenarios) = OptionsFromArgs(args);
        return await RunPresetAsync(preset, providers, ct, scenarios);
    }

    private static async Task<int> RunPresetAsync(
        BenchPreset preset,
        IReadOnlyList<string> providers,
        CancellationToken ct,
        IReadOnlyList<string>? scenarios = null
    )
    {
        if (!await CheckDatabasesAsync(providers, ct).ConfigureAwait(false))
        {
            return 2;
        }

        var measurements = MeasurementCount(preset, providers, scenarios);
        var outputDir = BaselineCapture.OutputDirectory();
        Console.WriteLine(
            $"Running {preset.Name} benchmark for {string.Join(", ", providers)} "
                + $"(jobs={preset.Jobs}, rows={preset.QueryRows})  -  {measurements} measurements, ETA shown live"
        );
        Console.WriteLine($"Output: {outputDir}");

        var baseline = await BaselineCapture.CaptureAsync(
            preset,
            ct,
            ProgressRunner(preset, providers, scenarios),
            providers: providers,
            scenarios: scenarios
        );
        var outPath = BaselineCapture.OutputPath(baseline, outputDir);
        BaselineCapture.Write(baseline, outPath);
        var reportPath = Path.ChangeExtension(outPath, ".md");
        BaselineReport.WriteMarkdown(baseline, reportPath);
        Console.WriteLine($"Wrote baseline to {outPath}");
        Console.WriteLine($"Wrote report to   {reportPath}");
        return 0;
    }

    private static async Task<bool> CheckDatabasesAsync(IReadOnlyList<string> providers, CancellationToken ct)
    {
        foreach (var provider in providers)
        {
            try
            {
                await ProviderConn.CheckAvailableAsync(provider, ct).ConfigureAwait(false);
            }
            catch (BenchDbUnavailableException ex)
            {
                await Console.Error.WriteLineAsync($"Database unavailable: {provider}");
                await Console.Error.WriteLineAsync(ex.Message);
                await Console.Error.WriteLineAsync(
                    "Start the selected database and run again, or choose sqlite for a zero-setup local run."
                );
                await Console.Error.WriteLineAsync("Docker shortcut: docker compose up -d");
                return false;
            }
        }

        return true;
    }

    private static BenchPreset PromptPreset()
    {
        while (true)
        {
            Console.WriteLine("Select benchmark preset:");
            Console.WriteLine("  1) quick  - local 5-10 minute run for one database");
            Console.WriteLine("  2) full   - canonical full matrix");
            Console.Write("Choice [1]: ");
            var raw = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(raw) || raw.Trim() is "1" or "quick")
            {
                return BaselineSuite.QuickPreset;
            }
            if (raw.Trim() is "2" or "full")
            {
                return BaselineSuite.FullPreset;
            }

            Console.WriteLine("Choose 1, 2, quick, or full.");
        }
    }

    private static IReadOnlyList<string> PromptProviders()
    {
        while (true)
        {
            Console.WriteLine();
            Console.WriteLine("Select database:");
            Console.WriteLine("  1) pg");
            Console.WriteLine("  2) mssql");
            Console.WriteLine("  3) sqlite");
            Console.WriteLine("  4) all");
            Console.Write("Choice [1]: ");
            var raw = Console.ReadLine();
            var value = string.IsNullOrWhiteSpace(raw) ? "pg" : raw.Trim();
            if (value is "1")
            {
                value = "pg";
            }
            else if (value is "2")
            {
                value = "mssql";
            }
            else if (value is "3")
            {
                value = "sqlite";
            }
            else if (value is "4")
            {
                value = "all";
            }

            try
            {
                return ProvidersFromDb(value);
            }
            catch (ArgumentException)
            {
                Console.WriteLine("Choose pg, mssql, sqlite, or all.");
            }
        }
    }

    private static (IReadOnlyList<string> Providers, IReadOnlyList<string>? Scenarios) OptionsFromArgs(string[] args)
    {
        string? db = null;
        List<string>? scenarios = null;

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is not ("--db" or "--scenario"))
            {
                throw new ArgumentException($"Unknown option '{args[i]}'.");
            }
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Missing value for {args[i]}.");
            }

            if (args[i] == "--db")
            {
                db = args[++i];
            }
            else
            {
                (scenarios ??= []).Add(args[++i]);
            }
        }

        return db is null
            ? throw new ArgumentException("Missing --db sqlite|pg|mssql|all.")
            : ((IReadOnlyList<string> Providers, IReadOnlyList<string>? Scenarios))(ProvidersFromDb(db), scenarios);
    }

    private static IReadOnlyList<string> ProvidersFromDb(string db)
    {
        if (string.Equals(db, "all", StringComparison.OrdinalIgnoreCase))
        {
            return BaselineSuite.NormalizeProviders(null);
        }

        var providers = BaselineSuite.NormalizeProviders([db]);
        return providers.Count != 1 ? throw new ArgumentException("Choose one database or all.") : providers;
    }

    /// <summary>Total measured cells (median-of-N counted) - the denominator for the progress bar.</summary>
    private static int MeasurementCount(BenchPreset preset, IReadOnlyList<string> providers, IReadOnlyList<string>? scenarios = null)
    {
        var specs = BaselineSuite.Cells(preset, providers: providers, scenarios: scenarios);
        return specs.Count * (preset.Policy.WarmupIterations + preset.Policy.MeasuredRepeats);
    }

    private static BaselineCellRunner ProgressRunner(
        BenchPreset preset,
        IReadOnlyList<string> providers,
        IReadOnlyList<string>? scenarios = null
    )
    {
        var total = MeasurementCount(preset, providers, scenarios);
        var done = 0;
        // Whole-run wall clock: the ETA extrapolates from cells already finished. Console output happens
        // only here, between cells - never inside a scenario's internally-timed enqueue/drain window - so
        // the progress bar cannot perturb the measured numbers.
        var startedAll = DateTime.UtcNow;
        return async (spec, repeatIndex, warmup, ct) =>
        {
            var phase = warmup
                ? $"warmup {repeatIndex + 1}/{preset.Policy.WarmupIterations}"
                : $"run {repeatIndex + 1}/{preset.Policy.MeasuredRepeats}";
            var cell = DescribeBaselineCell(spec.Key);
            var number = Interlocked.Increment(ref done);
            var finished = number - 1;
            var pct = total > 0 ? (int)(100.0 * finished / total) : 0;
            var eta =
                finished >= 3
                    ? FormatDuration(TimeSpan.FromTicks((DateTime.UtcNow - startedAll).Ticks / finished * (total - finished)))
                    : "-";
            Console.Write($"[{Bar(finished, total, 20)} {pct, 3}%] {number, 3}/{total}  ETA {eta, -7} | {phase, -10} {cell, -60} ... ");
            var started = DateTime.UtcNow;
            var result = await BaselineCapture.RunCellAsync(spec, repeatIndex, warmup, ct).ConfigureAwait(false);
            var elapsed = DateTime.UtcNow - started;
            Console.WriteLine(
                $"{CellSummary.Summarize(spec.ActualScenario, result.Metrics).Trim()} [{result.Status}] {elapsed.TotalSeconds:F1}s"
            );
            return result;
        };
    }

    private static string Bar(int done, int total, int width)
    {
        var filled = Math.Clamp(total > 0 ? (int)Math.Round((double)done / total * width) : 0, 0, width);
        return new string('#', filled) + new string('.', width - filled);
    }

    private static string FormatDuration(TimeSpan t) =>
        t.TotalSeconds < 60 ? $"{t.TotalSeconds:F0}s"
        : t.TotalMinutes < 60 ? $"{(int)t.TotalMinutes}m{t.Seconds:D2}s"
        : $"{(int)t.TotalHours}h{t.Minutes:D2}m";

    private static string DescribeBaselineCell(BaselineCellKey c)
    {
        var parts = new List<string> { c.Scenario, c.Provider };
        if (c.ExecutionProfile is { } profile)
        {
            parts.Add(profile.ToLowerInvariant());
        }
        if (c.Jobs > 0)
        {
            parts.Add($"j={c.Jobs}");
        }
        if (c.Executors > 0)
        {
            parts.Add($"e={c.Executors}");
        }
        if (c.ClaimBatch > 0)
        {
            parts.Add($"b={c.ClaimBatch}");
        }
        if (c.Workers > 0)
        {
            parts.Add($"w={c.Workers}");
        }
        if (c.Rows > 0)
        {
            parts.Add($"r={c.Rows}");
        }
        return string.Join(' ', parts);
    }

    private static int Usage()
    {
        Console.WriteLine(
            """
            acta-bench - Acta benchmarking CLI.

            Usage:
              acta-bench
              acta-bench quick --db sqlite|pg|mssql|all
              acta-bench full --db sqlite|pg|mssql|all

            Presets:
              quick   Local 5-10 minute run for one database. Uses 1 measured run, 1,000 jobs, and 10,000 query rows.
              full    Canonical full matrix. Uses 1 warmup, median of 3 measured runs, 10,000 jobs, and 100,000 query rows.

            Output:
              Every run writes JSON and Markdown to anvil/Anvil.Bench/.benchmarks/.

            Database connection:
              ACTA_TEST_PG / ACTA_TEST_MSSQL, else the local fallback.

            Examples:
              acta-bench
              acta-bench quick --db pg
              acta-bench full --db mssql
              acta-bench quick --db all

            Exit codes: 0 ok; 2 usage.
            """
        );
        return 0;
    }
}
