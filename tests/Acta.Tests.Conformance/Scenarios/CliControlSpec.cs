using Acta.Runtime.Cli;
using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Modules.Execution.Jobs;
using Acta.Runtime.Modules.Execution.Signals;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// Conformance for the jobs CLI runner: each verb maps to the matching IJobs call, exit codes
/// follow the applied/rejected/not-found bands, and debug claims exactly the targeted job and
/// runs it in-process through the normal pipeline.
/// </summary>
[ConformanceSpec(
    "cli.control-surface",
    "CLI verbs map onto IJobs and debug runs the targeted job in-process",
    Area = "Control",
    Contract = "CLI verbs apply the matching IJobs control or read with banded exit codes and debug claims only the targeted job for an in-process run.",
    Arrange = "A CliCommandRunner is wired over a namespace with one enqueued Ready job.",
    Act = "The pause, resume, cancel, restart, signal, info, status, debug, result, and events verbs run against the job.",
    Assert = "Each verb maps to the matching IJobs call with banded exit codes and debug claims only the targeted job for an in-process run."
)]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.PauseJobAsync))]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.ResumeJobAsync))]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.CancelJobAsync))]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.RestartJobAsync))]
[CoversStoreMethod(typeof(ISignalStore), nameof(ISignalStore.RaiseSignalAsync))]
[CoversStoreMethod(typeof(IExecutionStore), nameof(IExecutionStore.ClaimOneAsync))]
public abstract class CliControlSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    protected override bool RunAsWorker => true;

    [Fact(DisplayName = "Pause and resume verbs map to IJobs, exit zero, and apply the transitions")]
    public async Task Pause_resume_round_trip_exits_zero_and_transitions()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await EnqueueOneAsync(TestKey("cli-pause"), ct);
        var runner = CreateRunner(out var output);

        var pauseExit = await runner.RunAsync(Parse($"pause {enqueued.JobId} --reason hold"), ct);
        Assert.Equal(0, pauseExit);
        Assert.Equal(JobStatusCode.Paused, await Jobs.GetStatusAsync(enqueued, ct));
        Assert.Contains("status: Paused", output.ToString());

        var resumeExit = await runner.RunAsync(Parse($"resume {enqueued.JobId}"), ct);
        Assert.Equal(0, resumeExit);
        Assert.Equal(JobStatusCode.Ready, await Jobs.GetStatusAsync(enqueued, ct));
    }

    [Fact(DisplayName = "Cancel and restart verbs map to IJobs and apply the transitions")]
    public async Task Cancel_applies_and_restart_revives()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await EnqueueOneAsync(TestKey("cli-cancel"), ct);
        var runner = CreateRunner(out _);

        Assert.Equal(0, await runner.RunAsync(Parse($"cancel {enqueued.JobId} --reason done"), ct));
        Assert.Equal(JobStatusCode.Cancelled, await Jobs.GetStatusAsync(enqueued, ct));

        Assert.Equal(0, await runner.RunAsync(Parse($"restart {enqueued.JobId}"), ct));
        Assert.Equal(JobStatusCode.Ready, await Jobs.GetStatusAsync(enqueued, ct));
    }

    [Fact(DisplayName = "Exit codes follow action bands: illegal move exits one, missing job exits two")]
    public async Task Illegal_resume_exits_one_missing_job_exits_two()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await EnqueueOneAsync(TestKey("cli-bands"), ct);
        var runner = CreateRunner(out _);

        // Ready job: resume is Rejected.
        Assert.Equal(1, await runner.RunAsync(Parse($"resume {enqueued.JobId}"), ct));
        Assert.Equal(2, await runner.RunAsync(Parse($"cancel {long.MaxValue}"), ct));
        Assert.Equal(2, await runner.RunAsync(Parse($"info {long.MaxValue}"), ct));
    }

    [Fact(DisplayName = "Signal verb maps to IJobs and applies on a live job")]
    public async Task Signal_applies_on_live_job()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await EnqueueOneAsync(TestKey("cli-signal"), ct);
        var runner = CreateRunner(out _);

        Assert.Equal(0, await runner.RunAsync(Parse($"signal {enqueued.JobId} approval"), ct));
    }

    [Fact(DisplayName = "Info and status read verbs print the job row")]
    public async Task Info_and_status_print_the_row()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await EnqueueOneAsync(TestKey("cli-info"), ct);
        var runner = CreateRunner(out var output);

        // Addressed by the public ref, the identity the CLI also prints back.
        Assert.Equal(0, await runner.RunAsync(Parse($"info {enqueued.JobRef}"), ct));
        Assert.Equal(0, await runner.RunAsync(Parse($"status {enqueued.JobRef}"), ct));
        var text = output.ToString();
        Assert.Contains($"job: {enqueued.JobRef}", text);
        Assert.Contains("name: add-numbers", text);
        Assert.Contains("status: Ready", text);
    }

    [Fact(DisplayName = "A verb resolves a job by deduplication key with an explicit namespace")]
    public async Task DeduplicationKey_lookup_resolves_with_explicit_ns()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = TestKey("cli-key");
        var enqueued = await EnqueueOneAsync(key, ct);
        var runner = CreateRunner(out _);

        Assert.Equal(0, await runner.RunAsync(Parse($"pause {key} --ns {TestNamespace}"), ct));
        Assert.Equal(JobStatusCode.Paused, await Jobs.GetStatusAsync(enqueued, ct));
    }

    [Fact(DisplayName = "Debug claims only the targeted id, runs it in-process to Succeeded, and result surfaces the payload")]
    public async Task Debug_runs_the_targeted_job_to_done()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await EnqueueOneAsync(TestKey("cli-debug"), ct);
        var runner = CreateRunner(out var output);

        var exit = await runner.RunAsync(Parse($"debug {enqueued.JobId}"), ct);

        Assert.Equal(0, exit);
        Assert.Equal(JobStatusCode.Succeeded, await Jobs.GetStatusAsync(enqueued, ct));
        Assert.Contains("run: Completed", output.ToString());

        // The result verb surfaces the handler's payload: AddNumbers(2, 3) produced Sum = 5.
        var resultRunner = CreateRunner(out var resultOutput);
        Assert.Equal(0, await resultRunner.RunAsync(Parse($"result {enqueued.JobId}"), ct));
        Assert.Contains("5", resultOutput.ToString());
    }

    [Fact(DisplayName = "Events verb prints the job timeline after a run")]
    public async Task Events_prints_the_job_timeline_after_a_run()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await EnqueueOneAsync(TestKey("cli-events"), ct);
        var runner = CreateRunner(out _);

        Assert.Equal(0, await runner.RunAsync(Parse($"debug {enqueued.JobId}"), ct));

        var eventsRunner = CreateRunner(out var eventsOutput);
        Assert.Equal(0, await eventsRunner.RunAsync(Parse($"events {enqueued.JobId}"), ct));
        var text = eventsOutput.ToString();
        Assert.Contains($"Events for job {enqueued.JobRef}", text);
        Assert.Contains("job.execution-started", text);
        Assert.Contains("job.execution-finished", text);
    }

    private CliCommandRunner CreateRunner(out StringWriter output)
    {
        output = new StringWriter();
        return new CliCommandRunner(Jobs, Operations, [Runtime], [TestNamespace], output, output);
    }

    private static CliCommand Parse(string commandLine)
    {
        var ok = CliCommandParser.TryParse(commandLine.Split(' '), out var command, out var error);
        Assert.True(ok, error);
        return command;
    }

    private async Task<JobEnqueueOutcome> EnqueueOneAsync(string deduplicationKey, CancellationToken ct)
    {
        var serializers = Services.GetRequiredService<IJobPayloadSerializerRegistry>();
        var payload = serializers.Resolve(JobPayloadFormat.Json.Id).Serialize(new AddNumbers(2, 3));

        return await Jobs.EnqueueAsync(
            new JobEnqueueRequest(
                JobNamespace: TestNamespace,
                JobName: "add-numbers",
                Input: payload,
                DeduplicationKey: deduplicationKey,
                CorrelationKey: null,
                Priority: null
            ),
            ct
        );
    }
}
