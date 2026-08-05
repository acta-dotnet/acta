using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

[ConformanceSpec(
    "testing.scenario-session",
    "Scenario sessions drive jobs through common durable states",
    Area = "Testing",
    Contract = "Scenario sessions pin one enqueued job and drive typed results, signals, timers, retries, diagnostics and failures without conformance boilerplate.",
    Arrange = "An ActaTestHost is started for TestJobsManifest in an isolated namespace.",
    Act = "The public Scenario API enqueues typed and contract jobs, ticks them, raises signals, fast-forwards due rows and reads diagnostics.",
    Assert = "Sessions observe pinned job state, return typed results, expose diagnostics and drive Done or Failed outcomes deterministically."
)]
public abstract class ScenarioSessionSpec<TFixture> : ActaTestBase<TFixture>
    where TFixture : IConformanceFixture, new()
{
    [Fact(DisplayName = "Typed result sessions run to Done and return TResult plus timeline diagnostics")]
    public async Task Typed_result_session_runs_to_done_and_reads_diagnostics()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartScenarioHostAsync(ct);

        var session = await Scenario.For<AddNumbers, AddNumbersResult>(host).EnqueueAsync(new AddNumbers(2, 3), ct: ct);

        Assert.Equal(TestNamespace, session.Namespace);
        Assert.Equal("add-numbers", session.JobName);
        Assert.Equal(JobLookupKind.JobId, session.Lookup.Kind);
        Assert.Equal(session.JobId, session.Lookup.JobId);

        await session.RunUntilDoneAsync(ct: ct);

        Assert.Equal(new AddNumbersResult(5), await session.ResultAsync(ct));
        await session.AssertResultAsync(new AddNumbersResult(5), ct: ct);

        var job = await session.JobAsync(ct);
        Assert.Equal(JobStatusCode.Succeeded, job.Status);
        var events = await session.EventsAsync(ct);
        Assert.Contains(
            events,
            e => e.EventCode == JobEventCode.JobExecutionFinished && e.ExecutionStatus == ExecutionStatusCode.Succeeded
        );

        var contract = await Scenario.For(TestJobsManifest.AddNumbers, host).EnqueueAsync(new AddNumbers(4, 6), ct: ct);
        await contract.RunUntilDoneAsync(ct: ct);
        Assert.Equal(new AddNumbersResult(10), await contract.ResultAsync(ct));
    }

    [Fact(DisplayName = "No-input contract sessions run until a signal, raise it and complete")]
    public async Task No_input_contract_session_runs_until_signal_and_completes()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartScenarioHostAsync(ct);

        var session = await Scenario.For(TestJobsManifest.JobWaitSignal, host).EnqueueAsync(ct: ct);

        await session.RunUntilSignalAsync("go", ct: ct);
        Assert.Equal(JobStatusCode.Suspended, await session.StatusAsync(ct));
        Assert.Equal(JobCheckpointStateCode.Pending, (await session.SignalAsync("go", ct))!.State);

        var raise = await session.RaiseSignalAsync("go", ct);
        Assert.Equal(JobControlAction.Applied, raise.Action);

        await session.RunUntilDoneAsync(ct: ct);
        Assert.Equal(JobCheckpointStateCode.Set, (await session.SignalAsync("go", ct))!.State);
    }

    [Fact(DisplayName = "Timer and step retry helpers fast-forward only the pinned session job")]
    public async Task Timer_and_step_retry_helpers_fast_forward_pinned_job()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartScenarioHostAsync(ct);

        var sleep = await Scenario.For(TestJobsManifest.JobSleepBasic, host).EnqueueAsync(ct: ct);
        Assert.Equal(ActaRunOutcome.Rearmed, await sleep.RunOnceAsync(ct));
        var timer = Assert.Single(await sleep.TimersAsync(ct));
        Assert.Equal("nap", timer.Name);
        Assert.Equal(JobCheckpointStateCode.Pending, timer.State);

        await sleep.FastForwardToNextTimerAsync(ct);
        await sleep.RunUntilDoneAsync(ct: ct);
        Assert.Equal(JobCheckpointStateCode.Consumed, (await sleep.TimerAsync("nap", ct))!.State);

        var step = await Scenario.For(TestJobsManifest.JobStepRetry, host).EnqueueAsync(ct: ct);
        Assert.Equal(ActaRunOutcome.Rearmed, await step.RunOnceAsync(ct));
        Assert.Equal(JobStepStateCode.Pending, (await step.StepAsync("flaky", ct))!.State);

        await step.FastForwardToStepRetryAsync("flaky", ct);
        await step.RunUntilDoneAsync(ct: ct);
        Assert.Equal(JobStepStateCode.Succeeded, (await step.StepAsync("flaky", ct))!.State);
    }

    [Fact(DisplayName = "RunUntilFailed stops on Failed and assertion failures include a scenario dump")]
    public async Task Run_until_failed_and_assertion_dump_work()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartScenarioHostAsync(ct);

        var failed = await Scenario.For(TestJobsManifest.JobThrowNotImplemented, host).EnqueueAsync(ct: ct);
        await failed.RunUntilFailedAsync(ct: ct);

        Assert.Equal(JobStatusCode.Failed, await failed.StatusAsync(ct));

        var ex = await Assert.ThrowsAsync<ScenarioAssertionException>(() => failed.RunUntilDoneAsync(maxTicks: 1, ct));
        Assert.Contains("job-throw-not-implemented", ex.Message, StringComparison.Ordinal);
        Assert.Contains("status=Failed", ex.Message, StringComparison.Ordinal);
    }

    private Task<IActaTestHost> StartScenarioHostAsync(CancellationToken ct) =>
        ActaTestHost.StartAsync(
            (j, schema) =>
            {
                Fixture.ApplyProvider(j, schema);
                j.Run<TestJobsManifest>(TestNamespace, ownerTeam: "test", description: GetType().FullName);
            },
            new ActaTestHostOptions
            {
                Schema = Schema.SchemaName,
                ConfigureServices = services => services.Configure<JobsOptions>(o => o.RegisterFrameworkJobs = false),
            },
            ct
        );
}
