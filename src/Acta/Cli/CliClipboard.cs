using System.Diagnostics;

namespace Acta;

/// <summary>
/// Clipboard fallback for a read verb's target: when info, status, result, events, or explain is invoked
/// without one, the runner reads the OS clipboard and accepts one token up to the DeduplicationKey size.
/// </summary>
internal static class CliClipboard
{
    /// <summary>
    /// Validates clipboard text as a CLI target: trimmed, non-empty, at most <see cref="DeduplicationKey.MaxLength"/>
    /// characters, and printable ASCII without whitespace, as a job ref, an <c>id:&lt;n&gt;</c>, and a deduplication key
    /// all are. Which of the three it is stays in the runner, same as for an explicit argument.
    /// </summary>
    public static bool TryResolveTarget(string? text, out string target)
    {
        target = "";
        if (text is null)
        {
            return false;
        }

        var candidate = text.Trim();
        if (candidate.Length == 0 || candidate.Length > DeduplicationKey.MaxLength || candidate.AsSpan().ContainsAnyExceptInRange('!', '~'))
        {
            return false;
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

            // The read is bounded with the wait: a tool that never closes its output would otherwise hold the verb.
            var text = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromSeconds(10)) || !text.Wait(TimeSpan.FromSeconds(1)))
            {
                process.Kill();
                return null;
            }
            return process.ExitCode == 0 ? text.Result : null;
        }
        catch (Exception)
        {
            // A missing tool or denied spawn means no clipboard; the caller reports a usage error.
            return null;
        }
    }
}
