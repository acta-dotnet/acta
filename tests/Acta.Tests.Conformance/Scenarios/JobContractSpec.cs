using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Modules.Execution.Jobs;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// Conformance for the explicit-target contract façade: <c>EnqueueAsync(JobContract&lt;T&gt;, input)</c>
/// resolves namespace + format from the manifest binding (no input-type inference), the no-input and
/// result-bearing overloads enqueue correctly, and a contract RunAndWaitAsync round-trips the typed result.
/// </summary>
[ConformanceSpec(
    "job-contract.facade",
    "Contract enqueue names the job explicitly and resolves its route",
    Area = "Enqueue",
    Contract = "The contract IJobs façade resolves namespace and format from a JobContract, and supports no-input, fire-and-forget, and RunAndWaitAsync result paths.",
    Arrange = "TestJobsManifest exposes typed JobContract members bound to namespace and payload format.",
    Act = "Jobs are enqueued and executed through the typed overloads, including no-input, fire-and-forget, RunAndWaitAsync, and a mismatched contract.",
    Assert = "The contract façade resolves each route without input-type inference and round-trips the typed result."
)]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.EnqueueOneAsync))]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.EnqueueBatchAsync))]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.GetJobResultAsync))]
public abstract class JobContractSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    protected override bool RunAsWorker => true;

    [Fact(DisplayName = "Contract enqueue resolves the route without input-type inference and round-trips the typed result")]
    public async Task Contract_enqueue_resolves_route_and_round_trips_result()
    {
        var ct = TestContext.Current.CancellationToken;

        var outcome = await Jobs.EnqueueAsync(TestJobsManifest.AddNumbers, new AddNumbers(2, 3), ct: ct);
        Assert.Equal(JobEnqueueAction.Inserted, outcome.Action);

        var snapshot = await Jobs.GetAsync(outcome, ct);
        Assert.Equal("add-numbers", snapshot!.JobName);

        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(outcome, ct));
        var result = await Jobs.GetResultAsync<AddNumbersResult>(outcome, ct);
        Assert.Equal(5, result!.Sum);
    }

    [Fact(DisplayName = "No-input contract enqueues a None-format row")]
    public async Task No_input_contract_enqueues_a_none_format_row()
    {
        var ct = TestContext.Current.CancellationToken;

        var outcome = await Jobs.EnqueueAsync(TestJobsManifest.Cancellable, ct: ct);
        Assert.Equal(JobEnqueueAction.Inserted, outcome.Action);

        var snapshot = await Jobs.GetAsync(outcome, ct);
        Assert.Equal("cancellable", snapshot!.JobName);
    }

    [Fact(DisplayName = "Contract RunAndWaitAsync round-trips the typed result")]
    public async Task Contract_execute_round_trips_typed_result()
    {
        var ct = TestContext.Current.CancellationToken;

        using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var driver = Task.Run(async () =>
        {
            while (!loopCts.IsCancellationRequested)
            {
                try
                {
                    await Runtime.RunOnceAsync(TestNamespace, loopCts.Token);
                    await Task.Delay(50, loopCts.Token);
                }
                catch (Exception) when (loopCts.IsCancellationRequested)
                {
                    // Teardown cancels an in-flight tick; under a slow SQL Server SqlClient can surface
                    // that as a SqlException ("Operation cancelled by user") rather than
                    // OperationCanceledException: both are expected once we are shutting down.
                    return;
                }
            }
        });

        try
        {
            var outcome = await Jobs.RunAndWaitAsync(
                TestJobsManifest.AddNumbers,
                new AddNumbers(4, 5),
                new JobExecutionOptions { WaitTimeout = TimeSpan.FromSeconds(60), PollInterval = TimeSpan.FromMilliseconds(100) },
                ct
            );
            Assert.True(outcome.IsSuccess);
            Assert.Equal(9, outcome.ValueOrThrow().Sum);
        }
        finally
        {
            await loopCts.CancelAsync();
            try
            {
                await driver;
            }
            catch (OperationCanceledException) { }
        }
    }

    [Fact(DisplayName = "A result job's fire-and-forget overload binds and enqueues, dropping the result")]
    public async Task Result_job_fire_and_forget_binds_two_arg_overload_and_enqueues()
    {
        var ct = TestContext.Current.CancellationToken;
        // AddNumbers is a result job, so TestJobsManifest.AddNumbers is JobContract<AddNumbers, AddNumbersResult>.
        // This binds EnqueueAsync<TInput, TResult>(JobContract<TInput, TResult>, ...) and drops the result.
        var outcome = await Jobs.EnqueueAsync(TestJobsManifest.AddNumbers, new AddNumbers(2, 3), ct: ct);
        Assert.Equal(JobEnqueueAction.Inserted, outcome.Action);
    }

    [Fact(DisplayName = "A wrong input type on a hand-built contract throws before enqueue")]
    public async Task Wrong_input_type_on_hand_built_contract_throws_before_enqueue()
    {
        var ct = TestContext.Current.CancellationToken;
        // add-numbers' registered input is AddNumbers; a hand-built JobContract<int> must not enqueue.
        var bad = new JobContract<int>(typeof(TestJobsManifest), "add-numbers");
        await Assert.ThrowsAsync<ArgumentException>(() => Jobs.EnqueueAsync(bad, 5, ct: ct).AsTask());
    }

    [Fact(DisplayName = "A wrong result type on a hand-built contract throws")]
    public async Task Wrong_result_type_on_hand_built_contract_throws()
    {
        var ct = TestContext.Current.CancellationToken;
        // add-numbers' registered result is AddNumbersResult; a hand-built TResult of int must throw.
        var bad = new JobContract<AddNumbers, int>(typeof(TestJobsManifest), "add-numbers");
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Jobs.RunAndWaitAsync(
                    bad,
                    new AddNumbers(1, 1),
                    new JobExecutionOptions { WaitTimeout = TimeSpan.FromSeconds(5), PollInterval = TimeSpan.FromMilliseconds(100) },
                    ct
                )
                .AsTask()
        );
    }
}
