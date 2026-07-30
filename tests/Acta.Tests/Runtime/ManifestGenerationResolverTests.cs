using Acta.Configuration;
using Acta.Modules.Execution.Definitions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Acta.Tests.Runtime;

public class ManifestGenerationResolverTests
{
    [Fact]
    public void Explicit_option_is_returned_as_utc()
    {
        var options = new JobsOptions { ManifestGenerationUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Unspecified) };

        var result = ManifestGenerationResolver.Resolve(options, entryAssembly: null);

        Assert.Equal(DateTimeKind.Utc, result.Kind);
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), result);
    }

    [Fact]
    public void Result_is_truncated_to_millisecond_precision()
    {
        var options = new JobsOptions { ManifestGenerationUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc).AddTicks(1234) };

        var result = ManifestGenerationResolver.Resolve(options, entryAssembly: null);

        Assert.Equal(0, result.Ticks % TimeSpan.TicksPerMillisecond);
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), result);
    }

    [Fact]
    public void Falls_back_to_entry_assembly_file_timestamp()
    {
        var asm = typeof(ManifestGenerationResolverTests).Assembly;

        var result = ManifestGenerationResolver.Resolve(new JobsOptions(), asm);

        Assert.Equal(DateTimeKind.Utc, result.Kind);
        Assert.True(result > new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Falls_back_to_running_executable_when_no_assembly_location()
    {
        // Simulates single-file / AOT publish: the entry assembly reports no file location, so the
        // resolver derives the generation from the running executable's publish stamp instead of throwing.
        var result = ManifestGenerationResolver.Resolve(new JobsOptions(), entryAssembly: null);

        Assert.Equal(DateTimeKind.Utc, result.Kind);
        Assert.Equal(0, result.Ticks % TimeSpan.TicksPerMillisecond);
        Assert.True(result > new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Fallback_resolution_logs_a_warning_naming_the_resolved_generation()
    {
        // The generation is the monotonic governor for definition promotion; the file-timestamp
        // fallback can be skewed by container image copies and artifact restores, so booting on it
        // must say so loudly.
        var log = new RecordingLogger();

        var resolved = ManifestGenerationResolver.Resolve(new JobsOptions(), typeof(ManifestGenerationResolverTests).Assembly, log);

        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("ManifestGenerationUtc", entry.Message);
        Assert.Contains(resolved.ToString("O"), entry.Message);
    }

    [Fact]
    public void Explicit_generation_logs_nothing()
    {
        var log = new RecordingLogger();
        var options = new JobsOptions { ManifestGenerationUtc = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc) };

        ManifestGenerationResolver.Resolve(options, typeof(ManifestGenerationResolverTests).Assembly, log);

        Assert.Empty(log.Entries);
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => Entries.Add((logLevel, formatter(state, exception)));
    }
}
