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
            null!,
            null!,
            null
        )
        {
            JobRetentionSeconds = 123,
        };
        var job = new ClaimedJob(1, Guid.Empty, 1, null, 2, null, null, null, 0, ReadOnlyMemory<byte>.Empty, null, default, 0)
        {
            LaneId = 7,
            ParentJobId = 8,
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
}
