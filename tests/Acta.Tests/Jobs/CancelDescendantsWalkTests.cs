using Acta.Tests.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Jobs;

/// <summary>
/// What follows an operator's cancel once it commits. Nothing else raises the parent's latch or cancels the rest of the
/// tree, so the latch, the wake and the walk run to the end whatever the caller does.
/// </summary>
public sealed class CancelDescendantsWalkTests
{
    [Fact(DisplayName = "A cancel raises the parent's latch and walks every descendant after its caller stops waiting")]
    public async Task A_cancel_finishes_its_follow_ups_after_its_caller_stops_waiting()
    {
        var ct = TestContext.Current.CancellationToken;
        var latching = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var walked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Job 1, a child of job 9, has children 2 and 3. Every call gives up when its token is cancelled, as a database
        // call does, and the latch raise waits until the test lets it go.
        var session = new ScriptedSession(
            async (call, token) =>
            {
                token.ThrowIfCancellationRequested();
                switch (call.Stem)
                {
                    case "CancelJob":
                        return new CancelJobRow(
                            ControlAction.Applied,
                            JobStatusCode.Cancelled,
                            call.Get(ActaSchema.Job.JobId) == 1 ? 9 : null,
                            1,
                            false
                        );
                    case "RaiseSignal":
                        latching.TrySetResult();
                        await resume.Task.WaitAsync(token);
                        return new RaiseSignalRow(ControlAction.Applied, JobStatusCode.Ready);
                    case "GetChildJobIds":
                        var parent = call.Get(ActaSchema.Job.ParentJobId);
                        if (parent == 3)
                        {
                            walked.TrySetResult();
                        }
                        return parent == 1 ? new long[] { 2, 3 } : Array.Empty<long>();
                    default:
                        throw new NotSupportedException(call.Stem);
                }
            }
        );
        var cancel = session.Services().GetRequiredService<CancelJobHandler>();

        using var caller = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var cancelling = cancel.HandleAsync(new CancelJobCommand(JobLookup.ById(1), null, null, null), caller.Token).AsTask();
        await latching.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        await caller.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelling);

        resume.SetResult();
        await walked.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        Assert.Equal([1L, 2L, 3L], session.Calls.Where(call => call.Stem == "CancelJob").Select(call => call.Get(ActaSchema.Job.JobId)));
    }
}
