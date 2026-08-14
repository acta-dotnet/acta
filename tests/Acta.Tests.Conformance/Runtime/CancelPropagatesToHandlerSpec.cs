using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Runtime;

/// <summary>
/// Conformance for manual-cancel -> <c>CancellationToken</c> propagation. An external cancel of a running
/// job (via the public <c>IJobs.CancelAsync</c>) reaches the executing handler's token through the next
/// heartbeat tick, so the handler stops cooperatively and the row settles Cancelled.
/// </summary>
[ConformanceSpec(
    "cancel.propagates-to-handler",
    "An external cancel reaches the running handler's token via heartbeat",
    Area = "Control",
    Contract = "An external cancel reaches the handler's CancellationToken through the next heartbeat tick so the handler stops cooperatively and the row settles Cancelled.",
    Arrange = "A cancellable handler that blocks on its attempt token is registered.",
    Act = "The job runs, is cancelled through the public IJobs.CancelAsync, and one heartbeat ticks.",
    Assert = "The handler's token fires so it stops cooperatively and the row settles Cancelled."
)]
public abstract class CancelPropagatesToHandlerSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    [Fact(
        DisplayName = "An external cancel fires the handler token via heartbeat, the handler stops cooperatively, and the job settles Cancelled"
    )]
    public async Task ExternalCancel_OfARunningJob_CancelsTheHandlersToken()
    {
        var ct = TestContext.Current.CancellationToken;
        var timeout = TimeSpan.FromSeconds(15);
        CancellableHandler.Reset(TestNamespace);

        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "cancellable", JobPayload.None), ct);

        // Dispatch in the background; the handler blocks on its token until cancelled.
        var run = Runtime.RunOnceAsync(enqueued, ct);
        await CancellableHandler.Started(TestNamespace).WaitAsync(timeout, ct);

        // Cancel through the public surface (Executing -> Cancelled), then drive one heartbeat tick - the
        // channel that propagates an external cancel to the running handler's token.
        var cancel = await Jobs.CancelAsync(JobLookup.ById(enqueued.JobId), ct: ct);
        Assert.Equal(ControlAction.Applied, cancel.Action);
        await Runtime.RunHeartbeatOnceAsync(ct);

        // The handler observed cancellation; the dispatch unwinds; the row is terminal Cancelled.
        Assert.True(await CancellableHandler.Observed(TestNamespace).WaitAsync(timeout, ct));
        await run.WaitAsync(timeout, ct);
        Assert.Equal(JobStatusCode.Cancelled, await Jobs.GetStatusAsync(JobLookup.ById(enqueued.JobId), ct));
    }
}
