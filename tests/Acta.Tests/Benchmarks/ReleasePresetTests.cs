using Anvil.Bench;
using Xunit;

namespace Acta.Tests.Benchmarks;

/// <summary>
/// The release round's preset: every execution profile at the quick matrix's points, one warmup and one
/// measured run, and the two serial lanes cells capped at a thousand jobs, so a database takes about a
/// quarter of an hour and a candidate and its control fit beside each other in the same hour.
/// </summary>
public sealed class ReleasePresetTests
{
    private static readonly ExecutionProfile[] AllProfiles = [ExecutionProfile.Buffered, ExecutionProfile.Direct, ExecutionProfile.Bulk];

    [Fact]
    public void Release_covers_every_profile_at_the_quick_points_with_one_measured_run()
    {
        var preset = BaselineSuite.Preset("release");
        var cells = BaselineSuite.Cells(preset, providers: ["pg"]);

        Assert.Equal(1, preset.Policy.WarmupIterations);
        Assert.Equal(1, preset.Policy.MeasuredRepeats);
        Assert.Equal(AllProfiles, cells.Where(s => s.Scenario == "throughput").Select(s => s.KeyProfile!.Value).Distinct());
        Assert.Equal([1, 8, 32], cells.Where(s => s.Scenario == "throughput").Select(s => s.ActualParams.Executors).Distinct());
        Assert.Equal([1, 16], cells.Where(s => s.Scenario == "drain").Select(s => s.ActualParams.Workers).Distinct());
        Assert.Equal(AllProfiles.Length * LanesScenario.Variants.Length, cells.Count(s => s.Scenario == "lanes"));
    }

    [Fact]
    public void Release_runs_the_serial_lanes_cells_at_a_thousand_jobs()
    {
        var preset = BaselineSuite.ReleasePreset;

        Assert.Equal(1_000, BaselineSuite.LanesJobs(preset, ExecutionProfile.Direct, LanesScenario.KeyHot));
        Assert.Equal(1_000, BaselineSuite.LanesJobs(preset, ExecutionProfile.Buffered, LanesScenario.LaneDeep));
        Assert.Equal(50, BaselineSuite.LanesJobs(preset, ExecutionProfile.Bulk, LanesScenario.LaneDeep));
        Assert.Equal(preset.Jobs, BaselineSuite.LanesJobs(preset, ExecutionProfile.Direct, LanesScenario.LaneWide));
    }
}
