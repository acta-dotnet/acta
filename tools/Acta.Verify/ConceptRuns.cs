using System.Diagnostics;
using System.Text.Json;

namespace Acta.Verify;

/// <summary>
/// Runs every concept rung as a learner does, one at a time, and judges it by <c>concepts/concept-runs.json</c>: the
/// lines its output must show, or why it is not run. The <c>800-testing</c> band is xunit and runs under dotnet test.
/// </summary>
public static class ConceptRuns
{
    /// <summary>How long a rung that printed every awaited line has to exit by itself before it is stopped as a pass.</summary>
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(3);

    public static async Task<int> RunAsync(CancellationToken ct)
    {
        var rungs = Rungs("concepts");
        var entries = Load("concepts");
        var unmatched = rungs.Keys.ToHashSet();
        unmatched.SymmetricExceptWith(entries.Keys);
        if (unmatched.Count > 0)
        {
            throw new InvalidOperationException(
                $"concepts/concept-runs.json and the rungs on disk differ on {string.Join(", ", unmatched.Order(StringComparer.Ordinal))}."
            );
        }
        var output = Path.GetFullPath(Path.Combine("artifacts", "concepts", $"{DateTime.UtcNow:yyyyMMddTHHmmssZ}"));
        Directory.CreateDirectory(output);
        using (var build = Process.Start("dotnet", ["build", "concepts/Concepts.slnx", "-c", "Release", "-v:q", "-nologo"]))
        {
            await build.WaitForExitAsync(ct);
            if (build.ExitCode != 0)
            {
                await Console.Error.WriteLineAsync("The concepts build failed; no rung ran.");
                return 3;
            }
        }
        List<string> table = ["| Rung | Verdict | Seconds | Awaited lines or reason |", "|---|---|---|---|"];
        var passed = true;
        foreach (var (rung, project) in rungs)
        {
            var (lines, skip) = entries[rung];
            var clock = Stopwatch.StartNew();
            var failure = skip is null ? await RunRungAsync(project, lines, Path.Combine(output, rung), ct) : null;
            var verdict =
                skip is not null ? "NOT RUN"
                : failure is null ? "PASS"
                : "FAIL";
            passed &= failure is null;
            Console.WriteLine($"{verdict} {rung} {clock.Elapsed.TotalSeconds:0} s{(failure is null ? "" : ": " + failure)}");
            var detail = skip ?? (failure is null ? "" : failure + "; awaited ") + Quoted(lines);
            table.Add($"| {rung} | {verdict} | {clock.Elapsed.TotalSeconds:0} | {detail.Replace("|", "\\|", StringComparison.Ordinal)} |");
        }
        var result = Path.Combine(output, "result.md");
        await File.WriteAllTextAsync(result, $"# Concept runs\n\n{(passed ? "PASS" : "FAIL")}\n\n{string.Join('\n', table)}\n", ct);
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")}: {result}");
        return passed ? 0 : 1;
    }

    /// <summary>Every concept project outside <c>800-testing</c>, by its folder name, in ladder order.</summary>
    public static SortedDictionary<string, string> Rungs(string concepts) =>
        new(
            Directory
                .GetDirectories(concepts)
                .Where(band => Path.GetFileName(band) != "800-testing")
                .SelectMany(Directory.GetDirectories)
                .SelectMany(rung => Directory.GetFiles(rung, "*.csproj"))
                .ToDictionary(project => Path.GetFileName(Path.GetDirectoryName(project))!, Path.GetFullPath),
            StringComparer.Ordinal
        );

    /// <summary>
    /// The entries of <c>concept-runs.json</c>, strictly: each rung maps to a non-empty list of lines or to
    /// <c>{ "skip": "why" }</c>, and anything else, a repeated rung included, is an error.
    /// </summary>
    public static Dictionary<string, (string[] Lines, string? Skip)> Load(string concepts)
    {
        using var file = JsonDocument.Parse(File.ReadAllText(Path.Combine(concepts, "concept-runs.json")));
        var entries = new Dictionary<string, (string[] Lines, string? Skip)>(StringComparer.Ordinal);
        foreach (var rung in file.RootElement.EnumerateObject())
        {
            entries.Add(
                rung.Name,
                rung.Value switch
                {
                    { ValueKind: JsonValueKind.Array } lines
                        when lines.GetArrayLength() > 0
                            && lines
                                .EnumerateArray()
                                .All(l => l.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(l.GetString())) => (
                        [.. lines.EnumerateArray().Select(l => l.GetString()!)],
                        null
                    ),
                    { ValueKind: JsonValueKind.Object } entry
                        when entry.EnumerateObject().Count() == 1
                            && entry.TryGetProperty("skip", out var skip)
                            && skip.ValueKind == JsonValueKind.String
                            && !string.IsNullOrWhiteSpace(skip.GetString()) => ([], skip.GetString()),
                    _ => throw new InvalidOperationException(
                        $"concepts/concept-runs.json writes {rung.Name} as neither the lines its output shows nor {{ \"skip\": \"why\" }}."
                    ),
                }
            );
        }
        return entries;
    }

    /// <summary>Why the rung did not show its idea, or null when it did.</summary>
    private static async Task<string?> RunRungAsync(string project, string[] lines, string folder, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        // The Web SDK runs a project in its own folder unless RunWorkingDirectory says otherwise.
        string[] run =
        [
            "run",
            "--no-build",
            "--no-launch-profile",
            "-c",
            "Release",
            "--project",
            project,
            $"-p:RunWorkingDirectory={folder}",
        ];
        var start = new ProcessStartInfo("dotnet", run)
        {
            WorkingDirectory = folder,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.Environment.Remove("ACTA_LOCAL_PROVIDER");
        start.Environment.Remove("Acta__Provider");
        start.Environment.Remove("ConnectionStrings__acta");
        start.Environment["TEMP"] = start.Environment["TMP"] = start.Environment["TMPDIR"] = folder;
        start.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        // What a web rung's launch profile sets; the other rungs run in the environment a learner's dotnet run gives them.
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        var missing = new HashSet<string>(lines, StringComparer.Ordinal);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(Limit);
        await using var log = File.CreateText(folder + ".log");
        using var process = new Process { StartInfo = start };
        DataReceivedEventHandler read = (_, e) =>
        {
            lock (missing)
            {
                if (e.Data is null)
                {
                    return;
                }
                log.WriteLine(e.Data);
                if (missing.RemoveWhere(line => e.Data.Contains(line, StringComparison.Ordinal)) > 0 && missing.Count == 0)
                {
                    limit.CancelAfter(Grace);
                }
            }
        };
        process.OutputDataReceived += read;
        process.ErrorDataReceived += read;
        process.Start();
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        var stopped = false;
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        finally
        {
            // A rung that ended by itself keeps its exit code, even when the wait gave up while its output still drained.
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                stopped = true;
            }
            // Once the output is drained no handler runs, so what is missing is final.
            await process.WaitForExitAsync(CancellationToken.None);
        }
        return !stopped && process.ExitCode != 0 ? $"exited with {process.ExitCode}"
            : missing.Count == 0 ? null
            : !stopped ? $"exited before printing {Quoted(missing)}"
            : $"did not print {Quoted(missing)} within {Limit.TotalSeconds:0} s";
    }

    private static string Quoted(IEnumerable<string> lines) => string.Join(", ", lines.Select(line => $"`{line}`"));
}
