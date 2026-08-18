using System.Diagnostics;

namespace Acta.Runtime.Cli;

/// <summary>
/// Clipboard fallback for the CLI target: when a verb is invoked without a job id or deduplication-key,
/// the runner reads the OS clipboard and accepts a single-line value up to the DeduplicationKey size.
/// </summary>
internal static class CliClipboard
{
    /// <summary>
    /// Validates clipboard text as a CLI target: trimmed, single line, non-empty, and at most
    /// <see cref="DeduplicationKey.MaxLength"/> characters. Numeric-vs-deduplication-key resolution stays in
    /// the runner, same as for an explicit argument.
    /// </summary>
    public static bool TryResolveTarget(string? text, out string target)
    {
        target = "";
        if (text is null)
        {
            return false;
        }

        var candidate = text.Trim();
        if (candidate.Length == 0 || candidate.Length > DeduplicationKey.MaxLength)
        {
            return false;
        }

        foreach (var ch in candidate)
        {
            if (ch < 0x20 || ch == 0x7F)
            {
                return false;
            }
        }

        target = candidate;
        return true;
    }

    /// <summary>Reads the OS clipboard text via the platform tool; null when unavailable.</summary>
    public static string? ReadText()
    {
        if (OperatingSystem.IsWindows())
        {
            return Run("powershell", "-NoProfile -NonInteractive -Command Get-Clipboard -Raw");
        }
        return OperatingSystem.IsMacOS()
            ? Run("pbpaste", "")
            : Run("xclip", "-selection clipboard -o") ?? Run("xsel", "--clipboard --output") ?? Run("wl-paste", "--no-newline");
    }

    private static string? Run(string fileName, string arguments)
    {
        try
        {
            using var process = Process.Start(
                new ProcessStartInfo(fileName, arguments)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            );
            if (process is null)
            {
                return null;
            }

            var text = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(TimeSpan.FromSeconds(10)))
            {
                process.Kill();
                return null;
            }
            return process.ExitCode == 0 ? text : null;
        }
        catch (Exception)
        {
            // A missing tool or denied spawn means no clipboard; the caller reports a usage error.
            return null;
        }
    }
}
