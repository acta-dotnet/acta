using System.Diagnostics;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Engine.Worker;

/// <summary>
/// A steady flood of enqueues leaves the worker its turn: one job is claimed, run, and completed while eight callers
/// enqueue as fast as the database takes them. SQLite gets there through its write queue
/// (<c>SqliteDialect.OwnedWriteQueue</c>).
/// </summary>
[ConformanceSpec(
    "worker.enqueue-flood",
    "An enqueue flood leaves the worker its turn",
    Area = "Workers",
    Contract = "While eight callers enqueue as fast as the database takes them, a job is claimed, run, and completed within two seconds."
)]
public sealed class EnqueueFloodSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    private const int Callers = 8;

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(2);

    [Fact(DisplayName = "A job is claimed, run, and completed within two seconds while eight callers flood enqueues")]
    public async Task A_job_runs_through_an_enqueue_flood()
    {
        var ct = TestContext.Current.CancellationToken;
        var target = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "echo", JobPayload.Json(new Echo("target"))), ct);

        var enqueued = new long[Callers];
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var flood = Enumerable.Range(0, Callers).Select(i => Task.Run(() => FloodAsync(enqueued, i, stop.Token), ct)).ToArray();
        try
        {
            // Every caller has landed an enqueue, and the connection the worker will need beside the flood's is open,
            // so the clock times the wait for the database and not a start-up.
            while (Enumerable.Range(0, Callers).Any(i => Interlocked.Read(ref enqueued[i]) == 0))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), ct);
            }

            Assert.NotNull(await Jobs.GetAsync(JobLookup.ById(target.JobId), ct));
            var before = Total(enqueued);
            var clock = Stopwatch.StartNew();

            // A starved claim fails here, at five times the bound, rather than hanging the run.
            var outcome = await Runtime.RunOnceAsync(target, ct).WaitAsync(Bound * 5, ct);

            Assert.Equal(RunOnceStatus.Completed, outcome);
            var meanwhile = Total(enqueued) - before;
            Assert.True(
                clock.Elapsed < Bound,
                $"the job took {clock.Elapsed.TotalMilliseconds:0} ms while {meanwhile} enqueues landed beside it"
            );
            Assert.True(meanwhile > 0, "the flood stopped while the job ran");
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
        }
    }
}
