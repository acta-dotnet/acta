using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Runtime;

/// <summary>
/// Conformance for <c>JobsOptions.MaxInlinePayloadBytes</c> enforcement. Caller-controlled writes
/// (enqueue input, raised signal value, handler variable / progress writes) hard-throw
/// <c>PayloadTooLargeException</c> past the cap; a handler result that exceeds the cap instead
/// warns-and-persists (the job still completes). The cap is configured small so a modest payload trips it.
/// </summary>
[ConformanceSpec(
    "payload-size-limit.enforcement",
    "Caller writes hard-throw past the cap but a result warns and persists",
    Area = "Payloads",
    Contract = "Caller writes past the cap throw PayloadTooLargeException and an oversize handler result is warned-and-persisted so the job still completes.",
    Arrange = "MaxInlinePayloadBytes is configured to a small 1 KB cap.",
    Act = "Oversize enqueue input, signal value, and handler variable writes are attempted, and a handler returns an oversize result.",
    Assert = "Each caller write throws PayloadTooLargeException while the oversize result is warned-and-persisted and the job lands Done."
)]
public abstract class PayloadSizeLimitSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
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
        var oversize = JobPayload.Text(new string('x', Cap * 4));

        await Assert.ThrowsAsync<PayloadTooLargeException>(async () =>
            await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "add-numbers", oversize), ct)
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
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "oversize-variable-probe", JobPayload.None), ct);

        await Runtime.RunOnceAsync(enqueued, ct);

        Assert.Equal(JobStatusCode.Succeeded, await Jobs.GetStatusAsync(JobLookup.ById(enqueued.JobId), ct));
        Assert.Equal("caught", await Jobs.GetResultAsync<string>(enqueued, ct));
    }

    [Fact(DisplayName = "Oversize handler result persists and the job completes Done")]
    public async Task Oversize_handler_result_is_persisted_not_thrown()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "oversize-result-probe", JobPayload.None), ct);

        await Runtime.RunOnceAsync(enqueued, ct);

        Assert.Equal(JobStatusCode.Succeeded, await Jobs.GetStatusAsync(JobLookup.ById(enqueued.JobId), ct));
        var result = await Jobs.GetResultAsync<string>(enqueued, ct);
        Assert.NotNull(result);
        Assert.True(result!.Length > Cap, "oversize result was not persisted intact.");
    }
}
