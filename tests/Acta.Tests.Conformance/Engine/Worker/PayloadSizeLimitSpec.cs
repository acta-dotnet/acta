using System.Collections.Concurrent;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Engine.Worker;

/// <summary>
/// Conformance for <c>JobsOptions.MaxInlinePayloadBytes</c>. What a caller sends (enqueue input, raised signal value,
/// handler variable / progress writes) hard-throws <c>PayloadTooLargeException</c> past the cap. A handler's or a step's
/// result past it is stored whole, with a warning, since its body has already run. The cap is configured small so a
/// modest payload trips it.
/// </summary>
[ConformanceSpec(
    "payload-size-limit.enforcement",
    "Caller writes past the cap fail, and results past it are stored",
    Area = "Payloads",
    Contract = "Caller writes past the cap throw PayloadTooLargeException, and a handler's or a step's result past it is stored whole with a warning."
)]
[Deploys("add-numbers", "job-step-oversize-result", "job-step-oversize-result-replay", "oversize-result-probe", "oversize-variable-probe")]
public sealed class PayloadSizeLimitSpec : ActaRuntimeTestBase<ConformanceFixture, JobsOf<PayloadSizeLimitSpec>>
{
    private const int Cap = 1024;

    private readonly RuntimeLog _log = new();

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        base.ConfigureServices(services, testNamespace);
        services.Configure<JobsOptions>(o => o.MaxInlinePayloadBytes = Cap);
        services.AddSingleton<ILogger<WorkerRuntime>>(_log);
    }

    [Fact(DisplayName = "Oversize enqueue input throws PayloadTooLargeException")]
    public async Task Oversize_enqueue_input_throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var oversize = JobPayload.Json(new string('x', Cap * 4));

        await Assert.ThrowsAsync<PayloadTooLargeException>(async () =>
            await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "add-numbers", oversize), ct)
        );
    }

    [Fact(DisplayName = "Oversize signal value throws PayloadTooLargeException")]
    public async Task Oversize_signal_value_throws()
    {
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<PayloadTooLargeException>(async () =>
            await Jobs.RaiseSignalAsync(JobLookup.ById(1), "sig", new string('x', Cap * 4), ct: ct)
        );
    }

    [Fact(DisplayName = "Oversize handler variable write throws PayloadTooLargeException")]
    public async Task Oversize_variable_write_in_handler_throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "oversize-variable-probe", JobPayload.None), ct);

        await Runtime.RunOnceAsync(enqueued, ct);

        Assert.Equal(JobStatusCode.Succeeded, (await Jobs.GetStatusAsync(JobLookup.ById(enqueued.JobId), ct))?.Status);
        Assert.Equal("caught", await Jobs.GetResultAsync<string>(enqueued, ct));
    }

    [Fact(DisplayName = "A handler result past the cap is stored whole, the job succeeds on its first attempt, and a warning names it")]
    public async Task Oversize_handler_result_is_stored_with_a_warning()
    {
        var ct = TestContext.Current.CancellationToken;
        const int length = 8 * Cap;
        var enqueued = await EnqueueOversizeResultAsync(length, ct);

        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(enqueued, ct));

        var job = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Succeeded, job.Status);
        Assert.Equal(0, job.FailureCount);
        Assert.Equal(1, job.ExecutionNumber);
        Assert.Equal(PayloadProbes.Text(length), await Jobs.GetResultAsync<string>(enqueued, ct));
        var warning = Assert.Single(_log.WarningsAbout(enqueued.JobRef));
        Assert.Contains("(oversize-result-probe)", warning, StringComparison.Ordinal);
        Assert.Contains($"the handler's result: {length + 2} bytes, cap {Cap} bytes", warning, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "A step result past the cap is stored whole, the step and its job succeed, and a warning names the step")]
    public async Task Oversize_step_result_is_stored_with_a_warning()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-step-oversize-result", JobPayload.None), ct);
        var expected = PayloadProbes.Text(JobStepProbes.OversizeStepResultLength);

        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(enqueued, ct));

        var job = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Succeeded, job.Status);
        Assert.Equal(0, job.FailureCount);
        Assert.Equal(1, JobStepProbes.BodyInvocations[enqueued.JobId]);
        var step = Assert.Single(await Db.From<StepEntity>().Where(s => s.JobId == enqueued.JobId).ToListAsync(ct));
        Assert.Equal(StepStatusCode.Succeeded, step.Status);
        Assert.Equal(expected, await Jobs.GetStepResultOrDefaultAsync<string>(enqueued, "oversize", ct));
        Assert.Equal(expected, await Jobs.GetResultAsync<string>(enqueued, ct));
        Assert.Single(_log.WarningsAbout(enqueued.JobRef), warning => warning.Contains("step 'oversize': ", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "A step result past the cap is replayed whole on the next execution, its body not run again")]
    public async Task Oversize_step_result_is_replayed_whole()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "job-step-oversize-result-replay", JobPayload.None),
            ct
        );

        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(ControlAction.Applied, (await Jobs.RaiseSignalAsync(enqueued, "proceed", JobPayload.None, ct: ct)).Action);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(enqueued, ct));

        Assert.Equal(1, JobStepProbes.BodyInvocations[enqueued.JobId]);
        Assert.Equal(0, (await ReadJobAsync(enqueued.JobId, ct)).FailureCount);
        Assert.Equal(PayloadProbes.Text(JobStepProbes.OversizeStepResultLength), await Jobs.GetResultAsync<string>(enqueued, ct));
    }

    [Fact(DisplayName = "A result past the completion batch's byte bound lands whole through the run loop")]
    public async Task Result_past_the_batch_bound_lands_through_the_run_loop()
    {
        var ct = TestContext.Current.CancellationToken;
        int[] lengths = [16, WorkerFinalizer.MaxBatchBytes + Cap, 16];
        var enqueued = new List<EnqueueJobResponse>();
        foreach (var length in lengths)
        {
            enqueued.Add(await EnqueueOversizeResultAsync(length, ct));
        }

        using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var loop = Runtime.RunLoopAsync(loopCts.Token);
        try
        {
            var statuses = await SpecWaits.RepeatUntilAsync(
                async () => await Task.WhenAll(enqueued.Select(job => Jobs.GetStatusAsync(job, ct))),
                answers => answers.All(answer => answer?.Status == JobStatusCode.Succeeded),
                ct
            );
            Assert.All(statuses, answer => Assert.Equal(JobStatusCode.Succeeded, answer?.Status));
        }
        finally
        {
            await loopCts.CancelAsync();
            await loop;
        }

        for (var i = 0; i < lengths.Length; i++)
        {
            Assert.Equal(PayloadProbes.Text(lengths[i]), await Jobs.GetResultAsync<string>(enqueued[i], ct));
        }
    }

    private Task<EnqueueJobResponse> EnqueueOversizeResultAsync(int length, CancellationToken ct) =>
        Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "oversize-result-probe", JobPayload.Json(new OversizeResultInput(length))),
            ct
        );

    /// <summary>The lines the worker writes, read by the facts whose contract includes a warning.</summary>
    private sealed class RuntimeLog : ILogger<WorkerRuntime>
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Line)> _lines = new();

        public IEnumerable<string> WarningsAbout(JobRef job) =>
            _lines
                .Where(entry => entry.Level == LogLevel.Warning && entry.Line.Contains(job.ToString(), StringComparison.Ordinal))
                .Select(entry => entry.Line);

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => _lines.Enqueue((logLevel, formatter(state, exception)));
    }
}
