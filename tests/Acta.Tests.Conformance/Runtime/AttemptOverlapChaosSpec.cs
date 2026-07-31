using Acta.Runtime.Modules.Execution;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Runtime;

/// <summary>
/// Conformance for in-process attempt overlap. A reclaimed job can be re-dispatched by the same
/// worker while the stale attempt's handler is still unwinding; when the stale attempt finally
/// exits, the replacement attempt must stay registered with the heartbeat so external cancels and
/// lock renewals still reach it.
/// </summary>
[ConformanceSpec(
    "chaos.attempt-overlap",
    "A stale attempt's unwind does not untrack its in-process replacement",
    Area = "Chaos",
    Contract = "A stale attempt that unwinds after its job was reclaimed and re-dispatched in-process leaves the replacement attempt tracked for heartbeat cancellation.",
    Arrange = "A blocking attempt-overlap probe is registered so the first attempt can outlive its lease.",
    Act = "The lease expires, the job is reclaimed and re-dispatched in-process, the stale attempt unwinds, and an external cancel is issued.",
    Assert = "The replacement attempt stays tracked so the cancel reaches it via heartbeat and the job settles Cancelled."
)]
public abstract class AttemptOverlapChaosSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    [Fact(
        DisplayName = "A stale attempt's cleanup removes only its own tracking entry, the external cancel reaches the replacement via heartbeat, and the job settles Cancelled once"
    )]
    public async Task Stale_attempt_unwind_keeps_the_replacement_attempt_tracked()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var enqueued = await ChaosSpecHelpers.EnqueueNoPayloadAsync(Jobs, TestNamespace, "attempt-overlap", ct);
        AttemptOverlapProbe.Reset(enqueued.JobId);

        // --- 1. The first attempt blocks mid-handler; its lease expires and reclaim re-readies the job.
        var staleRun = Runtime.RunOnceAsync(enqueued, ct);
        await AttemptOverlapProbe.Started(enqueued.JobId, 1).WaitAsync(Timeout, ct);
        await ChaosSpecHelpers.ExpireLeaseAsync(Db, enqueued.JobId, ct);
        Assert.Equal(1, await ChaosSpecHelpers.ReclaimAsync(Services, ns, ct));
        await ChaosSpecHelpers.SetReadyAsync(Db, enqueued.JobId, ct);

        // --- 2. The same worker re-dispatches the job while the stale handler is still blocked.
        var replacementRun = Runtime.RunOnceAsync(enqueued, ct);
        await AttemptOverlapProbe.Started(enqueued.JobId, 2).WaitAsync(Timeout, ct);

        // --- 3. Release the stale attempt so its cleanup runs while the replacement is mid-handler.
        AttemptOverlapProbe.Release(enqueued.JobId, 1);
        var staleOutcome = await staleRun.WaitAsync(Timeout, ct);
        Assert.Contains(staleOutcome, new[] { RunOnceOutcome.NothingClaimed, RunOnceOutcome.Rearmed });

        // --- 4. An external cancel must still reach the replacement attempt through the heartbeat.
        var cancel = await Jobs.CancelAsync(JobLookup.ById(enqueued.JobId), ct: ct);
        Assert.Equal(JobControlAction.Applied, cancel.Action);
        await Runtime.RunHeartbeatOnceAsync(ct);
        await AttemptOverlapProbe.Cancelled(enqueued.JobId, 2).WaitAsync(Timeout, ct);

        await replacementRun.WaitAsync(Timeout, ct);
        Assert.Equal(JobStatusCode.Cancelled, await Jobs.GetStatusAsync(enqueued, ct));
    }
}
