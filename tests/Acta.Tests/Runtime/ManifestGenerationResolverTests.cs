using Acta.Configuration;
using Acta.Features.Definitions;
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
}
