using System.Globalization;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Engine.Worker;

/// <summary>
/// Conformance for <c>ctx.RunWithLockAsync</c> through the worker: a lock one job is inside keeps another job's action
/// from running, a spent wait budget throws, and the lock is free again however the action ended. The lock's row is
/// keyed by its namespace, so the same key in another namespace is another lock.
/// </summary>
[ConformanceSpec(
    "handler-lock.exclusion",
    "A handler lock admits one job's action at a time and is free when it ends",
    Area = "Execution",
    Contract = "RunWithLockAsync runs one job's action at a time, throws when its wait budget is spent, and frees the lock when the action returns, throws or suspends."
)]
public sealed class RunWithLockSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobsManifest>
{
    [Fact(DisplayName = "A job that asks for a lock another job is inside never runs its action and fails with the timeout")]
    public async Task A_held_lock_keeps_the_other_action_out()
    {
        var ct = TestContext.Current.CancellationToken;
        LockHolder.Reset(TestNamespace);
        var holder = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "lock-holder", JobPayload.None), ct);
        var holding = Runtime.RunOnceAsync(holder, ct);
        try
        {
            await LockHolder.Entered(TestNamespace).WaitAsync(SpecWaits.Gate, ct);

            // The lock's row carries the namespace, so the same key in another namespace is another row.
            var key = string.Create(
                CultureInfo.InvariantCulture,
                $"{Runtime.RegisteredNamespaceIds[TestNamespace]}.lock.{LockHolder.LockName}"
            );
            Assert.Single(await Db.From<LockEntity>().Where(l => l.LockKey == key).ToListAsync(ct));

            var contender = await ContendAsync("return", ct);
            Assert.Equal(RunOnceStatus.Failed, await Runtime.RunOnceAsync(contender, ct));
            Assert.Equal(0, await InsideNotesAsync(contender.JobId, ct));
            var failed = await ReadLatestEventAsync(contender.JobId, EventCode.JobFailed, ct);
            Assert.Contains($"Could not acquire lock '{LockHolder.LockName}'", failed.EventMessage, StringComparison.Ordinal);
        }
        finally
        {
            LockHolder.Release(TestNamespace);
        }

        Assert.Equal(RunOnceStatus.Completed, await holding.WaitAsync(SpecWaits.Gate, ct));

        // The holder's action returned, so the next job is let in.
        var next = await ContendAsync("return", ct);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(next, ct));
        Assert.Equal(1, await InsideNotesAsync(next.JobId, ct));
    }

    [Theory(DisplayName = "The lock is free for the next job when the action inside it throws or suspends")]
    [InlineData("throw")]
    [InlineData("wait")]
    public async Task The_lock_is_free_after_the_action_ends(string inside)
    {
        var ct = TestContext.Current.CancellationToken;
        var first = await ContendAsync(inside, ct);
        // A throw fails the job, whose budget is one attempt, and a wait suspends it.
        Assert.Equal(inside == "wait" ? RunOnceStatus.Rearmed : RunOnceStatus.Failed, await Runtime.RunOnceAsync(first, ct));
        Assert.Equal(1, await InsideNotesAsync(first.JobId, ct));

        var next = await ContendAsync("return", ct);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(next, ct));
        Assert.Equal(1, await InsideNotesAsync(next.JobId, ct));
    }

    private async Task<EnqueueJobResponse> ContendAsync(string inside, CancellationToken ct) =>
        await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "lock-contender", JobPayload.Json(new LockContention(inside))), ct);

    private Task<int> InsideNotesAsync(long jobId, CancellationToken ct) => CountEventsAsync(jobId, EventCode.JobNoteRecorded, ct);
}
