using Anvil.Bench;
using Xunit;

namespace Acta.Tests.Benchmarks;

/// <summary>
/// The lanes cells ride the same matrix as the rest of the suite, and a baseline captured before lanes
/// existed still reads back and keys its cells as before, so the lanes cells are simply new beside it.
/// </summary>
public sealed class LanesCellTests
{
    // An rc.3 full-matrix round on PostgreSQL, captured before the lanes scenario existed.
    private static readonly string Rc3Baseline = Path.Combine(
        BaselineCapture.RepoRoot(),
        "docs",
        "benchmarks",
        "baseline-20260921T184939Z.json"
    );

    [Fact]
    public void Full_matrix_has_every_lanes_cell_on_every_profile_and_provider()
    {
        var lanes = BaselineSuite.Cells(BaselineSuite.FullPreset).Where(s => s.Scenario == "lanes").ToList();

        Assert.Equal(3 * 3 * LanesScenario.Variants.Length, lanes.Count);
        foreach (var provider in new[] { "sqlite", "pg", "mssql" })
        {
            foreach (var profile in new[] { ExecutionProfile.Buffered, ExecutionProfile.Direct, ExecutionProfile.Bulk })
            {
                Assert.Equal(
                    LanesScenario.Variants,
                    lanes.Where(s => s.Provider == provider && s.KeyProfile == profile).Select(s => s.ActualParams.Variant)
                );
            }
        }
        var bulkDeep = lanes.Where(s => s.KeyProfile == ExecutionProfile.Bulk && s.ActualParams.Variant == LanesScenario.LaneDeep).ToList();
        Assert.Equal(3, bulkDeep.Count);
        Assert.All(bulkDeep, s => Assert.Equal(200, s.ActualParams.Jobs));
        Assert.All(lanes.Except(bulkDeep), s => Assert.Equal(BaselineSuite.FullPreset.Jobs, s.ActualParams.Jobs));
        Assert.All(lanes, s => Assert.Equal(s.Key.Jobs, s.ActualParams.Jobs));
        Assert.All(lanes, s => Assert.Equal(s.Key.Variant, s.ActualParams.Variant));
        Assert.Contains("j=200", BaselineReport.DescribeCell(bulkDeep[0].Key));
    }

    [Fact]
    public void Quick_bulk_lane_deep_would_run_fifty_jobs()
    {
        Assert.Equal(50, BaselineSuite.LanesJobs(BaselineSuite.QuickPreset, ExecutionProfile.Bulk, LanesScenario.LaneDeep));
        Assert.Equal(
            BaselineSuite.QuickPreset.Jobs,
            BaselineSuite.LanesJobs(BaselineSuite.QuickPreset, ExecutionProfile.Direct, LanesScenario.LaneDeep)
        );
        Assert.Equal(
            BaselineSuite.QuickPreset.Jobs,
            BaselineSuite.LanesJobs(BaselineSuite.QuickPreset, ExecutionProfile.Bulk, LanesScenario.KeyHot)
        );
    }

    [Fact]
    public void A_baseline_from_before_lanes_keys_its_cells_as_before_and_lacks_only_the_variant_cells()
    {
        var rc3 = BaselineCapture.Read(Rc3Baseline);
        var databases = rc3.Databases.ToDictionary(d => d.Provider, StringComparer.OrdinalIgnoreCase);
        var current = BaselineSuite.Cells(BaselineSuite.FullPreset, databases, ["pg"]);

        Assert.All(rc3.Cells, c => Assert.Null(c.Key.Variant));
        var keys = current.Select(s => s.Key).ToHashSet();
        Assert.All(rc3.Cells, c => Assert.Contains(c.Key, keys));

        var old = rc3.Cells.Select(c => c.Key).ToHashSet();
        var added = current.Where(s => !old.Contains(s.Key)).ToList();
        Assert.NotEmpty(added);
        Assert.All(added, s => Assert.Contains(s.Scenario, (string[])["lanes", "claim-skew"]));
    }

    [Fact]
    public void Report_and_cell_line_name_the_lanes_cell()
    {
        var key = new BaselineCellKey("lanes", "sqlite", "3", "Bulk", 50, 8, 16, 0, 1, 0, 200, Variant: LanesScenario.LaneDeep);
        var metrics = new BaselineMetrics(4, 0, 0, 0, 1.5, 2, 3.5, 4, 2, 0, 0, 50, 0, 0, null);
        var baseline = new BaselineFile(
            BaselineCapture.SchemaVersion,
            "quick",
            "2026-09-27T00:00:00Z",
            "test",
            "test",
            false,
            new BaselinePolicy(0, 1, "single"),
            new BaselineEnvironmentInfo("test", "test", "test", 1, 1, "test"),
            [],
            [new BaselineCellResult(key, "ok", metrics, [metrics], ["ok"])]
        );

        Assert.Equal("lanes sqlite bulk lane-deep j=50 e=8 b=16 w=1", BaselineReport.DescribeCell(key));
        var markdown = BaselineReport.Markdown(baseline);
        Assert.Contains("## Lanes", markdown);
        Assert.Contains("| sqlite | Bulk | lane-deep | 50 | 4 | 1.5 | 3.5 | ok |", markdown);
    }
}
