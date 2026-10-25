using System.Text;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// Conformance for the calls the generated manifest returns: a call names its job, so the route comes from the
/// manifest's registration rather than the input's type; a handler's several parameters travel as one JSON object; and
/// enqueue, schedule, and run-and-wait take the call, its options as a configure callback. A hand-built call whose types
/// disagree with the registered job fails before anything is stored.
/// </summary>
[ConformanceSpec(
    "jobs.generated-calls",
    "A generated call enqueues, schedules, and runs its job",
    Area = "Enqueue",
    Contract = "Enqueue, schedule, and run-and-wait take a generated call, which names its job and its input, and several handler parameters are stored as one JSON object."
)]
public sealed class JobCallSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    protected override bool RunAsWorker => true;

    private static readonly JobExecutionOptions Wait = new()
    {
        WaitTimeout = TimeSpan.FromSeconds(60),
        PollInterval = TimeSpan.FromMilliseconds(100),
    };

    [Fact(DisplayName = "A call resolves its route without input-type inference and round-trips the typed result")]
    public async Task Call_resolves_its_route_and_round_trips_the_result()
    {
        var ct = TestContext.Current.CancellationToken;

        JobHandle<AddNumbersResult> handle = await Jobs.EnqueueAsync(TestJobsManifest.AddNumbers(new AddNumbers(2, 3)), ct: ct);

        Assert.Equal(EnqueueJobAction.Inserted, handle.Action);
        Assert.Equal("add-numbers", (await Jobs.GetAsync(handle, ct))!.JobName);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(handle, ct));
        Assert.Equal(5, (await handle.GetResultAsync(ct))!.Sum);
    }

    [Fact(DisplayName = "A call of a handler taking several parameters stores them as one object and answers the typed result")]
    public async Task Several_parameters_travel_as_one_object()
    {
        var ct = TestContext.Current.CancellationToken;

        var handle = await Jobs.EnqueueAsync(TestJobsManifest.FormatInvoice(42, "acme"), ct: ct);

        var input = (await Jobs.GetInputAsync(handle, ct))!.Input;
        Assert.Equal("""{"invoiceId":42,"customer":"acme"}""", Encoding.UTF8.GetString(input.Data.Span));
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(handle, ct));
        Assert.Equal("acme-42", (await handle.WaitAsync(ct: ct)).ValueOrThrow().Text);
    }

    [Fact(DisplayName = "A no-input call enqueues its own job")]
    public async Task No_input_call_enqueues_its_own_job()
    {
        var ct = TestContext.Current.CancellationToken;

        var handle = await Jobs.EnqueueAsync(TestJobsManifest.Cancellable(), ct: ct);

        Assert.Equal("cancellable", (await Jobs.GetAsync(handle, ct))!.JobName);
    }

    [Fact(DisplayName = "A call's configure callback applies its options")]
    public async Task Configure_callback_applies_its_options()
    {
        var ct = TestContext.Current.CancellationToken;

        var first = await Jobs.EnqueueAsync(TestJobsManifest.FormatInvoice(7, "dedup"), o => o.DeduplicationKey("invoice-7"), ct);
        var second = await Jobs.EnqueueAsync(TestJobsManifest.FormatInvoice(7, "dedup"), o => o.DeduplicationKey("invoice-7"), ct);

        Assert.Equal(EnqueueJobAction.Inserted, first.Action);
        Assert.Equal(EnqueueJobAction.Deduplicated, second.Action);
        Assert.Equal(first.JobRef, second.JobRef);
    }

    [Fact(DisplayName = "A call runs and waits for the outcome, with or without its result type")]
    public async Task Call_runs_and_waits_for_the_outcome()
    {
        var ct = TestContext.Current.CancellationToken;
        var untyped = new JobCall<TestJobsManifest.FormatInvoiceArgs>(
            new JobContract<TestJobsManifest.FormatInvoiceArgs>(typeof(TestJobsManifest), "format-invoice"),
            new(8, "zeta")
        );

        var typedOutcome = await DriveAsync(Jobs.RunAndWaitAsync(TestJobsManifest.FormatInvoice(7, "zeta"), Wait, ct), ct);
        var untypedOutcome = await DriveAsync(Jobs.RunAndWaitAsync(untyped, Wait, ct), ct);

        Assert.Equal("zeta-7", typedOutcome.ValueOrThrow().Text);
        Assert.True(untypedOutcome.IsSuccess);
    }

    [Fact(DisplayName = "A scheduled call holds its job until the delay passes")]
    public async Task Scheduled_call_holds_its_job()
    {
        var ct = TestContext.Current.CancellationToken;

        var handle = await Jobs.ScheduleAsync(TestJobsManifest.FormatInvoice(1, "later"), TimeSpan.FromMinutes(5), ct: ct);

        Assert.Equal(RunOnceStatus.NothingClaimed, await Runtime.RunOnceAsync(TestNamespace, ct));
        Assert.Equal(JobStatusCode.Ready, (await handle.GetStatusAsync(ct))!.Status);
    }

    [Fact(DisplayName = "A hand-built call with the wrong input type throws before enqueue")]
    public async Task Wrong_input_type_throws_before_enqueue()
    {
        var ct = TestContext.Current.CancellationToken;
        var bad = new JobCall<int>(new JobContract<int>(typeof(TestJobsManifest), "add-numbers"), 5);

        await Assert.ThrowsAsync<ArgumentException>(() => Jobs.EnqueueAsync(bad, ct: ct).AsTask());
    }

    [Fact(DisplayName = "A hand-built call with the wrong result type throws before enqueue")]
    public async Task Wrong_result_type_throws_before_enqueue()
    {
        var ct = TestContext.Current.CancellationToken;
        var bad = new JobCall<AddNumbers, int>(new JobContract<AddNumbers, int>(typeof(TestJobsManifest), "add-numbers"), new(1, 1));

        await Assert.ThrowsAsync<ArgumentException>(() => Jobs.EnqueueAsync(bad, ct: ct).AsTask());
    }

    // Run-and-wait polls for the outcome while this test's worker claims only when ticked.
    private async Task<T> DriveAsync<T>(ValueTask<T> waiting, CancellationToken ct)
    {
        var wait = waiting.AsTask();
        while (!wait.IsCompleted)
        {
            await Runtime.RunOnceAsync(TestNamespace, ct);
            await Task.WhenAny(wait, Task.Delay(50, ct));
        }
        return await wait;
    }
}
