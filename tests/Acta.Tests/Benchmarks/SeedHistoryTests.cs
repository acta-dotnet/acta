using Anvil.Bench;
using Xunit;

namespace Acta.Tests.Benchmarks;

public sealed class SeedHistoryTests
{
    [Fact]
    public void Seed_history_defaults_to_the_empty_ledger_run()
    {
        var options = BenchRunOptions.Parse(["--db", "sqlite"]);

        Assert.Equal(0, options.SeedHistory);
    }

    [Fact]
    public void Seed_history_parses_next_to_the_other_scripted_options()
    {
        var options = BenchRunOptions.Parse(["--db", "sqlite", "--seed-history", "20000", "--scenario", "throughput:direct"]);

        Assert.Equal(20000, options.SeedHistory);
        Assert.Equal(["sqlite"], options.Providers);
        Assert.Equal(["throughput:direct"], options.Scenarios);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("many")]
    public void Seed_history_rejects_a_count_that_is_not_a_non_negative_number(string value)
    {
        Assert.Throws<ArgumentException>(() => BenchRunOptions.Parse(["--db", "sqlite", "--seed-history", value]));
    }

    [Fact]
    public void Seed_history_needs_a_value()
    {
        Assert.Throws<ArgumentException>(() => BenchRunOptions.Parse(["--db", "sqlite", "--seed-history"]));
    }

    [Fact]
    public void Report_states_the_seeded_depth_and_the_retained_row_counts_per_cell()
    {
        var markdown = BaselineReport.Markdown(
            Fixture(
                new Dictionary<string, double>
                {
                    ["seedHistory"] = 20000,
                    ["retainedJobs"] = 21000,
                    ["retainedRuntimes"] = 21000,
                    ["retainedEvents"] = 20000,
                    ["retainedResults"] = 20000,
                }
            )
        );

        Assert.Contains("## Seeded history: rows in the ledger per cell", markdown);
        Assert.Contains("| throughput pg direct j=1000 e=8 b=16 w=1 | 20000 | 21000 | 21000 | 20000 | 20000 |", markdown);
    }

    [Fact]
    public void Report_omits_the_seeded_history_section_on_an_unseeded_run()
    {
        var markdown = BaselineReport.Markdown(Fixture(new Dictionary<string, double> { ["seedHistory"] = 0, ["retainedJobs"] = 1000 }));

        Assert.DoesNotContain("Seeded history", markdown);
    }

    private static BaselineFile Fixture(IReadOnlyDictionary<string, double> extra)
    {
        var metrics = new BaselineMetrics(
            JobsPerSecond: 1000,
            EnqueuePerSecond: 2000,
            DrainPerSecond: 0,
            DurationMs: 1000,
            P50LatencyMs: 1,
            P95LatencyMs: 2,
            P99LatencyMs: 3,
            MaxLatencyMs: 4,
            MeanLatencyMs: 1.5,
            EnqueueDurationMs: 500,
            DrainDurationMs: 500,
            JobsObserved: 1000,
            AllocatedBytes: 0,
            GcCollections: 0,
            ExtraMetrics: extra
        );

        return new BaselineFile(
            SchemaVersion: BaselineCapture.SchemaVersion,
            Preset: "test",
            CapturedAtUtc: "2026-09-19T10:00:00Z",
            EngineVersion: "test",
            GitCommit: "test",
            GitDirty: false,
            Policy: new BaselinePolicy(0, 1, "single"),
            Environment: new BaselineEnvironmentInfo(".NET test", "test OS", "test CPU", 8, 1024, "test disk"),
            Databases: [new BaselineDatabaseInfo("pg", "18.4", "18", "10.0.3.0", "local", "fingerprint")],
            Cells:
            [
                new BaselineCellResult(
                    new BaselineCellKey("throughput", "pg", "18", "Direct", 1000, 8, 16, 0, 1, 0, 200),
                    "ok",
                    metrics,
                    [metrics],
                    ["ok"]
                ),
            ]
        );
    }
}
