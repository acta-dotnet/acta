using Anvil.Bench;
using Xunit;

namespace Acta.Tests.Benchmarks;

/// <summary>
/// The lanes cells ride the same matrix as the rest of the suite, every variant on every provider.
/// </summary>
public sealed class LanesCellTests
{
    [Fact]
    public void Full_matrix_has_every_lanes_cell_on_every_provider()
    {
        var lanes = BaselineSuite.Cells(BaselineSuite.FullPreset).Where(s => s.Scenario == "lanes").ToList();

        Assert.Equal(3 * LanesScenario.Variants.Length, lanes.Count);
        foreach (var provider in new[] { "sqlite", "pg", "mssql" })
        {
            Assert.Equal(LanesScenario.Variants, lanes.Where(s => s.Provider == provider).Select(s => s.ActualParams.Variant));
        }
        Assert.All(lanes, s => Assert.Equal(BaselineSuite.FullPreset.Jobs, s.ActualParams.Jobs));
        Assert.All(lanes, s => Assert.Equal(s.Key.Jobs, s.ActualParams.Jobs));
        Assert.All(lanes, s => Assert.Equal(s.Key.Variant, s.ActualParams.Variant));
    }

    [Fact]
    public void Report_and_cell_line_name_the_lanes_cell()
    {
        var key = new BaselineCellKey("lanes", "sqlite", "3", 50, 8, 0, 1, 0, 200, Variant: LanesScenario.LaneDeep);
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

        Assert.Equal("lanes sqlite lane-deep j=50 e=8 w=1", BaselineReport.DescribeCell(key));
        var markdown = BaselineReport.Markdown(baseline);
        Assert.Contains("## Lanes", markdown);
        Assert.Contains("| sqlite | lane-deep | 50 | 4 | 1.5 | 3.5 | ok |", markdown);
    }
}
