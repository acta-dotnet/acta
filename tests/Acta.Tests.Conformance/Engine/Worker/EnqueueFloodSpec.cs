using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Engine.Worker;

/// <summary>
/// A steady flood of enqueues leaves the worker its turn: one job is claimed, run, and completed while eight callers
/// enqueue as fast as the database takes them, well inside a starvation's tens of seconds on every provider. SQLite takes
/// every write under one lock, in arrival order through its write queue (<c>SqliteDialect.OwnedWriteQueue</c>), so there
/// the job also finishes within a bounded number of the flood's enqueues.
/// </summary>
[ConformanceSpec(
    "worker.enqueue-flood",
    "An enqueue flood leaves the worker its turn",
    Area = "Workers",
    Contract = "Beside eight callers enqueuing as fast as the database takes them, a job is claimed, run, and completed within 30 s, on SQLite within 400 of their enqueues."
)]
public sealed class EnqueueFloodSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    private const int Callers = 8;

    /// <summary>
    /// SQLite's write queue serves writes in arrival order, so each of the job's writes waits behind at most one enqueue per
    /// caller and a few dozen land beside the job; a claim that lost its turns to the flood would see the count grow with
    /// it. Counted in enqueues, so a slow machine slows both sides alike.
    /// </summary>
    private const int MaxBesideTheJobOnSqlite = 400;

    /// <summary>
    /// Many times what a loaded runner takes beside the flood, so only a claim the flood starves for tens of seconds
    /// reaches it.
    /// </summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    [Fact(
        DisplayName = "A job is claimed, run, and completed within 30 s while eight callers flood enqueues, within 400 of them on SQLite"
    )]
    public async Task A_job_runs_through_an_enqueue_flood()
    {
        var ct = TestContext.Current.CancellationToken;
        var target = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "echo", JobPayload.Json(new Echo("target"))), ct);

        var enqueued = new long[Callers];
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var flood = Enumerable.Range(0, Callers).Select(i => Task.Run(() => FloodAsync(enqueued, i, stop.Token), ct)).ToArray();
        try
        {
            // Every caller has landed an enqueue, so the flood is running before the job is claimed.
            await SpecWaits.UntilAsync(
                () => Enumerable.Range(0, Callers).All(i => Interlocked.Read(ref enqueued[i]) > 0),
                "a flood caller landed no enqueue",
                ct
            );

            var before = Total(enqueued);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var outcome = await Runtime.RunOnceAsync(target, ct).WaitAsync(SpecWaits.Gate, ct);
            var took = clock.Elapsed;
            var beside = Total(enqueued) - before;

            Assert.Equal(RunOnceStatus.Completed, outcome);
            Assert.True(took < Bound, $"the job took {took.TotalSeconds:0.0} s while {beside} enqueues landed beside it");
            if (Fixture.DialectToken == "sqlite")
            {
                Assert.True(beside <= MaxBesideTheJobOnSqlite, $"{beside} enqueues landed beside the job");
            }

            // The flood still lands enqueues after the job, so it was pressing the whole time and not failing quietly.
            var after = Total(enqueued);
            await SpecWaits.UntilAsync(() => Total(enqueued) > after, "the flood landed no enqueue after the job", ct);
        }
        finally
        {
            await stop.CancelAsync();
            await Task.WhenAll(flood);
        }
    }

    private static long Total(long[] enqueued) => Enumerable.Range(0, enqueued.Length).Sum(i => Interlocked.Read(ref enqueued[i]));

    private async Task FloodAsync(long[] enqueued, int caller, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "echo", JobPayload.Json(new Echo("flood"))), ct);
                Interlocked.Increment(ref enqueued[caller]);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            // An enqueue a loaded server timed out, or ran out of pooled connections for, is pressure all the same; a short
            // pause keeps a caller whose enqueues fail fast from spinning.
            catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException or TimeoutException)
            {
                await Task.Delay(10, CancellationToken.None);
            }
        }
    }
}
