using System.Globalization;
using Anvil.Bench;
using Xunit;

namespace Acta.Tests.Benchmarks;

/// <summary>
/// A multi-worker drain spreads its workers and backlog over two to four namespaces, as a deployment runs several
/// namespaces of a few workers each, never more than 16 workers in one; a one-worker drain stays in one, and a baseline
/// captured before the spread reads back as the one namespace it ran in.
/// </summary>
public sealed class DrainNamespacesTests
{
    // An rc.3 full-matrix round on PostgreSQL, captured before drains spread over namespaces.
    private static readonly string Rc3Baseline = Path.Combine(
        BaselineCapture.RepoRoot(),
        "docs",
        "benchmarks",
        "baseline-20260921T184939Z.json"
    );

    [Fact]
    public void A_multi_worker_drain_spreads_over_two_namespaces_by_default()
    {
        var drains = BaselineSuite.Cells(BaselineSuite.ReleasePreset).Where(s => s.Scenario == "drain").ToList();

        Assert.NotEmpty(drains);
        Assert.All(drains, s => Assert.Equal(s.Key.Workers > 1 ? 2 : 1, s.Key.Namespaces));
        Assert.All(drains, s => Assert.Equal(s.Key.Namespaces, s.ActualParams.Namespaces));
    }

    [Fact]
    public void The_namespaces_option_sets_the_spread_and_never_exceeds_the_workers()
    {
        var options = BenchRunOptions.Parse(["--db", "pg", "--namespaces", "4", "--drain-workers", "2,16"]);
        var drains = BaselineSuite.Cells(options.Apply(BaselineSuite.ReleasePreset)).Where(s => s.Scenario == "drain").ToList();

        Assert.All(drains.Where(s => s.Key.Workers == 2), s => Assert.Equal(2, s.Key.Namespaces));
        Assert.All(drains.Where(s => s.Key.Workers == 16), s => Assert.Equal(4, s.Key.Namespaces));
    }

    [Theory]
    [InlineData(32, 2)]
    [InlineData(33, 3)]
    [InlineData(48, 3)]
    [InlineData(64, 4)]
    public void More_workers_take_more_namespaces_so_none_holds_more_than_sixteen(int workers, int namespaces)
    {
        var options = BenchRunOptions.Parse(["--db", "pg", "--drain-workers", workers.ToString(CultureInfo.InvariantCulture)]);
        var drain = Assert.Single(
            BaselineSuite.Cells(options.Apply(BaselineSuite.ReleasePreset)),
            s => s.Scenario == "drain" && s.Provider == "pg" && s.KeyProfile == ExecutionProfile.Direct
        );

        Assert.Equal(namespaces, drain.Key.Namespaces);
        Assert.True(workers <= namespaces * BenchPreset.MaxWorkersPerNamespace);
    }

    [Fact]
    public void A_drain_of_more_than_sixty_four_workers_is_refused() =>
        Assert.Throws<ArgumentException>(() => BenchRunOptions.Parse(["--db", "pg", "--drain-workers", "16,65"]));

    [Theory]
    [InlineData("1")]
    [InlineData("5")]
    public void The_namespaces_option_takes_two_to_four(string value) =>
        Assert.Throws<ArgumentException>(() => BenchRunOptions.Parse(["--db", "pg", "--namespaces", value]));

    [Fact]
    public void A_baseline_from_before_the_spread_reads_back_in_one_namespace()
    {
        var rc3 = BaselineCapture.Read(Rc3Baseline);

        Assert.All(rc3.Cells, c => Assert.Equal(1, c.Key.Namespaces));
    }
}
