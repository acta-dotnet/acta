using Acta.Relational.Entities;
using Acta.Runtime.Hosting;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Runtime;

/// <summary>
/// The heartbeat's orphan release against a Bulk completion that is still being written. Under Bulk the
/// executor buffers a plain completion and lets go of the attempt at once, so while the flush is held
/// nothing running accounts for the row; the flusher repeats a failing write until it lands, which can
/// outlast two heartbeats. The row must stay this worker's until the completion settles, or the release
/// reschedules a job whose success is still in memory and the handler runs again. SQLite has no
/// set-based completion routine, so Bulk runs as Direct there and the facts skip.
/// </summary>
[ConformanceSpec(
    "runtime.bulk-pending-completion-ownership",
    "A Bulk completion waiting to be written keeps its row through the heartbeat",
    Area = "Runtime",
    Contract = "A row whose Bulk completion is buffered or being written is not released as an orphan, including while a row the set call turned back is written per job.",
    Arrange = "A Bulk worker whose set-based completion write, or the per-job write after the set call turns the row back, is held open after the handler ran.",
    Act = "The heartbeat ticks twice while the write is held, and then the write is let go.",
    Assert = "No Rescheduled event is written, the handler ran once, and the job is Succeeded with its result."
)]
[CoversStoreMethod(
    typeof(Acta.Runtime.Modules.Execution.IExecutionStore),
    nameof(Acta.Runtime.Modules.Execution.IExecutionStore.CompleteExecutionsBatchAsync)
)]
public abstract class BulkPendingCompletionOwnershipSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    private StoreFaultPlan _faults = null!;

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        base.ConfigureServices(services, testNamespace);
        _faults = services.AddStoreFaultInjection();
        services.Configure<JobsOptions>(o =>
        {
            o.ExecutionProfile = ExecutionProfile.Bulk;
            o.MaxConcurrentExecutors = 1;
            o.BatchCompletionSize = 1;
        });
    }

    [Fact(DisplayName = "A held Bulk flush keeps its row across two heartbeats and the job completes once with its result")]
    public async Task Held_set_based_completion_keeps_the_row()
    {
        var ct = TestContext.Current.CancellationToken;
        SkipWithoutBatchCompletion();
        var (entered, release) = Hold();
        _faults.RunBeforeBatchCompleteOnce(async () =>
        {
            entered.TrySetResult();
            await release.Task;
        });

        await RunHeldAcrossTwoHeartbeatsAsync(null, entered, release, ct);
    }

    [Fact(DisplayName = "A row the set call turned back stays owned while its per-job write is held across two heartbeats")]
    public async Task Held_per_job_fallback_keeps_the_row()
    {
        var ct = TestContext.Current.CancellationToken;
        SkipWithoutBatchCompletion();
        var (entered, release) = Hold();

        // A false from the set call is not a refusal: the per-job write that follows is the same
        // completion, and it is the one held here.
        _faults.RunBeforeCompleteOnce(async () =>
        {
            entered.TrySetResult();
            await release.Task;
        });

        await RunHeldAcrossTwoHeartbeatsAsync(jobId => _faults.TurnBackFromBatchOnce(jobId), entered, release, ct);
    }

    private async Task RunHeldAcrossTwoHeartbeatsAsync(
        Action<long>? arrange,
        TaskCompletionSource entered,
        TaskCompletionSource release,
        CancellationToken ct
    )
    {
        var enqueued = await ChaosSpecHelpers.EnqueueNoPayloadAsync(Jobs, TestNamespace, "chaos-counting-result", ct);
        arrange?.Invoke(enqueued.JobId);

        using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var loop = Runtime.RunLoopAsync(loopCts.Token);
        try
        {
            await entered.Task.WaitAsync(SpecWaits.Gate, ct);

            // The executor has handed the completion over and let go of the attempt, so from here only the
            // pending completion accounts for the row.
            Assert.True(await WaitUntilAsync(() => Task.FromResult(Runtime.InFlightCount == 0), SpecWaits.Converge, ct));

            await Runtime.RunHeartbeatOnceAsync(ct);
            await Runtime.RunHeartbeatOnceAsync(ct);

            // The release runs detached after the second tick, so a wrong release shows up a moment later
            // as a Rescheduled event. Nothing must appear.
            Assert.False(
                await WaitUntilAsync(() => RescheduledAsync(enqueued.JobId, ct), TimeSpan.FromSeconds(3), ct),
                "the row was released as an orphaned claim while its completion was still being written"
            );

            release.SetResult();
            Assert.True(
                await WaitUntilAsync(
                    async () => await Jobs.GetStatusAsync(enqueued, ct) is JobStatusCode.Succeeded,
                    SpecWaits.Converge,
                    ct
                ),
                "the held completion did not land once it was let go"
            );
        }
        finally
        {
            release.TrySetResult();
            await loopCts.CancelAsync();
            await loop;
        }

        Assert.False(await RescheduledAsync(enqueued.JobId, ct));
        Assert.Equal(1, ChaosProbes.CountingInvocations[enqueued.JobId]);
        Assert.Equal("counted", await Jobs.GetResultAsync<string>(enqueued, ct));
    }

    private void SkipWithoutBatchCompletion()
    {
        if (!Services.GetRequiredService<ActaProviderInfo>().SupportsBatchCompletion)
        {
            Assert.Skip("Bulk runs as Direct on a provider with no set-based completion routine, so no completion is ever buffered.");
        }
    }

    private static (TaskCompletionSource Entered, TaskCompletionSource Release) Hold() =>
        (
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        );

    private async Task<bool> RescheduledAsync(long jobId, CancellationToken ct)
    {
        var events = await Db.From<JobEvent>().Where(e => e.JobId == jobId).ToListAsync(ct);
        return events.Any(e => e.ExecutionStatus == ExecutionStatusCode.Rescheduled);
    }

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, TimeSpan budget, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + budget;
        do
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), ct);
        } while (DateTime.UtcNow < deadline);

        return false;
    }
}
