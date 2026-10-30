using System.Diagnostics;
using System.Text.Json;

namespace Acta.Verify;

/// <summary>
/// Refuses a run on a box that still carries another run's processes or owned containers: each writes to the drive the
/// databases use and takes cores the run measures, and a leftover owned container also holds the Docker VM's memory.
/// </summary>
internal static class CleanStart
{
    private static readonly string[] Leftovers = ["acta-verify", "Acta.Verify", "acta-bench", "Acta.Bench", "Anvil", "pg_test_fsync"];

    /// <summary>
    /// Whether the run may start. A test host does not leave anything behind, so it only warns, unless the run's
    /// verdict rests on measurement.
    /// </summary>
    public static async Task<bool> CheckAsync(bool measures, CancellationToken ct)
    {
        var lines = await CommandLinesAsync(ct);
        List<string> refusals = [.. Running(Leftovers, lines).Select(p => $"PID {p.Key} {p.Value}")];
        try
        {
            var containers = await OwnedProvider.DockerAsync(
                ["ps", "--filter", "label=acta.bench.owner", "--format", "{{.ID}} {{.Image}} {{.Status}}"],
                null,
                ct
            );
            refusals.AddRange(containers.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(c => $"owned container {c}"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // No Docker engine means no owned container can be running.
        }
        var busy = Running(["testhost"], lines).Select(p => $"PID {p.Key} {p.Value}").ToArray();
        if (measures)
        {
            refusals.AddRange(busy);
        }
        else
        {
            foreach (var host in busy)
            {
                Console.WriteLine($"Busy box: {host}. Correctness holds; timings from this run are not comparable.");
            }
        }
        if (refusals.Count == 0)
        {
            return true;
        }
        Console.WriteLine("The box is not clean; nothing ran:");
        foreach (var refusal in refusals)
        {
            Console.WriteLine("  " + refusal);
        }
        Console.WriteLine("Stop these processes, and run acta-verify cleanup --yes for owned containers and databases.");
        return false;
    }

    /// <summary>
    /// The processes other than this one whose image is one of <paramref name="names"/>, or whose command line runs one
    /// of them from its DLL under dotnet, by PID, each with its command line where <paramref name="lines"/> has it.
    /// </summary>
    public static Dictionary<int, string> Running(IEnumerable<string> names, IReadOnlyDictionary<int, string> lines)
    {
        Dictionary<int, string> found = [];
        foreach (var name in names)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    if (process.Id != Environment.ProcessId)
                    {
                        found[process.Id] = lines.GetValueOrDefault(process.Id, process.ProcessName);
                    }
                }
            }
            foreach (var (pid, line) in lines)
            {
                if (pid != Environment.ProcessId && line.Contains(name + ".dll", StringComparison.OrdinalIgnoreCase))
                {
                    found[pid] = line;
                }
            }
        }
        return found;
    }

    /// <summary>The command line of every process, where the platform tells; an empty map where it does not.</summary>
    private static async Task<Dictionary<int, string>> CommandLinesAsync(CancellationToken ct)
    {
        Dictionary<int, string> lines = [];
        try
        {
            if (OperatingSystem.IsLinux())
            {
                foreach (var directory in Directory.EnumerateDirectories("/proc"))
                {
                    if (int.TryParse(Path.GetFileName(directory), out var pid) && File.Exists(Path.Combine(directory, "cmdline")))
                    {
                        lines[pid] = (await File.ReadAllTextAsync(Path.Combine(directory, "cmdline"), ct)).Replace('\0', ' ').Trim();
                    }
                }
            }
            else if (OperatingSystem.IsWindows())
            {
                var json = await OwnedProvider.RunAsync(
                    "pwsh",
                    [
                        "-NoProfile",
                        "-NonInteractive",
                        "-Command",
                        "Get-CimInstance Win32_Process | Select-Object ProcessId, CommandLine | ConvertTo-Json -Compress",
                    ],
                    null,
                    ct
                );
                foreach (var process in JsonDocument.Parse(json).RootElement.EnumerateArray())
                {
                    if (process.GetProperty("CommandLine").GetString() is { } line)
                    {
                        lines[process.GetProperty("ProcessId").GetInt32()] = line;
                    }
                }
            }
        }
        catch (Exception ex)
            when (ex
                    is IOException
                        or System.ComponentModel.Win32Exception
                        or JsonException
                        or UnauthorizedAccessException
                        or InvalidOperationException
            )
        {
            // Without command lines each process is named by its image alone.
        }
        return lines;
    }
}
