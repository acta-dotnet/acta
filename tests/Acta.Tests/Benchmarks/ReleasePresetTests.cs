using Anvil.Bench;
using Xunit;

namespace Acta.Tests.Benchmarks;

/// <summary>
/// The release round's preset: the quick matrix's points, one warmup and one
/// measured run, and the two serial lanes cells capped at a thousand jobs, so a database takes about a
/// quarter of an hour and a candidate and its control fit beside each other in the same hour.
/// </summary>
public sealed class ReleasePresetTests
{
    [Fact]
    public void Release_covers_the_quick_points_with_one_measured_run()
    {
        var preset = BaselineSuite.Preset("release");
        var cells = BaselineSuite.Cells(preset, providers: ["pg"]);

        Assert.Equal(1, preset.Policy.WarmupIterations);
        Assert.Equal(1, preset.Policy.MeasuredRepeats);
        Assert.Equal([1, 8, 32], cells.Where(s => s.Scenario == "throughput").Select(s => s.ActualParams.Executors).Distinct());
        Assert.Equal([1, 16], cells.Where(s => s.Scenario == "drain").Select(s => s.ActualParams.Workers).Distinct());
        Assert.Equal(LanesScenario.Variants.Length, cells.Count(s => s.Scenario == "lanes"));
    }

    [Fact]
    public void Release_runs_the_serial_lanes_cells_at_a_thousand_jobs()
    {
        var preset = BaselineSuite.ReleasePreset;

        Assert.Equal(1_000, BaselineSuite.LanesJobs(preset, LanesScenario.KeyHot));
        Assert.Equal(1_000, BaselineSuite.LanesJobs(preset, LanesScenario.LaneDeep));
        Assert.Equal(preset.Jobs, BaselineSuite.LanesJobs(preset, LanesScenario.LaneWide));
    }
}
