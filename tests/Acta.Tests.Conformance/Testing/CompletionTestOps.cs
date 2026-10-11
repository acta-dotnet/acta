using Xunit;

namespace Acta.Tests.Conformance.Testing;

/// <summary>Completes claimed attempts the way the worker's finalizer does: rows through <c>complete_executions</c>.</summary>
internal static class CompletionTestOps
{
    /// <summary>A landing for <paramref name="claimed"/> with no result: Succeeded, or Failed when not <paramref name="succeeded"/>.</summary>
    public static CompleteExecutionsValues Landing(ClaimedJob claimed, bool succeeded = true) =>
        new(
            claimed.JobId,
            claimed.ExecutionNumber,
            succeeded,
            JobPayloadFormat.None.Id,
            ReadOnlyMemory<byte>.Empty,
            null,
            null,
            null,
            null,
            null
        )
        {
            HasLane = claimed.LaneId is not null,
            HasParent = claimed.ParentId is not null,
        };

    /// <summary>Writes <paramref name="row"/> alone as <paramref name="workerId"/> and answers its row.</summary>
    public static async Task<CompleteExecutionsRow> CompleteAsync(
        this ExecutionOperations execution,
        int workerId,
        CompleteExecutionsValues row,
        CancellationToken ct
    ) => Assert.Single((await execution.CompleteExecutionsAsync(new CompleteExecutionsCommand(workerId, [row]), ct)).Rows);
}
