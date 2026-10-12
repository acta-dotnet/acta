using System.Diagnostics;
using Acta;

namespace Anvil.Bench;

/// <summary>
/// Dashboard list latency on a large table: batch-enqueue a big backlog (rows stay future-dated so they
/// are never executed), then time first-page and deep-cursor <c>ListJobsAsync</c> reads.
/// </summary>
public sealed class QueryScenario : IScenario
{
    private const int DefaultRows = 1_000_000;
    private const int IdleHorizonSeconds = 36_000;
    private const int Rounds = 10;
    private const int PageSize = 100;

    public string Name => "query";

    public string Description => "Dashboard list latency on a large backlog.";

    public int ExpectedObserved(CellParams p) => 1;

    public async Task<CellMetrics> RunAsync(CellParams p, string schema, BenchConfig cfg, CancellationToken ct)
    {
        var rows = p.Rows > 0 ? p.Rows : DefaultRows;
        await using var host = await BenchHost.StartAsync(
            new BenchHostOptions
            {
                Provider = p.Provider,
                Schema = schema,
                Executors = 1,
                SeedHistory = cfg.SeedHistory,
            },
            ct
        );

        await Workload.EnqueueAsync(host.Jobs, rows, p.PayloadBytes, IdleHorizonSeconds, ct);

        var ms = new List<long>();
        for (var round = 0; round < Rounds; round++)
        {
            var sw = Stopwatch.GetTimestamp();
            var page = await host.Queries.Ledger.ListJobsAsync(
                new ListJobsQuery(JobNamespace: BenchHost.Namespace, PageSize: PageSize),
                ct
            );
            ms.Add(Stopwatch.GetTimestamp() - sw);

            var cursor = page.NextCursor;
            for (var depth = 0; depth < 3 && cursor is not null; depth++)
            {
                var s2 = Stopwatch.GetTimestamp();
                var deep = await host.Queries.Ledger.ListJobsAsync(
                    new ListJobsQuery(JobNamespace: BenchHost.Namespace, PageSize: PageSize, Cursor: cursor),
                    ct
                );
                ms.Add(Stopwatch.GetTimestamp() - s2);
                cursor = deep.NextCursor;
            }
        }

        var (P50, P95, P99, Max, Mean) = Stats.Percentiles([.. ms]);
        return new CellMetrics(
            EnqueueRatePerSec: 0,
            EndToEndRatePerSec: 0,
            DrainRatePerSec: 0,
            LatencyP50Ms: P50,
            LatencyP95Ms: P95,
            LatencyP99Ms: P99,
            LatencyMaxMs: Max,
            LatencyMeanMs: Mean,
            EnqueueSeconds: 0,
            DrainSeconds: 0,
            JobsObserved: ms.Count,
            Extra: new Dictionary<string, double> { ["rows"] = rows, ["queryP95Ms"] = P95 }
        );
    }
}
