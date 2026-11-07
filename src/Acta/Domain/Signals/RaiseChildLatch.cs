using System.Globalization;

namespace Acta;

/// <summary>
/// Raises a child's terminal-outcome latch (<c>sys.child.{childId}</c>) on its parent through
/// <c>raise_job_signal</c>: upserted Set with the JSON envelope, releasing a Suspended parent. A terminal or
/// missing parent is a no-op, and so is a child that no longer holds <c>childStatus</c>, whose next landing
/// raises the latch itself. Serves the C#-side raise sites (operator cancel, retire, reclaim, the maintenance
/// backstop), and names no execution, since one read before the raise may since have been replaced: the parent
/// reads the child's latest result. Returns true when the raise released the parent.
/// </summary>
internal static class RaiseChildLatch
{
    public const string NamePrefix = "sys.child.";

    public static async Task<bool> Run(
        SignalsOperations signals,
        long childJobId,
        long parentJobId,
        JobStatusCode childStatus,
        CancellationToken ct
    )
    {
        // Write side of the child-latch checkpoint key. The name is persisted in the ledger and matched
        // as text by JobContext.WaitChildAsync, in another process and possibly another culture, so the
        // two renderings must agree byte for byte; the invariant culture is stated on both sides rather
        // than inherited from whatever the ambient one happens to be.
        var latchName = NamePrefix + childJobId.ToString(CultureInfo.InvariantCulture);
        var envelope = ChildOutcomeEnvelope.Write(childJobId, childStatus, executionNumber: null);
        var input = new JobControlInput(null, EventReason.SignalReleased, null);
        var outcome = await signals.RaiseJobSignalAsync(
            new RaiseJobSignalCheckpoint(
                parentJobId,
                CheckpointKindCode.ChildLatch,
                latchName,
                JobPayloadFormat.Json.Id,
                envelope,
                input,
                childJobId,
                childStatus
            ),
            ct
        );
        return outcome.Action == ControlAction.Applied && outcome.Status == JobStatusCode.Ready;
    }
}
