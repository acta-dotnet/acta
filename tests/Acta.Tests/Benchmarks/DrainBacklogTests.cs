using Anvil.Bench;
using Xunit;

namespace Acta.Tests.Benchmarks;

/// <summary>
/// A release or full-matrix drain measures sustained capacity, so it drains a backlog of its own: 500,000 jobs on a server
/// provider and 100,000 on SQLite's one writer. <c>--jobs</c> still sets every cell's backlog.
/// </summary>
public sealed class DrainBacklogTests
{
    [Fact]
    public void A_release_drain_drains_a_sustained_backlog_and_the_other_cells_keep_theirs()
    {
        var cells = BaselineSuite.Cells(BaselineSuite.ReleasePreset);

        Assert.All(
            cells.Where(s => s.Scenario == "drain"),
            s =>
                Assert.Equal(
                    s.Provider == "sqlite" ? BaselineSuite.SqliteSustainedDrainJobs : BaselineSuite.SustainedDrainJobs,
                    s.ActualParams.Jobs
                )
        );
        Assert.All(cells.Where(s => s.Scenario == "throughput"), s => Assert.Equal(BaselineSuite.ReleasePreset.Jobs, s.ActualParams.Jobs));
    }

    [Fact]
    public void The_jobs_option_sets_the_drain_backlog_too()
    {
        var options = BenchRunOptions.Parse(["--db", "all", "--jobs", "20000"]);

        Assert.All(
            BaselineSuite.Cells(options.Apply(BaselineSuite.ReleasePreset)).Where(s => s.Scenario == "drain"),
            s => Assert.Equal(20_000, s.ActualParams.Jobs)
        );
    }
}
