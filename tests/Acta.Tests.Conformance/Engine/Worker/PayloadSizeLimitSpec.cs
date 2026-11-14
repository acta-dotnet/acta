using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Engine.Worker;

/// <summary>
/// Conformance for <c>JobsOptions.MaxInlinePayloadBytes</c> enforcement. Caller-controlled writes
/// (enqueue input, raised signal value, handler variable / progress writes) hard-throw
/// <c>PayloadTooLargeException</c> past the cap, and a handler result past it fails the job for good, since a retry
/// would run the finished handler again. The cap is configured small so a modest payload trips it.
/// </summary>
[ConformanceSpec(
    "payload-size-limit.enforcement",
    "Every write past the cap fails, a handler's result included",
    Area = "Payloads",
    Contract = "Caller writes past the cap throw PayloadTooLargeException, and a handler result past it fails the job for good instead of being dropped."
)]
[Deploys("add-numbers", "oversize-result-probe", "oversize-variable-probe")]
public sealed class PayloadSizeLimitSpec : ActaRuntimeTestBase<ConformanceFixture, JobsOf<PayloadSizeLimitSpec>>
{
    private const int Cap = 1024;

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        base.ConfigureServices(services, testNamespace);
        services.Configure<JobsOptions>(o => o.MaxInlinePayloadBytes = Cap);
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

    [Fact(DisplayName = "An oversize handler result fails the job for good")]
    public async Task Oversize_handler_result_fails_the_job_for_good()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "oversize-result-probe", JobPayload.None), ct);

        // The default budget has attempts left: a retryable failure would re-arm the job and run the handler again.
        Assert.Equal(RunOnceStatus.Failed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(RunOnceStatus.NothingClaimed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(JobStatusCode.Failed, (await Jobs.GetStatusAsync(JobLookup.ById(enqueued.JobId), ct))?.Status);
        Assert.False((await Jobs.TryGetResultAsync<string>(enqueued, ct)).Found);
    }
}
