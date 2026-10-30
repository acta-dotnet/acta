using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// Conformance for following a job after a typed enqueue: the handle waits for the outcome, answers a result
/// call's typed result, stops waiting on a timeout or a cancelled token while the job keeps going, and a handle
/// rebuilt from the ref addresses the same job.
/// </summary>
[ConformanceSpec(
    "jobs.handle-and-wait",
    "A typed enqueue's handle waits for the outcome without stopping the job",
    Area = "Enqueue",
    Contract = "A typed enqueue's handle waits for the terminal outcome and typed result, and a timed-out or cancelled wait leaves the job running."
)]
public sealed class JobHandleSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    protected override bool RunAsWorker => true;

    [Fact(DisplayName = "A result call's handle waits for the outcome and answers the typed result")]
    public async Task Result_call_handle_waits_for_the_typed_result()
    {
        var ct = TestContext.Current.CancellationToken;
        var handle = await Jobs.EnqueueAsync(TestJobsManifest.AddNumbers(new AddNumbers(2, 3)), ct: ct);

        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(handle, ct));
        var outcome = await handle.WaitAsync(ct: ct);

        Assert.True(outcome.IsSuccess);
        Assert.Equal(handle.JobRef, outcome.JobRef);
        Assert.Equal(5, outcome.ValueOrThrow().Sum);
        Assert.Equal(5, (await handle.GetResultAsync(ct))!.Sum);
    }

    [Fact(DisplayName = "An input-typed enqueue's handle names the result type at the wait")]
    public async Task Input_typed_handle_names_the_result_at_the_wait()
    {
        var ct = TestContext.Current.CancellationToken;
        var handle = await Jobs.EnqueueAsync(new AddNumbers(4, 5), ct: ct);

        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(handle, ct));

        Assert.Equal(9, (await handle.WaitAsync<AddNumbersResult>(ct: ct)).ValueOrThrow().Sum);
    }

    [Fact(
        DisplayName = "A typed handle deduplicated onto another definition's job refuses its outcome and its result rather than decoding them"
    )]
    public async Task Handle_deduplicated_onto_another_definition_refuses_its_result()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = $"shared-{Guid.NewGuid():N}";
        var sum = await Jobs.EnqueueAsync(TestJobsManifest.AddNumbers(new AddNumbers(2, 3)), o => o.DeduplicationKey(key), ct);
        var invoice = await Jobs.EnqueueAsync(TestJobsManifest.FormatInvoice(7, "acme"), o => o.DeduplicationKey(key), ct);

        Assert.Equal(EnqueueJobAction.Deduplicated, invoice.Action);
        Assert.Equal(sum.JobRef, invoice.JobRef);
        // Refused on the first read, before the job runs, as well as after it succeeds.
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await invoice.WaitAsync(TimeSpan.FromMilliseconds(100), ct));
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(sum, ct));
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(async () => await invoice.WaitAsync(ct: ct));
        Assert.Contains(typeof(AddNumbersResult).FullName!, refused.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await invoice.GetResultAsync(ct));
        Assert.Equal(5, (await sum.WaitAsync(ct: ct)).ValueOrThrow().Sum);
    }

    [Fact(DisplayName = "A wait that times out reports the job's status and leaves it to run")]
    public async Task Timed_out_wait_leaves_the_job_running()
    {
        var ct = TestContext.Current.CancellationToken;
        var handle = await Jobs.EnqueueAsync(new AddNumbers(1, 1), ct: ct);

        var outcome = await handle.WaitAsync(TimeSpan.FromMilliseconds(300), ct);

        Assert.True(outcome.IsTimedOut);
        Assert.Equal(JobStatusCode.Ready, outcome.Status);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(handle, ct));
        Assert.True((await handle.WaitAsync(ct: ct)).IsSuccess);
    }

    [Fact(DisplayName = "A cancelled wait throws and never cancels the job")]
    public async Task Cancelled_wait_never_cancels_the_job()
    {
        var ct = TestContext.Current.CancellationToken;
        var handle = await Jobs.EnqueueAsync(new AddNumbers(1, 2), ct: ct);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stop.CancelAfter(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await handle.WaitAsync(TimeSpan.FromSeconds(30), stop.Token));

        Assert.Equal(JobStatusCode.Ready, (await handle.GetStatusAsync(ct))!.Status);
    }

    [Fact(DisplayName = "A handle rebuilt from the ref addresses the same job, and an unknown ref has none")]
    public async Task Rebuilt_handle_addresses_the_same_job()
    {
        var ct = TestContext.Current.CancellationToken;
        var original = await Jobs.EnqueueAsync(new AddNumbers(3, 3), ct: ct);

        var rebuilt = await Jobs.GetHandleAsync(original.JobRef, ct);

        Assert.NotNull(rebuilt);
        Assert.Equal(original.JobId, rebuilt.JobId);
        Assert.Equal(EnqueueJobAction.Deduplicated, rebuilt.Action);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(rebuilt, ct));
        Assert.True((await rebuilt.WaitAsync(ct: ct)).IsSuccess);
        Assert.Null(await Jobs.GetHandleAsync(JobRef.New(), ct));
    }

    [Fact(DisplayName = "Waiting on a job that does not exist throws at once")]
    public async Task Waiting_on_a_missing_job_throws()
    {
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await Jobs.WaitAsync(JobRef.New(), TimeSpan.FromSeconds(30), ct));
    }
}
