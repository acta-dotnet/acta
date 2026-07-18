using System.Globalization;
using Anvil.Bench;
using Xunit;

namespace Acta.Tests.Benchmarks;

public sealed class BaselineCultureTests
{
    [Fact]
    public void Markdown_uses_invariant_numbers_without_digit_grouping()
    {
        var markdown = UnderDecimalCommaCulture(() => BaselineReport.Markdown(Fixture()));

        Assert.Contains("- RAM: 95.8 GB", markdown);
        Assert.Contains("| Direct | 12346 |", markdown);
        Assert.Contains("Peak: 12346 jobs/s", markdown);
        Assert.Contains("| pg | 4 | 23457 | 23.45 |", markdown);
        Assert.Contains("| pg | 100000 | 12.34 | 23.45 | 34.56 |", markdown);
        Assert.DoesNotMatch(@"\d[,.]\d{3}\b", markdown);
        Assert.DoesNotContain("12,34", markdown);
    }

    [Fact]
    public void Json_round_trips_invariant_numbers_under_decimal_comma_culture()
    {
        var path = Path.Combine(Path.GetTempPath(), $"acta-baseline-{Guid.NewGuid():N}.json");
        try
        {
            var (json, roundTrip) = UnderDecimalCommaCulture(() =>
            {
                var baseline = Fixture();
                BaselineCapture.Write(baseline, path);
                return (File.ReadAllText(path), BaselineCapture.Read(path));
            });

            Assert.Contains("\"jobsPerSecond\": 12345.67", json);
            Assert.DoesNotContain("12345,67", json);
            Assert.Equal(12345.67, roundTrip.Cells[0].MedianMetrics.JobsPerSecond);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static T UnderDecimalCommaCulture<T>(Func<T> action)
    {
        T? result = default;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("sl-SI");
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("sl-SI");
                result = action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw new InvalidOperationException("Culture-isolated benchmark assertion failed.", failure);
        }

        return result!;
    }

    private static BaselineFile Fixture()
    {
        var metrics = new BaselineMetrics(
            JobsPerSecond: 12345.67,
            EnqueuePerSecond: 23456.78,
            DrainPerSecond: 34567.89,
            DurationMs: 45.67,
            P50LatencyMs: 12.34,
            P95LatencyMs: 23.45,
            P99LatencyMs: 34.56,
            MaxLatencyMs: 45.67,
            MeanLatencyMs: 20.25,
            EnqueueDurationMs: 56.78,
            DrainDurationMs: 67.89,
            JobsObserved: 100000,
            AllocatedBytes: 123456,
            GcCollections: 2,
            ExtraMetrics: new Dictionary<string, double> { ["ratio"] = 1.25 }
        );

        BaselineCellResult Cell(string scenario, int workers = 1, int rows = 0) =>
            new(
                new BaselineCellKey(scenario, "pg", "18", scenario == "query-list" ? null : "Direct", 100000, 4, 8, 0, workers, rows, 200),
                "ok",
                metrics,
                [metrics, metrics, metrics],
                ["ok", "ok", "ok"]
            );

        return new BaselineFile(
            SchemaVersion: BaselineCapture.SchemaVersion,
            Preset: "test",
            CapturedAtUtc: "2026-07-14T18:28:46Z",
            EngineVersion: "test",
            GitCommit: "test",
            GitDirty: false,
            Policy: new BaselinePolicy(1, 3, "median"),
            Environment: new BaselineEnvironmentInfo(
                ".NET test",
                "test OS",
                "test CPU",
                32,
                (long)(95.8 * 1024 * 1024 * 1024),
                "test disk"
            ),
            Databases: [new BaselineDatabaseInfo("pg", "18.4", "18", "10.0.3.0", "local", "fingerprint")],
            Cells: [Cell("throughput"), Cell("enqueue", workers: 4), Cell("query-list", rows: 100000)]
        );
    }
}
