using Xunit;

namespace Acta.Tests.Execution;

public sealed class CompletionCommandTests
{
    [Fact]
    public void A_one_shot_success_allocates_one_command_and_preserves_its_result_and_graph_flags()
    {
        var descriptor = new JobDescriptor(
            "one-shot",
            typeof(CompletionCommandTests),
            "Run",
            typeof(object),
            null,
            default,
            null,
            default,
            1,
            default,
            default,
            default,
            default,
            null!,
            null!,
            null
        )
        {
            JobRetentionSeconds = 123,
        };
        var job = new ClaimedJob(1, Guid.Empty, 1, null, 2, null, null, 0, ReadOnlyMemory<byte>.Empty, null, default, 0)
        {
            LaneId = 7,
            ParentJobId = 8,
            WaitMs = 11,
        };
        var completion = new ExecutionCompletion(CompletionKind.Succeeded)
        {
            ResultFormatId = 3,
            Result = new byte[] { 4, 5, 6 },
            DurationMs = 9,
        };
        var expected = WorkerFinalizer.BuildCommand(descriptor, job, completion, default);
        Assert.Equal(completion.Result, expected.Result);
        Assert.Equal(completion.ResultFormatId, expected.ResultFormatId);
        Assert.Equal(123, expected.RetentionSeconds);
        Assert.Equal(9, expected.DurationMs);
        Assert.Equal(11, expected.WaitMs);
        Assert.True(expected.HasLane);
        Assert.True(expected.HasParent);
        Assert.Null(expected.HandlerStatusCode);
        Assert.Null(expected.FinalStatus);

        var cancelled = WorkerFinalizer.BuildCommand(
            descriptor,
            job,
            completion with
            {
                HandlerStatusCode = JobStatusCode.Cancelled,
            },
            default
        );
        Assert.Equal((byte)JobStatusCode.Cancelled, cancelled.HandlerStatusCode);
        Assert.True(cancelled.Result.IsEmpty);
        var rescheduled = WorkerFinalizer.BuildCommand(
            descriptor,
            job,
            completion with
            {
                Outcome = CompletionKind.Rescheduled,
                RescheduleDelaySeconds = 5,
            },
            default
        );
        Assert.Equal(EventCode.JobRescheduled, rescheduled.RescheduleEventCode);
        Assert.True(rescheduled.Result.IsEmpty);
        Assert.Equal<(int?, int?)>((11, 11), (cancelled.WaitMs, rescheduled.WaitMs));

        var oneRecord = Allocations(() => expected with { });
        var actual = Allocations(() => WorkerFinalizer.BuildCommand(descriptor, job, completion, default));
        Assert.True(
            actual <= oneRecord + 1024,
            $"Successful completions allocated {actual:N0} bytes; one command each needs {oneRecord:N0}."
        );

        static long Allocations(Func<CompleteExecutionsValues> create)
        {
            for (var i = 0; i < 128; i++)
            {
                GC.KeepAlive(create());
            }
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1024; i++)
            {
                GC.KeepAlive(create());
            }
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
    }

    [Theory]
    [InlineData(EventReason.NonRetryableException)]
    [InlineData(EventReason.WaitTimedOut)]
    public void A_non_retryable_failure_of_a_permanent_job_settles_the_firing_and_keeps_the_job(string reason)
    {
        var descriptor = new JobDescriptor(
            "slot",
            typeof(CompletionCommandTests),
            "Run",
            typeof(object),
            null,
            default,
            null,
            default,
            3,
            default,
            default,
            default,
            default,
            null!,
            null!,
            null
        );
        var job = new ClaimedJob(1, Guid.Empty, 1, null, 2, null, null, 0, ReadOnlyMemory<byte>.Empty, null, default, 0)
        {
            Kind = JobKindCode.Permanent,
        };
        var completion = ExecutionCompletion.Terminal(CompletionKind.Failed, reason, "no", JobStatusCode.Failed);

        var slot = WorkerFinalizer.BuildCommand(descriptor, job, completion, default);
        var oneShot = WorkerFinalizer.BuildCommand(descriptor, job with { Kind = JobKindCode.Ordinary }, completion, default);

        // The firing failed for good and the slot waits for its next one; the same exception ends a one-shot job.
        Assert.Equal((JobStatusCode.Idle, EventCode.JobExecutionFailed, false), (slot.FinalStatus, slot.EventCode, slot.Succeeded));
        Assert.Null(slot.FailureCount);
        Assert.Equal((byte)JobStatusCode.Failed, oneShot.HandlerStatusCode);
        Assert.Null(oneShot.FinalStatus);
    }

    [Fact]
    public void The_metric_counts_a_sleep_as_slept_and_a_wait_as_suspended()
    {
        var sleep = new ExecutionCompletion(CompletionKind.Suspended) { RescheduleResumeAtUtc = DateTime.UnixEpoch };
        var wait = new ExecutionCompletion(CompletionKind.Suspended) { WaitSignalName = "approval" };

        Assert.Equal("slept", WorkerFinalizer.OutcomeTag(sleep));
        Assert.Equal("suspended", WorkerFinalizer.OutcomeTag(wait));
    }
}
