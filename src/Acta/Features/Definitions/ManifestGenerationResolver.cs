using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Acta.Configuration;

namespace Acta.Features.Definitions;

/// <summary>
/// Resolves the worker's manifest generation, the monotonic governor for definition promotion:
/// the explicit <see cref="JobsOptions.ManifestGenerationUtc"/> if set, otherwise the entry
/// assembly's file last-write-time, falling back to the running executable's last-write-time under
/// single-file or AOT publish (where the entry assembly reports no file location). Never process
/// start time. The result is normalized to UTC and truncated to millisecond precision so it survives
/// a DB write/read round trip on both providers.
/// </summary>
internal static class ManifestGenerationResolver
{
    [UnconditionalSuppressMessage(
        "SingleFile",
        "IL3000",
        Justification = "Assembly.Location is empty under single-file/AOT; that case falls back to the running executable below."
    )]
    public static DateTime Resolve(JobsOptions options, Assembly? entryAssembly)
    {
        if (options.ManifestGenerationUtc is { } explicitGeneration)
        {
            return Normalize(DateTime.SpecifyKind(explicitGeneration, DateTimeKind.Utc));
        }

        // The entry assembly's file last-write-time, falling back to the running executable's publish
        // stamp when Assembly.Location is empty (single-file / AOT publish). Both are file timestamps
        // with the same monotonic-governor semantics, so a worker boots without an explicit generation.
        return Normalize(
            FileTimestamp(entryAssembly?.Location)
                ?? FileTimestamp(Environment.ProcessPath)
                ?? throw new InvalidOperationException(
                    "Acta could not resolve a manifest generation from the entry assembly or the running "
                        + "executable. Set JobsOptions.ManifestGenerationUtc explicitly."
                )
        );

        static DateTime? FileTimestamp(string? path) =>
            !string.IsNullOrEmpty(path) && File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
    }

    private static DateTime Normalize(DateTime utc) => new(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerMillisecond), DateTimeKind.Utc);
}
