using Anvil.Bench;
using Xunit;

namespace Acta.Tests.Benchmarks;

/// <summary>
/// The claim-skew cells ride the same matrix as the rest of the suite and drain the same due backlog on
/// every preset, so a quick cell and a full cell measure the same background.
/// </summary>
public sealed class ClaimSkewCellTests
{
    [Fact]
    public void Full_matrix_has_every_claim_skew_cell_on_every_profile_and_provider()
    {
        var cells = BaselineSuite.Cells(BaselineSuite.FullPreset).Where(s => s.Scenario == "claim-skew").ToList();

        Assert.Equal(3 * 3 * ClaimSkewScenario.Variants.Length, cells.Count);
        foreach (var provider in new[] { "sqlite", "pg", "mssql" })
        {
            foreach (var profile in new[] { ExecutionProfile.Buffered, ExecutionProfile.Direct, ExecutionProfile.Bulk })
            {
                Assert.Equal(
                    ClaimSkewScenario.Variants,
                    cells.Where(s => s.Provider == provider && s.KeyProfile == profile).Select(s => s.ActualParams.Variant)
                );
            }
        }
        Assert.All(cells, s => Assert.Equal(ClaimSkewScenario.DueJobs, s.ActualParams.Jobs));
        Assert.All(cells, s => Assert.Equal(s.Key.Variant, s.ActualParams.Variant));
    }

    [Fact]
    public void Quick_runs_the_same_due_backlog_on_direct_only()
    {
        var cells = BaselineSuite.Cells(BaselineSuite.QuickPreset, providers: ["pg"], scenarios: ["claim-skew"]);

        Assert.Equal(ClaimSkewScenario.Variants, cells.Select(s => s.ActualParams.Variant));
        Assert.All(cells, s => Assert.Equal(ExecutionProfile.Direct, s.KeyProfile));
        Assert.All(cells, s => Assert.Equal(ClaimSkewScenario.DueJobs, s.ActualParams.Jobs));
    }

    [Fact]
    public void Report_and_cell_line_name_the_claim_skew_cell()
    {
        var key = new BaselineCellKey(
            "claim-skew",
            "pg",
            "18",
            "Direct",
            ClaimSkewScenario.DueJobs,
            16,
            32,
            0,
            1,
            0,
            200,
            Variant: ClaimSkewScenario.Parked
        );
        var metrics = new BaselineMetrics(0, 0, 4321, 0, 1.5, 2, 3.5, 4, 2, 0, 0, ClaimSkewScenario.DueJobs, 0, 0, null);
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

        Assert.Equal("claim-skew pg direct parked j=10000 e=16 b=32 w=1", BaselineReport.DescribeCell(key));
        var markdown = BaselineReport.Markdown(baseline);
        Assert.Contains("## Claim skew", markdown);
        Assert.Contains("| pg | Direct | parked | 10000 | 4321 | 1.5 | 3.5 | ok |", markdown);
    }
}
